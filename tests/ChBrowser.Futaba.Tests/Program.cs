using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChBrowser.Models;
using ChBrowser.Services.Api;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var tests = 0;
var threadToolbar = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ChBrowser/Views/Panes/ThreadDisplayPane.xaml")));
if (!threadToolbar.Contains("次スレ検索", StringComparison.Ordinal))
    throw new Exception("RED: toolbar must expose the next-thread search action");
tests++; Console.WriteLine("PASS toolbar exposes next-thread search");
var toolbarXml = System.Xml.Linq.XDocument.Parse(threadToolbar);
var searchButton = toolbarXml.Descendants().Single(e => e.Name.LocalName == "Button"
    && (string?)e.Attribute("Click") == "NavSearchButton_Click");
if (!double.TryParse((string?)searchButton.Attribute("Width"), out var searchButtonWidth) || searchButtonWidth < 55)
    throw new Exception("search button must fit its full label instead of the 28px icon width");
tests++; Console.WriteLine("PASS search button fits its full label");
var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ChBrowser-Futaba-Tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var uri = new Uri("https://may.2chan.net/b/res/100.htm");
var path = Path.Combine(root, "100.futaba.htm");
var cachePath = FutabaAnalysisCache.PathFor(path);
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
void Check(bool ok, string name) { if (!ok) throw new Exception(name); tests++; Console.WriteLine("PASS " + name); }
Post P(int n, string text, int depth = 0, string? attachment = null) => new(n, "", "", "", "", text, null, null,
    new([new(text, depth, text, text)], attachment is null ? [] : [attachment], text));
byte[] Html(string first = "original text", string quoted = "original text") => Encoding.GetEncoding(932).GetBytes(
    $"<div class=thre><span class=cno>No.100</span><blockquote>{first}</blockquote></div>"
    + $"<td class=rtd><span class=cno>No.200</span><blockquote><font color=\"#789922\">&gt;{quoted}</font><br>reply</blockquote></td>");
