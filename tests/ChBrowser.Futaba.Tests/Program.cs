using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChBrowser.Models;
using ChBrowser.Services.Api;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ChBrowser-Futaba-Tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var uri = new Uri("https://may.2chan.net/b/res/100.htm");
var path = Path.Combine(root, "100.futaba.htm");
var cachePath = FutabaAnalysisCache.PathFor(path);
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var tests = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); tests++; Console.WriteLine("PASS " + name); }
Post P(int n, string text, int depth = 0, string? attachment = null) => new(n, "", "", "", "", text, null, null,
    new([new(text, depth, text, text)], attachment is null ? [] : [attachment], text));
byte[] Html(string first = "original text", string quoted = "original text") => Encoding.GetEncoding(932).GetBytes(
    $"<div class=thre><span class=cno>No.100</span><blockquote>{first}</blockquote></div>"
    + $"<td class=rtd><span class=cno>No.200</span><blockquote><font color=\"#789922\">&gt;{quoted}</font><br>reply</blockquote></td>");
try
{
    var textResult = FutabaQuoteAnalyzer.Analyze([P(100, "some original text"), P(200, "original", 1)]);
    Check(textResult[1].FutabaQuoteResolution is { State: "resolved" } tr && tr.ParentNumbers.SequenceEqual([100]), "indexed substring matches");
    var ambiguous = FutabaQuoteAnalyzer.Analyze([P(100, "same original"), P(150, "same original"), P(200, "original", 1)]);
    Check(ambiguous[2].FutabaQuoteResolution!.State == "unresolved", "ambiguous text does not choose a parent");
    var attached = FutabaQuoteAnalyzer.Analyze([P(100, "image", attachment: "https://example.com/1788556515329.jpg"), P(200, "1788556515329.jpg", 1)]);
    Check(attached[1].FutabaQuoteResolution!.ParentNumbers.SequenceEqual([100]), "attachment owner matches");
    var multi = FutabaQuoteAnalyzer.Analyze([P(100, "image", attachment: "https://a.test/a.jpg"), P(150, "image", attachment: "https://b.test/a.jpg"), P(200, "a.jpg", 1)]);
    Check(multi[2].FutabaQuoteResolution!.State == "unresolved", "ambiguous attachment is preserved");
    var future = FutabaQuoteAnalyzer.Analyze([P(100, "future quote", 1), P(200, "future quote")]);
    Check(future[0].FutabaQuoteResolution!.ParentNumbers.Count == 0, "future text does not resolve");
    var no = FutabaQuoteAnalyzer.Analyze([P(1432656555, "parent"), P(1432690399, "No.1432656555", 1)]);
    Check(no[1].FutabaQuoteResolution!.ParentNumbers.SequenceEqual([1432656555]), "sparse numeric reference");
    var chain = FutabaQuoteAnalyzer.Analyze([
        P(1432664524, "旅行に行く"),
        new Post(1432664980, "", "", "", "", "", null, null,
            new([new("旅行に行く", 1, "", ""), new("どこいくので？", 0, "", "")], [], "")),
        new Post(1432665033, "", "", "", "", "", null, null,
            new([new("旅行に行く", 2, "", ""), new("どこいくので？", 1, "", ""), new("ディズニー", 0, "", "")], [], "")),
        new Post(1432665126, "", "", "", "", "", null, null,
            new([new("旅行に行く", 3, "", ""), new("どこいくので？", 2, "", ""), new("ディズニー", 1, "", "")], [], ""))]);
    Check(chain.Select(p => p.FutabaQuoteResolution!.ParentNumbers).Skip(1).SelectMany(x => x).SequenceEqual([1432664524, 1432664980, 1432665033]), "context chain resolves direct parents");
    var inheritedNo = FutabaQuoteAnalyzer.Analyze([
        P(100, "ancestor"),
        new Post(200, "", "", "", "", "", null, null, new([new("No.100", 1, "", ""), new("direct parent", 0, "", "")], [], "")),
        new Post(300, "", "", "", "", "", null, null, new([new("No.100", 2, "", ""), new("direct parent", 1, "", ""), new("reply", 0, "", "")], [], ""))]);
    Check(inheritedNo[2].FutabaQuoteResolution!.ParentNumbers.SequenceEqual([200]), "deep No reference uses inherited context parent");
    var multiple = FutabaQuoteAnalyzer.Analyze([
        P(100, "first quoted source"), P(200, "second quoted source"),
        new Post(300, "", "", "", "", "", null, null, new([new("first quoted source", 1, "", ""), new("reply", 0, "", ""), new("unknown quoted source", 1, "", "")], [], ""))]);
    Check(multiple[2].FutabaQuoteResolution is { State: "resolved" } resolved && resolved.ParentNumbers.SequenceEqual([100]), "multiple quote blocks use first parent");
    var parsed = FutabaThreadClient.Parse(Encoding.GetEncoding(932).GetBytes(
        "<div class=thre><span class=cno>No.1</span><blockquote>base</blockquote></div>" +
        "<td class=rtd><span class=cno>No.2</span><blockquote><font color=\"#789922\">&gt;one<br><font color=\"#789922\">&gt;&gt;two</font><br></font>reply</blockquote></td>"), uri);
    Check(parsed[1].FutabaQuoteInfo!.Lines.Select(x => x.QuoteDepth).SequenceEqual([1, 2, 0]), "nested quote tags and br preserve depth");

    var html = Html();
    var cold = await FutabaAnalysisCache.LoadAsync(html, uri, path);
    Check(cold.Count == 2 && cold[1].FutabaQuoteResolution!.ParentNumbers.SequenceEqual([100]) && File.Exists(cachePath), "actual parser + analysis sidecar created");
    var originalCache = await File.ReadAllBytesAsync(cachePath);
    var writeTime = File.GetLastWriteTimeUtc(cachePath);
    var warm = await FutabaAnalysisCache.LoadAsync(html, uri, path);
    Check(JsonSerializer.Serialize(cold, jsonOptions) == JsonSerializer.Serialize(warm, jsonOptions)
        && File.GetLastWriteTimeUtc(cachePath) == writeTime, "warm hit does not rewrite/reanalyze");

    var changed = await FutabaAnalysisCache.LoadAsync(Html("modified text"), uri, path);
    Check(changed[1].FutabaQuoteResolution!.State == "unresolved", "source hash invalidates same-size edit");
    await File.WriteAllBytesAsync(cachePath, originalCache);
    var otherUri = new Uri("https://jun.2chan.net/b/res/100.htm");
    await FutabaAnalysisCache.LoadAsync(html, otherUri, path);
    Check(JsonNode.Parse(await File.ReadAllTextAsync(cachePath))!["pageUrl"]!.GetValue<string>() == otherUri.AbsoluteUri, "host/page identity invalidates cache");
    await File.WriteAllBytesAsync(cachePath, originalCache);
    var old = JsonNode.Parse(originalCache)!;
    old["version"] = -1;
    await File.WriteAllTextAsync(cachePath, old.ToJsonString());
    await FutabaAnalysisCache.LoadAsync(html, uri, path);
    Check(JsonNode.Parse(await File.ReadAllTextAsync(cachePath))!["version"]!.GetValue<int>() == FutabaAnalysisCache.Version, "schema version invalidates cache");
    await File.WriteAllTextAsync(cachePath, "{broken");
    Check((await FutabaAnalysisCache.LoadAsync(html, uri, path)).Count == 2, "corrupt JSON falls back to HTML");
    old = JsonNode.Parse(originalCache)!;
    old["posts"]![1]!["futabaQuoteResolution"] = null;
    await File.WriteAllTextAsync(cachePath, old.ToJsonString());
    Check((await FutabaAnalysisCache.LoadAsync(html, uri, path))[1].FutabaQuoteResolution is not null, "incomplete analysis is rebuilt");
    var blocked = Path.Combine(root, "blocked.htm");
    Directory.CreateDirectory(FutabaAnalysisCache.PathFor(blocked));
    Check((await FutabaAnalysisCache.LoadAsync(html, uri, blocked)).Count == 2, "cache write failure still returns posts");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await FutabaAnalysisCache.LoadAsync(html, uri, path, cancelled.Token); throw new Exception("cancellation ignored"); }
    catch (OperationCanceledException) { Check(true, "cancellation propagates"); }
    await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => FutabaAnalysisCache.LoadAsync(Html("concurrent text", "concurrent text"), uri, path)));
    Check((await FutabaAnalysisCache.LoadAsync(Html("concurrent text", "concurrent text"), uri, path)).Count == 2
        && Directory.GetFiles(root, "*.tmp").Length == 0, "concurrent sidecar writes are atomic");

    if (args.Length > 0)
    {
        var sampleBytes = await File.ReadAllBytesAsync(args[0]);
        var sampleUri = new Uri("https://may.2chan.net/b/res/1432656555.htm");
        var samplePath = Path.Combine(root, "sample.htm");
        var watch = Stopwatch.StartNew();
        var sample = await FutabaAnalysisCache.LoadAsync(sampleBytes, sampleUri, samplePath);
        var coldMs = watch.ElapsedMilliseconds;
        watch.Restart();
        await FutabaAnalysisCache.LoadAsync(sampleBytes, sampleUri, samplePath);
        Console.WriteLine($"SAMPLE posts={sample.Count} coldParseAnalyzeWriteMs={coldMs} warmHashReadMs={watch.ElapsedMilliseconds}");
        Check(sample.All(p => p.FutabaQuoteResolution is not null), "sample resolutions complete");
        if (args.Length > 1) await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(sample, jsonOptions));
    }
    Console.WriteLine($"PASS total={tests}");
}
finally
{
    if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe test path");
    Directory.Delete(root, recursive: true);
}
