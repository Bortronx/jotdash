using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Jotdash;
using Jotdash.Services;
using Jotdash.Shared;

internal static class WorkspaceTests
{
    public static async Task RunAsync()
    {
        string originalRoot = FileSystem.AppDataDirectory;
        FileSystem.AppDataDirectory = Path.Combine(originalRoot, "workspaces");
        Directory.CreateDirectory(FileSystem.AppDataDirectory);
        try
        {
            TestStorage();
            FileSystem.AppDataDirectory = Path.Combine(originalRoot, "workspace-project");
            if (Directory.Exists(FileSystem.AppDataDirectory)) Directory.Delete(FileSystem.AppDataDirectory, true);
            Directory.CreateDirectory(FileSystem.AppDataDirectory);
            TestProjectScope();
            FileSystem.AppDataDirectory = Path.Combine(originalRoot, "workspaces");
            FileSystem.AppDataDirectory = Path.Combine(originalRoot, "workspace-sync");
            Directory.CreateDirectory(FileSystem.AppDataDirectory);
            await TestSyncAsync();
        }
        finally { FileSystem.AppDataDirectory = originalRoot; }
    }

    private static MetaDto Metadata() => new()
    {
        DefaultWorkspace = "alpha", DefaultProjectId = "alpha-project",
        Workspaces =
        [
            new() { Slug = "alpha", Name = "Alpha", Projects = [new() { Id = "alpha-project", Name = "Quick Notes" }, new() { Id = "alpha-two", Name = "Second" }] },
            new() { Slug = "beta", Name = "Beta", Projects = [new() { Id = "beta-project", Name = "Beta project" }] },
        ],
    };

    private static void TestProjectScope()
    {
        var store = new LocalStore();
        store.SetSyncState(meta: Metadata());
        var first = store.Create("In Quick Notes");
        store.SelectWorkspace("alpha", "alpha-two");
        Check(store.Recent().Count == 0, "A project scope hides tasks from other projects.");
        var second = store.Create("In Second");
        Check(second.Data.ProjectId == "alpha-two" && store.Recent().Count == 1, "Captures inherit the selected project.");
        store.SelectWorkspace("alpha");
        Check(store.SelectedProject is null && store.Recent().Count == 2, "Entire workspace shows every project.");
        try { store.SelectWorkspace("alpha", "beta-project"); throw new Exception("Foreign project accepted."); }
        catch (ArgumentException) { }
        Check(first.Data.ProjectId == "alpha-project", "Default project is used for the whole workspace.");
    }

    private static void TestStorage()
    {
        var store = new LocalStore();
        var oldOffline = store.Create("Before first sync");
        store.SetSyncState(meta: Metadata());
        var alpha = store.Create("Alpha note");
        Check(store.SelectedWorkspace == "alpha", "The server default is selected initially.");
        store.SelectWorkspace("beta");
        Check(store.Recent().Count == 0, "Selecting Beta hides Alpha and unassigned legacy notes.");
        foreach (string kind in new[] { "note", "audio", "image", "video" })
        {
            LocalTask task;
            if (kind == "note") task = store.Create("Beta note");
            else
            {
                string source = Path.Combine(FileSystem.AppDataDirectory, kind + ".dat");
                File.WriteAllBytes(source, [1, 2, 3]);
                task = store.Get(store.SaveCapture(source, kind, "application/octet-stream", "Beta " + kind))!;
            }
            Check(task.Data.Workspace == "beta" && task.Data.ProjectId == "beta-project",
                "Every capture inherits the selected workspace and its project.");
        }
        var beta = store.Recent()[0];
        store.Edit(beta.Data.Id, t => t.StateGroup = "completed");
        Check(store.Recent().Last().Data.Id == beta.Data.Id, "Completed tasks remain at the bottom within the workspace.");
        Check(store.All(false).Count == 3 && store.All(true).Single().Data.Id == beta.Data.Id,
            "Both All tasks tabs respect the selection.");
        Check(new LocalStore().SelectedWorkspace == "beta" && new LocalStore().Recent().Count == 4,
            "Workspace selection and its cached tasks survive restart.");
        store.SelectWorkspace("alpha");
        Check(store.Recent().Select(t => t.Data.Id).ToHashSet().SetEquals([alpha.Data.Id, oldOffline.Data.Id]),
            "Switching back restores cached Alpha tasks without deleting other workspaces.");
        try { store.SelectWorkspace("missing"); throw new Exception("Invalid selection accepted."); }
        catch (ArgumentException) { Check(store.SelectedWorkspace == "alpha", "Invalid selection leaves current workspace unchanged."); }

        store.Write(ts => ts.First(t => t.Data.Id == alpha.Data.Id).Dirty = false);
        int edits = 0;
        store.Edited += () => edits++;
        var before = LocalStore.Copy(store.Get(alpha.Data.Id)!);
        store.MarkRead(alpha.Data.Id);
        var read = new LocalStore().Get(alpha.Data.Id)!;
        Check(read.LastReadAt is not null, "Last-read time is persisted.");
        Check(!read.Dirty && read.EditStamp == before.EditStamp && read.Data.UpdatedAt == before.Data.UpdatedAt && edits == 0,
            "Reading never changes modified time or schedules an upload.");
        store.Edit(alpha.Data.Id, t => t.Text += " edited");
        Check(store.Get(alpha.Data.Id)!.Data.UpdatedAt >= before.Data.UpdatedAt
            && store.Get(alpha.Data.Id)!.LastReadAt == read.LastReadAt && edits == 1,
            "Editing updates modified time without changing last-read time.");
    }

