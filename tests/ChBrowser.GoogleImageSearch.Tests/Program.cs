using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ChBrowser.Services.Image;
using ChBrowser.Services.Storage;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static async Task Rejected(Func<Task> action)
{
    try { await action(); } catch (InvalidDataException) { return; }
    throw new Exception("Invalid image/source accepted");
}
static async Task Closed(HttpClient browser, string url)
{
    try { using var response = await browser.GetAsync(url); Check(!response.IsSuccessStatusCode, "Replay succeeded"); }
    catch (HttpRequestException) { }
}
static async Task<string> Page(HttpClient browser, GoogleImageSearchService.UploadTicket ticket, byte[] bytes, string mime, string filename)
{
    using var response = await browser.GetAsync(ticket.Url);
    Check(response.StatusCode == HttpStatusCode.OK, "authorized GET");
    Check(response.Headers.CacheControl?.NoStore == true, "no-store");
    Check(response.Headers.GetValues("Referrer-Policy").Single() == "no-referrer", "no-referrer");
    Check(response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "nosniff");
    Check(response.Headers.GetValues("X-Frame-Options").Single() == "DENY", "DENY");
    Check(!response.Headers.Contains("Access-Control-Allow-Origin"), "no CORS");
    var html = await response.Content.ReadAsStringAsync();
    var nonce = Regex.Match(html, "<script nonce=\"([^\"]+)\"").Groups[1].Value;
    Check(nonce.Length >= 32, "random nonce");
    Check(response.Headers.GetValues("Content-Security-Policy").Single() == $"default-src 'none'; script-src 'nonce-{nonce}'; form-action https://lens.google.com https://www.google.com; frame-ancestors 'none'; base-uri 'none'", "nonce CSP");
    Check(html.Contains("action=\"https://lens.google.com/v3/upload?hl=ja&amp;ep=ccm\"")
        && html.Contains("method=\"POST\"") && html.Contains("enctype=\"multipart/form-data\""), "multipart form target");
    Check(html.Contains("type=\"file\" name=\"encoded_image\"") && html.Contains("new DataTransfer()")
        && html.Contains("new File([bytes], '" + filename + "', {type: '" + mime + "'}")
        && html.Contains(".files = transfer.files") && html.Contains("form.submit()"), "browser File upload");
    var encoded = Regex.Match(html, "atob\\('([^']+)'\\)").Groups[1].Value;
    Check(Convert.FromBase64String(encoded).SequenceEqual(bytes), "exact bytes");
    Check(html.Contains("Googleへ画像を送信") && html.Contains("<noscript>"), "manual fallback");
    Check(!html.Contains("uploadbyurl") && !html.Contains("www.google.com/search"), "no cross-client result URL");
    await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    await Closed(browser, ticket.Url);
    return html;
}

