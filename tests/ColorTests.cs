using Jotdash;
using Jotdash.Services;
using Jotdash.Shared;

static class ColorTests
{
    public static void Run()
    {
        string original = FileSystem.AppDataDirectory;
        FileSystem.AppDataDirectory = Path.Combine(original, "colors");
        Directory.CreateDirectory(FileSystem.AppDataDirectory);
        try
        {
            var store = new LocalStore();
            var meta = new MetaDto
            {
                DefaultWorkspace = "alpha", DefaultProjectId = "one",
                Workspaces = [
                    new() { Slug = "alpha", Projects = [
                        new() { Id = "one", States = [
                            new() { Id = "todo", Name = "To do", Group = "unstarted", Color = "#AbC" },
                            new() { Id = "doing", Name = "Working", Group = "started", Color = "#D97706" },
                            new() { Id = "done", Name = "Done", Group = "completed", Color = "#abc" },
                            new() { Id = "bad", Name = "Invalid color", Group = "cancelled", Color = "red;display:none" },
                        ] },
                        new() { Id = "two" },
                    ] },
                    new() { Slug = "beta", Projects = [new() { Id = "other" }] },
                ],
            };
            store.SetSyncState(meta: meta);
            int edits = 0;
            store.Edited += () => edits++;
            var todo = store.Create("Todo");
            var doing = store.Create("Doing");
            var done = store.Create("Done");
            store.Edit(doing.Data.Id, t => Tasks.SetGroup(meta, t, "started"));
            store.Edit(done.Data.Id, t => Tasks.SetGroup(meta, t, "completed"));
            Check(Tasks.StatusColor(meta, todo.Data) == "#aabbcc", "Offline task without StateId resolves group color.");
            Check(Tasks.StatusColor(meta, doing.Data) == "#d97706", "Configured colors normalize case.");
            Check(Tasks.StatusColor(meta, new TaskDto { StateId = "bad", ProjectId = "one" }) == "#dc2626",
                "Unsafe colors fall back to the state group's color.");
            int before = edits;
            long version = store.ServerVersion;
            Check(!store.ShowTaskDetails, "Task details are off by default, including stores with no preference.");
            int changes = 0;
            store.Changed += () => changes++;
            store.ToggleTaskDetails();
            Check(store.ShowTaskDetails && new LocalStore().ShowTaskDetails && changes == 1,
                "Details toggle notifies lists and persists across restart.");
            store.ToggleTaskDetails();
            Check(!store.ShowTaskDetails && !new LocalStore().ShowTaskDetails && changes == 2
                && store.Recent().Count == 3 && edits == before,
                "Simple view is restored without editing tasks or hiding tasks.");
            store.SelectColor("#ABC");
            Check(store.Recent().Select(t => t.Data.Id).SequenceEqual(new[] { todo.Data.Id, done.Data.Id }),
                "Home filters exact normalized colors and retains open/completed order.");
            Check(store.All(false).Single().Data.Id == todo.Data.Id && store.All(true).Single().Data.Id == done.Data.Id,
                "All tasks filters both tabs.");
            Check(store.Get(doing.Data.Id) is not null && store.PendingCount == 3 && edits == before && store.ServerVersion == version,
                "Filter leaves stored tasks, sync counts and edits untouched.");
            Check(new LocalStore().SelectedColor == "#aabbcc" && new LocalStore().Recent().Count == 2,
                "Color preference and filtered results persist.");
            Check(store.ColorOptions().Count(o => o.Color == "#aabbcc") == 1, "Shared status colors have one option.");
            store.Edit(todo.Data.Id, t => Tasks.SetGroup(meta, t, "started"));
            Check(store.Recent().Single().Data.Id == done.Data.Id, "Status edits immediately update filter membership.");
            store.SelectColor(null);
            Check(store.Recent().Count == 3, "Clearing color restores every task in scope.");
            store.SelectWorkspace("alpha", "two");
            var second = store.Create("Other project");
            store.SelectColor(Tasks.StatusColor(meta, second.Data));
            Check(store.Recent().Single().Data.Id == second.Data.Id, "Color composes with project scope.");
            store.SelectWorkspace("beta");
            Check(store.Recent().Count == 0, "Color does not broaden workspace scope.");
            try { store.SelectColor("invalid"); throw new Exception("Invalid color accepted."); }
            catch (ArgumentException) { }
            Check(Tasks.NormalizeColor("#12345") is null && Tasks.NormalizeColor("#12zz00") is null, "Malformed hex rejected.");
            Console.WriteLine("PASS: status colors, safe normalization, color filtering, persistence, scope and sync isolation.");
        }
        finally { FileSystem.AppDataDirectory = original; }
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
