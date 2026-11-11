using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace WinboardNews;

/// <summary>
/// Minimal HTTP/1.1 server (one request per connection) for the Widgets board page. Binds to loopback only.
/// Serves the news page, /news.json, /diag and /healthz. TLS when a certificate is given, plain HTTP otherwise (dev/tests).
/// </summary>
public sealed class LocalServer
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 16 * 1024;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(20);

    // The page only needs itself, Hagizra media, and inline script/style.
    private const string PageCsp =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; " +
        "img-src https://stream.hagizra.news https://hagizra.news data:; media-src https://stream.hagizra.news https://hagizra.news; " +
        "base-uri 'none'; form-action 'none'";

    private readonly TcpListener _listener;
    private readonly X509Certificate2? _certificate;
    private readonly NewsService _news;
    private readonly string _version;
    private readonly string _pageHtml;
    private readonly Action<string>? _log;

    public LocalServer(IPAddress address, int port, X509Certificate2? certificate, NewsService news, string version, Action<string>? log = null)
    {
        _listener = new TcpListener(address, port);
        _certificate = certificate;
        _news = news;
        _version = version;
        _pageHtml = LoadPage(version);
        _log = log;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static string LoadPage(string version)
    {
        using var stream = typeof(LocalServer).Assembly.GetManifestResourceStream("app.html")
            ?? throw new InvalidOperationException("app.html resource missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string html = reader.ReadToEnd();
        int i = html.IndexOf("__VERSION__", StringComparison.Ordinal);
        return i < 0 ? html : html[..i] + version + html[(i + "__VERSION__".Length)..];
    }

    public void Start() => _listener.Start(backlog: 32);

    public void Stop() => _listener.Stop();

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                _log?.Invoke($"accept failed: {e.Message}");
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        using var cts = new CancellationTokenSource(ConnectionTimeout);   // also drops idle preconnect sockets
        try
        {
            client.NoDelay = true;
            Stream stream = client.GetStream();
            if (_certificate is not null)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                }, cts.Token).ConfigureAwait(false);
                stream = ssl;
            }

            await using (stream)
            {
                var request = await ReadRequestAsync(stream, cts.Token).ConfigureAwait(false);
                if (request is null) return;
                var response = await RouteAsync(request, cts.Token).ConfigureAwait(false);
                await WriteResponseAsync(stream, response, request.Method == "HEAD", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // clients (WebView2 preconnects, aborts navigations) drop connections all the time
        }
        catch (Exception e)
        {
            _log?.Invoke($"request failed: {e}");
        }
    }

    // ---------- routing ----------

    private sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, string Body);
    private sealed record Response(int Status, string Reason, string ContentType, byte[] Body, string? ContentSecurityPolicy = null);

    private async Task<Response> RouteAsync(Request r, CancellationToken ct)
    {
        _ = ct;
        string path = r.Path;
        if (path == "/healthz")
            return Text(200, "OK", "ok");

        if (path == "/news.json" && r.Method is "GET" or "HEAD")
        {
            var query = ParseQuery(r.Query);
            long cursor = query.TryGetValue("offset", out var o) && long.TryParse(o, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                ? parsed : 0;   // "offset" is the before-id cursor (see HagizraApi)
            // fresh=1 is the manual refresh button: accept at most 5 s old data instead of the default 45 s.
            TimeSpan? maxAge = query.TryGetValue("fresh", out var f) && f == "1" ? TimeSpan.FromSeconds(5) : null;
            var page = await _news.GetAsync(cursor, maxAge).ConfigureAwait(false);
            return new Response(200, "OK", "application/json; charset=utf-8", NewsJson.Serialize(page, _version));
        }

        if (path == "/diag" && r.Method == "POST")
        {
            _log?.Invoke($"diag {Truncate(r.Body, 600)}");
            return new Response(204, "No Content", "text/plain", Array.Empty<byte>());
        }

        r.Headers.TryGetValue("accept", out var accept);
        bool wantsHtml = accept?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true;
        if (r.Method is "GET" or "HEAD" && (path is "/" or "/app.html" || (wantsHtml && !Path.HasExtension(path))))
            return new Response(200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(_pageHtml), PageCsp);

        return Text(404, "Not Found", "not found");
    }

    private static Response Text(int status, string reason, string body) =>
        new(status, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(body));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            string value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[key] = value;
        }
        return result;
    }

    // ---------- HTTP/1.1 plumbing ----------

    private static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        int length = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (length == buffer.Length) return null;
            int n = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (n == 0) return null;
            int from = Math.Max(0, length - 3);
            length += n;
            headerEnd = buffer.AsSpan(from, length - from).IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0) headerEnd += from;
        }

        string head = Encoding.Latin1.GetString(buffer, 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length < 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0) headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        string target = first[1];
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        string query = q < 0 ? "" : target[(q + 1)..];

        string body = "";
        if (headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out int want) && want > 0)
        {
            if (want > MaxBodyBytes) return null;
            var bodyBytes = new byte[want];
            int have = Math.Min(want, length - (headerEnd + 4));
            Array.Copy(buffer, headerEnd + 4, bodyBytes, 0, have);
            while (have < want)
            {
                int n = await stream.ReadAsync(bodyBytes.AsMemory(have), ct).ConfigureAwait(false);
                if (n == 0) break;
                have += n;
            }
            body = Encoding.UTF8.GetString(bodyBytes, 0, have);
        }

        return new Request(first[0].ToUpperInvariant(), path, query, headers, body);
    }

    private static async Task WriteResponseAsync(Stream stream, Response r, bool head, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(r.Reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(r.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        if (r.ContentSecurityPolicy is not null) sb.Append("Content-Security-Policy: ").Append(r.ContentSecurityPolicy).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        if (!head && r.Body.Length > 0)
            await stream.WriteAsync(r.Body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>JSON for /news.json. Hand-written with Utf8JsonWriter so it stays trim/AOT friendly.</summary>
public static class NewsJson
{
    public static byte[] Serialize(NewsPage page, string version)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("items");
            foreach (var item in page.Items)
            {
                w.WriteStartObject();
                w.WriteNumber("id", item.Id);
                w.WriteNumber("time", item.Time.ToUnixTimeMilliseconds());
                w.WriteString("title", item.Title);
                w.WriteString("body", item.Body);
                w.WriteBoolean("breaking", item.Breaking);
                if (item.Quote is { } q)
                {
                    w.WriteStartObject("quote");
                    w.WriteNumber("id", q.Id);
                    w.WriteString("title", q.Title);
                    w.WriteString("body", q.Body);
                    w.WriteEndObject();
                }
                w.WriteStartArray("media");
                foreach (var m in item.Media)
                {
                    w.WriteStartObject();
                    w.WriteString("kind", m.Kind);
                    w.WriteString("url", m.Url);
                    w.WriteNumber("w", m.W);
                    w.WriteNumber("h", m.H);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("next", page.Next);
            w.WriteBoolean("hasMore", page.HasMore);
            w.WriteNumber("fetchedAt", page.FetchedAt == default ? 0 : page.FetchedAt.ToUnixTimeMilliseconds());
            w.WriteBoolean("stale", page.Stale);
            if (page.Error is null) w.WriteNull("error"); else w.WriteString("error", page.Error);
            w.WriteString("v", version);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}
