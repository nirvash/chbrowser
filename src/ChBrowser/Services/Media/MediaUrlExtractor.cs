using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Media;

/// <summary>スレ本文テキストから先読み対象のメディア URL を抽出する純粋関数群。
///
/// <para>判定ルールは thread.js の linkify / メディアスロット生成と同期して維持する。
/// 同期元: src/ChBrowser/Resources/thread.js の <c>BODY_URL_RE</c> / <c>normalizeUrlScheme</c> /
/// <c>bareToHttps</c> / <c>bareDomainTldIsFileExt</c> / <c>isSsspIcon</c> / <c>IMAGE_EXT_RE</c> /
/// <c>VIDEO_EXT_RE</c> / <c>YOUTUBE_RES</c> / <c>URL_EXPANDERS</c>。JS 側の判定を変えたらここも同期すること
/// (= 非同期展開対象の判定は C# 側の <see cref="ChBrowser.Services.Image.UrlExpander"/> に委譲)。</para>
///
/// <para>YouTube (サムネは表示時の受動キャッシュで足りる) と 5ch 系スレ URL は媒体扱いしない。</para></summary>
public static class MediaUrlExtractor
{
    private enum MediaKind { None, Image, Video }

    /// <summary>thread.js BODY_URL_RE と同じ prefix セット (sssp:// / フル形 / ttp 等の省略形 / 裸ドメイン形)。
    /// 省略形は negative lookbehind で「直前が英字でない」ことを要求する。
    /// 裸ドメイン形 (NG 回避で "https://" を省いた文字列) はパス ("/" 以降) 必須
    /// (= パス無しの "英字.英字" はテンプレ FAQ の "Q.NovelAI" 等の誤検出が支配的なため対象外)。
    /// group 1 = ドメイン部 (拡張子っぽいトークンの除外判定用)。</summary>
    private static readonly Regex BodyUrlRe = new(
        "sssp://[A-Za-z0-9\\-._~:/?#@!$&*+,;=%]+"
        + "|https?://[A-Za-z0-9\\-._~:/?#@!$&*+,;=%]+"
        + "|(?<![A-Za-z])(?:ttps?|tps?|ps?|s)?://[A-Za-z0-9\\-._~:/?#@!$&*+,;=%]+"
        + "|(?<![A-Za-z0-9._\\-/@:])((?:[A-Za-z0-9](?:[A-Za-z0-9\\-]*[A-Za-z0-9])?\\.)+[A-Za-z]{2,})(?::\\d{1,5})?(?:/[A-Za-z0-9\\-._~:/?#@!$&*+,;=%]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>裸ドメイン候補のドメイン部最終ラベルがこれに該当すれば媒体扱いしない
    /// (= "1787631575.dat" / "read.cgi/JNVA/&lt;key&gt;" 等の誤検出防止。thread.js BARE_DOMAIN_FILE_EXT と同期)。</summary>
    private static readonly HashSet<string> FileExtLikeTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        // 画像 / 動画 / 音声
        "png", "jpg", "jpeg", "jpe", "gif", "webp", "bmp", "svg", "ico", "icns",
        "tif", "tiff", "avif", "heic", "heif",
        "mp4", "m4v", "webm", "mov", "avi", "mkv", "wmv", "flv", "mpg", "mpeg", "ts",
        "mp3", "wav", "flac", "ogg", "m4a", "opus", "aac", "mid", "midi",
        // ドキュメント / データ
        "txt", "text", "md", "json", "xml", "html", "htm", "css", "js", "mjs", "cjs",
        "ts", "tsx", "jsx", "csv", "tsv", "pdf", "dat", "log", "cfg", "ini", "conf",
        "yml", "yaml", "sql", "db", "sqlite",
        // アーカイブ / バイナリ / 実行形式
        "zip", "rar", "7z", "lzh", "lha", "tar", "gz", "bz2", "xz", "zst", "tgz",
        "exe", "dll", "msi", "bat", "cmd", "ps1", "sh", "bash", "iso", "img", "dmg",
        "bin", "apk", "jar", "class", "so", "dylib",
        // フォント / デザイン
        "ttf", "otf", "woff", "woff2", "eot", "psd", "ai", "xcf", "clip", "sai",
        // スクリプト言語
        "php", "asp", "aspx", "jsp", "cgi", "pl", "py", "rb",
    };

    /// <summary>裸ドメイン候補のドメイン部最終ラベルがファイル拡張子と同名か
    /// (thread.js bareDomainTldIsFileExt と同じ判定)。</summary>
    private static bool IsFileExtLikeBareDomain(string domain)
    {
        var i = domain.LastIndexOf('.');
        if (i < 0) return false;
        return FileExtLikeTlds.Contains(domain[(i + 1)..]);
    }

