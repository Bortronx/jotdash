// JSON contracts between the Blinkjot phone app and its sync server (see docs/server-api.md).
// The server keeps an identical copy: change the JSON shape in both places, or only add
// optional fields, so older apps and servers keep working.
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Blinkjot.Shared;

/// <summary>A task as the phone and the server exchange it.</summary>
public class TaskDto
{
    public string Id { get; set; } = "";              // GUID created on the phone (or the Plane issue id for Plane-created tasks)
    public string Text { get; set; } = "";            // first line = title, rest = description
    public string? Workspace { get; set; }            // Plane workspace slug (null = server default)
    public string? ProjectId { get; set; }            // Plane project id (null = server default)
    public string? StateId { get; set; }              // Plane state id (null = project default)
    public string? StateGroup { get; set; }           // backlog / unstarted / started / completed / cancelled
    public bool CreatedLocally { get; set; } = true;  // false = the task came from Plane
    public bool Deleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long Version { get; set; }                 // server version; the phone echoes back the version its edit is based on
    public string? PlaneUrl { get; set; }
    public List<FileDto> Files { get; set; } = new();
}

public class FileDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "file";             // audio / image / video / file
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string? Transcript { get; set; }
    public string TranscriptStatus { get; set; } = "none";  // none / pending / done / failed
}

public class SyncResponse
{
    public long Version { get; set; }
    public List<TaskDto> Tasks { get; set; } = new();
}

public class MetaDto
{
    public string? DefaultWorkspace { get; set; }
    public string? DefaultProjectId { get; set; }
    public List<WorkspaceDto> Workspaces { get; set; } = new();
}

public class WorkspaceDto
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ProjectDto> Projects { get; set; } = new();
}

public class ProjectDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Identifier { get; set; } = "";
    public List<StateDto> States { get; set; } = new();
}

public class StateDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Color { get; set; } = "#888888";
}

/// <summary>Helpers that turn the single note text into a Plane title + description and back.</summary>
public static class TaskText
{
    public const int MaxTitle = 120;

    public static bool IsDone(string? stateGroup) => stateGroup is "completed" or "cancelled";

    public static string Title(string? text)
    {
        string first = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (first.Length <= MaxTitle) return first;
        int cut = first.LastIndexOf(' ', MaxTitle);
        return first[..(cut > 40 ? cut : MaxTitle)] + "…";
    }

    /// <summary>Everything after the title line (or the full text when the title had to be shortened).</summary>
    public static string Body(string? text)
    {
        string t = (text ?? "").Replace("\r", "").Trim();
        if (Title(t).EndsWith('…')) return t;
        int nl = t.IndexOf('\n');
        return nl < 0 ? "" : t[(nl + 1)..].Trim();
    }

    public static string FromPlane(string name, string description)
    {
        name = name.Trim();
        description = description.Trim();
        if (name.EndsWith('…') && description.StartsWith(name.TrimEnd('…'))) return description;
        return description.Length == 0 ? name : name + "\n" + description;
    }

    public static string ToHtml(string text)
    {
        var sb = new StringBuilder();
        foreach (string line in text.Replace("\r", "").Split('\n'))
            sb.Append("<p>").Append(WebUtility.HtmlEncode(line)).Append("</p>");
        return sb.ToString();
    }

    public static string FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        string s = Regex.Replace(html, @"<br\s*/?>|</p>|</li>|</h\d>|</div>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s).Replace("\r", "");
        return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
    }
}
