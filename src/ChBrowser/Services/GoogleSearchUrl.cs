namespace ChBrowser.Services;

/// <summary>選択文字列を Google 検索 URL に変換する。</summary>
public static class GoogleSearchUrl
{
    public static bool TryCreate(string? text, out string url)
    {
        var query = text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            url = "";
            return false;
        }

        url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
        return true;
    }
}