    private static readonly Regex ImageExtRe = new(
        @"\.(jpe?g|png|gif|webp)(?:[?#]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VideoExtRe = new(
        @"\.(mp4|webm|mov)(?:[?#]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>imgur のシングル画像ページ (thread.js URL_EXPANDERS と同じ展開)。</summary>
    private static readonly Regex ImgurPageRe = new(
        @"^https?://(?:www\.|m\.)?imgur\.com/([a-zA-Z0-9]+)(?:[/?#].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>5ch 系スレ URL は媒体ではないため除外する。</summary>
    private static readonly Regex FiveChThreadRe = new(
        @"^https?://[^/]+/test/read\.cgi/", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>YouTube — thread.js YOUTUBE_RES と同じ 4 形式。媒体扱いしない。</summary>
    private static readonly Regex[] YouTubeRes =
    {
        new(@"^https?://(?:www\.|m\.)?youtube\.com/watch\?(?:[^#]*&)?v=[A-Za-z0-9_-]{11}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^https?://youtu\.be/[A-Za-z0-9_-]{11}",                                       RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^https?://(?:www\.|m\.)?youtube\.com/shorts/[A-Za-z0-9_-]{11}",               RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^https?://(?:www\.)?youtube\.com/embed/[A-Za-z0-9_-]{11}",                    RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    /// <summary>本文列からメディア URL を抽出する。
    /// 戻り値は出現順・重複排除済み。画像には x.com / pixiv 等の「非同期展開で実体が決まる」
    /// ページ URL も含む (= 展開は <see cref="MediaPrefetchService"/> の worker が担う)。</summary>
    public static (IReadOnlyList<string> Images, IReadOnlyList<string> Videos) Extract(IEnumerable<string>? bodies)
    {
        var images = new List<string>();
        var videos = new List<string>();
        if (bodies is null) return (images, videos);

        // 正規化済み URL の重複排除 (同一 URL が複数レスに出ても 1 回だけ返す)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in bodies)
        {
            if (string.IsNullOrEmpty(body)) continue;
            foreach (Match m in BodyUrlRe.Matches(body))
            {
                var url = Normalize(m.Value);
                if (url is null || !seen.Add(url)) continue;
                switch (Classify(url))
                {
                    case MediaKind.Image: images.Add(url); break;
                    case MediaKind.Video: videos.Add(url); break;
                }
            }
        }
        return (images, videos);
    }

    /// <summary>BODY_URL_RE マッチを実体 URL に正規化する。媒体扱いしない sssp アイコン記法と
    /// 拡張子っぽい裸ドメイントークンは null。</summary>
    private static string? Normalize(string u)
    {
        // sssp:// 記法 (= thread.js isSsspIcon / ssspToHttps と同じ判定):
        //   host/ の後にさらに / がある (= パスが複数セグメント) ならアイコン扱いでスキップ。
        if (u.StartsWith("sssp://", StringComparison.OrdinalIgnoreCase))
        {
            var rest  = u["sssp://".Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0 && rest.IndexOf('/', slash + 1) >= 0) return null;
            return "https://" + rest;
        }
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return u;
        // 省略形 (ttp:// 等): "://" 直前の prefix が空 or 's' 付きなら https、それ以外は http。
        var i = u.IndexOf("://", StringComparison.Ordinal);
        if (i < 0)
        {
            // 裸ドメイン形 (= thread.js bareToHttps / bareDomainTldIsFileExt と同じ判定):
            // 実体は https:// 固定。ドメイン部最終ラベルが拡張子っぽいトークンは除外。
            var domEnd = u.IndexOfAny(new[] { '/', ':' });
            var domain = domEnd < 0 ? u : u[..domEnd];
            return IsFileExtLikeBareDomain(domain) ? null : "https://" + u;
        }
        var prefix = u[..i];
        return (prefix.Length == 0 || prefix.EndsWith('s') || prefix.EndsWith('S'))
            ? "https" + u[i..]
            : "http"  + u[i..];
    }

    private static MediaKind Classify(string url)
    {
        foreach (var yt in YouTubeRes)
        {
            if (yt.IsMatch(url)) return MediaKind.None;
        }
        if (FiveChThreadRe.IsMatch(url)) return MediaKind.None;
        if (VideoExtRe.IsMatch(url)) return MediaKind.Video;
        if (ImageExtRe.IsMatch(url)) return MediaKind.Image;

        // x.com / pixiv / imgur アルバム等、非同期展開で実体画像が決まる候補。
        // (= imgur アルバムは下の ImgurPageRe の「album 対象外」分岐に先捕まりされるので先に判定する)
        if (ChBrowser.Services.Image.UrlExpander.IsAsyncExpandable(url)) return MediaKind.Image;

        // imgur シングル画像ページ → 実体 URL へ同期展開 (thread.js URL_EXPANDERS と同じ)。
        var imgur = ImgurPageRe.Match(url);
        if (imgur.Success)
        {
            var id = imgur.Groups[1].Value;
            if (!id.Equals("a", StringComparison.OrdinalIgnoreCase)
                && !id.Equals("gallery", StringComparison.OrdinalIgnoreCase)
                && !id.Equals("t", StringComparison.OrdinalIgnoreCase))
            {
                return MediaKind.Image;
            }
            return MediaKind.None; // album / gallery / tag は複数画像のため対象外
        }

        return MediaKind.None;
    }
}
