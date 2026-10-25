using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jotdash.Services;

internal static class PangolinSessionTests
{
    public static async Task RunAsync()
    {
        Check(new ConnectionSettings().AuthMode == "token", "New configurations default to Pangolin share links.");
        var fresh = new ConnectionSettings { Secret = "https://app.pangolin.net/s/short-secret" };
        Check(fresh.IsSet, "A share link alone is a configured connection.");
        using (var discoveryTransport = new FakePangolin(new CookieContainer(), "ok"))
        using (var discovery = new HttpClient(discoveryTransport))
        {
            await PangolinSessionHandler.DiscoverServerAsync(fresh, discovery, default);
            Check(fresh.ServerUrl == "https://notes.example/quicknotes/", "Link discovers resource origin and fixed QuickNotes endpoint.");
            Check(discoveryTransport.Redemptions == 1 && discoveryTransport.ApiRequests == 0, "Discovery sends no notes to the resource.");
            await PangolinSessionHandler.DiscoverServerAsync(fresh, discovery, default);
            Check(discoveryTransport.Redemptions == 1, "Saved addresses are not rediscovered.");
        }
        var savedSettings = new SettingsService();
        await savedSettings.SaveAsync(fresh);
        Check((await new SettingsService().GetAsync()).ServerUrl == fresh.ServerUrl, "Discovered address persists in saved settings.");
        var savedPrivate = new ConnectionSettings { AuthMode = "none", ServerUrl = "http://100.88.1.2:8126/quicknotes/" };
        await savedSettings.SaveAsync(savedPrivate);
        Check((await new SettingsService().GetAsync()).AuthMode == "none", "Existing private-network sign-in selection is preserved.");
        foreach (string failure in new[] { "denied", "wrong-resource", "bad-json", "missing-session" })
        {
            using var transport = new FakePangolin(new CookieContainer(), failure);
            using var discovery = new HttpClient(transport);
            var pending = new ConnectionSettings { Secret = "https://app.pangolin.net/s/short-secret" };
            if (failure == "wrong-resource")
            {
                await PangolinSessionHandler.DiscoverServerAsync(pending, discovery, default);
                Check(pending.ServerUrl == "https://other.example/quicknotes/", "Discovery uses the linked resource rather than a hardcoded host.");
                continue;
            }
            try
            {
                await PangolinSessionHandler.DiscoverServerAsync(pending, discovery, default);
                throw new Exception("Discovery should fail: " + failure);
            }
            catch (InvalidOperationException)
            {
                Check(pending.ServerUrl == "", "Failed discovery never saves a fabricated address.");
            }
        }
        var connection = new ConnectionSettings
        {
            ServerUrl = "https://notes.example/quicknotes/",
            AuthMode = "token",
            Secret = "https://app.pangolin.net/s/short-secret",
            ApiKey = "server-secret",
        };
        Check(PangolinSessionHandler.ShortLink(connection) is not null, "Single-value browser links use redemption.");
        Check(PangolinSessionHandler.ShortLink(new ConnectionSettings
        {
            AuthMode = "token", Secret = "https://pangolin.example/s/id.token",
        }) is null, "Legacy ID.token links retain header authentication.");

        foreach (string mode in new[] { "ok", "denied", "wrong-resource", "bad-json", "missing-session", "no-cookie", "exchange-denied", "http-link" })
        {
            var settings = connection.Clone();
            if (mode == "http-link") settings.Secret = "http://app.pangolin.net/s/short-secret";
            var cookies = new CookieContainer();
            using var transport = new FakePangolin(cookies, mode);
            using var client = new HttpClient(new PangolinSessionHandler(settings, cookies) { InnerHandler = transport });
            if (mode == "ok")
            {
                using var meta = await client.GetAsync("https://notes.example/quicknotes/api/meta");
                Check(meta.IsSuccessStatusCode, "Short-link session reaches API metadata.");
                using var upload = await client.PostAsJsonAsync("https://notes.example/quicknotes/api/tasks", new { text = "note" });
                Check(upload.IsSuccessStatusCode && transport.ApiRequests == 2, "Uploads reuse the established session.");
                Check(transport.Redemptions == 1 && transport.Exchanges == 1, "Redeem only once per sync client.");
            }
            else
            {
                try
                {
                    using var response = await client.GetAsync("https://notes.example/quicknotes/api/meta");
                    throw new Exception("Expected share-link sign-in to fail: " + mode);
                }
                catch (InvalidOperationException e)
                {
                    Check(!e.Message.Contains("short-secret") && !e.Message.Contains("session-secret") &&
                        !e.Message.Contains("server-secret"), "Sign-in errors never contain credentials.");
                }
                Check(transport.ApiRequests == 0, "Failed sign-in never sends API requests or note contents.");
                if (mode == "wrong-resource")
                    Check(transport.Exchanges == 0, "Mismatched resource never receives the request token.");
            }
        }

        var backupCookies = new CookieContainer();
        using var backups = new FakePangolin(backupCookies, "backup");
        using var backupClient = new HttpClient(new PangolinSessionHandler(connection, backupCookies) { InnerHandler = backups });
        using var backup = await backupClient.GetAsync("http://100.88.1.2:8126/quicknotes/api/meta");
        Check(backups.Redemptions == 0 && backups.ApiRequests == 1, "NetBird backups do not require Pangolin redemption.");

        using var legacy = new FakePangolin(new CookieContainer(), "legacy");
        var legacySettings = connection.Clone();
        legacySettings.Secret = "https://pangolin.example/s/id.token";
        using var legacyClient = new HttpClient(new PangolinSessionHandler(legacySettings, new CookieContainer()) { InnerHandler = legacy });
        using var legacyResponse = await legacyClient.GetAsync("https://notes.example/quicknotes/api/meta");
        Check(legacy.Redemptions == 0 && legacy.ApiRequests == 1, "Legacy links still use both access-token headers.");
    }

