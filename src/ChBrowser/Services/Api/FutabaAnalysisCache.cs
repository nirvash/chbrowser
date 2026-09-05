using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ChBrowser.Models;
using ChBrowser.Services.Logging;

namespace ChBrowser.Services.Api;

/// <summary>Disposable sidecar for both parsed HTML and quote resolutions.
/// HTML remains the source of truth. Bump Version for parser/resolver semantic changes.</summary>
internal static class FutabaAnalysisCache
{
    internal const int Version = 6;
    internal static string PathFor(string htmlPath) => htmlPath + ".analysis.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Snapshot(int Version, string SourceHash, string PageUrl, IReadOnlyList<Post> Posts);

    internal static Task<IReadOnlyList<Post>> LoadAsync(byte[] html, Uri pageUri, string htmlPath, CancellationToken ct = default)
        // Parsing, hashing and deserialization must never run synchronously on the WPF caller.
        => Task.Run(() => LoadCoreAsync(html, pageUri, htmlPath, ct), ct);

    private static async Task<IReadOnlyList<Post>> LoadCoreAsync(byte[] html, Uri pageUri, string htmlPath, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var hash = Convert.ToHexString(SHA256.HashData(html));
        var path = PathFor(htmlPath);
        try
        {
            await using var stream = File.OpenRead(path);
            var cached = await JsonSerializer.DeserializeAsync<Snapshot>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (cached is not null && cached.Version == Version && cached.SourceHash == hash
                && cached.PageUrl == pageUri.AbsoluteUri && IsValid(cached.Posts))
            {
                LogService.Instance.Write($"[futabaAnalysis] hit {pageUri.AbsoluteUri} posts={cached.Posts.Count} elapsedMs={timer.ElapsedMilliseconds}");
                return cached.Posts;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { /* Missing, damaged, or inaccessible derived data is regenerated from HTML. */ }

        ct.ThrowIfCancellationRequested();
        var posts = FutabaQuoteAnalyzer.Analyze(FutabaThreadClient.Parse(html, pageUri), ct);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, new Snapshot(Version, hash, pageUri.AbsoluteUri, posts), JsonOptions, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Instance.Write($"[futabaAnalysis] cache write skipped {pageUri.AbsoluteUri}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        LogService.Instance.Write($"[futabaAnalysis] rebuilt {pageUri.AbsoluteUri} posts={posts.Count} elapsedMs={timer.ElapsedMilliseconds}");
        return posts;
    }

    private static bool IsValid(IReadOnlyList<Post>? posts)
    {
        if (posts is null) return false;
        var seen = new HashSet<int>();
        foreach (var post in posts)
        {
            if (post is null || !seen.Add(post.Number) || post.Body is null
                || post.FutabaQuoteInfo is not { Lines: not null, AttachmentUrls: not null } info
                || info.Lines.Any(l => l is null || l.Text is null || l.OriginalText is null)
                || post.FutabaQuoteResolution is not { ParentNumbers: not null, Evidence: not null, AmbiguousLines: not null } result
                || result.Number != post.Number || result.State is not ("resolved" or "ambiguous" or "unresolved")) return false;
        }
        return true;
    }
}
