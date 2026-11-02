using ChBrowser.Services;

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var text = " \t日本語 & plus+ #tag\n次行  ";
Expect(GoogleSearchUrl.TryCreate(text, out var url), "non-empty selection should create a URL");
Expect(url == "https://www.google.com/search?q=" + Uri.EscapeDataString("日本語 & plus+ #tag\n次行"), "query must be trimmed and escaped as one query value");
Expect(url.Contains("%26") && url.Contains("%2B") && url.Contains("%23") && url.Contains("%0A"), "reserved characters and newline must be encoded");
Expect(!GoogleSearchUrl.TryCreate(" \r\n\t ", out var emptyUrl) && emptyUrl == "", "whitespace-only selection must not create a URL");
Expect(!GoogleSearchUrl.TryCreate(null, out _), "null selection must not create a URL");
Expect(GoogleSearchUrl.TryCreate(text, out var unchangedTextUrl) && unchangedTextUrl == url, "existing text search must remain unchanged");
Console.WriteLine("GoogleSearchUrl checks passed.");
