using System.Text.Json;
using Xunit;

namespace WinboardNews.Tests;

public class NormalizerTests
{
    [Fact]
    public void Title_body_and_image_marker()
    {
        var r = NewsNormalizer.Normalize("**שמחת בית צאנז |** צילום משה גולדשטיין.\n[image-embedded#](https://stream.hagizra.news/x.jpg#1280x723:)");
        Assert.Equal("שמחת בית צאנז", r.Title);
        Assert.Equal("צילום משה גולדשטיין.", r.Body);
        Assert.Equal(new MediaRef("image", "https://stream.hagizra.news/x.jpg", 1280, 723), Assert.Single(r.Media));
    }

    [Fact]
    public void Video_audio_relative_and_untrusted_media()
    {
        var r = NewsNormalizer.Normalize(string.Join('\n',
            "**כותרת |** טקסט",
            "[video-embedded#](https://stream.hagizra.news/a/v.mp4#464x848)",
            "[audio-embedded#](https://stream.hagizra.news/b/a.mp3)",
            "[image-embedded#](/api/files/abc)",
            "[image-embedded#](https://evil.example/x.jpg#10x10)",
            "[image-embedded#](http://stream.hagizra.news/plain.jpg)",
            "[image-embedded#](https://user:pw@stream.hagizra.news/c.jpg)"));

        Assert.Equal("טקסט", r.Body);
        Assert.Equal(new[] { "video", "audio", "image", "image", "image", "image" }, r.Media.Select(m => m.Kind));
        Assert.Equal(new MediaRef("video", "https://stream.hagizra.news/a/v.mp4", 464, 848), r.Media[0]);
        Assert.Equal(new MediaRef("audio", "https://stream.hagizra.news/b/a.mp3", 0, 0), r.Media[1]);
        Assert.Equal("https://hagizra.news/api/files/abc", r.Media[2].Url);
        Assert.All(r.Media.Skip(3), m => Assert.Equal("", m.Url));   // labelled, never loaded
    }

    [Fact]
    public void Strips_html_links_and_decodes_entities()
    {
        var r = NewsNormalizer.Normalize("**כותרת**<b>גוף</b> &amp; [קישור](https://x.y) ![](a.png)");
        Assert.Equal("כותרת", r.Title);
        Assert.Equal("גוף & קישור", r.Body);
        Assert.Equal("image", Assert.Single(r.Media).Kind);
    }

    [Fact]
    public void Reply_keeps_the_quoted_flash_apart_from_the_message()
    {
        var r = NewsNormalizer.Normalize(
            "[quote-embedded#](2313598@**החוצפה של הצבא |** יעקב פרבר (בצה\"ל) הגדירו אותו כעריק...)\n\n**קצת מביך |** למרות כל הסערה...");
        Assert.Equal("קצת מביך", r.Title);
        Assert.Equal("למרות כל הסערה...", r.Body);
        Assert.Equal(new QuoteRef(2313598, "החוצפה של הצבא", "יעקב פרבר (בצה\"ל) הגדירו אותו כעריק..."), r.Quote);
        Assert.DoesNotContain("quote", r.Title + r.Body);
    }

    [Fact]
    public void Reply_without_an_id_and_plain_message_have_sane_quotes()
    {
        Assert.Equal(0, NewsNormalizer.Normalize("[quote-embedded#](טקסט מצוטט)\nתגובה").Quote!.Id);
        Assert.Null(NewsNormalizer.Normalize("**כותרת |** גוף").Quote);
    }

    [Fact]
    public void Never_lets_html_through()
    {
        Assert.DoesNotContain('<', NewsNormalizer.Normalize("<script>alert(1)</script>טקסט").Body);
    }
}

public class HagizraApiTests
{
    private static string Json(params object[] messages) => JsonSerializer.Serialize(messages);

    [Fact]
    public void Parses_array_and_wrapped_object_skips_deleted_ads_and_bad_ids_sorts_newest_first()
    {
        object[] msgs =
        {
            new { id = 1, text = "ישן", timestamp = "2026-11-10T10:00:00Z" },
            new { id = 2, text = "חדש", timestamp = "2026-11-10T11:00:00Z", breakingId = 5 },
            new { id = 3, text = "נמחק", deleted = true, timestamp = "2026-11-10T12:00:00Z" },
            new { id = 4, text = "פרסומת", is_ads = true, timestamp = "2026-11-10T12:00:00Z" },
            new { id = "x", text = "בלי מזהה", timestamp = "2026-11-10T12:00:00Z" },
        };

        foreach (string json in new[] { Json(msgs), JsonSerializer.Serialize(new { messages = msgs }) })
        {
            var (items, raw, minId) = HagizraApi.Parse(json);
            Assert.Equal(5, raw);
            Assert.Equal(1, minId);                                    // deleted/ad messages still count for the cursor
            Assert.Equal(new long[] { 2, 1 }, items.Select(i => i.Id));
            Assert.True(items[0].Breaking);
            Assert.False(items[1].Breaking);
        }
        Assert.Throws<FormatException>(() => HagizraApi.Parse("{\"nope\":1}"));
    }

