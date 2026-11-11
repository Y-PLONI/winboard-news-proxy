using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace WinboardNews;

/// <summary>Turns the raw markdown/HTML mix of a Hagizra message into plain title, body and media references.</summary>
public static partial class NewsNormalizer
{
    public const string SiteUrl = "https://hagizra.news/";

    // Media is only ever loaded from these hosts (https, no credentials); anything else is labelled but not loaded.
    private static readonly HashSet<string> MediaHosts = new(StringComparer.OrdinalIgnoreCase) { "stream.hagizra.news", "hagizra.news" };

    [GeneratedRegex(@"\[(image|video|audio)-embedded#\]\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\(([^)]*)\)")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"</?[a-zA-Z][^>]{0,200}>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^[\s‎‏]*\*{2,}(?<t>[^*]+?)\*{2,}")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"(\d+)x(\d+)")]
    private static partial Regex SizeRegex();

    private static readonly char[] Edge =
    {
        ' ', '‎', '‏', '‪', '‫', '‬', '‭', '‮', '⁦', '⁧', '⁨', '⁩',
    };

    // A reply: "[quote-embedded#](<id>@<quoted text, usually cut with ...>)" on its own line, then the reply text.
    // The quoted text is not guaranteed to be free of ")", so the match ends at a ")" that closes the line.
    [GeneratedRegex(@"\[quote-embedded#\]\((?:(?<id>\d+)@)?(?<t>[^\r\n]*?)\)[ \t]*(?=\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex QuoteRegex();

    public static (string Title, string Body, List<MediaRef> Media, QuoteRef? Quote) Normalize(string? raw)
    {
        var media = new List<MediaRef>();
        if (string.IsNullOrEmpty(raw))
            return ("", "", media, null);

        QuoteRef? quote = null;
        string source = raw;
        var q = QuoteRegex().Match(source);
        if (q.Success)
        {
            var (qTitle, qBody) = SplitTitle(StripMarkup(q.Groups["t"].Value));
            long.TryParse(q.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long qid);
            quote = new QuoteRef(qid, qTitle, qBody);
            source = source.Remove(q.Index, q.Length);
        }

        string text = EmbeddedRegex().Replace(source, m =>
        {
            media.Add(MakeRef(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value));
            return " ";
        });
        text = MarkdownImageRegex().Replace(text, m =>
        {
            media.Add(MakeRef("image", m.Groups[1].Value));
            return " ";
        });

        var (title, body) = SplitTitle(StripMarkup(text));
        return (title, body, media, quote);
    }

    private static string StripMarkup(string text)
    {
        text = LinkRegex().Replace(text, "$1");
        return WebUtility.HtmlDecode(TagRegex().Replace(text, ""));
    }

    private static (string Title, string Body) SplitTitle(string text)
    {
        string title = "";
        var match = TitleRegex().Match(text);
        if (match.Success)
        {
            title = Clean(match.Groups["t"].Value.TrimEnd('|'));
            text = text[match.Length..];
        }
        return (title, Clean(text));
    }

    /// <summary>"https://host/x.mp4#464x848" becomes a MediaRef; Url stays empty when the host/scheme is not allowed.</summary>
    private static MediaRef MakeRef(string kind, string reference)
    {
        string[] parts = reference.Trim().Split('#', 2);
        string url = "";
        if (parts[0].Length > 0
            && Uri.TryCreate(new Uri(SiteUrl), parts[0], out var u)
            && u.Scheme == Uri.UriSchemeHttps
            && u.UserInfo.Length == 0
            && MediaHosts.Contains(u.Host))
        {
            url = u.AbsoluteUri;
        }

        int w = 0, h = 0;
        if (parts.Length > 1)
        {
            var size = SizeRegex().Match(parts[1]);
            if (size.Success)
            {
                int.TryParse(size.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out w);
                int.TryParse(size.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out h);
            }
        }
        return new MediaRef(kind, url, w, h);
    }

    private static string Clean(string s)
    {
        s = s.Replace("*", "").Replace('_', ' ');
        return SpaceRegex().Replace(s, " ").Trim(Edge);
    }
}
