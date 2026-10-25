using System.Net;
using System.Net.Sockets;
using System.Text;
using Jotdash.Services;

internal static class AuthTests
{
    public static async Task RunAsync(LocalStore store)
    {
        var defaults = new ConnectionSettings();
        Check(defaults.ServerUrl == "" && defaults.BackupUrls == "" && defaults.LearnedUrls.Count == 0
            && defaults.User == "" && defaults.Secret == "" && defaults.ApiKey == "" && !defaults.IsSet,
            "The app ships without deployment addresses, accounts or credentials.");
        Check(!new ConnectionSettings { ServerUrl = "/" }.IsSet, "A blank address normalized to a slash is not a server.");
        Check(!new ConnectionSettings { ServerUrl = "file:///tmp/server" }.IsSet, "Only HTTP(S) server addresses are accepted.");
        using var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        int port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();
        string origin = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin);
        listener.Start();

        string mode = "redirect";
        int loginRequests = 0, writeRequests = 0;
        bool tokenReceived = false, basicReceived = false;
        var server = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    string path = ctx.Request.Url!.AbsolutePath;
                    if (path == "/login") loginRequests++;
                    if (ctx.Request.HttpMethod != "GET") writeRequests++;
                    tokenReceived |= ctx.Request.Headers["P-Access-Token-Id"] == "test-id"
                        && ctx.Request.Headers["P-Access-Token"] == "test-token";
                    basicReceived |= ctx.Request.Headers["Authorization"] ==
                        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test-user:test-password"));
                    string body;
                    if (mode == "redirect")
                    {
                        ctx.Response.StatusCode = 302;
                        ctx.Response.RedirectLocation = origin + "login";
                        body = "";
                    }
                    else if (mode == "denied")
                    {
                        ctx.Response.StatusCode = 401;
                        body = "";
                    }
                    else if (mode is "html" or "untyped-html")
                    {
                        if (mode == "html") ctx.Response.ContentType = "text/html";
                        body = "<html><body>Sign in</body></html>";
                    }
                    else
                    {
                        ctx.Response.ContentType = "application/json";
                        body = "{\"workspaces\":[]}";
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
            var conn = new ConnectionSettings { ServerUrl = origin + "quicknotes/", AuthMode = "basic", User = "test-user", Secret = "test-password" };
            foreach (string failure in new[] { "redirect", "html", "untyped-html", "denied" })
            {
                mode = failure;
                string report = "";
                string result = (await SyncService.TestAsync(conn, text => report = text)).Message;
                Check(result.Contains("Basic Header Auth") && result.Contains("share link"), $"{failure}: actionable account-vs-Basic explanation.");
                Check(report.Contains("Response: HTTP"), "Diagnostics include HTTP response metadata.");
                Check(!report.Contains("test-user") && !report.Contains("test-password") && !report.Contains("<html>"), "Diagnostics omit credentials and page contents.");
                var settings = new SettingsService();
                await settings.SaveAsync(conn);
                string? error = await new SyncService(store, settings).SyncAsync();
                Check(error?.Contains("stay saved on this phone") == true, $"{failure}: background sync reports authentication failure.");
            }
            Check(loginRequests == 0, "API client never follows a login redirect.");
            Check(writeRequests == 0, "Authentication failure never sends notes or files.");
            Check(store.PendingCount > 0, "Blocked sync keeps local work queued.");
            Check(basicReceived, "Explicit HTTP Basic credentials are sent.");
            mode = "json";
            conn.AuthMode = "token";
            conn.User = "test-id";
            conn.Secret = "test-token";
            Check((await SyncService.TestAsync(conn)).Ok, "A JSON API response passes connection test.");
            Check(tokenReceived, "Both documented Pangolin token headers are sent.");

            Check(ConnectionSettings.Normalize("100.88.1.2") == "http://100.88.1.2:8126/quicknotes/", "A bare IP becomes a full backup address.");
            Check(ConnectionSettings.Normalize("192.0.2.10:8099") == "http://192.0.2.10:8099/quicknotes/", "A typed port is kept.");
            Check(ConnectionSettings.Normalize("https://apps.example.com/quicknotes") == "https://apps.example.com/quicknotes/", "A trailing slash is added.");
            Check(ConnectionSettings.Normalize("not a url ::") == "", "Invalid addresses are ignored.");
            Check(ConnectionSettings.ParseToken("", "https://pangolin.example.com/s/abc123.tok456") == ("abc123", "tok456"), "Share links are parsed.");
            Check(ConnectionSettings.ParseToken("", "https://apps.example.com/x?p_token=abc123.tok456") == ("abc123", "tok456"), "p_token links are parsed.");
            Check(ConnectionSettings.ParseToken(" abc123 ", " tok456 ") == ("abc123", "tok456"), "Separate token ID and token are trimmed.");

            var headers = new HttpRequestMessage().Headers;
            new ConnectionSettings { ServerUrl = "https://apps.example.com/quicknotes/", AuthMode = "basic", User = "u", Secret = "p" }
                .Apply(headers, new Uri("http://100.88.1.2:8126/quicknotes/api/meta"));
            Check(headers.Authorization is null, "Pangolin credentials are never sent to plain-HTTP backups.");

            // Blocked main address → falls back to a working backup.
            mode = "json";
            var fallback = new ConnectionSettings { ServerUrl = "http://127.0.0.1:1/quicknotes/", BackupUrls = origin + "quicknotes/" };
            var viaBackup = await SyncService.TestAsync(fallback);
            Check(viaBackup.Ok && viaBackup.Message.Contains("via backup 1"), "Test falls back to the backup address.");
            using var safe = new ConnectionDiagnostics(new ConnectionSettings
            {
                ServerUrl = "https://secret-user:secret-password@example.com/secret-path?token=secret-token",
                AuthMode = "secret-mode", User = "secret-user", Secret = "secret-password", ApiKey = "secret-key",
            });
            safe.Failure(new HttpRequestException("secret-token"));
            Check(!safe.Report.Contains("secret-user") && !safe.Report.Contains("secret-password") &&
                !safe.Report.Contains("secret-token") && !safe.Report.Contains("secret-key") &&
                !safe.Report.Contains("secret-path") && !safe.Report.Contains("secret-mode"),
                "Diagnostics omit credential-bearing URL parts, unknown auth labels and exception messages.");
            for (int i = 0; i < 25; i++) safe.Failure(new IOException());
            Check(safe.Report.Split("Failure type:").Length == 21, "Diagnostics retain at most twenty transport events.");
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
