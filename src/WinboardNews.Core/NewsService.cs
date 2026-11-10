namespace WinboardNews;

/// <summary>
/// Shared cache of Hagizra pages (by cursor). One request serves all callers, concurrent callers share one fetch,
/// and stale data is served with a backoff while Hagizra is unreachable.
/// </summary>
public sealed class NewsService
{
    public const int PageSize = 25;
    private const int MaxCachedPages = 40;
    private static readonly int[] BackoffSeconds = { 30, 60, 120, 300, 900 };

    public static readonly TimeSpan FirstPageTtl = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan OlderPageTtl = TimeSpan.FromMinutes(5);

    private readonly Func<long, CancellationToken, Task<string>> _fetchJson;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _pages = new();

    private sealed class Entry
    {
        public NewsPage? Page;
        public int Failures;
        public DateTimeOffset RetryAt;
        public DateTimeOffset Touched;
        public string? LastError;
        public Task<NewsPage>? Inflight;
    }

    public NewsService(Func<long, CancellationToken, Task<string>>? fetchJson = null, Func<DateTimeOffset>? now = null)
    {
        _fetchJson = fetchJson ?? HagizraApi.FetchJsonAsync;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <param name="cursor">0 is the newest page; otherwise the NewsPage.Next of the page before it (a before-id cursor).</param>
    /// <param name="maxAge">Accept cached data at most this old (a manual refresh passes a small value).</param>
    public Task<NewsPage> GetAsync(long cursor, TimeSpan? maxAge = null)
    {
        lock (_gate)
        {
            var now = _now();
            if (!_pages.TryGetValue(cursor, out var e))
                _pages[cursor] = e = new Entry();
            e.Touched = now;
            Evict(cursor);

            var ttl = maxAge ?? (cursor == 0 ? FirstPageTtl : OlderPageTtl);
            if (e.Page is not null && now - e.Page.FetchedAt < ttl)
                return Task.FromResult(e.Page);
            if (now < e.RetryAt)
                return Task.FromResult(Stale(e, cursor));
            return e.Inflight ??= RefreshAsync(cursor, e);
        }
    }

    private async Task<NewsPage> RefreshAsync(long cursor, Entry e)
    {
        await Task.Yield();   // never complete synchronously inside GetAsync's lock
        try
        {
            string json = await _fetchJson(cursor, CancellationToken.None).ConfigureAwait(false);
            var (items, raw, minId) = HagizraApi.Parse(json);
            // minId is the cursor for older messages; a short page (or reaching id 1) means there is nothing older.
            var page = new NewsPage(items, minId > 0 ? minId : cursor, raw >= PageSize && minId > 1, _now(), false, null);
            lock (_gate)
            {
                e.Page = page;
                e.Failures = 0;
                e.RetryAt = default;
                e.LastError = null;
                e.Inflight = null;
            }
            return page;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                e.Failures++;
                e.LastError = ex.Message;
                e.RetryAt = _now() + TimeSpan.FromSeconds(BackoffSeconds[Math.Min(e.Failures - 1, BackoffSeconds.Length - 1)]);
                e.Inflight = null;
                return Stale(e, cursor);
            }
        }
    }

    private static NewsPage Stale(Entry e, long cursor) =>
        e.Page is null
            ? new NewsPage(Array.Empty<NewsItem>(), cursor, false, default, true, e.LastError)
            : e.Page with { Stale = true, Error = e.LastError };

    private void Evict(long keep)
    {
        while (_pages.Count > MaxCachedPages)
        {
            long oldest = -1;
            DateTimeOffset oldestTouched = DateTimeOffset.MaxValue;
            foreach (var (key, entry) in _pages)
            {
                if (key == keep || key == 0 || entry.Inflight is not null) continue;
                if (entry.Touched < oldestTouched) { oldest = key; oldestTouched = entry.Touched; }
            }
            if (oldest < 0) return;
            _pages.Remove(oldest);
        }
    }
}