try
{
    Check(FutabaCatalogClient.CatalogGridColumns == 14
        && FutabaCatalogClient.CatalogGridRows == 50
        && FutabaCatalogClient.CatalogSettingsCookie == "cxyl=14x50x100x0x0", "catalog fetch grid covers 700 threads");
    Check(FutabaThreadClient.ExtractExpiryText(Encoding.GetEncoding(932).GetBytes("<span class=cnw>26/09/06(日)05:00:01</span> 09:06頃消えます")) == "09:06頃消えます", "Futaba expiry time is extracted from OP metadata");
    Check(FutabaThreadClient.ExtractExpiryText(Encoding.GetEncoding(932).GetBytes("<span class=cnw>26/09/06(日)05:00:01</span> 01日09:06頃消えます")) == "01日09:06頃消えます", "Futaba expiry time retains its day");
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
    var spanning = FutabaQuoteAnalyzer.Analyze([
        new Post(2613, "", "", "", "", "", null, null,
            new([new("昨日脳内物質おじさんをsolで出力してたから", 0, "", ""), new("Astra版と比べてみよう", 0, "", ""), new("solと", 0, "", "")], [], "")),
        P(2641, "Astra"),
        new Post(8487, "", "", "", "", "", null, null,
            new([new("昨日脳内物質おじさんをsolで出力してたから", 1, "", ""), new("Astra版と比べてみよう", 1, "", ""), new("solと", 1, "", ""), new("Astra", 1, "", ""), new("一気に哀愁漂う感じになったな", 0, "", ""), new("すき", 0, "", "")], [], ""))]);
    var spanningResult = spanning[2].FutabaQuoteResolution;
    Check(spanningResult is not null && spanningResult.State == "resolved"
        && spanningResult!.ParentNumbers.SequenceEqual([2613])
        && spanningResult.Blocks?.FirstOrDefault()?.ParentNumber == 2613, "spanning quote block uses first source parent");
    var parsed = FutabaThreadClient.Parse(Encoding.GetEncoding(932).GetBytes(
        "<div class=thre><span class=cno>No.1</span><blockquote>base</blockquote></div>" +
        "<td class=rtd><span class=cno>No.2</span><blockquote><font color=\"#789922\">&gt;one<br><font color=\"#789922\">&gt;&gt;two</font><br></font>reply</blockquote></td>"), uri);
    Check(parsed[1].FutabaQuoteInfo!.Lines.Select(x => x.QuoteDepth).SequenceEqual([1, 2, 0]), "nested quote tags and br preserve depth");
    var idParsed = FutabaThreadClient.Parse(Encoding.GetEncoding(932).GetBytes(
        "<div class=thre><span class=cnw>26/09/06(土)02:13:40 ID:Qr4bAJCk</span><span class=cno>No.1</span><blockquote>base</blockquote></div>"), uri);
    Check(idParsed[0].Id == "Qr4bAJCk" && idParsed[0].DateText == "2026/09/06(土) 02:13:40", "Futaba post ID is extracted separately from the date");

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
    var catalogBoard = new Board("b", "fixture", "https://may.2chan.net/b/", "", 0);
    var catalogHandler = new SequenceHttpHandler(
        Encoding.GetEncoding(932).GetBytes("<td><a href=\"res/100.htm\"><img src=\"/b/cat/100s.jpg\"></a><br><small>Same daily thread old</small><br><font size=2>10</font></td>"),
        Encoding.GetEncoding(932).GetBytes("<td><a href=\"res/200.htm\"><img src=\"/b/cat/200s.jpg\"></a><br><small>Same daily thread new</small><br><font size=2>1</font></td>"));
    using (var catalogHttp = new MonazillaClient(catalogHandler))
    {
        var catalogClient = new FutabaCatalogClient(catalogHttp);
        var oldCatalog = await catalogClient.FetchAsync(catalogBoard);
        var refreshedCatalog = await catalogClient.FetchAsync(catalogBoard);
        Check(catalogHandler.RequestCount == 2 && oldCatalog.Any(x => x.Key == "100")
            && refreshedCatalog.Any(x => x.Key == "200"), "catalog refresh fetches current fixture after an earlier result");
    }
    var navFetchHandler = new SequenceHttpHandler(
        Encoding.GetEncoding(932).GetBytes("<td><a href=\"res/100.htm\"><img src=\"/b/cat/100s.jpg\"></a><br><small>Same daily thread old</small><br><font size=2>10</font></td>"),
        Encoding.GetEncoding(932).GetBytes("<td><a href=\"res/200.htm\"><img src=\"/b/cat/200s.jpg\"></a><br><small>Same daily thread next day</small><br><font size=2>1</font></td>"));
    using (var navHttp = new MonazillaClient(navFetchHandler))
    {
        var navMain = (ChBrowser.ViewModels.MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ChBrowser.ViewModels.MainViewModel));
        var paths = new ChBrowser.Services.Storage.DataPaths(Path.Combine(root, "nav-data"));
        typeof(ChBrowser.ViewModels.MainViewModel).GetField("_subjectClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(navMain, new SubjectTxtClient(navHttp, paths));
        var navCache = new Dictionary<(string Host, string Dir), (DateTimeOffset FetchedAt, IReadOnlyList<ThreadInfo> Subjects)>();
        typeof(ChBrowser.ViewModels.MainViewModel).GetField("_navSubjectCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(navMain, navCache);
        var getNavSubjects = typeof(ChBrowser.ViewModels.MainViewModel).GetMethod("GetSubjectsForNavAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task<IReadOnlyList<ThreadInfo>> FetchNavSubjects(bool force)
            => await (Task<IReadOnlyList<ThreadInfo>>)getNavSubjects.Invoke(navMain, [catalogBoard, force])!;
        var cachedOld = await FetchNavSubjects(false);
        var forcedNew = await FetchNavSubjects(true);
        Check(cachedOld.Any(x => x.Key == "100") && forcedNew.Any(x => x.Key == "200")
            && navFetchHandler.RequestCount == 2, "manual refresh bypasses warm nav cache and finds next-day catalog thread");
    }
    using (var failureHttp = new MonazillaClient(new ThrowingHttpHandler()))
    {
        var failureMain = (ChBrowser.ViewModels.MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ChBrowser.ViewModels.MainViewModel));
        var paths = new ChBrowser.Services.Storage.DataPaths(Path.Combine(root, "failure-data"));
        typeof(ChBrowser.ViewModels.MainViewModel).GetField("_subjectClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(failureMain, new SubjectTxtClient(failureHttp, paths));
        typeof(ChBrowser.ViewModels.MainViewModel).GetField("_threadIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(failureMain, new ChBrowser.Services.Storage.ThreadIndexService(paths));
        var oldState = new Dictionary<(string Host, string Dir), (DateTimeOffset FetchedAt, IReadOnlyList<ThreadInfo> Subjects)>
        {
            [(catalogBoard.Host, catalogBoard.DirectoryName)] = (DateTimeOffset.UtcNow, [new ThreadInfo("100", "Same daily thread old", 10, 1)])
        };
        typeof(ChBrowser.ViewModels.MainViewModel).GetField("_navSubjectCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(failureMain, oldState);
        var getNavSubjects = typeof(ChBrowser.ViewModels.MainViewModel).GetMethod("GetSubjectsForNavAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            await (Task<IReadOnlyList<ThreadInfo>>)getNavSubjects.Invoke(failureMain, [catalogBoard, true])!;
            throw new Exception("forced search must propagate network failure");
        }
        catch (HttpRequestException) { Check(oldState[(catalogBoard.Host, catalogBoard.DirectoryName)].Subjects[0].Key == "100", "refresh failure retains cached candidates without disk fallback"); }
        var failureTab = new ChBrowser.ViewModels.ThreadTabViewModel(catalogBoard, new ThreadInfo("100", "Same daily thread old", 10, 1), _ => { })
        {
            NextNavKey = "150",
            NextNavTitle = "previous candidate",
            IsNextNavConfirmed = true,
            NextNavCandidates = [new ChBrowser.ViewModels.ThreadNavCandidate("150", "previous candidate", 1, 10)]
        };
        await failureMain.SearchNextThreadAsync(failureTab);
        Check(failureTab.NextNavKey == "150" && failureTab.IsNextNavConfirmed
            && failureTab.StatusMessage.Contains("失敗", StringComparison.Ordinal), "refresh failure reports error and preserves adopted next target");
    }
    var successHandler = new SequenceHttpHandler(
        Encoding.GetEncoding(932).GetBytes("<td><a href=\"res/1433000200.htm\"><img src=\"/b/cat/200s.jpg\"></a><br><small>毎日21時の読書会スレッド 10/08</small><br><font size=2>1</font></td>"),
        Encoding.GetEncoding(932).GetBytes("<td><a href=\"res/1433000300.htm\"><img src=\"/b/cat/300s.jpg\"></a><br><small>別の話題</small><br><font size=2>1</font></td>"));
    using (var successHttp = new MonazillaClient(successHandler))
    {
        var main = (ChBrowser.ViewModels.MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ChBrowser.ViewModels.MainViewModel));
        var paths = new ChBrowser.Services.Storage.DataPaths(Path.Combine(root, "success-data"));
        typeof(ChBrowser.ViewModels.MainViewModel).GetField("_subjectClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, new SubjectTxtClient(successHttp, paths));
        typeof(ChBrowser.ViewModels.MainViewModel).GetField("_threadIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, new ChBrowser.Services.Storage.ThreadIndexService(paths));
        typeof(ChBrowser.ViewModels.MainViewModel).GetField("_navSubjectCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, new Dictionary<(string Host, string Dir), (DateTimeOffset FetchedAt, IReadOnlyList<ThreadInfo> Subjects)>
            {
                [(catalogBoard.Host, catalogBoard.DirectoryName)] = (DateTimeOffset.UtcNow,
                    [new ThreadInfo("1433000100", "毎日21時の読書会スレッド 10/07", 1, 1)])
            });
        var oldTab = new ChBrowser.ViewModels.ThreadTabViewModel(catalogBoard,
            new ThreadInfo("1433000100", "毎日21時の読書会スレッド 10/07", 1, 1), _ => { });
        oldTab.AppendPosts([P(1433000100, "前日の保存済み本文")]);
        oldTab.NavResolvedAtPostCount = oldTab.Posts.Count;
        await main.SearchNextThreadAsync(oldTab);
        Check(oldTab.NextNavKey == "1433000200" && oldTab.NextNavCandidates?.Single().Key == "1433000200"
            && successHandler.RequestCount == 1 && oldTab.Posts.Count == 1
            && oldTab.ThreadKey == "1433000100" && !oldTab.IsNavSearchInProgress
            && oldTab.StatusMessage.Contains("更新", StringComparison.Ordinal),
            "search from expired previous-day cached thread resolves new catalog candidate without reloading old thread or navigating");
        await main.SearchNextThreadAsync(oldTab);
        Check(oldTab.NextNavKey is null && oldTab.NextNavCandidates?.Count == 0
            && successHandler.RequestCount == 2 && !oldTab.IsNavSearchInProgress
            && oldTab.StatusMessage.Contains("見つかりません", StringComparison.Ordinal),
            "manual search clears unconfirmed stale target and reports no matching current thread");
    }
    var queuedMain = (ChBrowser.ViewModels.MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ChBrowser.ViewModels.MainViewModel));
    var queuedTab = new ChBrowser.ViewModels.ThreadTabViewModel(catalogBoard, new ThreadInfo("100", "Same daily thread old", 10, 1), _ => { }) { NavResolving = true };
    await queuedMain.SearchNextThreadAsync(queuedTab);
    Check(queuedTab.NavSearchRequested && queuedTab.IsNavSearchInProgress, "search request during automatic resolution is queued");
    var duplicateTab = new ChBrowser.ViewModels.ThreadTabViewModel(catalogBoard, new ThreadInfo("100", "Same daily thread old", 10, 1), _ => { }) { NavResolving = true, IsNavSearchInProgress = true };
    await queuedMain.SearchNextThreadAsync(duplicateTab);
    Check(!duplicateTab.NavSearchRequested, "duplicate search while active is ignored");
    var tab = new ChBrowser.ViewModels.ThreadTabViewModel(catalogBoard, new ThreadInfo("100", "Same daily thread old", 1, 1), _ => { });
    Check(tab.CanSearchNextThread, "next-thread search enabled for an open tab");
    tab.IsNavSearchInProgress = true;
    Check(!tab.CanSearchNextThread, "next-thread search disabled while searching");
    Console.WriteLine($"PASS total={tests}");
}
finally
{
    if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe test path");
    Directory.Delete(root, recursive: true);
}

sealed class SequenceHttpHandler(params byte[][] responses) : HttpMessageHandler
{
    private readonly Queue<byte[]> _responses = new(responses);
    public int RequestCount { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var bytes = _responses.Dequeue();
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        });
    }
}

sealed class ThrowingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture unavailable"));
}