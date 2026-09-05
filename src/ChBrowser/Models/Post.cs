namespace ChBrowser.Models;

/// <summary>One post in a thread. FutabaQuoteInfo is null for non-Futaba posts.</summary>
public sealed record Post(
    int Number,
    string Name,
    string Mail,
    string DateText,
    string Id,
    string Body,
    string? ThreadTitle,
    int? SoudaneCount = null,
    FutabaQuoteInfo? FutabaQuoteInfo = null);

public sealed record FutabaQuoteInfo(
    IReadOnlyList<FutabaQuoteLine> Lines,
    IReadOnlyList<string> AttachmentUrls,
    string RawHtml);

public sealed record FutabaQuoteLine(
    string Text,
    int QuoteDepth,
    string RawHtml,
    string OriginalText);
