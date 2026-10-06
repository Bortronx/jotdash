using Jotdash.Shared;

namespace Jotdash.Services;

/// <summary>Turning a capture into a task, and the Plane project/status lookups the pages need.</summary>
public static class Tasks
{
    /// <summary>Opens the camera and saves the photo/video into a task (a new one when taskId is null). Returns the task id.</summary>
    public static async Task<string?> CaptureAsync(LocalStore store, bool video, string? taskId = null)
    {
        var shot = await Phone.CaptureAsync(video);
        if (shot is null) return null;
        taskId ??= store.Create($"{(video ? "Video" : "Photo")} {DateTime.Now:HH:mm}").Data.Id;
        store.AddFile(taskId, shot.Value.Path, shot.Value.Kind, shot.Value.ContentType);
        return taskId;
    }

    /// <summary>The project a task is (or will be) in: its own, else the server's default.</summary>
    public static ProjectDto? Project(MetaDto meta, TaskDto t)
    {
        string? id = t.ProjectId ?? meta.DefaultProjectId;
        return meta.Workspaces.SelectMany(w => w.Projects).FirstOrDefault(p => p.Id == id);
    }

    public static string ProjectName(MetaDto meta, TaskDto t) =>
        Project(meta, t)?.Name is { } name ? name : "Default project";

    public static string StatusName(MetaDto meta, TaskDto t) =>
        Project(meta, t)?.States.FirstOrDefault(s => s.Id == t.StateId)?.Name ?? GroupLabel(t.StateGroup);

    public static string GroupLabel(string? group) => group switch
    {
        "backlog" => "Backlog",
        "unstarted" => "To do",
        "started" => "In progress",
        "completed" => "Done",
        "cancelled" => "Cancelled",
        _ => "To do",
    };

    /// <summary>Sets a status group (e.g. "completed"), picking the matching Plane state when the project is known.</summary>
    public static void SetGroup(MetaDto meta, TaskDto t, string group)
    {
        t.StateGroup = group;
        t.StateId = Project(meta, t)?.States.FirstOrDefault(s => s.Group == group)?.Id;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };

    public static string Ago(DateTime? utc)
    {
        if (utc is null) return "never";
        var d = DateTime.UtcNow - utc.Value;
        return d.TotalSeconds < 60 ? "just now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalHours < 24 ? $"{(int)d.TotalHours} h ago" : utc.Value.ToLocalTime().ToString("d MMM HH:mm");
    }
}
