using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ChBrowser.Controls;
using ChBrowser.Models;
using ChBrowser.Services.Logging;
using ChBrowser.Services.Url;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser.Services.Api;

/// <summary>Use the live official form and its submit handler, including dynamic fields and browser cookies.</summary>
internal static class FutabaBrowserPoster
{
    internal const int MaxAttachmentBytes = 8000 * 1024;

    internal static async Task<(HttpResponseMessage Response, string Body)> SendAsync(PostRequest request, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("ふたば投稿にはアプリのブラウザが必要です。");
        if (!dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(() => SendAsync(request, ct)).Task.Unwrap().ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var pageUrl = request.IsReply
            ? FutabaUrl.BuildThreadUrl(request.Board.Host, request.Board.DirectoryName, request.ThreadKey!)
            : request.Board.Url;
        var endpoint = new Uri(new Uri(request.Board.Url), "futaba.php?guid=on");
        using var browser = new Microsoft.Web.WebView2.Wpf.WebView2();
        // A realized control is required by WebView2. This window never takes focus or appears in the taskbar.
        var host = new Window { Content = browser, Width = 800, Height = 600, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Left = -32000, Top = -32000 };
        var submitted = false;
        try
        {
            host.Show();
            await WebView2Helper.EnsureCoreAsync(browser).WaitAsync(token);
            var core = browser.CoreWebView2;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) =>
            {
                if (!submitted)
                {
                    if (e.IsSuccess) loaded.TrySetResult();
                    else loaded.TrySetException(new IOException($"公式フォームを開けませんでした: {e.WebErrorStatus}"));
                }
            };
            core.Navigate(pageUrl);
            await loaded.Task.WaitAsync(token);
            var responseTask = new TaskCompletionSource<(HttpResponseMessage, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.WebResourceResponseReceived += async (_, e) =>
            {
                if (!submitted || e.Request.Method != "POST" ||
                    !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var target) ||
                    target.GetLeftPart(UriPartial.Path) != endpoint.GetLeftPart(UriPartial.Path)) return;
                try
                {
                    using var stream = await e.Response.GetContentAsync();
                    using var buffer = new MemoryStream();
                    if (stream is not null) await stream.CopyToAsync(buffer, token);
                    var contentType = e.Response.Headers.Contains("Content-Type")
                        ? e.Response.Headers.GetHeader("Content-Type") : "";
                    var encoding = contentType.Contains("shift_jis", StringComparison.OrdinalIgnoreCase) ||
                        contentType.Contains("shift-jis", StringComparison.OrdinalIgnoreCase)
                        ? Encoding.GetEncoding(932) : Encoding.UTF8;
                    var response = new HttpResponseMessage((HttpStatusCode)e.Response.StatusCode)
                    { RequestMessage = new HttpRequestMessage(HttpMethod.Post, target) };
                    responseTask.TrySetResult((response, encoding.GetString(buffer.ToArray())));
                }
                catch (Exception ex) { responseTask.TrySetException(ex); }
            };
            var payload = JsonSerializer.Serialize(new {
                name = request.Name, email = request.Mail, sub = request.Subject ?? "", com = request.Message,
                resto = request.ThreadKey ?? "", endpoint = endpoint.AbsoluteUri,
                bytes = request.Attachment is {} file ? Convert.ToBase64String(file.Data) : null,
                fileName = request.Attachment?.FileName, mime = request.Attachment?.ContentType,
                maxBytes = MaxAttachmentBytes
            });
            // Prepare only: every validation finishes before the single submit click.
            var preparation = await core.ExecuteScriptAsync(BuildPrepareScript(payload)).WaitAsync(token);
            var error = JsonSerializer.Deserialize<string>(preparation);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            LogService.Instance.Write($"[FutabaPost] prepared host={endpoint.Host} board={request.Board.DirectoryName} thread={request.ThreadKey ?? "new"} bytes={request.Attachment?.Data.Length ?? 0} officialForm=true");
            token.ThrowIfCancellationRequested();
            submitted = true;
            await core.ExecuteScriptAsync("document.querySelector('#fm input[type=submit], #fm button[type=submit]').click()").WaitAsync(token);
            return await responseTask.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (submitted)
        {
            throw new IOException("送信後に応答を確認できませんでした。二重投稿を避けるため、スレッドを確認してから再送してください。");
        }
        finally
        {
            host.Content = null;
            host.Close();
        }
    }

    internal static string BuildPrepareScript(string payload) => """
        (() => {
          try {
            const p = PAYLOAD;
            const fm = document.getElementById('fm');
            if (!fm || new URL(fm.action).href !== p.endpoint || fm.method.toLowerCase() !== 'post' ||
                fm.enctype !== 'multipart/form-data' || typeof window.ptfk !== 'function')
              throw new Error('公式投稿フォームの構造を確認できませんでした。');
            const field = n => { const x = fm.elements.namedItem(n); if (!x) throw new Error('公式フォームに項目がありません: ' + n); return x; };
            if (field('mode').value !== 'regist' || (p.resto && field('resto').value !== p.resto))
              throw new Error('公式フォームの投稿先が一致しません。');
            const button = fm.querySelector('input[type=submit], button[type=submit]');
            if (!button || !button.getAttribute('onclick')?.includes('ptfk('))
              throw new Error('公式の送信処理を確認できませんでした。');
            for (const n of ['name','email','sub','com']) field(n).value = p[n];
            const upload = field('upfile');
            const data = new DataTransfer();
            if (p.bytes !== null) {
              const raw = atob(p.bytes), bytes = Uint8Array.from(raw, c => c.charCodeAt(0));
              const officialLimit = Number(field('MAX_FILE_SIZE').value);
              if (!Number.isSafeInteger(officialLimit) || officialLimit <= 0)
                throw new Error('公式の添付容量上限を確認できませんでした。');
              if (bytes.length > Math.min(officialLimit, p.maxBytes))
                throw new Error('ファイルが大きすぎます。' + Math.min(officialLimit, p.maxBytes) + ' バイトまでです。');
              data.items.add(new File([bytes], p.fileName, {type:p.mime}));
            }
            upload.files = data.files;
            field('textonly').checked = p.bytes === null;
            return '';
          } catch (e) { return e.message; }
        })()
        """.Replace("PAYLOAD", payload);
}