    private sealed class FakePangolin(CookieContainer cookies, string mode) : HttpMessageHandler
    {
        public int Redemptions, Exchanges, ApiRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "app.pangolin.net")
            {
                Redemptions++;
                Check(uri.AbsolutePath == "/api/v1/auth/access-token" && request.Method == HttpMethod.Post, "Use Pangolin's browser redemption endpoint.");
                Check(!request.Headers.Contains("X-QuickNotes-Key") && !request.Headers.Contains("P-Access-Token"), "No server credentials on dashboard requests.");
                Check(request.Headers.GetValues("X-CSRF-Token").Single() == "x-csrf-protection", "Pangolin CSRF header is present.");
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                Check(body.GetProperty("accessToken").GetString() == "short-secret" && !body.TryGetProperty("accessTokenId", out _), "Send short token without inventing an ID.");
                if (mode == "denied") return new(HttpStatusCode.Unauthorized);
                if (mode == "bad-json") return new(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", System.Text.Encoding.UTF8, "application/json") };
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        data = new
                        {
                            session = mode == "missing-session" ? "" : "session-secret",
                            redirectUrl = mode == "wrong-resource" ? "https://other.example/" : "https://notes.example/",
                        },
                    }),
                };
            }
            if (uri.Query.Length > 0)
            {
                Exchanges++;
                Check(uri.Host == "notes.example" && request.Method == HttpMethod.Get, "Exchange only on the configured resource.");
                Check(uri.Query == "?resource_session_request_param=session-secret", "Exchange uses the documented default request-token parameter.");
                Check(!request.Headers.Contains("X-QuickNotes-Key") && request.Content is null, "Exchange never carries server keys or notes.");
                if (mode == "exchange-denied") return new(HttpStatusCode.Unauthorized);
                if (mode != "no-cookie")
                    cookies.Add(new Uri("https://notes.example/"), new Cookie("p_session_token", "cookie-secret", "/") { Secure = true });
                return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://notes.example/quicknotes/") } };
            }
            ApiRequests++;
            Check(uri.AbsolutePath.Contains("/api/"), "Never follow login or exchange redirects.");
            Check(request.Headers.GetValues("X-QuickNotes-Key").Single() == "server-secret", "Server key is applied only to API requests.");
            if (mode == "legacy")
                Check(request.Headers.GetValues("P-Access-Token-Id").Single() == "id" &&
                    request.Headers.GetValues("P-Access-Token").Single() == "token", "Legacy token headers preserved.");
            else
            {
                Check(!request.Headers.Contains("P-Access-Token"), "Short link is not sent as a legacy access header.");
                if (mode != "backup")
                    Check(cookies.GetCookies(uri)["p_session_token"]?.Value == "cookie-secret", "Session cookie is available for API requests.");
                else
                    Check(cookies.GetCookies(uri).Count == 0, "No resource cookies on plain HTTP backups.");
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { workspaces = Array.Empty<object>() }) };
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
