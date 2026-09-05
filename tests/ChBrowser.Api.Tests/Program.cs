using System.Reflection;
using System.Text;
using System.Text.Json;
using ChBrowser.Services.Api;
using ChBrowser.Services.Image;

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
