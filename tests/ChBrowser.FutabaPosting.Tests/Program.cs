using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        app.Startup += async (_, _) =>
        {
            try { await Run(); Environment.ExitCode = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
    }

    private static async Task Run()
    {
        using var browser = new WebView2();
        var window = new Window { Content = browser, Width = 800, Height = 600, Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Left = -32000 };
        window.Show();
        try
        {
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(Path.GetTempPath(), "chbrowser-post-test-" + Guid.NewGuid().ToString("N")));
            await browser.EnsureCoreWebView2Async(env);
            var core = browser.CoreWebView2;
            var loaded = new TaskCompletionSource();
            core.NavigationCompleted += (_, e) => loaded.TrySetResult();
            core.NavigateToString("""
                <!doctype html><meta charset="utf-8">
                <form id="fm" action="https://may.2chan.net/b/futaba.php?guid=on" method="post" enctype="multipart/form-data">
                  <input name="mode" value="regist"><input name="resto" value="123456">
                  <input name="hash" value="live-token"><input name="pthb" value="browser-state">
                  <input name="MAX_FILE_SIZE" value="4"><input name="name"><input name="email"><input name="sub"><textarea name="com"></textarea>
                  <input type="file" name="upfile"><input type="checkbox" name="textonly">
                  <input type="submit" onclick="return ptfk(123456)">
                </form><script>window.submitCalls=0; function ptfk(){window.submitCalls++;return false;}</script>
                """);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var poster = typeof(ChBrowser.Services.Api.PostClient).Assembly.GetType("ChBrowser.Services.Api.FutabaBrowserPoster")!;
            var prepare = poster.GetMethod("BuildPrepareScript", BindingFlags.NonPublic | BindingFlags.Static)!;
            async Task<string?> Prepare(byte[]? bytes, string resto = "123456", string endpoint = "https://may.2chan.net/b/futaba.php?guid=on")
            {
                var payload = JsonSerializer.Serialize(new { name="なまえ",email="sage",sub="",com="本文😀",resto,endpoint,
                    bytes=bytes is null ? null : Convert.ToBase64String(bytes),fileName="画像.png",mime="image/png",maxBytes=8192000 });
                return JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync((string)prepare.Invoke(null, [payload])!));
            }
            if (await Prepare([1,2,3,4]) != "") throw new Exception("Official limit boundary rejected");
            var state = JsonDocument.Parse(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify({hash:fm.elements.hash.value,pthb:fm.elements.pthb.value,com:fm.elements.com.value,name:fm.elements.upfile.files[0].name,size:fm.elements.upfile.files[0].size,textonly:fm.elements.textonly.checked,calls:submitCalls})"))!);
            var value = state.RootElement;
            if (value.GetProperty("hash").GetString() != "live-token" || value.GetProperty("pthb").GetString() != "browser-state" ||
                value.GetProperty("com").GetString() != "本文😀" || value.GetProperty("name").GetString() != "画像.png" ||
                value.GetProperty("size").GetInt32() != 4 || value.GetProperty("textonly").GetBoolean() || value.GetProperty("calls").GetInt32() != 0)
                throw new Exception("Official fields, file metadata, text, or no-submit contract changed");
            if (string.IsNullOrEmpty(await Prepare([1,2,3,4,5]))) throw new Exception("Live board capacity not enforced");
            if (string.IsNullOrEmpty(await Prepare([1], "999"))) throw new Exception("Wrong thread was accepted");
            if (string.IsNullOrEmpty(await Prepare([1], endpoint:"https://example.com/"))) throw new Exception("Wrong endpoint was accepted");
            if (await Prepare(null) != "") throw new Exception("Text-only preparation failed");
            if (await core.ExecuteScriptAsync("fm.elements.textonly.checked && fm.elements.upfile.files.length===0 && submitCalls===0") != "true")
                throw new Exception("Text-only did not clear file, or submission occurred");
            Console.WriteLine("PASS real WebView2: live fields preserved, Unicode/file metadata, dynamic size boundary, wrong destination rejected, no network submission");
        }
        finally { window.Content = null; window.Close(); }
    }
}
