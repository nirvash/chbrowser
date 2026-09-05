using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Api;

/// <summary>Converts Futaba Channel thread HTML into posts for the existing thread renderer.</summary>
public sealed class FutabaThreadClient
{
    private static readonly Regex TitleRe = new(@"<title>(?<title>.*?)</title>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex PostRe = new(@"<(?:div[^>]*\bclass\s*=\s*(?:['""][^'""]*\bthre\b[^'""]*['""]|thre)[^>]*|td[^>]*\bclass\s*=\s*(?:['""][^'""]*\brtd\b[^'""]*['""]|rtd)[^>]*)>(?<content>.*?)(?:</div>|</td>)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex NumberRe = new(@"<span\s+class\s*=\s*(?:['""])?cno(?:['""])?\s*>\s*No\.(?<number>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameRe = new(@"<span\s+class\s*=\s*(?:['""])?cnm(?:['""])?\s*>(?<name>.*?)</span>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex DateRe = new(@"<span\s+class\s*=\s*(?:['""])?cnw(?:['""])?\s*>(?<date>.*?)</span>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex BodyRe = new(@"<blockquote[^>]*>(?<body>.*?)</blockquote>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex AttachmentRe = new(@"<a\s+[^>]*href\s*=\s*(?:['""])?(?<url>[^'""\s>]+)(?:['""])?[^>]*>\s*<img\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex QuoteFontRe = new(@"<font\b[^>]*\bcolor\s*=\s*(?:['""])?#789922(?:['""])?[^>]*>(?<body>.*?)</font>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex FontTagRe = new(@"</?font\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuoteTagRe = new(@"</?(?:span|font)\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BodyLineBreakRe = new(@"<br\s*/?>|\r?\n", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTagRe = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LeadingQuoteRe = new(@"^\s*(?:>\s*)+", RegexOptions.Compiled);
    private static readonly Regex SoudaneRe = new(@"<a\b[^>]*\bclass\s*=\s*['""]?sod['""]?[^>]*>\s*そうだねx(?<count>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FutabaDateRe = new(@"^(?<yy>\d{2})/(?<mm>\d{2})/(?<dd>\d{2})(?<dow>\([^)]*\))(?<time>\d{2}:\d{2}:\d{2})(?:\.(?<frac>\d{1,2}))?$", RegexOptions.Compiled);

    private readonly MonazillaClient _client;

    public FutabaThreadClient(MonazillaClient client) => _client = client;

    public async Task<DatFetchResult> FetchAsync(Board board, string threadKey, CancellationToken ct = default)
    {
        var bytes = await FetchBytesAsync(board, threadKey, ct).ConfigureAwait(false);
        var url = FutabaUrl.BuildThreadUrl(board.Host, board.DirectoryName, threadKey);
        var posts = await Task.Run(() => FutabaQuoteAnalyzer.Analyze(Parse(bytes, new Uri(url)), ct), ct).ConfigureAwait(false);
        return new DatFetchResult(posts, bytes.LongLength);
    }

    public async Task<byte[]> FetchBytesAsync(Board board, string threadKey, CancellationToken ct = default)
    {
        var url = FutabaUrl.BuildThreadUrl(board.Host, board.DirectoryName, threadKey);
        using var response = await _client.Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    internal static IReadOnlyList<Post> Parse(byte[] shiftJisHtml, Uri pageUri)
    {
        var html = Encoding.GetEncoding(932).GetString(shiftJisHtml);
        var titleMatch = TitleRe.Match(html);
        var title = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups["title"].Value).Trim() : "";
        var posts = new List<Post>();

        foreach (Match match in PostRe.Matches(html))
        {
            var content = match.Groups["content"].Value;
            var numberMatch = NumberRe.Match(content);
            var bodyMatch = BodyRe.Match(content);
            if (!numberMatch.Success || !bodyMatch.Success || !int.TryParse(numberMatch.Groups["number"].Value, out var number))
                continue;

            var name = NameRe.Match(content) is { Success: true } nameMatch
                ? nameMatch.Groups["name"].Value.Trim() : "";
            var date = DateRe.Match(content) is { Success: true } dateMatch
                ? NormalizeDate(WebUtility.HtmlDecode(dateMatch.Groups["date"].Value).Trim()) : "";
            var rawBodyHtml = bodyMatch.Groups["body"].Value;
            var attachments = ExtractAttachments(content, pageUri);
            var quoteInfo = BuildQuoteInfo(rawBodyHtml, attachments);
            var body = QuoteFontRe.Replace(rawBodyHtml, "<span class=\"futaba-quote\">${body}</span>");
            body = FontTagRe.Replace(body, "");
            // ふたば本文の &gt; は引用記号として表示する。未デコードのまま JS で escape すると &amp;gt; と文字列表示される。
            body = WebUtility.HtmlDecode(body).Trim();

            // Attached images are passed as absolute URLs so thread.js can create its usual media slots.
            if (attachments.Count > 0) body = string.Join("\n", attachments) + "\n" + body;
            if (posts.Count == 0) body = "[[CHB_FUTABA_OP]]" + body;

            int? soudane = SoudaneRe.Match(content) is { Success: true } soudaneMatch && int.TryParse(soudaneMatch.Groups["count"].Value, out var sc) ? sc : null;
            posts.Add(new Post(number, name, "", date, "", body, posts.Count == 0 ? title : null, soudane, quoteInfo));
        }
        return posts;
    }

    private static List<string> ExtractAttachments(string content, Uri pageUri)
    {
        var attachments = new List<string>();
        foreach (Match attachment in AttachmentRe.Matches(content))
        {
            var rawUrl = WebUtility.HtmlDecode(attachment.Groups["url"].Value);
            if (Uri.TryCreate(pageUri, rawUrl, out var absolute)) attachments.Add(absolute.AbsoluteUri);
        }
        return attachments;
    }

    private static FutabaQuoteInfo BuildQuoteInfo(string rawHtml, IReadOnlyList<string> attachmentUrls)
    {
        var lines = new List<FutabaQuoteLine>();
        var decodedBody = WebUtility.HtmlDecode(HtmlTagRe.Replace(rawHtml, ""));
        var hasTextualQuoteMarkers = Regex.IsMatch(decodedBody, @"(?m)^\s*>");
        var quoteTags = new List<(string Name, bool IsQuote)>();
        var cursor = 0;
        foreach (Match lineBreak in BodyLineBreakRe.Matches(rawHtml))
        {
            AddQuoteLine(rawHtml[cursor..lineBreak.Index], quoteTags, lines, hasTextualQuoteMarkers);
            cursor = lineBreak.Index + lineBreak.Length;
        }
        AddQuoteLine(rawHtml[cursor..], quoteTags, lines, hasTextualQuoteMarkers);
        return new FutabaQuoteInfo(lines, attachmentUrls, rawHtml);
    }

    private static void AddQuoteLine(string rawLine, List<(string Name, bool IsQuote)> quoteTags, ICollection<FutabaQuoteLine> lines, bool hasTextualQuoteMarkers)
    {
        var lineDepth = quoteTags.Count(static tag => tag.IsQuote);
        foreach (Match tag in QuoteTagRe.Matches(rawLine))
        {
            var name = tag.Value.TrimStart('<', '/').Split([' ', '\t', '>'], 2)[0];
            if (tag.Value.StartsWith("</", StringComparison.Ordinal))
            {
                for (var i = quoteTags.Count - 1; i >= 0; i--)
                    if (string.Equals(quoteTags[i].Name, name, StringComparison.OrdinalIgnoreCase)) { quoteTags.RemoveAt(i); break; }
            }
            else
            {
                var isQuote = (name.Equals("span", StringComparison.OrdinalIgnoreCase)
                    && Regex.IsMatch(tag.Value, @"\bclass\s*=\s*['""]?[^'""]*\bfutaba-quote\b", RegexOptions.IgnoreCase))
                    || (name.Equals("font", StringComparison.OrdinalIgnoreCase)
                    && Regex.IsMatch(tag.Value, @"\bcolor\s*=\s*['""]?#789922", RegexOptions.IgnoreCase));
                quoteTags.Add((name, isQuote));
                if (isQuote) lineDepth = Math.Max(lineDepth, quoteTags.Count(static item => item.IsQuote));
            }
        }
        var text = WebUtility.HtmlDecode(HtmlTagRe.Replace(rawLine, ""));
        var originalText = text;
        var leadingQuotes = LeadingQuoteRe.Match(text);
        if (leadingQuotes.Success)
        {
            var leadingQuoteDepth = leadingQuotes.Value.Count(static c => c == '>');
            // 行頭の > は表示上の引用深度そのもの。外側の <font> が複数行を
            // 包んでいても、タグ深度を加算すると `>` 1個が深度2になるため、
            // 明示的な行頭記号をHTMLタグの深度より優先する。
            lineDepth = leadingQuoteDepth;
            text = text[leadingQuotes.Length..];
        }
        else if (hasTextualQuoteMarkers)
        {
            // 本文中に視覚的な行頭引用が存在する形式では、マーカーのない行を
            // 外側の引用font/spanの深さだけで引用扱いにしない。
            lineDepth = 0;
        }
        // >No.123 は引用本文ではなく、明示的なレス番号参照。
        lines.Add(new FutabaQuoteLine(text.TrimEnd(), lineDepth, rawLine, originalText));
    }

    private static string NormalizeDate(string value)
    {
        var match = FutabaDateRe.Match(value);
        if (!match.Success) return value;
        var year = 2000 + int.Parse(match.Groups["yy"].Value);
        var fraction = match.Groups["frac"].Success ? $".{match.Groups["frac"].Value.PadRight(2, '0')}" : "";
        return $"{year:0000}/{match.Groups["mm"].Value}/{match.Groups["dd"].Value}{match.Groups["dow"].Value} {match.Groups["time"].Value}{fraction}";
    }
}
