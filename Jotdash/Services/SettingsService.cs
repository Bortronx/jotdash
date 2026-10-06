using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Jotdash.Services;

/// <summary>How the phone reaches the sync server (optional: the app works fully offline without it).</summary>
public class ConnectionSettings
{
    public string ServerUrl { get; set; } = "";       // e.g. https://your-server/quicknotes/
    public string AuthMode { get; set; } = "none";    // none / basic / token / header
    public string User { get; set; } = "";            // basic: username      token: Pangolin token id   header: header name
    public string Secret { get; set; } = "";          // basic: password      token: Pangolin token      header: header value
    public string ApiKey { get; set; } = "";          // optional QUICKNOTES_API_KEY

    public bool IsSet => Uri.TryCreate(ServerUrl, UriKind.Absolute, out _);

    /// <summary>Adds the Pangolin / server credentials to a request.</summary>
    public void Apply(HttpRequestHeaders h)
    {
        switch (AuthMode)
        {
            case "basic":
                h.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Secret}")));
                break;
            case "token": // Pangolin share-link / access token
                h.TryAddWithoutValidation("P-Access-Token-Id", User);
                h.TryAddWithoutValidation("P-Access-Token", Secret);
                break;
            case "header" when User.Length > 0:
                h.TryAddWithoutValidation(User, Secret);
                break;
        }
        if (ApiKey.Length > 0) h.TryAddWithoutValidation("X-QuickNotes-Key", ApiKey);
    }
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
        s.ServerUrl = s.ServerUrl.Trim();
        if (s.ServerUrl.Length > 0 && !s.ServerUrl.EndsWith('/')) s.ServerUrl += "/";
        _cached = s;
        await SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(s));
    }
}
