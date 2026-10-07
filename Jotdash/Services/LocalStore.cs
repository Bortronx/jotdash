using System.Text.Json;
using Jotdash.Shared;

namespace Jotdash.Services;

/// <summary>A task on the phone: the shared DTO plus what still has to be sent to the server.</summary>
public class LocalTask
{
    public TaskDto Data { get; set; } = new();
    public bool Dirty { get; set; }                                   // local edit not yet on the server
    public int EditStamp { get; set; }                                // bumps on every edit (detects edits during a sync)
    public DateTime? LastReadAt { get; set; }                          // last opened on this phone; not a content edit
    public Dictionary<string, string> LocalFiles { get; set; } = new(); // fileId → path on this phone
    public HashSet<string> Uploaded { get; set; } = new();            // fileIds the server already has

    public string Title => TaskText.Title(Data.Text) is { Length: > 0 } t ? t : "Untitled note";
    public string Preview => TaskText.Body(Data.Text);
    public bool IsDone => TaskText.IsDone(Data.StateGroup);
    public bool PendingUpload => Dirty || LocalFiles.Keys.Any(id => !Uploaded.Contains(id));
}

/// <summary>
/// Everything the app knows, kept in one JSON file in app-private storage.
/// The phone is always the first place a change is saved; sync happens afterwards.
/// </summary>
public class LocalStore
{
    private class StoreFile
    {
        public long ServerVersion { get; set; }
        public DateTime? LastSync { get; set; }
        public string? LastError { get; set; }
        public MetaDto Meta { get; set; } = new();
        public string? SelectedWorkspace { get; set; }
        public List<LocalTask> Tasks { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "jotdash.json");
    private readonly object _lock = new();
    private StoreFile _data;

    public string FilesDir { get; } = Path.Combine(FileSystem.AppDataDirectory, "files");

    /// <summary>Raised after every change (UI refresh + sync trigger).</summary>
    public event Action? Changed;

    /// <summary>Raised only for user edits (create / edit / attach): this is what triggers a sync.</summary>
    public event Action? Edited;

