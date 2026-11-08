using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jotdash.Services;

static class UpdateTests
{
    public static async Task RunAsync()
    {
        byte[] apk = Encoding.UTF8.GetBytes("Synthetic signed APK");
        string digest = Convert.ToHexString(SHA256.HashData(apk));
        string Release(string tag = "v1.0.16", string? checksum = null, string? url = null, bool prerelease = false) =>
            JsonSerializer.Serialize(new
            {
                tag_name = tag, draft = false, prerelease,
                assets = new[] { new { name = "jotdash.apk", size = apk.Length,
                    digest = "sha256:" + (checksum ?? digest),
                    browser_download_url = url ?? $"https://github.com/Bortronx/jotdash/releases/download/{tag}/jotdash.apk" } },
            });
        var installed = new Version(1, 0, 15);
        Check(AppUpdates.ParseRelease(Release("v1.0.15"), installed) is null, "Current release is not an update.");
        Check(AppUpdates.ParseRelease(Release("v1.0.14"), installed) is null, "No downgrade.");
        Check(AppUpdates.ParseRelease(Release(prerelease: true), installed) is null, "Prereleases are ignored.");
        Check(AppUpdates.ParseRelease(Release(), installed)?.Version == new Version(1, 0, 16), "New release discovered.");
        ExpectInvalid(() => AppUpdates.ParseRelease(Release(url: "https://example.com/other.apk"), installed));
        ExpectInvalid(() => AppUpdates.ParseRelease(Release(checksum: "bad"), installed));

        string root = Path.Combine(Path.GetTempPath(), "jotdash-updates-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var handler = new FakeUpdateHttp { Metadata = Release(), Apk = apk };
            using var http = new HttpClient(handler);
            int validated = 0, installedCount = 0;
            bool rejectSignature = false;
            var updates = new AppUpdates(http, installed, root, path =>
            {
                Check(File.ReadAllBytes(path).SequenceEqual(apk), "Validator receives complete APK bytes.");
                if (rejectSignature) throw new InvalidOperationException("Signing key mismatch.");
                validated++;
            }, path =>
            {
                Check(File.ReadAllBytes(path).SequenceEqual(apk), "Installer receives complete verified APK.");
                installedCount++;
                return Task.CompletedTask;
            });
            await updates.CheckAsync(automatic: true);
            await updates.CheckAsync(automatic: true);
            Check(handler.Checks == 1 && updates.Available is not null, "Automatic checks throttled.");
            await updates.CheckAsync();
            Check(handler.Checks == 2, "Manual check bypasses interval.");
            await updates.InstallAsync();
            Check(validated == 1 && installedCount == 1 && updates.Progress == 100 && updates.Error is null, "Valid APK installed.");
            handler.Apk = Encoding.UTF8.GetBytes("Synthetic wrong APK!");
            await updates.InstallAsync();
            Check(updates.Error is not null && installedCount == 1 && !File.Exists(Path.Combine(root, "jotdash-update.apk.part")), "Corrupt download rejected and cleaned.");
            handler.Apk = apk;
            rejectSignature = true;
            await updates.InstallAsync();
            Check(updates.Error == "Signing key mismatch." && installedCount == 1, "Signer validation blocks install.");
            handler.Metadata = "{}";
            await updates.CheckAsync();
            Check(updates.Error is not null && !updates.Busy, "Malformed release reports an error.");
            handler.Fail = true;
            await updates.CheckAsync();
            Check(updates.Error is not null && !updates.Busy, "Network failure reports an error without blocking.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS: update discovery, release validation, check throttling, verified download, signing rejection and failures.");
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected invalid update metadata to be rejected.");
    }

    private sealed class FakeUpdateHttp : HttpMessageHandler
    {
        public string Metadata = "";
        public byte[] Apk = [];
        public int Checks;
        public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("Synthetic offline error.");
            Check(request.Headers.UserAgent.Count > 0, "GitHub request has a user agent.");
            bool metadata = request.RequestUri!.Host == "api.github.com";
            if (metadata) Checks++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = metadata ? new StringContent(Metadata) : new ByteArrayContent(Apk),
            });
        }
    }
}
