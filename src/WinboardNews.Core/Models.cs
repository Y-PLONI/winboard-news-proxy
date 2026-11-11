namespace WinboardNews;

/// <summary>One image/video/audio attached to a flash. Url is empty unless it is https on a Hagizra host.</summary>
public sealed record MediaRef(string Kind, string Url, int W, int H);

/// <summary>The flash a message replies to (Id is 0 when unknown). The text is what Hagizra embedded, usually cut short.</summary>
public sealed record QuoteRef(long Id, string Title, string Body);

/// <summary>One flash, already stripped of markdown, HTML and media markers. Quote is set when it replies to another flash.</summary>
public sealed record NewsItem(long Id, DateTimeOffset Time, string Title, string Body, IReadOnlyList<MediaRef> Media, bool Breaking, QuoteRef? Quote = null);

/// <summary>A page of flashes. Next is the cursor to pass back to get the following (older) page.</summary>
public sealed record NewsPage(IReadOnlyList<NewsItem> Items, long Next, bool HasMore, DateTimeOffset FetchedAt, bool Stale, string? Error);
