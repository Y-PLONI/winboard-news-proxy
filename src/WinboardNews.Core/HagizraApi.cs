using System.Globalization;
using System.Net;
using System.Text.Json;

namespace WinboardNews;

/// <summary>Talks to the Hagizra JSON API (https://hagizra.news/api/messages).</summary>
public static class HagizraApi
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),   // no socket stays open between refreshes
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 2,
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinboardNews/1.0");
        return client;
    }

    /// <param name="cursor">0 = newest page. Otherwise the API's "offset" acts as a before-id cursor: it returns the messages
    /// with ids below this value (verified against the live API), so pass the smallest id of the previous page.</param>
    public static async Task<string> FetchJsonAsync(long cursor, CancellationToken ct = default)
    {
        string url = $"{NewsNormalizer.SiteUrl}api/messages?limit={NewsService.PageSize.ToString(CultureInfo.InvariantCulture)}&offset={cursor.ToString(CultureInfo.InvariantCulture)}";
        using var response = await Client.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Accepts a bare array or an object holding it under "messages"; unknown fields are ignored.</summary>
    /// <returns>Items to show, how many raw messages the page had, and the smallest raw id (0 if none) = cursor for older ones.</returns>
    public static (List<NewsItem> Items, int RawCount, long MinId) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var inner))
            root = inner;
        if (root.ValueKind != JsonValueKind.Array)
            throw new FormatException("Unexpected messages payload");

        int raw = root.GetArrayLength();
        var items = new List<NewsItem>(raw);
        long minId = 0;
        foreach (var m in root.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
                continue;
            if (!m.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out long id))
                continue;
            if (minId == 0 || id < minId) minId = id;   // deleted/ad messages still advance the cursor
            if (GetBool(m, "deleted") || GetBool(m, "is_ads"))
                continue;

            var (title, body, media) = NewsNormalizer.Normalize(GetString(m, "text"));
            if (title.Length == 0 && body.Length == 0 && media.Count == 0)
                continue;

            items.Add(new NewsItem(
                id,
                ParseTime(GetString(m, "timestamp")),
                title,
                body,
                media,
                Breaking: m.TryGetProperty("breakingId", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out long bid) && bid > 0));
        }

        items.Sort((a, b) => a.Time != b.Time ? b.Time.CompareTo(a.Time) : b.Id.CompareTo(a.Id));
        return (items, raw, minId);
    }

    private static DateTimeOffset ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UtcNow;

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
