using System.Reflection;
using System.IO;
using System.Text;
using System.Text.Json;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Services.Image;
using ChBrowser.Services.Ng;
using ChBrowser.Services.Storage;
using ChBrowser.ViewModels;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var method = typeof(PostClient).GetMethod(
    "EncodeAsSjisForm",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(typeof(PostClient).FullName, "EncodeAsSjisForm");

var fields = new List<KeyValuePair<string, string>>
{
    new("body", "絵文字 😀 を含む投稿"),
};
var encoded = (byte[])(method.Invoke(null, [fields])
    ?? throw new InvalidOperationException("EncodeAsSjisForm returned null"));
var form = Encoding.ASCII.GetString(encoded);

if (form.Contains("%3F", StringComparison.OrdinalIgnoreCase))
    throw new Exception($"Emoji was replaced with '?': {form}");

if (!form.Contains("%26%23x1F600%3B", StringComparison.OrdinalIgnoreCase))
    throw new Exception($"Emoji entity fallback was not encoded: {form}");

Console.WriteLine("PASS emoji survives SJIS form encoding as an HTML numeric entity");

var futabaMethod = typeof(PostClient).GetMethod(
    "BuildFutabaFormBody",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(typeof(PostClient).FullName, "BuildFutabaFormBody");
var futabaBoard = new Board("b", "二次元裏", "https://may.2chan.net/b/", "img", 0);
var futabaRequest = new PostRequest(futabaBoard, "123456", null, "なまえ", "sage", "本文😀", PostAuthMode.None);
var futabaForm = Encoding.UTF8.GetString((byte[])(futabaMethod.Invoke(null, [futabaRequest])
    ?? throw new InvalidOperationException("BuildFutabaFormBody returned null")));
if (!futabaForm.Contains("mode=regist") || !futabaForm.Contains("resto=123456") ||
    !futabaForm.Contains("textonly=on") || !futabaForm.Contains("com=%E6%9C%AC%E6%96%87%F0%9F%98%80"))
    throw new Exception($"Unexpected Futaba form: {futabaForm}");
if (futabaForm.Contains("bbs=", StringComparison.Ordinal))
    throw new Exception($"Futaba form contains 5ch-only field: {futabaForm}");
Console.WriteLine("PASS Futaba reply form uses regist/resto and UTF-8 text");

var fiveChBoard = new Board("news4vip", "ニュース速報(VIP)", "https://mi.5ch.net/news4vip/", "ニュース", 0);
var fiveChTab = new ThreadListTabViewModel(fiveChBoard, _ => { }) { IsCatalogView = true };
fiveChTab.SetThreads([new ThreadInfo("1234567890", "通常リストで表示するスレ", 10, 1)], DateTimeOffset.UtcNow);
if (fiveChTab.Html?.Contains("<table class=\"catalog-table", StringComparison.Ordinal) == true ||
    fiveChTab.Html?.Contains("<table><thead>", StringComparison.Ordinal) != true)
    throw new Exception("5ch thread list must ignore a leaked Futaba catalog setting");
if (fiveChTab.Html.Contains("<th class=\"col-board", StringComparison.Ordinal) ||
    fiveChTab.Html.Contains("<td class=\"col-board", StringComparison.Ordinal))
    throw new Exception("single-board thread list must hide the redundant board column");
Console.WriteLine("PASS 5ch thread list ignores a leaked Futaba catalog setting");

var columnConfigRoot = Path.Combine(Path.GetTempPath(), "ChBrowser-Column-Width-Test-" + Guid.NewGuid().ToString("N"));
var columnConfigStore = new ConfigStorage(new DataPaths(columnConfigRoot));
columnConfigStore.Save(new AppConfig { ThreadListColumnWidths = new Dictionary<string, int> { ["title"] = 321, ["count"] = 72 } });
var savedColumnWidths = columnConfigStore.Load().ThreadListColumnWidths;
if (savedColumnWidths is null || savedColumnWidths.GetValueOrDefault("title") != 321 || savedColumnWidths.GetValueOrDefault("count") != 72)
    throw new Exception("Thread-list column widths were not persisted in AppConfig");
Console.WriteLine("PASS thread-list column widths persist in AppConfig");

var promptMethod = typeof(AiImageMetadataService).GetMethod(
    "ExtractTextFromComfyNode",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(typeof(AiImageMetadataService).FullName, "ExtractTextFromComfyNode");
using var workflow = JsonDocument.Parse("""
{
  "1": {
    "inputs": {
      "string1": "positive one",
      "string2": "positive two",
      "system_prompt": "must not be treated as image prompt",
      "negative_prompt": "must not be included"
    }
  }
}
""");
var prompt = (string?)promptMethod.Invoke(null, [workflow.RootElement.GetProperty("1"), workflow.RootElement, 0]);
if (prompt != "positive one\npositive two")
    throw new Exception($"Unexpected video prompt fallback result: {prompt}");

Console.WriteLine("PASS video prompt fallback reads string fields and excludes system/negative fields");

var ngRoot = Path.Combine(Path.GetTempPath(), "ChBrowser-Ng-Test-" + Guid.NewGuid().ToString("N"));
var ng = new NgService(new NgStorage(new DataPaths(ngRoot)));
ng.Save(new NgRuleSet
{
    Rules = [new NgRule { Target = "word", Pattern = "blocked" }],
});
var firstBatch = ng.ComputeHiddenWithBreakdown(
    [new Post(1, "", "", "", "", "blocked", null)], "example.test", "board");
var secondBatch = ng.ComputeHiddenWithBreakdown(
    [new Post(2, "", "", "", "", ">>1 reply", null)], "example.test", "board",
    previouslyHidden: firstBatch.HiddenNumbers);
if (!firstBatch.HiddenNumbers.SetEquals([1]) || !secondBatch.HiddenNumbers.SetEquals([2]))
    throw new Exception("Cross-batch NG chain was not propagated");

Console.WriteLine("PASS NG chain propagates from previously hidden posts");

var appMethod = typeof(AiImageMetadataService).GetMethod(
    "DetectVideoAppFromSoftware", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(typeof(AiImageMetadataService).FullName, "DetectVideoAppFromSoftware");
var appLabel = (string?)appMethod.Invoke(null, [new Dictionary<string, string> { ["software"] = "\"scom-v 0.1.0\"" }]);
if (appLabel != "scom-v") throw new Exception($"Unexpected video app label: {appLabel}");
Console.WriteLine("PASS scom-v video software tag gets a stable generator label");
