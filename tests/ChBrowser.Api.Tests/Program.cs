using System.Reflection;
using System.Text;
using ChBrowser.Services.Api;

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
