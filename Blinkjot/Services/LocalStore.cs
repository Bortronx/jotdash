using System.Text.Json;
using Blinkjot.Shared;

namespace Blinkjot.Services;

/// <summary>A task on the phone: the shared DTO plus what still has to be sent to the server.</summary>
public class LocalTask
{
    public TaskDto Data { get; set; } = new();
    public bool Dirty { get; set; }                                   // local edit not yet on the server
    public int EditStamp { get; set; }                                // bumps on every edit (detects edits during a sync)
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
        public List<LocalTask> Tasks { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "blinkjot.json");
    private readonly object _lock = new();
    private readonly StoreFile _data;

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
    public List<LocalTask> Recent(int max = 30) => Read(ts => ts
        .Where(t => !t.Data.Deleted && !t.IsDone)
        .OrderByDescending(t => t.Data.CreatedAt).Take(max).ToList());

    public List<LocalTask> All(bool done) => Read(ts => ts
        .Where(t => !t.Data.Deleted && t.IsDone == done)
        .OrderByDescending(t => t.Data.UpdatedAt).ToList());

    public LocalTask? Get(string id) => Read(ts => ts.FirstOrDefault(t => t.Data.Id == id));
    public MetaDto Meta { get { lock (_lock) return _data.Meta; } }
    public long ServerVersion { get { lock (_lock) return _data.ServerVersion; } }
    public DateTime? LastSync { get { lock (_lock) return _data.LastSync; } }
    public string? LastError { get { lock (_lock) return _data.LastError; } }
    public int PendingCount => Read(ts => ts.Count(t => t.PendingUpload));

    public T Read<T>(Func<List<LocalTask>, T> read) { lock (_lock) return read(_data.Tasks); }

    // ── Local edits (always instant, never wait for the network) ────────────
    public LocalTask Create(string text)
    {
        var now = DateTime.UtcNow;
        var t = new LocalTask { Data = new TaskDto { Id = Guid.NewGuid().ToString(), Text = text, CreatedAt = now, UpdatedAt = now }, Dirty = true };
        Write(ts => ts.Add(t));
        Edited?.Invoke();
        return t;
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

    /// <summary>Move a captured file into app storage and attach it to the task.</summary>
    public FileDto AddFile(string taskId, string sourcePath, string kind, string contentType)
    {
        string id = Guid.NewGuid().ToString();
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        string path = Path.Combine(FilesDir, id + ext);
        File.Move(sourcePath, path);
        var info = new FileDto
        {
            Id = id, Kind = kind, ContentType = contentType, Size = new FileInfo(path).Length,
            Name = $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}",
            TranscriptStatus = kind == "audio" ? "pending" : "none",
        };
        Write(ts =>
        {
            var t = ts.First(x => x.Data.Id == taskId);
            t.Data.Files.Add(info);
            t.LocalFiles[id] = path;
        }, edited: true);
        return info;
    }

    // ── Used by SyncService ──────────────────────────────────────────────────
    public void Write(Action<List<LocalTask>> change, bool edited = false)
    {
        lock (_lock)
        {
            change(_data.Tasks);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
            File.Move(tmp, _path, true);
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
