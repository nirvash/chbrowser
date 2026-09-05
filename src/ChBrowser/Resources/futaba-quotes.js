// Pure Futaba quote resolver. It has no DOM, network, or renderer dependencies.
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.FutabaQuotes = factory();
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';
    const SHORT_TEXT_MIN = 4;
    const CANDIDATE_LIMIT = 128;
    function normalize(value) { return String(value || '').replace(/\r\n?/g, '\n').replace(/[ \t]+/g, ' ').trim(); }
    function info(post) { return post && (post.futabaQuoteInfo || post.FutabaQuoteInfo); }
    function lines(post) { const v = info(post); return v ? (v.lines || v.Lines || []) : []; }
    function lineDepth(line) { return Number(line && (line.quoteDepth ?? line.QuoteDepth)) || 0; }
    function lineText(line) {
        const text = normalize(line && (line.text ?? line.Text));
        // 通常はC#解析済みだが、旧キャッシュや別経路では引用記号が残る場合がある。
        // 深度情報がある引用行だけ、照合用テキストから行頭の > を除去する。
        return lineDepth(line) > 0 ? normalize(text.replace(/^(?:>\s*)+/, '')) : text;
    }
    function attachments(post) { const v = info(post); return v ? (v.attachmentUrls || v.AttachmentUrls || []) : []; }
    function basename(url) { try { return decodeURIComponent(String(url).split(/[?#]/, 1)[0].split('/').pop() || ''); } catch (_) { return String(url).split(/[?#]/, 1)[0].split('/').pop() || ''; } }
    function explicitNumber(text) { const m = normalize(text).match(/(?:^|\s)>?\s*No\.(\d+)\b/i); return m ? Number(m[1]) : null; }
    function candidateLines(post) { return lines(post).filter(line => lineDepth(line) === 0).map(lineText).filter(Boolean); }
    function resolve(posts) {
        const byNumber = new Map(posts.map(p => [Number(p.number), p]));
        return posts.map((post, i) => {
            const number = Number(post.number), parentNumbers = new Set(), evidence = [], ambiguous = [];
            for (const line of lines(post)) {
                const text = lineText(line);
                // 深い引用は直前の引用投稿の文脈。親候補として直接解決するのは1段目だけ。
                if (!text) continue;
                const direct = explicitNumber(text);
                if (direct !== null && lineDepth(line) <= 1) {
                    if (direct < number && byNumber.has(direct)) { parentNumbers.add(direct); evidence.push({ kind: 'number', number: direct, text }); }
                    else ambiguous.push(text);
                    continue;
                }
                if (lineDepth(line) !== 1) continue;
                const owners = [];
                let referencedAttachment = null;
                for (const previous of posts.slice(0, i)) {
                    for (const url of attachments(previous)) {
                        if (text.includes(basename(url))) {
                            owners.push(previous);
                            referencedAttachment = url;
                            break;
                        }
                    }
                }
                const uniqueOwners = [...new Map(owners.map(p => [Number(p.number), p])).values()];
                if (referencedAttachment) {
                    if (uniqueOwners.length === 1) { parentNumbers.add(Number(uniqueOwners[0].number)); evidence.push({ kind: 'attachment', number: Number(uniqueOwners[0].number), text, url: referencedAttachment }); }
                    else ambiguous.push(text);
                    continue;
                }
                if (text.length < SHORT_TEXT_MIN) { ambiguous.push(text); continue; }
                const candidates = [];
                for (let j = 0; j < i; j++) {
                    if (candidateLines(posts[j]).some(source => source === text || source.includes(text))) candidates.push(Number(posts[j].number));
                    if (candidates.length > CANDIDATE_LIMIT) break;
                }
                const unique = [...new Set(candidates)];
                if (unique.length === 1) { parentNumbers.add(unique[0]); evidence.push({ kind: 'text', number: unique[0], text }); }
                else ambiguous.push(text);
            }
            const blocks = [];
            for (let start = 0; start < lines(post).length;) {
                if (lineDepth(lines(post)[start]) === 0) { start++; continue; }
                const values = [], depths = [];
                let end = start;
                while (end < lines(post).length && lineDepth(lines(post)[end]) > 0) {
                    values.push(lineText(lines(post)[end])); depths.push(lineDepth(lines(post)[end])); end++;
                }
                const blockParents = evidence.filter(e => values.includes(e.text)).map(e => Number(e.number));
                let blockParent = [...new Set(blockParents)];
                if (Math.max(...depths) >= 2) {
                    const query = values.map((text, j) => ({ depth: Math.max(0, depths[j] - 1), text }));
                    const candidates = contextCandidates(posts, i, query);
                    blockParent = candidates.length === 1 ? candidates : [];
                }
                const mediaUrls = [];
                for (let j = 0; j < i; j++) {
                    for (const url of attachments(posts[j])) {
                        if (values.some(value => value.includes(basename(url))) && !mediaUrls.includes(url)) mediaUrls.push(url);
                    }
                }
                blocks.push({ startLine: start, length: end - start, quoteDepth: Math.max(...depths), lines: values, depths,
                    parentNumber: blockParent.length === 1 ? blockParent[0] : null, mediaUrls: mediaUrls.slice(0, 2) });
                start = end;
            }
            if (blocks.length > 1 && blocks[0].parentNumber != null) {
                const firstParent = Number(blocks[0].parentNumber);
                for (const n of [...parentNumbers]) if (n !== firstParent) parentNumbers.delete(n);
                for (let j = evidence.length - 1; j >= 0; j--) if (Number(evidence[j].number) !== firstParent) evidence.splice(j, 1);
                ambiguous.length = 0;
                for (const block of blocks) if (block.parentNumber == null) block.parentNumber = firstParent;
            }
            const state = ambiguous.length > 0 ? (parentNumbers.size ? 'ambiguous' : 'unresolved') : (parentNumbers.size ? 'resolved' : 'unresolved');
            return { number, parentNumbers: [...parentNumbers], state, evidence, ambiguousLines: ambiguous, blocks };
        });
    }
    function contextCandidates(posts, end, query) {
        const result = [];
        for (let i = 0; i < end; i++) {
            const source = lines(posts[i]).map(line => ({ depth: lineDepth(line), text: lineText(line) })).filter(x => x.text);
            for (let s = 0; s + query.length <= source.length; s++) {
                if (query.every((q, j) => q.depth === source[s + j].depth && q.text === source[s + j].text)) { result.push(Number(posts[i].number)); break; }
            }
        }
        return [...new Set(result)].slice(0, 2);
    }
    function prepareBatch(existing, batch, previous) {
        const missing = batch.some(post => info(post) && !(post.futabaQuoteResolution || post.FutabaQuoteResolution));
        if (!missing) return previous;
        // Only legacy payloads reach this fallback. Include replacements and attachment changes,
        // not a count/text signature; drawing the same posts again just reads the returned Map.
        const merged = new Map(existing.map(post => [Number(post.number), post]));
        for (const post of batch) merged.set(Number(post.number), post);
        return new Map(resolve([...merged.values()]).map(result => [result.number, result]));
    }
    return { resolve, prepareBatch, normalize, constants: { SHORT_TEXT_MIN, CANDIDATE_LIMIT } };
});
