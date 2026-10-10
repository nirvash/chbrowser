using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ChBrowser.Services.Image;

/// <summary>Prepares a one-use local page so the browser uploads image bytes to Google Lens.</summary>
public sealed class GoogleImageSearchService : IDisposable
{
    public const string UploadEndpoint = "https://lens.google.com/v3/upload?hl=ja&ep=ccm";
    private const long MaxImageBytes = 20 * 1024 * 1024;
    private readonly HttpClient _downloader;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _relayLifetime;
    private readonly TimeSpan _requestTimeout;

    public GoogleImageSearchService(HttpMessageHandler? downloadHandler = null, TimeSpan? timeout = null,
        TimeSpan? relayLifetime = null, TimeSpan? requestTimeout = null)
    {
        _downloader = new HttpClient(downloadHandler ?? new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }, true)
            { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _relayLifetime = relayLifetime ?? TimeSpan.FromSeconds(120);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<UploadTicket> PrepareUploadAsync(string source, ImageCacheService? cache = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var ct = timeout.Token;
        var (bytes, mime) = await ReadImageAsync(source, cache, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new UploadTicket(bytes, mime, _relayLifetime, _requestTimeout, cancellationToken);
    }

    /// <summary>Owns the relay independently of the downloader. Cancel only if launching fails.</summary>
    public sealed class UploadTicket : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly object _gate = new();
        private bool _finished;
        public string Url { get; }
        public Task Completion { get; }

        internal UploadTicket(byte[] bytes, string mime, TimeSpan lifetime, TimeSpan requestTimeout, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                _stop.CancelAfter(lifetime);
                listener.Start(); // Start errors reach the UI before a ticket is returned.
                var host = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                var route = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                Url = "http://" + host + route;
                Completion = RunAsync(listener, host, route, bytes, mime, requestTimeout);
            }
            catch { listener.Stop(); _stop.Dispose(); throw; }
        }

        public void Dispose()
        {
            lock (_gate) { if (!_finished) _stop.Cancel(); }
        }

        private async Task RunAsync(TcpListener listener, string host, string route, byte[]? bytes, string mime, TimeSpan requestTimeout)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    request.CancelAfter(requestTimeout);
                    try
                    {
                        await using var stream = client.GetStream();
                        var header = await ReadHeaderAsync(stream, request.Token).ConfigureAwait(false);
                        if (!Authorized(header, host, route))
                        {
                            await RespondAsync(stream, "404 Not Found", Array.Empty<byte>(), null, request.Token).ConfigureAwait(false);
                            continue;
                        }
                        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
                        var html = Encoding.UTF8.GetBytes(BuildHtml(bytes!, mime, nonce));
                        try { await RespondAsync(stream, "200 OK", html, nonce, request.Token).ConfigureAwait(false); }
                        finally { Array.Clear(html); }
                        break; // Consume only after an authorized response was fully written.
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException)
                    { /* Malformed/stalled clients do not consume the ticket. */ }
                }
            }
            catch (Exception) { /* Background relay failures are observed here; never call UI from this task. */ }
            finally
            {
                listener.Stop();
                if (bytes is not null) Array.Clear(bytes);
                bytes = null;
                lock (_gate) { _finished = true; _stop.Dispose(); }
            }
        }

        private static async Task<string> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
                if (read == 0) throw new InvalidDataException("Incomplete request.");
                count += read;
                var header = Encoding.ASCII.GetString(buffer, 0, count);
                var end = header.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0) return header[..end];
            }
            throw new InvalidDataException("Request header too large.");
        }

        private static bool Authorized(string header, string host, string route)
        {
            var lines = header.Split("\r\n", StringSplitOptions.None);
            if (lines[0] != $"GET {route} HTTP/1.1") return false;
            var hosts = 0;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || char.IsWhiteSpace(line[0])) return false;
                var name = line[..colon];
                if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c))
                    || line.Any(c => c < 32 && c != '\t' || c == 127)) return false;
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                { if (value != host || ++hosts != 1) return false; }
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0")) return false;
            }
            return hosts == 1;
        }

        private static async Task RespondAsync(NetworkStream stream, string status, byte[] body, string? nonce, CancellationToken ct)
        {
            var csp = nonce is null ? "default-src 'none'" : $"default-src 'none'; script-src 'nonce-{nonce}'; form-action https://lens.google.com https://www.google.com; frame-ancestors 'none'; base-uri 'none'";
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nContent-Security-Policy: {csp}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }

        private static string BuildHtml(byte[] bytes, string mime, string nonce) => $$"""
            <!doctype html><html lang="ja"><meta charset="utf-8"><title>Googleで画像検索</title>
            <form id="upload" action="{{UploadEndpoint.Replace("&", "&amp;")}}" method="POST" enctype="multipart/form-data">
            <input id="image" type="file" name="encoded_image" hidden>
            <button id="send" type="button" disabled>Googleへ画像を送信</button></form>
            <p id="status">画像をGoogleへ送信します。</p>
            <noscript>画像の送信にはJavaScriptが必要です。有効にして、アプリから画像検索をやり直してください。</noscript>
            <script nonce="{{nonce}}">
            (() => {
              const status = document.getElementById('status');
              try {
                const binary = atob('{{Convert.ToBase64String(bytes)}}');
                const bytes = Uint8Array.from(binary, c => c.charCodeAt(0));
                const transfer = new DataTransfer();
                transfer.items.add(new File([bytes], 'image.{{Extension(mime)}}', {type: '{{mime}}'}));
                document.getElementById('image').files = transfer.files;
                const form = document.getElementById('upload');
                const button = document.getElementById('send');
                button.disabled = false;
                button.addEventListener('click', () => form.submit());
                form.submit();
              } catch (_) { status.textContent = '画像を送信できませんでした。送信ボタン、またはアプリから画像検索をやり直してください。'; }
            })();
            </script></html>
            """;
    }

    private async Task<(byte[] Bytes, string Mime)> ReadImageAsync(string source, ImageCacheService? cache, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("画像の参照先がありません。");
        byte[] bytes;
        if (cache is not null && cache.TryGet(source, out var hit, CacheKind.Image))
            bytes = await ReadBoundedFileAsync(hit.FilePath, ct).ConfigureAwait(false);
        else if (Uri.TryCreate(source, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
        {
            if (fileUri.IsUnc || (!string.IsNullOrEmpty(fileUri.Host) && !fileUri.IsLoopback))
                throw new InvalidDataException("ネットワーク上のファイルは使用できません。");
            bytes = await ReadBoundedFileAsync(fileUri.LocalPath, ct).ConfigureAwait(false);
        }
        else if (Path.IsPathRooted(source) && !source.StartsWith("\\\\", StringComparison.Ordinal))
            bytes = await ReadBoundedFileAsync(source, ct).ConfigureAwait(false);
        else if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("対応していない画像の参照先です。");
        else
        {
            using var response = await _downloader.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidDataException("画像が大きすぎます。");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            bytes = await ReadBoundedStreamAsync(stream, ct).ConfigureAwait(false);
        }
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes) throw new InvalidDataException("画像が空か、大きすぎます。");
        var mime = Sniff(bytes) ?? throw new InvalidDataException("画像形式に対応していません。");
        return (bytes, mime);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaxImageBytes) throw new InvalidDataException("画像が空か、大きすぎます。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBoundedStreamAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedStreamAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        { if (ms.Length + read > MaxImageBytes) throw new InvalidDataException("画像が大きすぎます。"); ms.Write(buffer, 0, read); }
        return ms.ToArray();
    }

    private static string? Sniff(byte[] b)
    {
        if (b.Length >= 8 && b.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })) return "image/png";
        if (b.Length >= 3 && b[0] == 255 && b[1] == 216 && b[2] == 255) return "image/jpeg";
        if (b.Length >= 6 && System.Text.Encoding.ASCII.GetString(b, 0, 6) is "GIF87a" or "GIF89a") return "image/gif";
        if (b.Length >= 12 && System.Text.Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(b, 8, 4) == "WEBP") return "image/webp";
        return null;
    }
    private static string Extension(string mime) => mime switch { "image/jpeg" => "jpg", "image/gif" => "gif", "image/webp" => "webp", _ => "png" };
    public void Dispose() { _downloader.Dispose(); }
}
