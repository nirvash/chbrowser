using System.Reflection;
using System.IO;
using System.Text;
using System.Text.Json;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Services.Image;
using ChBrowser.Services.Ng;
using ChBrowser.Services.Storage;

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
