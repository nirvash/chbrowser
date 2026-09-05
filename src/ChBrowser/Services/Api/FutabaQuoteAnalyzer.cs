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
        var contexts = new ContextIndex();
        var attachments = new Dictionary<string, List<(int Number, string Url)>>(StringComparer.Ordinal);
        var output = new List<Post>(posts.Count);
        foreach (var post in posts)
        {
            ct.ThrowIfCancellationRequested();
            if (post.FutabaQuoteInfo is not { } info) { output.Add(post); continue; }
            var parents = new List<int>();
            var evidence = new List<FutabaQuoteEvidence>();
            var ambiguous = new List<string>();
            var blocks = new List<FutabaQuoteBlock>();
            foreach (var line in info.Lines)
            {
                var text = Text(line);
                if (text.Length == 0) continue;
                // Preserve existing resolution semantics; quotation-context improvements are separate.
                var explicitMatch = ExplicitNumber.Match(text);
                if (explicitMatch.Success && line.QuoteDepth <= 1)
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
            foreach (var block in QuoteBlocks(info.Lines))
            {
                int? parent = null;
                if (block.QuoteDepth >= 2)
                {
                    var depths = block.Depths ?? Enumerable.Repeat(block.QuoteDepth, block.Length).ToArray();
                    var query = block.Lines.Select((value, i) => (Depth: Math.Max(0, depths[i] - 1), Text: value)).ToArray();
                    var candidates = contexts.Find(query);
                    parent = candidates.Count == 1 ? candidates[0] : null;
                    if (parent is int parentNumber) AddParent(parentNumber, "context", string.Join("\n", block.Lines));
                    else ambiguous.Add(string.Join("\n", block.Lines));
                }
                else
                {
                var blockParents = evidence.Where(e => block.Lines.Contains(e.Text, StringComparer.Ordinal))
                    .Select(e => e.Number).Distinct().Take(2).ToArray();
                // A single quote block can span adjacent source posts. Keep the first
                // matched source as the parent, matching Futaba's multi-quote rule.
                parent = blockParents.Length > 0 ? blockParents[0] : null;
                }
                var mediaUrls = attachments
                    .Where(pair => block.Lines.Any(line => line.Contains(pair.Key, StringComparison.Ordinal)))
                    .SelectMany(pair => pair.Value.Select(owner => owner.Url))
                    .Distinct(StringComparer.Ordinal).Take(2).ToArray();
                blocks.Add(block with { ParentNumber = parent, MediaUrls = mediaUrls });
            }
            if (blocks.Count > 0 && blocks[0].ParentNumber is int firstParent)
            {
                parents.RemoveAll(number => number != firstParent);
                evidence.RemoveAll(item => item.Number != firstParent);
                if (blocks.Count > 1)
                {
                    ambiguous.Clear();
                    for (var i = 0; i < blocks.Count; i++)
                        if (blocks[i].ParentNumber is null) blocks[i] = blocks[i] with { ParentNumber = firstParent };
                }
                else if (ambiguous.Count > 0 && ambiguous.All(text =>
                    posts.FirstOrDefault(candidate => candidate.Number == firstParent)?.FutabaQuoteInfo?.Lines
                        .Any(line => Text(line).Contains(text, StringComparison.Ordinal)) == true))
                {
                    // A line can match another post as well (e.g. "Astra" vs
                    // "Astra版"). An anchored block still belongs to its first parent.
                    ambiguous.Clear();
                }
            }
            var state = ambiguous.Count > 0 ? (parents.Count > 0 ? "ambiguous" : "unresolved")
                : (parents.Count > 0 ? "resolved" : "unresolved");
            output.Add(post with { FutabaQuoteResolution = new(post.Number, parents, state, evidence, ambiguous, blocks) });

            // Index after resolution: a post cannot quote itself or a later arrival.
            foreach (var line in info.Lines)
                if (line.QuoteDepth == 0) texts.Add(post.Number, Text(line));
            contexts.Add(post.Number, info.Lines);
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

    private static IEnumerable<FutabaQuoteBlock> QuoteBlocks(IReadOnlyList<FutabaQuoteLine> lines)
    {
        for (var i = 0; i < lines.Count;)
        {
            if (lines[i].QuoteDepth == 0) { i++; continue; }
            var start = i;
            var values = new List<string>();
            var depths = new List<int>();
            while (i < lines.Count && lines[i].QuoteDepth > 0)
            {
                values.Add(Text(lines[i])); depths.Add(lines[i].QuoteDepth); i++;
            }
            yield return new FutabaQuoteBlock(start, i - start, depths.Max(), values, depths);
        }
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

    private sealed class ContextIndex
    {
        private readonly Dictionary<string, HashSet<int>> _index = new(StringComparer.Ordinal);
        internal void Add(int number, IReadOnlyList<FutabaQuoteLine> lines)
        {
            var values = lines.Select(line => (Depth: line.QuoteDepth, Text: Text(line))).Where(x => x.Text.Length > 0).ToArray();
            for (var start = 0; start < values.Length; start++)
            {
                var key = "";
                for (var length = 1; length <= Math.Min(32, values.Length - start); length++)
                {
                    key += (length == 1 ? "" : "\u001f") + values[start + length - 1].Depth + ":" + values[start + length - 1].Text;
                    if (!_index.TryGetValue(key, out var owners)) _index[key] = owners = [];
                    owners.Add(number);
                }
            }
        }
        internal List<int> Find(IReadOnlyList<(int Depth, string Text)> lines)
        {
            var key = string.Join("\u001f", lines.Select(x => x.Depth + ":" + x.Text));
            return _index.TryGetValue(key, out var owners) ? owners.Take(2).ToList() : [];
        }
    }
}
