using Jotdash;
using Jotdash.Services;
using Jotdash.Shared;

string root = Path.Combine(Path.GetTempPath(), "jotdash-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
FileSystem.AppDataDirectory = root;
try
{
    var store = new LocalStore();
    int edits = 0;
    store.Edited += () => edits++;

    string CaptureFile(string extension)
    {
        string path = Path.Combine(root, Guid.NewGuid() + extension);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    foreach (string kind in new[] { "audio", "image", "video" })
    {
        string source = CaptureFile(kind == "audio" ? ".webm" : kind == "image" ? ".jpg" : ".mp4");
        string id;
        if (kind == "audio")
            id = store.SaveCapture(source, kind, "audio/webm", "Offline voice");
        else
        {
            Phone.NextCapture = (source, kind, kind == "image" ? "image/jpeg" : "video/mp4");
            id = await Tasks.CaptureAsync(store, kind == "video")
                ?? throw new Exception("Camera did not create a task.");
        }
        var saved = new LocalStore().Get(id) ?? throw new Exception("Capture was not persisted.");
        Check(saved.Data.StateGroup == "unstarted" && !saved.IsDone, "New captures start as To do.");
        Check(saved.Data.Files.Count == 1 && saved.PendingUpload && saved.Dirty, "Capture queued offline.");
        Check(File.ReadAllBytes(saved.LocalFiles.Single().Value).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Attachment bytes preserved.");
        Check(!File.Exists(source), "Successful capture releases cache file.");
    }
    Check(edits == 3, "Each complete capture emits one edit notification.");

    Phone.NextCapture = null;
    Check(await Tasks.CaptureAsync(store, false) is null && store.Recent().Count == 3, "Cancel camera creates no task.");
    var first = store.Recent().First();
    store.Write(ts => ts.First(t => t.Data.Id == first.Data.Id).Dirty = false);
    store.AddFile(first.Data.Id, CaptureFile(".jpg"), "image", "image/jpeg");
    Check(store.Get(first.Data.Id)!.Dirty, "Attaching to an existing task marks it dirty.");

    string retained = CaptureFile(".webm");
    string tempSave = Path.Combine(root, "jotdash.json.tmp");
    Directory.CreateDirectory(tempSave);
    int beforeFailure = store.Recent().Count;
    try
    {
        store.SaveCapture(retained, "audio", "audio/webm", "Retry voice");
        throw new Exception("Expected a storage failure.");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Check(File.Exists(retained), "Failed save retains original recording.");
        Check(store.Recent().Count == beforeFailure, "Failed save rolls back task and attachment.");
    }
    Directory.Delete(tempSave);
    store.SaveCapture(retained, "audio", "audio/webm", "Retry voice");
    Check(new LocalStore().Recent().Count == beforeFailure + 1, "Retry creates exactly one task.");

    for (int i = 0; i < 40; i++)
        Check(store.Create($"Task {i}").Data.StateGroup == "unstarted", "New notes start as To do.");
    store.Edit(first.Data.Id, t => t.StateGroup = "completed");
    var tasks = new LocalStore().Recent();
    Check(tasks.Count == beforeFailure + 41, "Home list is not truncated at 30 tasks.");
    Check(tasks.Last().Data.Id == first.Data.Id && tasks.Last().IsDone, "Completed task remains at the bottom.");
    Check(tasks.Take(tasks.Count - 1).All(t => !t.IsDone), "Open tasks precede completed tasks.");
    store.Edit(first.Data.Id, t => t.StateGroup = "unstarted");
    Check(!store.Recent().Last().IsDone, "Completed task can be reopened offline.");
    store.Edit(first.Data.Id, t => t.Deleted = true);
    Check(store.Recent().All(t => t.Data.Id != first.Data.Id), "Deleted tasks stay hidden.");

    Console.WriteLine("PASS: offline audio/photo/video, restart persistence, cancel, attach, failed-save retry, and completed-task ordering.");
    return 0;
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// Platform seams only; tests run the actual storage and capture-to-task logic without a server.
namespace Jotdash
{
    public static class FileSystem
    {
        public static string AppDataDirectory { get; set; } = "";
    }

    public static class Phone
    {
        public static (string Path, string Kind, string ContentType)? NextCapture { get; set; }
        public static Task<(string Path, string Kind, string ContentType)?> CaptureAsync(bool video)
            => Task.FromResult(NextCapture);
    }
}
