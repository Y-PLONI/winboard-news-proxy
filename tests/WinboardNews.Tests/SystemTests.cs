using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WinboardNews.Tests;

public class HostsFileTests
{
    private const string Marker = "# winboard-news-proxy";

    [Fact]
    public void Adds_the_line_without_touching_the_rest_and_is_idempotent()
    {
        const string original = "127.0.0.1 localhost\r\n# K10-SafeSearch END";   // no trailing newline
        string once = HostsFile.Apply(original, add: true);
        Assert.Equal($"127.0.0.1 localhost\r\n# K10-SafeSearch END\r\n127.0.0.1 windows.msn.com {Marker}\r\n", once);
        Assert.Equal(once, HostsFile.Apply(once, add: true));
        Assert.True(HostsFile.IsInstalled(once));
    }

    [Fact]
    public void Repairs_a_malformed_tagged_line()
    {
        string broken = $"1.1.1.1 a\r\n.0.0.1 windows.msn.com {Marker}\r\n";
        string fixedText = HostsFile.Apply(broken, add: true);
        Assert.Equal($"1.1.1.1 a\r\n127.0.0.1 windows.msn.com {Marker}\r\n", fixedText);
    }

    [Fact]
    public void Removes_only_the_tagged_line()
    {
        string text = $"1.1.1.1 a\r\n127.0.0.1 windows.msn.com {Marker}\r\n2.2.2.2 b\r\n";
        Assert.Equal("1.1.1.1 a\r\n2.2.2.2 b\r\n", HostsFile.Apply(text, add: false));
        Assert.False(HostsFile.IsInstalled(HostsFile.Apply(text, add: false)));
    }

    [Fact]
    public void Update_writes_the_file_and_a_one_time_backup()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = Path.Combine(dir, "hosts");
            File.WriteAllText(path, "1.1.1.1 a\r\n");
            HostsFile.Update(true, path);
            Assert.Contains(HostsFile.WantedLine, File.ReadAllText(path));
            Assert.Equal("1.1.1.1 a\r\n", File.ReadAllText(path + ".winboard.bak"));
            HostsFile.Update(false, path);
            Assert.Equal("1.1.1.1 a\r\n", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ServerTests
{
    private static string Page(long firstId, int count) =>
        JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => new { id = firstId - i, text = $"**כותרת {firstId - i} |** גוף\n[video-embedded#](https://stream.hagizra.news/a/v.mp4#464x848)", timestamp = "2026-11-10T10:00:00Z" }));

    private static async Task<(LocalServer server, CancellationTokenSource cts)> StartAsync(X509Certificate2? cert)
    {
        var news = new NewsService((cursor, _) => Task.FromResult(Page(cursor == 0 ? 1000 : cursor - 1, 25)));
        var server = new LocalServer(IPAddress.Loopback, 0, cert, news, "9.9.9");
        server.Start();
        var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);
        await Task.Yield();
        return (server, cts);
    }

    [Fact]
    public async Task Serves_health_page_news_diag_and_404_over_plain_http()
    {
        var (server, cts) = await StartAsync(null);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

            Assert.Equal("ok", await http.GetStringAsync("/healthz"));

            using var page = await http.GetAsync("/windows/startfeed?aver=1");
            // not text/html Accept => 404 for an unknown path; the board does send Accept: text/html
            using var request = new HttpRequestMessage(HttpMethod.Get, "/windows/startfeed?aver=1");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var html = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, html.StatusCode);
            string body = await html.Content.ReadAsStringAsync();
            Assert.Contains("הגיזרה", body);
            Assert.Contains("var VERSION = '9.9.9';", body);             // version stamped into the page
            Assert.True(html.Headers.Contains("Content-Security-Policy"));
            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);

            using var doc = JsonDocument.Parse(await http.GetStringAsync("/news.json"));
            Assert.Equal("9.9.9", doc.RootElement.GetProperty("v").GetString());
            Assert.Equal(25, doc.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(976, doc.RootElement.GetProperty("next").GetInt64());
            Assert.True(doc.RootElement.GetProperty("hasMore").GetBoolean());
            var first = doc.RootElement.GetProperty("items")[0];
            Assert.Equal("video", first.GetProperty("media")[0].GetProperty("kind").GetString());

            using var older = JsonDocument.Parse(await http.GetStringAsync("/news.json?offset=976&fresh=1"));
            Assert.Equal(975, older.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64());
            Assert.Equal(951, older.RootElement.GetProperty("next").GetInt64());

            using var diag = await http.PostAsync("/diag", new StringContent("{\"x\":1}", Encoding.UTF8));
            Assert.Equal(HttpStatusCode.NoContent, diag.StatusCode);

            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/nope.js")).StatusCode);
        }
        finally { cts.Cancel(); server.Stop(); }
    }

    [Fact]
    public async Task Serves_over_tls_with_the_generated_certificate()
    {
        using var cert = CertTool.Create("windows.msn.com", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var (server, cts) = await StartAsync(cert);
        try
        {
            using var handler = new SocketsHttpHandler
            {
                // the test machine does not trust the cert; pin to this exact certificate instead
                SslOptions = { RemoteCertificateValidationCallback = (_, c, _, _) => c is X509Certificate2 x && x.Thumbprint == cert.Thumbprint
                                                                              || c is not null && new X509Certificate2(c).Thumbprint == cert.Thumbprint },
                ConnectCallback = async (ctx, ct) =>   // reach the local port while using the real host name for TLS/SNI
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await socket.ConnectAsync(IPAddress.Loopback, server.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };
            using var http = new HttpClient(handler);
            Assert.Equal("ok", await http.GetStringAsync("https://windows.msn.com/healthz"));
            using var json = JsonDocument.Parse(await http.GetStringAsync("https://windows.msn.com/news.json"));
            Assert.Equal(25, json.RootElement.GetProperty("items").GetArrayLength());
        }
        finally { cts.Cancel(); server.Stop(); }
    }

    [Fact]
    public void Generated_certificate_is_not_a_ca_and_only_covers_the_one_host()
    {
        using var cert = CertTool.Create("windows.msn.com", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Assert.Equal("CN=windows.msn.com", cert.Subject);
        Assert.Equal(cert.Subject, cert.Issuer);
        var basic = cert.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.False(basic.CertificateAuthority);
        var san = cert.Extensions["2.5.29.17"]!;
        Assert.Contains("windows.msn.com", san.Format(false));
        Assert.True(cert.HasPrivateKey);
    }
}
