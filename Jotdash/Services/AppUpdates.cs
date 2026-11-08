using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jotdash.Services;

public sealed record AppRelease(Version Version, Uri DownloadUrl, long Size, string Sha256);

public sealed class AppUpdates(HttpClient http, Version installedVersion, string cacheDirectory,
    Action<string> validatePackage, Func<string, Task> installPackage)
{
    private const string ReleaseApi = "https://api.github.com/repos/Bortronx/jotdash/releases/latest";
    private const long MaxApkSize = 200 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCheck = DateTime.MinValue;

    public event Action? Changed;
    public AppRelease? Available { get; private set; }
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    public string? Message { get; private set; }
    public int Progress { get; private set; }
    public Version InstalledVersion => installedVersion;

    public async Task CheckAsync(bool automatic = false)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (automatic && DateTime.UtcNow - _lastCheck < TimeSpan.FromHours(24)) return;
            _lastCheck = DateTime.UtcNow;
            Busy = true;
            Error = null;
            Message = "Checking GitHub for updates...";
            Changed?.Invoke();
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
            request.Headers.UserAgent.ParseAdd("Jotdash-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            Available = ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token), installedVersion);
            Message = Available is null ? "You're up to date." : $"Jotdash {Available.Version} is available.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException or KeyNotFoundException)
        {
            Error = $"Could not check for updates ({e.GetType().Name}). Check your connection and try again.";
            Message = null;
        }
        finally
        {
            Busy = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task InstallAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        string? partial = null;
        try
        {
            if (Available is not { } release) throw new InvalidOperationException("Check for an update first.");
            Busy = true;
            Error = null;
            Progress = 0;
            Message = "Downloading update...";
            Changed?.Invoke();
            Directory.CreateDirectory(cacheDirectory);
            string apk = Path.Combine(cacheDirectory, "jotdash-update.apk");
            partial = apk + ".part";
            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
            request.Headers.UserAgent.ParseAdd("Jotdash-Updater/1.0");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long size && size != release.Size)
                throw new InvalidOperationException("Update download size does not match the release.");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = File.Create(partial))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidOperationException("Update download is too large.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    int percent = (int)(total * 100 / release.Size);
                    if (percent != Progress) { Progress = percent; Changed?.Invoke(); }
                }
                if (total != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                    throw new InvalidOperationException("Update verification failed. Download again.");
            }
            validatePackage(partial);
            File.Move(partial, apk, overwrite: true);
            partial = null;
            await installPackage(apk);
            Message = "Confirm the update in Android's installer. Your notes stay on this phone.";
        }
        catch (Exception e)
        {
            Error = e is InvalidOperationException ? e.Message
                : $"Could not install the update ({e.GetType().Name}). Try again.";
            Message = null;
        }
        finally
        {
            try
            {
                if (partial is not null && File.Exists(partial)) File.Delete(partial);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Error = $"Could not remove the incomplete update ({e.GetType().Name}). No incomplete APK was installed.";
                Message = null;
            }
            finally
            {
                Busy = false;
                _gate.Release();
                Changed?.Invoke();
            }
        }
    }

    public static AppRelease? ParseRelease(string json, Version installed)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string? tag = root.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version))
            throw new InvalidOperationException("The release has an invalid version.");
        if (version <= installed) return null;
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == "jotdash.apk").ToList();
        if (assets.Count != 1) throw new InvalidOperationException("The release must contain one Jotdash APK.");
        var asset = assets[0];
        string expected = $"https://github.com/Bortronx/jotdash/releases/download/{tag}/jotdash.apk";
        if (asset.GetProperty("browser_download_url").GetString() != expected)
            throw new InvalidOperationException("The update is not a Jotdash release download.");
        if (!asset.GetProperty("size").TryGetInt64(out long size))
            throw new InvalidOperationException("The release has an invalid APK size.");
        string? digest = asset.GetProperty("digest").GetString();
        if (size <= 0 || size > MaxApkSize || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)
            || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("The release is missing a valid size or SHA-256 checksum.");
        return new AppRelease(version, new Uri(expected), size, digest[7..]);
    }
}