    private static async Task TestSyncAsync()
    {
        using var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        int port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();
        string origin = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin);
        listener.Start();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        bool fail = false, requestedBeta = false;
        long seenCursor = -1;
        var created = new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc);
        TaskDto Remote(string id, string ws, long version) => new()
        {
            Id = id, Text = ws + " task", Workspace = ws, ProjectId = ws + "-project",
            StateGroup = ws == "beta" ? "completed" : "unstarted", CreatedLocally = false,
            CreatedAt = created, UpdatedAt = created.AddHours(version), Version = version,
        };
        var server = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    ctx.Response.ContentType = "application/json";
                    string body;
                    if (fail) { ctx.Response.StatusCode = 503; body = "{}"; }
                    else if (ctx.Request.Url!.AbsolutePath.EndsWith("api/meta"))
                        body = JsonSerializer.Serialize(Metadata(), json);
                    else
                    {
                        seenCursor = long.Parse(ctx.Request.QueryString["since"]!);
                        requestedBeta = ctx.Request.QueryString["workspace"] == "beta" && ctx.Request.QueryString["refresh"] == "true";
                        var response = new SyncResponse
                        {
                            Version = requestedBeta ? 11 : 10,
                            Tasks = requestedBeta ? [Remote("beta-task", "beta", 11)] : [Remote("alpha-task", "alpha", 10)],
                        };
                        body = JsonSerializer.Serialize(response, json);
                    }
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            }
            catch (Exception e) when (!listener.IsListening && e is HttpListenerException or ObjectDisposedException) { }
        });
        try
        {
            var store = new LocalStore();
            var settings = new SettingsService();
            await settings.SaveAsync(new ConnectionSettings { ServerUrl = origin + "quicknotes/" });
            var sync = new SyncService(store, settings);
            Check(await sync.SyncAsync() is null && store.Recent().Single().Data.Id == "alpha-task",
                "First sync loads workspace metadata and uncached tasks.");
            store.MarkRead("alpha-task");
            DateTime? read = store.Get("alpha-task")!.LastReadAt;
            Check(await sync.SyncAsync() is null && store.Get("alpha-task")!.LastReadAt == read,
                "Replacing a task with server data preserves phone-local last read.");
            store.SelectWorkspace("beta");
            Check(store.Recent().Count == 0, "Uncached workspace initially has no cached rows.");
            fail = true;
            Check(await sync.SyncAsync(refreshWorkspace: true) is not null && store.SelectedWorkspace == "beta"
                && store.Get("alpha-task") is not null && store.ServerVersion == 10,
                "Failed workspace refresh preserves selection, cached tasks and sync cursor.");
            fail = false;
            Check(await sync.SyncAsync(refreshWorkspace: true) is null && requestedBeta && seenCursor == 10,
                "Selection asks the server to refresh the workspace, without resetting the global cursor.");
            var task = store.Recent().Single();
            Check(task.Data.Id == "beta-task" && task.IsDone && task.Data.CreatedAt == created && task.LastReadAt is null,
                "Refresh downloads uncached completed tasks and preserves server timestamps.");
            store.SelectWorkspace("alpha");
            Check(store.Recent().Single().Data.Id == "alpha-task", "Previously downloaded tasks remain available after switching.");
        }
        finally { listener.Stop(); await server; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
