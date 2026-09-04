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
    private static readonly Regex SoudaneRe = new(@"<a\b[^>]*\bclass\s*=\s*['""]?sod['""]?[^>]*>\s*そうだねx(?<count>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FutabaDateRe = new(@"^(?<yy>\d{2})/(?<mm>\d{2})/(?<dd>\d{2})(?<dow>\([^)]*\))(?<time>\d{2}:\d{2}:\d{2})(?:\.(?<frac>\d{1,2}))?$", RegexOptions.Compiled);

    private readonly MonazillaClient _client;

    public FutabaThreadClient(MonazillaClient client) => _client = client;

    public async Task<DatFetchResult> FetchAsync(Board board, string threadKey, CancellationToken ct = default)
    {
        var bytes = await FetchBytesAsync(board, threadKey, ct).ConfigureAwait(false);
        var url = FutabaUrl.BuildThreadUrl(board.Host, board.DirectoryName, threadKey);
        return new DatFetchResult(Parse(bytes, new Uri(url)), bytes.LongLength);
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
            var body = QuoteFontRe.Replace(bodyMatch.Groups["body"].Value, "<span class=\"futaba-quote\">${body}</span>");
            body = FontTagRe.Replace(body, "");
            // ふたば本文の &gt; は引用記号として表示する。未デコードのまま JS で escape すると &amp;gt; と文字列表示される。
            body = WebUtility.HtmlDecode(body).Trim();

            // Attached images are passed as absolute URLs so thread.js can create its usual media slots.
            var attachments = new List<string>();
            foreach (Match attachment in AttachmentRe.Matches(content))
            {
                var rawUrl = WebUtility.HtmlDecode(attachment.Groups["url"].Value);
                if (Uri.TryCreate(pageUri, rawUrl, out var absolute)) attachments.Add(absolute.AbsoluteUri);
            }
            if (attachments.Count > 0) body = string.Join("\n", attachments) + "\n" + body;
            if (posts.Count == 0) body = "[[CHB_FUTABA_OP]]" + body;

            int? soudane = SoudaneRe.Match(content) is { Success: true } soudaneMatch && int.TryParse(soudaneMatch.Groups["count"].Value, out var sc) ? sc : null;
            posts.Add(new Post(number, name, "", date, "", body, posts.Count == 0 ? title : null, soudane));
        }
        return posts;
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