    public LocalStore()
    {
        Directory.CreateDirectory(FilesDir);
        try { _data = File.Exists(_path) ? JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), Json) ?? new() : new(); }
        catch (JsonException) { File.Copy(_path, _path + ".broken", true); _data = new(); } // never crash on a bad file
    }

    // ── Reading ──────────────────────────────────────────────────────────────
    public List<LocalTask> Recent() => Read(ts => ts
        .Where(t => !t.Data.Deleted && InSelectedWorkspace(t.Data))
        .OrderBy(t => t.IsDone)
        .ThenByDescending(t => t.Data.CreatedAt).ToList());

    public List<LocalTask> All(bool done) => Read(ts => ts
        .Where(t => !t.Data.Deleted && t.IsDone == done && InSelectedWorkspace(t.Data))
        .OrderByDescending(t => t.Data.UpdatedAt).ToList());

    public LocalTask? Get(string id) => Read(ts => ts.FirstOrDefault(t => t.Data.Id == id));
    public MetaDto Meta { get { lock (_lock) return _data.Meta; } }
    public long ServerVersion { get { lock (_lock) return _data.ServerVersion; } }
    public DateTime? LastSync { get { lock (_lock) return _data.LastSync; } }
    public string? LastError { get { lock (_lock) return _data.LastError; } }
    public int PendingCount => Read(ts => ts.Count(t => t.PendingUpload));
    public string? SelectedWorkspace { get { lock (_lock) return _data.SelectedWorkspace ?? DefaultWorkspace; } }
    public string WorkspaceName => Meta.Workspaces.FirstOrDefault(w => w.Slug == SelectedWorkspace)?.Name
        ?? SelectedWorkspace ?? "Local notes";

    private string? DefaultWorkspace => _data.Meta.DefaultWorkspace ?? _data.Meta.Workspaces.FirstOrDefault()?.Slug;

    private bool InSelectedWorkspace(TaskDto task) => SelectedWorkspace is not { } ws
        || (task.Workspace ?? DefaultWorkspace) == ws;

    public void SelectWorkspace(string workspace)
    {
        if (!Meta.Workspaces.Any(w => w.Slug == workspace))
            throw new ArgumentException("This workspace is not available. Sync to refresh the workspace list.");
        Write(_ => _data.SelectedWorkspace = workspace);
    }

    public void MarkRead(string id) => Write(ts =>
    {
        var task = ts.FirstOrDefault(t => t.Data.Id == id && !t.Data.Deleted);
        if (task is not null) task.LastReadAt = DateTime.UtcNow;
    });

    public T Read<T>(Func<List<LocalTask>, T> read) { lock (_lock) return read(_data.Tasks); }

    // ── Local edits (always instant, never wait for the network) ────────────
    public LocalTask Create(string text)
    {
        var t = NewTask(text);
        Write(ts => ts.Add(t));
        Edited?.Invoke();
        return t;
    }

    private LocalTask NewTask(string text)
    {
        var now = DateTime.UtcNow;
        string? workspace = SelectedWorkspace;
        var projects = Meta.Workspaces.FirstOrDefault(w => w.Slug == workspace)?.Projects;
        string? project = projects?.FirstOrDefault(p => p.Id == Meta.DefaultProjectId)?.Id
            ?? projects?.FirstOrDefault(p => p.Name.Equals("Quick Notes", StringComparison.OrdinalIgnoreCase))?.Id
            ?? projects?.FirstOrDefault()?.Id;
        return new LocalTask
        {
            Data = new TaskDto
            {
                Id = Guid.NewGuid().ToString(), Text = text, StateGroup = "unstarted",
                CreatedAt = now, UpdatedAt = now,
                Workspace = workspace, ProjectId = project,
            },
            Dirty = true,
        };
    }

    /// <summary>Apply a user edit to a task and mark it for upload.</summary>
    public void Edit(string id, Action<TaskDto> change) => Write(ts =>
    {
        var t = ts.FirstOrDefault(x => x.Data.Id == id);
        if (t is null) return;
        change(t.Data);
        t.Data.UpdatedAt = DateTime.UtcNow;
        t.Dirty = true;
        t.EditStamp++;
    }, edited: true);

    /// <summary>Save a capture and its task together, without waiting for sync.</summary>
    public string SaveCapture(string sourcePath, string kind, string contentType, string title, string? taskId = null)
    {
        var created = taskId is null ? NewTask(title) : null;
        string id = created?.Data.Id ?? taskId!;
        AttachFile(id, sourcePath, kind, contentType, created);
        return id;
    }

    public FileDto AddFile(string taskId, string sourcePath, string kind, string contentType)
        => AttachFile(taskId, sourcePath, kind, contentType);

    private FileDto AttachFile(string taskId, string sourcePath, string kind, string contentType, LocalTask? created = null)
    {
        string id = Guid.NewGuid().ToString();
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        string path = Path.Combine(FilesDir, id + ext);
        // Cache and app data need not share a filesystem. Keep the source until the JSON is saved.
        File.Copy(sourcePath, path);
        var info = new FileDto
        {
            Id = id, Kind = kind, ContentType = contentType, Size = new FileInfo(path).Length,
            Name = $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}",
            TranscriptStatus = kind == "audio" ? "pending" : "none",
        };
        Write(ts =>
        {
            var t = created ?? ts.First(x => x.Data.Id == taskId);
            if (created is not null) ts.Add(created);
            t.Data.Files.Add(info);
            t.LocalFiles[id] = path;
            t.Data.UpdatedAt = DateTime.UtcNow;
            t.Dirty = true;
            t.EditStamp++;
        }, edited: true);
        try { File.Delete(sourcePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Capture saved, but the cache file could not be removed: {0}", e.Message);
        }
        return info;
    }

    // ── Used by SyncService ──────────────────────────────────────────────────
    public void Write(Action<List<LocalTask>> change, bool edited = false)
    {
        lock (_lock)
        {
            string before = JsonSerializer.Serialize(_data, Json);
            try
            {
                change(_data.Tasks);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
                File.Move(tmp, _path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A retry must start from the last saved state, not a half-attached capture.
                _data = JsonSerializer.Deserialize<StoreFile>(before, Json)!;
                throw;
            }
        }
        Changed?.Invoke();
        if (edited) Edited?.Invoke();
    }

    public void SetSyncState(long? version = null, MetaDto? meta = null, DateTime? lastSync = null, string? error = null) => Write(_ =>
    {
        if (version is long v) _data.ServerVersion = v;
        if (meta is not null) _data.Meta = meta;
        if (lastSync is not null) _data.LastSync = lastSync;
        _data.LastError = error;
    });

    public static LocalTask Copy(LocalTask t) => JsonSerializer.Deserialize<LocalTask>(JsonSerializer.Serialize(t, Json), Json)!;
}