var png = new byte[] { 137,80,78,71,13,10,26,10,1,2,3 };
var fixtureBase = Path.GetFullPath(Environment.GetEnvironmentVariable("TMPDIR") ?? AppContext.BaseDirectory);
var root = Path.Combine(fixtureBase, "google-image-test-" + Guid.NewGuid());
Directory.CreateDirectory(root);
using var browser = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
try
{
    var local = Path.Combine(root, "private-original-name.png");
    await File.WriteAllBytesAsync(local, png);
    GoogleImageSearchService.UploadTicket ticket;
    using (var service = new GoogleImageSearchService()) ticket = await service.PrepareUploadAsync(local);
    using (ticket)
    {
        var uri = new Uri(ticket.Url);
        Check(uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port > 0
            && Regex.IsMatch(uri.AbsolutePath, "^/[0-9A-F]{64}$"), "random loopback ticket and independent lifetime");
        using (var wrong = await browser.GetAsync(new Uri(uri, "/wrong"))) Check(wrong.StatusCode == HttpStatusCode.NotFound, "wrong route");
        using (var wrong = await browser.PostAsync(ticket.Url, new StringContent(""))) Check(wrong.StatusCode == HttpStatusCode.NotFound, "wrong method");
        using (var wrong = await browser.GetAsync(ticket.Url + "?query")) Check(wrong.StatusCode == HttpStatusCode.NotFound, "exact route");
        using (var request = new HttpRequestMessage(HttpMethod.Get, ticket.Url))
        { request.Headers.Host = "foreign.example:" + uri.Port; using var wrong = await browser.SendAsync(request); Check(wrong.StatusCode == HttpStatusCode.NotFound, "exact Host"); }
        foreach (var badHeaders in new[] { $"Host: {uri.Authority}\r\nHost: {uri.Authority}", $"Host: {uri.Authority}\r\nBad Name: value", "X: no-host" })
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, uri.Port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET {uri.AbsolutePath} HTTP/1.1\r\n{badHeaders}\r\n\r\n"));
            using var reader = new StreamReader(client.GetStream());
            Check((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3))) == "HTTP/1.1 404 Not Found", "malformed headers do not consume ticket");
        }
        var html = await Page(browser, ticket, png, "image/png", "image.png");
        Check(!html.Contains(local) && !html.Contains("private-original-name") && !html.Contains(root), "private source absent");
    }
    Console.WriteLine("PASS: relay protocol, rejected requests, replay, service disposal lifetime");

    using (var cache = new ImageCacheService(new DataPaths(Path.Combine(root, "data"))))
    {
        const string url = "https://cached.example/private-image";
        await cache.SaveAsync(url, new MemoryStream(png), "image/png");
        var get = new RecordingHandler(Encoding.ASCII.GetBytes("not the cache"));
        using var service = new GoogleImageSearchService(get);
        using var cached = await service.PrepareUploadAsync(url, cache);
        var html = await Page(browser, cached, png, "image/png", "image.png");
        Check(get.Calls == 0 && !html.Contains(url), "cache bytes without remote GET");
    }
    var download = new RecordingHandler(png);
    using (var service = new GoogleImageSearchService(download))
    using (var remote = await service.PrepareUploadAsync("https://cdn.example/private.png"))
    {
        var html = await Page(browser, remote, png, "image/png", "image.png");
        Check(download.Calls == 1 && !html.Contains("cdn.example"), "fallback downloader only");
    }
    using (var service = new GoogleImageSearchService())
    using (var file = await service.PrepareUploadAsync(new Uri(local).AbsoluteUri))
        await Page(browser, file, png, "image/png", "image.png");
    foreach (var (name, bytes, mime, filename) in new[] {
        ("jpeg", new byte[] { 255,216,255,0 }, "image/jpeg", "image.jpg"),
        ("gif", Encoding.ASCII.GetBytes("GIF89a"), "image/gif", "image.gif"),
        ("webp", Encoding.ASCII.GetBytes("RIFFxxxxWEBP"), "image/webp", "image.webp") })
    {
        var path = Path.Combine(root, name + ".img"); await File.WriteAllBytesAsync(path, bytes);
        using var service = new GoogleImageSearchService();
        using var image = await service.PrepareUploadAsync(path);
        await Page(browser, image, bytes, mime, filename);
    }
    Console.WriteLine("PASS: cache, local/file URI, downloader and PNG/JPEG/GIF/WebP");

    foreach (var invalid in new[] { "\\\\server\\share\\x.png", "file://server/share/x.png", "", "https://user:pass@example.test/image", "ftp://example.test/image" })
    { using var service = new GoogleImageSearchService(); await Rejected(async () => { using var unused = await service.PrepareUploadAsync(invalid); }); }
    await File.WriteAllTextAsync(Path.Combine(root, "not.png"), "<html>");
    using (var service = new GoogleImageSearchService())
        await Rejected(async () => { using var unused = await service.PrepareUploadAsync(Path.Combine(root, "not.png")); });
    var tooBig = new byte[20 * 1024 * 1024 + 1]; png.CopyTo(tooBig, 0);
    await File.WriteAllBytesAsync(Path.Combine(root, "large.png"), tooBig);
    using (var service = new GoogleImageSearchService())
        await Rejected(async () => { using var unused = await service.PrepareUploadAsync(Path.Combine(root, "large.png")); });
    foreach (var (bytes, unknownLength) in new[] { (Encoding.ASCII.GetBytes("<html>"), false), (tooBig, false), (tooBig, true) })
    {
        using var service = new GoogleImageSearchService(new RecordingHandler(bytes, unknownLength));
        await Rejected(async () => { using var unused = await service.PrepareUploadAsync("https://fixture.example/image"); });
    }
    foreach (var handler in new HttpMessageHandler[] { new StallHandler(), new StalledBodyHandler() })
    {
        using var service = new GoogleImageSearchService(handler, timeout: TimeSpan.FromMilliseconds(80));
        try { using var unused = await service.PrepareUploadAsync("https://fixture.example/stall"); throw new Exception("timeout failed"); }
        catch (OperationCanceledException) { }
    }
    using (var ct = new CancellationTokenSource())
    using (var service = new GoogleImageSearchService())
    {
        ct.Cancel();
        try { using var unused = await service.PrepareUploadAsync(local, cancellationToken: ct.Token); throw new Exception("cancellation failed"); }
        catch (OperationCanceledException) { }
    }
    Console.WriteLine("PASS: invalid/UNC/non-image/oversize/unknown length/stalls/cancellation");

    using (var service = new GoogleImageSearchService(relayLifetime: TimeSpan.FromMilliseconds(150)))
    using (var expired = await service.PrepareUploadAsync(local))
    { await expired.Completion.WaitAsync(TimeSpan.FromSeconds(3)); await Closed(browser, expired.Url); }
    using (var service = new GoogleImageSearchService())
    {
        var cancelled = await service.PrepareUploadAsync(local);
        using var distinct = await service.PrepareUploadAsync(local);
        Check(new Uri(cancelled.Url).AbsolutePath != new Uri(distinct.Url).AbsolutePath, "fresh random token per ticket");
        distinct.Dispose();
        await distinct.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        cancelled.Dispose();
        await cancelled.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        cancelled.Dispose();
        await Closed(browser, cancelled.Url);
    }
    using (var ct = new CancellationTokenSource())
    using (var service = new GoogleImageSearchService())
    using (var cancelled = await service.PrepareUploadAsync(local, cancellationToken: ct.Token))
    { ct.Cancel(); await cancelled.Completion.WaitAsync(TimeSpan.FromSeconds(3)); await Closed(browser, cancelled.Url); }
    using (var service = new GoogleImageSearchService(requestTimeout: TimeSpan.FromMilliseconds(120)))
    using (var robust = await service.PrepareUploadAsync(local))
    {
        var uri = new Uri(robust.Url);
        using (var slow = new TcpClient())
        {
            await slow.ConnectAsync(IPAddress.Loopback, uri.Port);
            await slow.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /"));
            var b = new byte[1];
            Check(await slow.GetStream().ReadAsync(b).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0, "slow client bounded");
        }
        using (var malformed = new TcpClient())
        {
            await malformed.ConnectAsync(IPAddress.Loopback, uri.Port);
            await malformed.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX: " + new string('a', 8192)));
            var b = new byte[1];
            try { Check(await malformed.GetStream().ReadAsync(b).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0, "header size bounded"); }
            catch (IOException) { }
        }
        await Page(browser, robust, png, "image/png", "image.png");
    }
    Console.WriteLine("PASS: expiry/dispose/caller cancellation and stalled/oversized header recovery");
    Console.WriteLine("GoogleImageSearchService checks passed (local fixtures only; no Google calls).");
}
finally
{
    var fullRoot = Path.GetFullPath(root);
    Check(fullRoot.StartsWith(fixtureBase.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(fullRoot).StartsWith("google-image-test-", StringComparison.Ordinal), "cleanup stays inside fixture directory");
    Directory.Delete(fullRoot, true);
}

sealed class RecordingHandler(byte[] bytes, bool unknownLength = false) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get) throw new Exception("Only downloader GET allowed");
        Calls++;
        HttpContent content = unknownLength ? new StreamContent(new NonSeekableStream(bytes)) : new ByteArrayContent(bytes);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
{ public override bool CanSeek => false; }
sealed class StallHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); }
}
sealed class StalledBodyHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledReadStream()) });
}
sealed class StalledReadStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
