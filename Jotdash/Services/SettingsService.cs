using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Jotdash.Services;

/// <summary>How the phone reaches the sync server (optional: the app works fully offline without it).</summary>
public class ConnectionSettings
{
    public const int DefaultPort = 8126;

    public string ServerUrl { get; set; } = "";       // e.g. https://your-server/quicknotes/
    public string AuthMode { get; set; } = "none";    // none / basic / token / header
    public string User { get; set; } = "";            // basic: username      token: Pangolin token id (optional)   header: header name
    public string Secret { get; set; } = "";          // basic: password      token: share link or id.token         header: header value
    public string ApiKey { get; set; } = "";          // optional QUICKNOTES_API_KEY
    public string BackupUrls { get; set; } = "";      // typed backups (NetBird / home Wi-Fi), one per line
    public List<string> LearnedUrls { get; set; } = new(); // backups the server reported after a successful sync

    /// <summary>Main address first, then typed backups, then learned ones.</summary>
    public List<string> Addresses() =>
        new[] { ServerUrl }.Concat(Lines(BackupUrls)).Concat(LearnedUrls)
            .Select(Normalize).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsSet => Addresses().Count > 0;

    public string Label(string url)
    {
        if (string.Equals(url, Normalize(ServerUrl), StringComparison.OrdinalIgnoreCase)) return "main address";
        int typed = Lines(BackupUrls).Select(Normalize).ToList().FindIndex(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
        return typed >= 0 ? $"backup {typed + 1}" : "auto-found backup";
    }

    /// <summary>"100.1.2.3" → "http://100.1.2.3:8126/quicknotes/"; full URLs just get a trailing slash. Invalid → "".</summary>
    public static string Normalize(string? text)
    {
        string s = (text ?? "").Trim();
        if (s.Length == 0) return "";
        bool bare = !s.Contains("://");
        if (bare) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return "";
        var b = new UriBuilder(uri) { Query = "", Fragment = "" };
        if (bare && uri.IsDefaultPort) b.Port = DefaultPort;
        if (b.Path is "" or "/") b.Path = "/quicknotes/";
        if (!b.Path.EndsWith('/')) b.Path += "/";
        return b.Uri.AbsoluteUri;
    }

    public static IEnumerable<string> Lines(string text) =>
        text.Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Accepts a Pangolin share link (https://pangolin/s/ID.TOKEN), a "?p_token=ID.TOKEN" URL,
    /// a raw "ID.TOKEN", or the token ID and token in separate fields.
    /// </summary>
    public static (string Id, string Token) ParseToken(string user, string secret)
    {
        string text = secret.Trim();
        string id = user.Trim();
        int q = text.IndexOf("p_token=", StringComparison.OrdinalIgnoreCase);
        if (q >= 0) text = text[(q + 8)..].Split('&', '#')[0];
        else if (text.Contains("://") && Uri.TryCreate(text, UriKind.Absolute, out var link))
        {
            var parts = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int s = Array.IndexOf(parts, "s");
            text = s >= 0 && s + 1 < parts.Length ? parts[s + 1] : parts.LastOrDefault() ?? "";
        }
        text = Uri.UnescapeDataString(text);
        int dot = text.IndexOf('.');
        return dot > 0 ? (text[..dot], text[(dot + 1)..]) : (id, text);
    }

    /// <summary>
    /// Adds credentials for one request. Pangolin credentials only go to the main address's host or HTTPS
    /// addresses – never to plain-HTTP NetBird/Wi-Fi backups, which don't need them.
    /// </summary>
    public void Apply(HttpRequestHeaders h, Uri? target)
    {
        bool pangolin = target is not null && (target.Scheme == "https"
            || (Uri.TryCreate(Normalize(ServerUrl), UriKind.Absolute, out var main) && main.Host == target.Host && main.Port == target.Port));
        if (pangolin)
        {
            switch (AuthMode)
            {
                case "basic":
                    h.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User.Trim()}:{Secret.Trim()}")));
                    break;
                case "token": // Pangolin share link → access token headers
                    var (id, token) = ParseToken(User, Secret);
                    if (id.Length > 0) h.TryAddWithoutValidation("P-Access-Token-Id", id);
                    h.TryAddWithoutValidation("P-Access-Token", token);
                    break;
                case "header" when User.Trim().Length > 0:
                    h.TryAddWithoutValidation(User.Trim(), Secret.Trim());
                    break;
            }
        }
        if (ApiKey.Trim().Length > 0) h.TryAddWithoutValidation("X-QuickNotes-Key", ApiKey.Trim());
    }

    public ConnectionSettings Clone() => new()
    {
        ServerUrl = ServerUrl, AuthMode = AuthMode, User = User, Secret = Secret, ApiKey = ApiKey,
        BackupUrls = BackupUrls, LearnedUrls = LearnedUrls.ToList(),
    };
}

/// <summary>Loads/saves the connection settings in Android's encrypted SecureStorage.</summary>
public class SettingsService
{
    private const string Key = "connection";
    private ConnectionSettings? _cached;

    public async Task<ConnectionSettings> GetAsync()
    {
        if (_cached is not null) return _cached;
        try
        {
            string? json = await SecureStorage.Default.GetAsync(Key);
            _cached = json is null ? new() : JsonSerializer.Deserialize<ConnectionSettings>(json) ?? new();
        }
        catch { _cached = new(); } // SecureStorage can fail after a backup restore: start fresh, never crash
        return _cached;
    }

    public async Task SaveAsync(ConnectionSettings s)
    {
        s.ServerUrl = ConnectionSettings.Normalize(s.ServerUrl) is { Length: > 0 } url ? url : s.ServerUrl.Trim();
        _cached = s;
        await SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(s));
    }

    /// <summary>Stores the direct addresses the server reported so sync can fall back to them later.</summary>
    public async Task RememberAsync(IEnumerable<string> reported)
    {
        var s = await GetAsync();
        var urls = reported.Select(ConnectionSettings.Normalize).Where(u => u.StartsWith("http")).Distinct().Take(8).ToList();
        if (urls.Count == 0 || urls.SequenceEqual(s.LearnedUrls)) return;
        s.LearnedUrls = urls;
        await SaveAsync(s);
    }
}
