using System;

namespace ChBrowser.Services.Url;

/// <summary>ふたばちゃんねるの板・スレ URL を判定して正規形を組み立てる。</summary>
public static class FutabaUrl
{
    public static bool IsFutabaHost(string host)
        => string.Equals(host, "2chan.net", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".2chan.net", StringComparison.OrdinalIgnoreCase);

    public static string BuildBoardUrl(string host, string directory)
        => $"https://{host}/{directory}/";

    public static string BuildThreadUrl(string host, string directory, string threadKey)
        => $"https://{host}/{directory}/res/{threadKey}.htm";

    public static string BuildCatalogUrl(string host, string directory, int? sort = null)
        => $"https://{host}/{directory}/futaba.php?mode=cat" + (sort is int s ? $"&sort={s}" : "");
}
