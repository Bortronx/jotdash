using System.Diagnostics;
using System.Text;

namespace Jotdash.Services;

/// <summary>Bounded transport metadata only: never credentials, bodies, or arbitrary URLs.</summary>
public sealed class ConnectionDiagnostics(ConnectionSettings connection) : DelegatingHandler
{
    private readonly object _lock = new();
    private readonly Queue<string> _lines = new();
    private readonly DateTime _started = DateTime.UtcNow;

    public string Report
    {
        get
        {
            lock (_lock)
            {
                var report = new StringBuilder();
                report.AppendLine("Jotdash connection diagnostics");
                report.AppendLine($"Started (UTC): {_started:O}");
                report.AppendLine($"Auth mode: {connection.AuthMode switch { "none" => "none", "basic" => "HTTP Basic", "token" => "Pangolin token", "header" => "custom header", _ => "unknown" }}");
                report.AppendLine($"Server key configured: {!string.IsNullOrWhiteSpace(connection.ApiKey)}");
                report.AppendLine($"Auth identifier configured: {!string.IsNullOrWhiteSpace(connection.User)}; secret configured: {!string.IsNullOrWhiteSpace(connection.Secret)}");
                report.AppendLine($"Auth values have surrounding whitespace: {connection.User != connection.User.Trim() || connection.Secret != connection.Secret.Trim()}");
                if (Uri.TryCreate(connection.ServerUrl, UriKind.Absolute, out var uri))
                {
                    report.AppendLine($"Server transport: {uri.Scheme}; port: {uri.Port}");
                    report.AppendLine($"Server path is /quicknotes/: {uri.AbsolutePath.TrimEnd('/') == "/quicknotes"}");
                }
                report.AppendLine("Credentials, URLs, file/task IDs and bodies are omitted.");
                foreach (string line in _lines) report.AppendLine(line);
                return report.ToString();
            }
        }
    }

    public void Failure(Exception exception) => Add($"Failure type: {exception.GetType().Name}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string endpoint = request.RequestUri?.AbsolutePath is { } path
            ? path.EndsWith("/api/meta") ? "metadata"
                : path.Contains("/api/tasks") ? "tasks/upload"
                : path.Contains("/api/files") ? "file download"
                : path.Contains("/api/sync") ? "sync changes" : "other path"
            : "unknown";
        var watch = Stopwatch.StartNew();
        Add($"Request: {request.Method} {endpoint}");
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            string? media = response.Content.Headers.ContentType?.MediaType;
            // Only known labels; never echo server-controlled header strings.
            string kind = media switch
            {
                "application/json" => "JSON",
                "text/html" or "application/xhtml+xml" => "HTML",
                "text/plain" => "plain text",
                null => "unspecified",
                _ => "other",
            };
            Add($"Response: HTTP {(int)response.StatusCode}; type: {kind}; elapsed: {watch.ElapsedMilliseconds} ms; redirect header present: {response.Headers.Location is not null}");
            return response;
        }
        catch (Exception e)
        {
            Failure(e);
            throw;
        }
    }

    private void Add(string line)
    {
        lock (_lock)
        {
            if (_lines.Count == 20) _lines.Dequeue();
            _lines.Enqueue(line);
        }
    }
}
