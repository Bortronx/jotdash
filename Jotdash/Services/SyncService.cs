using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jotdash.Shared;

namespace Jotdash.Services;

/// <summary>
/// Two-way sync with the sync server: push local edits → upload files → pull server changes.
/// Safe to call any time from anywhere (UI, timer, Android WorkManager); runs one at a time.
/// Conflict rules: tasks created on this phone keep the phone's version;
/// tasks that came from Plane follow Plane (the server copy).
/// </summary>
public class SyncService(LocalStore store, SettingsService settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _again;
    private DateTime _metaAt = DateTime.MinValue;

    public bool IsSyncing { get; private set; }
    public string LastDiagnostics { get; private set; } = "No sync attempt yet.";
    public event Action? StatusChanged;

    /// <summary>Sync now. Returns null on success, otherwise a short error message.</summary>
    public async Task<string?> SyncAsync(CancellationToken ct = default, bool refreshWorkspace = false)
    {
        var conn = await settings.GetAsync();
        if (!conn.IsSet)
        {
            LastDiagnostics = "No request sent: server address is not configured or is invalid.";
            return "No server set up yet (Menu → Settings). Everything is saved on this phone.";
        }
        if (Connectivity.Current.NetworkAccess == NetworkAccess.None)
        {
            LastDiagnostics = "No request sent: phone is offline.";
            return "Offline – will sync when back online.";
        }

        // Workspace selection must not lose its requested refresh behind a running background sync.
        if (refreshWorkspace) await _gate.WaitAsync(ct);
        else if (!await _gate.WaitAsync(0)) { _again = true; return null; } // already running: run once more afterwards
        string? error = null;
        using var diagnostics = new ConnectionDiagnostics(conn);
        HttpClient? http = null;
        try
        {
            IsSyncing = true;
            StatusChanged?.Invoke();
            // Confirm API access before sending notes/files; never post them to a login page.
            var (client, meta, _) = await ConnectAsync(conn, CreateHandler(conn, diagnostics), diagnostics, ct);
            http = client;
            store.SetSyncState(meta: meta);
            _metaAt = DateTime.UtcNow;
            await settings.RememberAsync(meta.Addresses);
            do
            {
                _again = false;
                await PushTasksAsync(http, ct);
                await UploadFilesAsync(http, ct);
                await PullAsync(http, ct, refreshWorkspace ? store.SelectedWorkspace : null);
                refreshWorkspace = false;
            } while (_again);
            store.SetSyncState(lastSync: DateTime.UtcNow);
        }
        catch (Exception e)
        {
            diagnostics.Failure(e);
            error = e is HttpRequestException { StatusCode: { } code } ? $"Server answered {(int)code} {code}" : e.Message;
            store.SetSyncState(error: error);
        }
        finally
        {
            http?.Dispose();
            LastDiagnostics = diagnostics.Report;
            IsSyncing = false;
            _gate.Release();
            StatusChanged?.Invoke();
        }
        return error;
    }

    /// <summary>Result of Settings → Test: message plus what the server reported (for remembering backups).</summary>
    public record TestResult(bool Ok, string Message, List<string> Addresses);

    /// <summary>Checks every address + credentials (Settings → Test). Stops at the first that works.</summary>
    public static async Task<TestResult> TestAsync(ConnectionSettings conn, Action<string>? report = null)
    {
        using var diagnostics = new ConnectionDiagnostics(conn);
        try
        {
            var (http, meta, url) = await ConnectAsync(conn, CreateHandler(conn, diagnostics), diagnostics, default);
            http.Dispose();
            string via = conn.Label(url) == "main address" ? "" : $" via {conn.Label(url)} ({url})";
            return new(true, $"✅ Connected{via} – {meta.Workspaces.Count} Plane workspace(s), {meta.Workspaces.Sum(w => w.Projects.Count)} project(s).", meta.Addresses);
        }
        catch (Exception e)
        {
            diagnostics.Failure(e);
            return new(false, "❌ " + e.Message, new());
        }
        finally { report?.Invoke(diagnostics.Report); }
    }

    /// <summary>Gets a file that only exists on the server (e.g. attached from another device). Returns the local path.</summary>
    public async Task<string?> DownloadAsync(string taskId, FileDto file)
    {
        var conn = await settings.GetAsync();
        if (!conn.IsSet) return null;
        using var handler = CreateHandler(conn, null);
        var (client, _, _) = await ConnectAsync(conn, handler, null, default);
        using var http = client;
        using var resp = await http.GetAsync($"api/files/{file.Id}", HttpCompletionOption.ResponseHeadersRead);
        await EnsureApiResponseAsync(resp);
        string path = Path.Combine(store.FilesDir, file.Id + Path.GetExtension(file.Name));
        await using (var fs = File.Create(path)) await resp.Content.CopyToAsync(fs);
        store.Write(ts =>
        {
            var t = ts.FirstOrDefault(x => x.Data.Id == taskId);
            if (t is null) return;
            t.LocalFiles[file.Id] = path;
            t.Uploaded.Add(file.Id);
        });
        return path;
    }

    /// <summary>diagnostics → credentials (per address) → network. Shared by the clients for every address.</summary>
    private static HttpMessageHandler CreateHandler(ConnectionSettings conn, ConnectionDiagnostics? diagnostics)
    {
        // Android's native HttpClientHandler can throw InvalidCastException when
        // automatic redirects are disabled; SocketsHttpHandler works cross-platform.
        var transport = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) };
        HttpMessageHandler handler = new CredentialsHandler(conn) { InnerHandler = transport };
        if (diagnostics is not null)
        {
            diagnostics.InnerHandler = handler;
            handler = diagnostics;
        }
        return handler;
    }

    private sealed class CredentialsHandler(ConnectionSettings conn) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            conn.Apply(request.Headers, request.RequestUri);
            return base.SendAsync(request, ct);
        }
    }

    /// <summary>Tries the main address, then backups, until one answers like the sync API.</summary>
    private static async Task<(HttpClient Http, MetaDto Meta, string Url)> ConnectAsync(
        ConnectionSettings conn, HttpMessageHandler handler, ConnectionDiagnostics? diagnostics, CancellationToken ct)
    {
        var urls = conn.Addresses();
        if (urls.Count == 0) throw new InvalidOperationException("Enter a server address first.");
        Exception? first = null;
        foreach (string url in urls)
        {
            var uri = new Uri(url);
            diagnostics?.Note($"Trying {conn.Label(url)} ({uri.Scheme}, port {uri.Port})");
            var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(10) };
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(TimeSpan.FromSeconds(20));
                var meta = await ReadJsonAsync<MetaDto>(http, "api/meta", probe.Token);
                return (http, meta, url);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                http.Dispose();
                diagnostics?.Failure(e);
                first ??= e;
            }
        }
        string message = first switch
        {
            OperationCanceledException => "The server did not answer in time.",
            HttpRequestException { StatusCode: null } h => "Can't reach the server: " + h.Message,
            _ => first!.Message,
        };
        if (urls.Count > 1) message += $" (Also tried {urls.Count - 1} backup address(es) – none answered.)";
        throw new InvalidOperationException(message, first);
    }

    private const string AuthHelp =
        "Pangolin blocked the request. Fix one of these: " +
        "(1) HTTP Basic: in Pangolin open the resource for THIS address → Authentication → Basic Header Auth, set a username + password and enter the same here. It is set per resource, so a Plane login does not count. " +
        "(2) Or choose Pangolin share link and paste a share link made for this resource. " +
        "(3) Or add a NetBird / home Wi-Fi backup address. Your tasks stay saved on this phone.";

    private static async Task EnsureApiResponseAsync(HttpResponseMessage response)
    {
        int code = (int)response.StatusCode;
        if (code is 401 or 403 && (await response.Content.ReadAsStringAsync()).Contains("X-QuickNotes-Key"))
            throw new InvalidOperationException("The sync server needs its server key: enter it under Server key.");
        if (code is >= 300 and < 400 or 401 or 403)
            throw new InvalidOperationException($"HTTP {code}. " + AuthHelp);
        response.EnsureSuccessStatusCode();
        string? type = response.Content.Headers.ContentType?.MediaType;
        if (type is "text/html" or "application/xhtml+xml")
            throw new InvalidOperationException("The address returned a web page, not the sync API. " + AuthHelp);
        // Some proxies omit Content-Type on their login pages.
        if (type is null || type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            string body = await response.Content.ReadAsStringAsync();
            if (body.TrimStart().StartsWith('<'))
                throw new InvalidOperationException("The address returned a web page, not the sync API. " + AuthHelp);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        await EnsureApiResponseAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new InvalidOperationException("The sync server returned an empty response.");
    }

    // ── 1. Push local edits ──────────────────────────────────────────────────
    private async Task PushTasksAsync(HttpClient http, CancellationToken ct)
    {
        foreach (var t in store.Read(ts => ts.Where(x => x.Dirty).Select(LocalStore.Copy).ToList()))
        {
            var dto = t.Data;
            var sent = new TaskDto
            {
                Id = dto.Id, Text = dto.Text, Workspace = dto.Workspace, ProjectId = dto.ProjectId, StateId = dto.StateId,
                StateGroup = dto.StateGroup, CreatedLocally = dto.CreatedLocally, Deleted = dto.Deleted,
                CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt, Version = dto.Version,
            };
            using var resp = await http.PostAsJsonAsync("api/tasks", sent, Json, ct);
            await EnsureApiResponseAsync(resp);
            var server = (await resp.Content.ReadFromJsonAsync<TaskDto>(Json, ct))!;
            store.Write(ts =>
            {
                var local = ts.FirstOrDefault(x => x.Data.Id == t.Data.Id);
                if (local is null) return;
                if (local.EditStamp == t.EditStamp) local.Dirty = false; // otherwise: edited again meanwhile, push next round
                if (!local.Data.CreatedLocally && server.Text != sent.Text)
                    ApplyServer(local, server); // Plane changed it first: Plane wins for Plane tasks
                else
                {
                    local.Data.Version = server.Version;
                    local.Data.PlaneUrl = server.PlaneUrl;
                    local.Data.StateGroup = server.StateGroup ?? local.Data.StateGroup;
                    FillDefaults(local, server);
                }
                if (local.Data.Deleted && !local.Dirty) RemoveLocal(ts, local);
            });
        }
    }

    // ── 2. Upload files (after their task exists on the server) ─────────────
    private async Task UploadFilesAsync(HttpClient http, CancellationToken ct)
    {
        var pending = store.Read(ts => ts
            .Where(t => !t.Data.Deleted && !(t.Dirty && t.Data.Version == 0))
            .SelectMany(t => t.LocalFiles.Where(f => !t.Uploaded.Contains(f.Key))
                .Select(f => (TaskId: t.Data.Id, FileId: f.Key, Path: f.Value, Info: t.Data.Files.FirstOrDefault(x => x.Id == f.Key))))
            .ToList());

        foreach (var p in pending)
        {
            if (p.Info is null || !File.Exists(p.Path)) continue;
            await using var stream = File.OpenRead(p.Path);
            var content = new StreamContent(stream);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(p.Info.ContentType);
            using var resp = await http.PutAsync(
                $"api/tasks/{p.TaskId}/files/{p.FileId}?kind={p.Info.Kind}&name={Uri.EscapeDataString(p.Info.Name)}", content, ct);
            await EnsureApiResponseAsync(resp);
            var server = (await resp.Content.ReadFromJsonAsync<TaskDto>(Json, ct))!;
            store.Write(ts =>
            {
                var local = ts.FirstOrDefault(x => x.Data.Id == p.TaskId);
                if (local is null) return;
                local.Uploaded.Add(p.FileId);
                local.Data.Files = MergeFiles(local, server.Files);
            });
        }
    }

    // ── 3. Pull server changes (Plane edits, deletions, transcripts) ────────
    private async Task PullAsync(HttpClient http, CancellationToken ct, string? refreshWorkspace = null)
    {
        if (store.Meta.Workspaces.Count == 0 || DateTime.UtcNow - _metaAt > TimeSpan.FromMinutes(10))
        {
            var meta = await ReadJsonAsync<MetaDto>(http, "api/meta", ct);
            if (meta is not null) { store.SetSyncState(meta: meta); _metaAt = DateTime.UtcNow; }
        }

        string query = $"api/sync?since={store.ServerVersion}";
        if (refreshWorkspace is not null)
            query += $"&workspace={Uri.EscapeDataString(refreshWorkspace)}&refresh=true";
        var changes = await ReadJsonAsync<SyncResponse>(http, query, ct);
        store.Write(ts =>
        {
            foreach (var server in changes.Tasks)
            {
                var local = ts.FirstOrDefault(x => x.Data.Id == server.Id);
                if (local is null)
                {
                    if (!server.Deleted) ts.Add(new LocalTask { Data = server, Uploaded = server.Files.Select(f => f.Id).ToHashSet() });
                    continue;
                }
                bool keepLocal = local.Dirty && local.Data.CreatedLocally; // phone wins for its own tasks
                if (server.Deleted && !keepLocal) { RemoveLocal(ts, local); continue; }
                if (keepLocal)
                {
                    local.Data.Version = server.Version;
                    local.Data.PlaneUrl = server.PlaneUrl;
                    local.Data.Files = MergeFiles(local, server.Files);
                    FillDefaults(local, server);
                }
                else ApplyServer(local, server);
            }
        });
        store.SetSyncState(version: changes.Version);
    }

    private static void ApplyServer(LocalTask local, TaskDto server)
    {
        server.Files = MergeFiles(local, server.Files);
        local.Data = server;
        local.Dirty = false;
    }

    /// <summary>Keeps the phone's edits but adopts the project/status the server picked when the phone left them empty.</summary>
    private static void FillDefaults(LocalTask local, TaskDto server)
    {
        local.Data.Workspace ??= server.Workspace;
        local.Data.ProjectId ??= server.ProjectId;
        if (local.Data.StateId is null && local.Data.ProjectId == server.ProjectId)
        {
            local.Data.StateId = server.StateId;
            local.Data.StateGroup = server.StateGroup ?? local.Data.StateGroup;
        }
    }

    /// <summary>Server file info (transcripts) + files captured here that the server doesn't have yet.</summary>
    private static List<FileDto> MergeFiles(LocalTask local, List<FileDto> serverFiles) =>
        serverFiles.Concat(local.Data.Files.Where(f => serverFiles.All(s => s.Id != f.Id))).ToList();

    private static void RemoveLocal(List<LocalTask> ts, LocalTask t)
    {
        foreach (var path in t.LocalFiles.Values)
            try { File.Delete(path); File.Delete(path + ".thumb.jpg"); } catch { }
        ts.Remove(t);
    }
}
