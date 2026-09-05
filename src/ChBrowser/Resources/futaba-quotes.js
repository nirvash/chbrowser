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
                if (direct !== null) {
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
            const state = ambiguous.length > 0 ? (parentNumbers.size ? 'ambiguous' : 'unresolved') : (parentNumbers.size ? 'resolved' : 'unresolved');
            return { number, parentNumbers: [...parentNumbers], state, evidence, ambiguousLines: ambiguous };
        });
    }
    return { resolve, normalize, constants: { SHORT_TEXT_MIN, CANDIDATE_LIMIT } };
});
