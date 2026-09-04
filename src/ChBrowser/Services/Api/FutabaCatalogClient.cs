using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Api;

/// <summary>Converts Futaba Channel catalog HTML into the existing thread-list model.</summary>
public sealed class FutabaCatalogClient
{
    // ふたばのカタログ設定 Cookie: 横14・縦6・文字数100・文字位置下・画像サイズ小。
    // cl=100 は公式設定画面で許可される最大値で、一覧用に短縮前に近い題名を取得する。
    private const string CatalogSettingsCookie = "cxyl=14x6x100x0x0";

    private static readonly Regex CatalogCellRe = new(
        @"<td\b[^>]*>\s*<a\s+[^>]*href\s*=\s*(?:['""])?res/(?<key>\d+)\.htm(?:['""])?[^>]*>\s*<img\b[^>]*\bsrc\s*=\s*(?:['""])?(?<thumb>[^'""\s>]+)(?:['""])?[^>]*>.*?</a>\s*<br\s*/?>\s*<small>(?<title>.*?)</small>\s*<br\s*/?>\s*<font\s+[^>]*\bsize\s*=\s*(?:['""])?2(?:['""])?[^>]*>(?<count>\d+)</font>\s*</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex CatalogThumbnailRe = new(
        @"^/(?<dir>[^/]+)/cat/(?<stem>\d+)s\.jpg$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly MonazillaClient _client;

    public FutabaCatalogClient(MonazillaClient client) => _client = client;

    public async Task<IReadOnlyList<ThreadInfo>> FetchAsync(Board board, CancellationToken ct = default)
    {
        var url = FutabaUrl.BuildCatalogUrl(board.Host, board.DirectoryName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", CatalogSettingsCookie);
        using var response = await _client.Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), board);
    }

    internal static IReadOnlyList<ThreadInfo> Parse(byte[] shiftJisHtml, Board? board = null)
    {
        var html = Encoding.GetEncoding(932).GetString(shiftJisHtml);
        var threads = new List<ThreadInfo>();
        var order = 0;
        foreach (Match match in CatalogCellRe.Matches(html))
        {
            if (!int.TryParse(match.Groups["count"].Value, out var count)) continue;
            order++;
            var thumbnail = match.Groups["thumb"].Success && board is not null
                ? ToThreadThumbnailUrl(board, WebUtility.HtmlDecode(match.Groups["thumb"].Value))
                : null;
            threads.Add(new ThreadInfo(match.Groups["key"].Value, WebUtility.HtmlDecode(match.Groups["title"].Value).Trim(), count, order, thumbnail));
        }
        return threads;
    }

    private static string ToThreadThumbnailUrl(Board board, string catalogThumbnail)
    {
        var match = CatalogThumbnailRe.Match(catalogThumbnail);
        if (match.Success)
            return $"https://{board.Host}/{match.Groups["dir"].Value}/thumb/{match.Groups["stem"].Value}s.jpg";

        return new Uri(new Uri(FutabaUrl.BuildBoardUrl(board.Host, board.DirectoryName)), catalogThumbnail).AbsoluteUri;
    }
}
