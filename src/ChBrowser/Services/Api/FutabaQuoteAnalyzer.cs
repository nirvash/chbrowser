using System.Text.RegularExpressions;
using ChBrowser.Models;

namespace ChBrowser.Services.Api;

/// <summary>Resolve once per fetched snapshot, before UI filtering or rendering.
/// Text and attachment indexes contain only preceding posts. No numeric-ID range scans.</summary>
internal static class FutabaQuoteAnalyzer
{
    private static readonly Regex Spaces = new("[ \\t]+", RegexOptions.Compiled);
    private static readonly Regex QuotePrefix = new(@"^(?:>\s*)+", RegexOptions.Compiled);
    private static readonly Regex ExplicitNumber = new(@"(?:^|\s)>?\s*No\.(\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string Normalize(string value) => Spaces.Replace(value.Replace("\r\n", "\n").Replace('\r', '\n'), " ").Trim();
    private static string Text(FutabaQuoteLine line)
    {
        var text = Normalize(line.Text);
        return line.QuoteDepth > 0 ? Normalize(QuotePrefix.Replace(text, "")) : text;
    }

    internal static IReadOnlyList<Post> Analyze(IReadOnlyList<Post> posts, CancellationToken ct = default)
    {
        var numbers = posts.Select(p => p.Number).ToHashSet();
        var texts = new TextIndex();
        var attachments = new Dictionary<string, List<(int Number, string Url)>>(StringComparer.Ordinal);
        var output = new List<Post>(posts.Count);
        foreach (var post in posts)
        {
            ct.ThrowIfCancellationRequested();
            if (post.FutabaQuoteInfo is not { } info) { output.Add(post); continue; }
            var parents = new List<int>();
            var evidence = new List<FutabaQuoteEvidence>();
            var ambiguous = new List<string>();
            foreach (var line in info.Lines)
            {
                var text = Text(line);
                if (text.Length == 0) continue;
                // Preserve existing resolution semantics; quotation-context improvements are separate.
                var explicitMatch = ExplicitNumber.Match(text);
                if (explicitMatch.Success)
                {
                    if (int.TryParse(explicitMatch.Groups[1].Value, out var n) && n < post.Number && numbers.Contains(n))
                        AddParent(n, "number", text);
                    else ambiguous.Add(text);
                    continue;
                }
                if (line.QuoteDepth != 1) continue;

                // Match attachment names in the quote without re-reading/decoding every prior URL.
                var owners = new Dictionary<int, string>();
                foreach (var pair in attachments)
                {
                    if (!text.Contains(pair.Key, StringComparison.Ordinal)) continue;
                    foreach (var owner in pair.Value) owners.TryAdd(owner.Number, owner.Url);
                    if (owners.Count > 1) break;
                }
                if (owners.Count > 0)
                {
                    if (owners.Count == 1) AddParent(owners.First().Key, "attachment", text, owners.First().Value);
                    else ambiguous.Add(text);
                    continue;
                }
                if (text.Length < 4) { ambiguous.Add(text); continue; }
                var candidates = texts.Find(text);
                if (candidates.Count == 1) AddParent(candidates[0], "text", text);
                else ambiguous.Add(text);
            }
            var state = ambiguous.Count > 0 ? (parents.Count > 0 ? "ambiguous" : "unresolved")
                : (parents.Count > 0 ? "resolved" : "unresolved");
            output.Add(post with { FutabaQuoteResolution = new(post.Number, parents, state, evidence, ambiguous) });

            // Index after resolution: a post cannot quote itself or a later arrival.
            foreach (var line in info.Lines)
                if (line.QuoteDepth == 0) texts.Add(post.Number, Text(line));
            foreach (var url in info.AttachmentUrls)
            {
                var name = url.Split('?', '#')[0].Split('/')[^1];
                try { name = Uri.UnescapeDataString(name); } catch (UriFormatException) { }
                if (name.Length == 0) continue;
                if (!attachments.TryGetValue(name, out var owners)) attachments[name] = owners = [];
                owners.Add((post.Number, url));
            }

            void AddParent(int number, string kind, string text, string? url = null)
            {
                if (!parents.Contains(number)) parents.Add(number);
                evidence.Add(new(kind, number, text, url));
            }
        }
        return output;
    }

    /// <summary>Four UTF-16 code units, as in the JS minimum quote length.
    /// Intersect via the least frequent gram, then verify the actual substring.</summary>
    private sealed class TextIndex
    {
        private readonly List<(int Number, string Text)> _lines = [];
        private readonly Dictionary<string, List<int>> _grams = new(StringComparer.Ordinal);
        internal void Add(int number, string text)
        {
            if (text.Length < 4) return;
            var id = _lines.Count;
            _lines.Add((number, text));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i <= text.Length - 4; i++)
            {
                var gram = text.Substring(i, 4);
                if (!seen.Add(gram)) continue;
                if (!_grams.TryGetValue(gram, out var ids)) _grams[gram] = ids = [];
                ids.Add(id);
            }
        }
        internal List<int> Find(string text)
        {
            List<int>? smallest = null;
            for (var i = 0; i <= text.Length - 4; i++)
            {
                if (!_grams.TryGetValue(text.Substring(i, 4), out var ids)) return [];
                if (smallest is null || ids.Count < smallest.Count) smallest = ids;
            }
            var result = new List<int>();
            if (smallest is null) return result;
            foreach (var id in smallest)
            {
                var line = _lines[id];
                if (!line.Text.Contains(text, StringComparison.Ordinal) || result.Contains(line.Number)) continue;
                result.Add(line.Number);
                if (result.Count == 2) break; // One vs many is sufficient; never pick an arbitrary winner.
            }
            return result;
        }
    }
}
