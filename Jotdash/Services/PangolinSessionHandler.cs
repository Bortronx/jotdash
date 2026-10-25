using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jotdash.Services;

/// <summary>Redeems short browser share links before accessing the API, without following API redirects.</summary>
public sealed class PangolinSessionHandler(ConnectionSettings connection, CookieContainer cookies) : DelegatingHandler
{
    private bool _connected;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static Uri? ShortLink(ConnectionSettings connection)
    {
        if (connection.AuthMode != "token" ||
            !Uri.TryCreate(connection.Secret.Trim(), UriKind.Absolute, out var link))
            return null;
        string[] parts = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0] == "s" && !parts[1].Contains('.') ? link : null;
    }

    public static async Task DiscoverServerAsync(ConnectionSettings connection, HttpClient http, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(connection.ServerUrl) || ShortLink(connection) is not { } link)
            return;
        var data = await AuthenticateAsync(link, (request, token) => http.SendAsync(request, token), ct);
        var resource = new Uri(data.RedirectUrl);
        connection.ServerUrl = new Uri(resource.GetLeftPart(UriPartial.Authority) + "/quicknotes/").AbsoluteUri;
    }

    private static async Task<AuthData> AuthenticateAsync(Uri link,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        if (link.Scheme != "https" || link.UserInfo.Length > 0)
            throw new InvalidOperationException("Pangolin share links require HTTPS.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var endpoint = new Uri(link.GetLeftPart(UriPartial.Authority) + "/api/v1/auth/access-token");
            using var auth = new HttpRequestMessage(HttpMethod.Post, endpoint);
            auth.Headers.Add("X-CSRF-Token", "x-csrf-protection");
            auth.Content = JsonContent.Create(new { accessToken = Uri.UnescapeDataString(link.Segments.Last().TrimEnd('/')) });
            using var response = await send(auth, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Pangolin share-link sign-in returned HTTP {(int)response.StatusCode}. Check that the link has not expired or been revoked.");
            var payload = await response.Content.ReadFromJsonAsync<AuthResponse>(timeout.Token);
            if (payload?.Data is not { Session.Length: > 0 } data ||
                !Uri.TryCreate(data.RedirectUrl, UriKind.Absolute, out var resource) ||
                resource.Scheme != "https" || resource.UserInfo.Length > 0)
                throw new InvalidOperationException("Pangolin share-link sign-in returned an invalid HTTPS resource session.");
            return data;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Pangolin share-link sign-in did not return valid JSON.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Pangolin share-link sign-in failed to connect. Check the link host and network access.");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var link = ShortLink(connection);
        if (link is not null && Uri.TryCreate(ConnectionSettings.Normalize(connection.ServerUrl), UriKind.Absolute, out var main)
            && request.RequestUri is { } target && SameOrigin(main, target))
        {
            if (link.Scheme != "https" || main.Scheme != "https" || link.UserInfo.Length > 0 || main.UserInfo.Length > 0)
                throw new InvalidOperationException("Pangolin share links require HTTPS for both the link and the main server address.");
            await _gate.WaitAsync(ct);
            try
            {
                if (!_connected)
                {
                    await RedeemAsync(link, main, ct);
                    _connected = true;
                }
            }
            finally { _gate.Release(); }
        }
        if (link is null)
            connection.Apply(request.Headers, request.RequestUri);
        else if (!string.IsNullOrWhiteSpace(connection.ApiKey))
            request.Headers.TryAddWithoutValidation("X-QuickNotes-Key", connection.ApiKey.Trim());
        return await base.SendAsync(request, ct);
    }

    private async Task RedeemAsync(Uri link, Uri main, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ct = timeout.Token;
        try
        {
            var data = await AuthenticateAsync(link, (request, token) => base.SendAsync(request, token), ct);
            var resource = new Uri(data.RedirectUrl);
            if (!SameOrigin(main, resource))
                throw new InvalidOperationException("The Pangolin share link belongs to a different resource than the main server address.");

            // Exchange the single-use request token for a resource cookie. Never follow the redirect
            // or send notes, QuickNotes keys, or the share token to the redemption endpoints.
            var exchangeUrl = new UriBuilder(main)
            {
                Query = "resource_session_request_param=" + Uri.EscapeDataString(data.Session),
                Fragment = "",
            };
            using var exchange = new HttpRequestMessage(HttpMethod.Get, exchangeUrl.Uri);
            using var result = await base.SendAsync(exchange, ct);
            if ((!result.IsSuccessStatusCode && result.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) ||
                cookies.GetCookies(new Uri(main, "api/meta")).Count == 0)
                throw new InvalidOperationException($"Pangolin did not establish a resource session (HTTP {(int)result.StatusCode}). Check the resource and its session request parameter configuration.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Pangolin share-link sign-in did not return valid JSON.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Pangolin share-link sign-in failed to connect. Check the link host and network access.");
        }
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
        base.Dispose(disposing);
    }

    private sealed record AuthResponse(AuthData? Data);
    private sealed record AuthData(string Session, string RedirectUrl);
}