    [Fact]
    public void Parses_hagizra_nanosecond_timestamps()
    {
        var (items, _, _) = HagizraApi.Parse(Json(new { id = 7, text = "א", timestamp = "2026-11-10T21:24:35.416680557Z" }));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 21, 24, 35, TimeSpan.Zero), items[0].Time, TimeSpan.FromSeconds(1));
    }
}

public class NewsServiceTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Get() => Now;
    }

    private static string Page(long firstId, int count) =>
        JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => new { id = firstId - i, text = $"הודעה {firstId - i}", timestamp = "2026-11-10T10:00:00Z" }));

    [Fact]
    public async Task Caches_dedupes_inflight_and_reports_paging()
    {
        int calls = 0;
        var clock = new Clock();
        var svc = new NewsService((cursor, _) => { calls++; return Task.FromResult(Page(cursor == 0 ? 1000 : cursor - 1, NewsService.PageSize)); }, clock.Get);

        var pages = await Task.WhenAll(svc.GetAsync(0), svc.GetAsync(0));
        Assert.Equal(1, calls);
        Assert.Equal(25, pages[0].Items.Count);
        Assert.Equal(976, pages[0].Next);              // smallest id on the page = before-id cursor for older ones
        Assert.True(pages[0].HasMore);
        Assert.False(pages[0].Stale);

        clock.Now += TimeSpan.FromSeconds(10);
        await svc.GetAsync(0);
        Assert.Equal(1, calls);                       // fresh cache reused

        var older = await svc.GetAsync(pages[0].Next);   // older pages are separate cache entries
        Assert.Equal(2, calls);
        Assert.Equal(951, older.Next);
        Assert.Equal(975, older.Items[0].Id);
    }

    [Fact]
    public async Task Short_page_means_no_more()
    {
        var svc = new NewsService((_, _) => Task.FromResult(Page(10, 7)));
        var page = await svc.GetAsync(100);
        Assert.False(page.HasMore);
        Assert.Equal(4, page.Next);
    }

    [Fact]
    public async Task Reaching_the_first_message_means_no_more()
    {
        var svc = new NewsService((_, _) => Task.FromResult(Page(25, 25)));   // ids 25..1, a full page
        var page = await svc.GetAsync(26);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Serves_stale_data_with_backoff_when_hagizra_is_down()
    {
        int calls = 0;
        bool fail = false;
        var clock = new Clock();
        var svc = new NewsService((_, _) =>
        {
            calls++;
            return fail ? Task.FromException<string>(new HttpRequestException("down")) : Task.FromResult(Page(5, 3));
        }, clock.Get);

        await svc.GetAsync(0);
        fail = true;
        clock.Now += TimeSpan.FromSeconds(60);
        var stale = await svc.GetAsync(0);
        Assert.Equal(2, calls);
        Assert.True(stale.Stale);
        Assert.Equal(3, stale.Items.Count);           // stale items are kept
        Assert.Equal("down", stale.Error);

        clock.Now += TimeSpan.FromSeconds(5);
        await svc.GetAsync(0);
        Assert.Equal(2, calls);                       // backoff suppresses the retry
        clock.Now += TimeSpan.FromSeconds(30);
        await svc.GetAsync(0);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Failure_with_no_data_returns_an_empty_stale_page()
    {
        var svc = new NewsService((_, _) => Task.FromException<string>(new HttpRequestException("down")));
        var page = await svc.GetAsync(0);
        Assert.Empty(page.Items);
        Assert.True(page.Stale);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Manual_refresh_accepts_a_younger_cache_than_the_default()
    {
        int calls = 0;
        var clock = new Clock();
        var svc = new NewsService((_, _) => { calls++; return Task.FromResult(Page(5, 3)); }, clock.Get);

        await svc.GetAsync(0);
        clock.Now += TimeSpan.FromSeconds(20);
        await svc.GetAsync(0);
        Assert.Equal(1, calls);                                   // default ttl (45 s) still caches
        await svc.GetAsync(0, TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);                                   // manual refresh refetches after 20 s
        clock.Now += TimeSpan.FromSeconds(2);
        await svc.GetAsync(0, TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);                                   // but not within 5 s
    }
}
