using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Blinkjot.Shared;

namespace Blinkjot.Services;

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
    public event Action? StatusChanged;

    /// <summary>Sync now. Returns null on success, otherwise a short error message.</summary>
    public async Task<string?> SyncAsync(CancellationToken ct = default)
    {
        var conn = await settings.GetAsync();
        if (!conn.IsSet) return "No server set up yet (Menu → Settings). Everything is saved on this phone.";
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet) return "Offline – will sync when back online.";

        if (!await _gate.WaitAsync(0)) { _again = true; return null; } // already running: run once more afterwards
        string? error = null;
        try
        {
            IsSyncing = true;
            StatusChanged?.Invoke();
            using var http = CreateClient(conn);
            do
            {
                _again = false;
                await PushTasksAsync(http, ct);
                await UploadFilesAsync(http, ct);
                await PullAsync(http, ct);
            } while (_again);
            store.SetSyncState(lastSync: DateTime.UtcNow);
        }
        catch (Exception e)
        {
            error = e is HttpRequestException { StatusCode: { } code } ? $"Server answered {(int)code} {code}" : e.Message;
            store.SetSyncState(error: error);
        }
        finally
        {
            IsSyncing = false;
            _gate.Release();
            StatusChanged?.Invoke();
        }
        return error;
    }

    /// <summary>Checks the server address + Pangolin credentials (Settings → Test).</summary>
    public static async Task<string> TestAsync(ConnectionSettings conn)
    {
        try
        {
            using var http = CreateClient(conn);
            using var resp = await http.GetAsync("api/meta");
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return $"❌ {(int)resp.StatusCode} {resp.ReasonPhrase} – check the Pangolin details / key.";
            if (body.TrimStart().StartsWith('<'))
                return "❌ Got a web page instead of the sync server – Pangolin is probably asking for a login. Check the auth method.";
            var meta = JsonSerializer.Deserialize<MetaDto>(body, Json)!;
            return $"✅ Connected – {meta.Workspaces.Count} Plane workspace(s), {meta.Workspaces.Sum(w => w.Projects.Count)} project(s).";
        }
        catch (Exception e) { return "❌ " + e.Message; }
    }

    /// <summary>Gets a file that only exists on the server (e.g. attached from another device). Returns the local path.</summary>
    public async Task<string?> DownloadAsync(string taskId, FileDto file)
    {
        var conn = await settings.GetAsync();
        if (!conn.IsSet) return null;
        using var http = CreateClient(conn);
        using var resp = await http.GetAsync($"api/files/{file.Id}", HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return null;
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

    private static HttpClient CreateClient(ConnectionSettings conn)
    {
        var http = new HttpClient { BaseAddress = new Uri(conn.ServerUrl), Timeout = TimeSpan.FromMinutes(10) };
        conn.Apply(http.DefaultRequestHeaders);
        return http;
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
            resp.EnsureSuccessStatusCode();
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
            resp.EnsureSuccessStatusCode();
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
    private async Task PullAsync(HttpClient http, CancellationToken ct)
    {
        if (store.Meta.Workspaces.Count == 0 || DateTime.UtcNow - _metaAt > TimeSpan.FromMinutes(10))
        {
            var meta = await http.GetFromJsonAsync<MetaDto>("api/meta", Json, ct);
            if (meta is not null) { store.SetSyncState(meta: meta); _metaAt = DateTime.UtcNow; }
        }

        var changes = (await http.GetFromJsonAsync<SyncResponse>($"api/sync?since={store.ServerVersion}", Json, ct))!;
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


