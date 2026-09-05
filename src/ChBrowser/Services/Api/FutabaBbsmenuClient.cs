using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Api;

/// <summary>ふたば公式 bbsmenu.html から閲覧可能な板を抽出する。</summary>
public sealed class FutabaBbsmenuClient
{
    private const string MenuUrl = "https://www.2chan.net/bbsmenu.html";
    private static readonly Regex TagRe = new(@"<(?:b|a)\b[^>]*>.*?</(?:b|a)>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex CategoryRe = new(@"^<b\b[^>]*>(?<name>.*?)</b>$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex BoardRe = new(@"^<a\b[^>]*href\s*=\s*(?:['""])?(?<url>[^'""\s>]+)(?:['""])?[^>]*>(?<name>.*?)</a>$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex StripTagsRe = new(@"<[^>]+>", RegexOptions.Compiled);

    private readonly MonazillaClient _client;
    private readonly DataPaths _paths;

    public FutabaBbsmenuClient(MonazillaClient client, DataPaths paths)
    {
        _client = client;
        _paths = paths;
    }

    public async Task<IReadOnlyList<BoardCategory>> FetchAndSaveAsync(CancellationToken ct = default)
    {
        using var response = await _client.Http.GetAsync(MenuUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(_paths.FutabaBbsmenuHtmlPath, bytes, ct).ConfigureAwait(false);
        return Parse(bytes);
    }

    public async Task<IReadOnlyList<BoardCategory>> LoadFromDiskAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_paths.FutabaBbsmenuHtmlPath)) return Array.Empty<BoardCategory>();
        return Parse(await File.ReadAllBytesAsync(_paths.FutabaBbsmenuHtmlPath, ct).ConfigureAwait(false));
    }

    internal static IReadOnlyList<BoardCategory> Parse(byte[] shiftJisHtml)
    {
        var html = Encoding.GetEncoding(932).GetString(shiftJisHtml);
        var categories = new List<BoardCategory>();
        var boards = new List<Board>();
        var categoryName = "";
        var categoryNumber = 0;

        void FlushCategory()
        {
            if (boards.Count == 0 || string.IsNullOrEmpty(categoryName)) return;
            categories.Add(new BoardCategory(categoryName, categoryNumber, boards.ToArray()));
            boards.Clear();
        }

        foreach (Match tag in TagRe.Matches(html))
        {
            var text = tag.Value.Trim();
            var category = CategoryRe.Match(text);
            if (category.Success)
            {
                FlushCategory();
                categoryName = "ふたば☆" + HtmlText(category.Groups["name"].Value);
                categoryNumber++;
                continue;
            }

            var board = BoardRe.Match(text);
            if (!board.Success || string.IsNullOrEmpty(categoryName)) continue;
            if (!Uri.TryCreate(WebUtility.HtmlDecode(board.Groups["url"].Value), UriKind.Absolute, out var uri)) continue;
            if (!FutabaUrl.IsFutabaHost(uri.Host)) continue;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 2 || !string.Equals(parts[1], "futaba.htm", StringComparison.OrdinalIgnoreCase)) continue;

            boards.Add(new Board(parts[0], HtmlText(board.Groups["name"].Value), uri.AbsoluteUri, categoryName, boards.Count));
        }
        FlushCategory();

        // 二次元裏・模型裏などは同名の板が複数ホストに存在するため、
        // 重複時だけ慣例どおりサブドメインを末尾に付けて区別する。
        var duplicateNames = categories
            .SelectMany(x => x.Boards)
            .GroupBy(x => x.BoardName, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);

        return categories
            .Select(category => category with
            {
                Boards = category.Boards
                    .Select(board => duplicateNames.Contains(board.BoardName)
                        ? board with { BoardName = board.BoardName + GetSubdomain(board.Url) }
                        : board)
                    .ToArray()
            })
            .ToArray();
    }

    private static string HtmlText(string html) => WebUtility.HtmlDecode(StripTagsRe.Replace(html, "")).Trim();

    private static string GetSubdomain(string url)
    {
        var host = new Uri(url).Host;
        return host[..host.IndexOf('.', StringComparison.Ordinal)];
    }
}
