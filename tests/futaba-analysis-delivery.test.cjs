const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const quotes = require('../src/ChBrowser/Resources/futaba-quotes.js');
const source = fs.readFileSync(path.join(__dirname, '../src/ChBrowser/Resources/thread.js'), 'utf8');
function functionSource(name) {
    const start = source.indexOf('    function ' + name + '(');
    assert.ok(start >= 0);
    return source.slice(start, source.indexOf('\n    }', start) + 6);
}
const post = (number, text, depth = 0, urls = []) => ({ number,
    futabaQuoteInfo: { lines: [{ text, quoteDepth: depth }], attachmentUrls: urls } });

test('5000 persisted posts skip browser analysis and reference reads never scan the thread', () => {
    const batch = Array.from({ length: 5000 }, (_, i) => ({ ...post(1432656555 + i, 'body'),
        futabaQuoteResolution: { number: 1432656555 + i, state: 'resolved', parentNumbers: [1432656554] } }));
    const previous = new Map();
    const unscannable = new Proxy([], { get() { throw new Error('persisted path must not scan existing posts'); } });
    assert.equal(quotes.prepareBatch(unscannable, batch, previous), previous);
    const context = vm.createContext({ futabaQuoteResolutions: previous, allPosts: unscannable,
        window: { FutabaQuotes: { resolve() { throw new Error('reference read called analysis'); } } },
        extractAnchorRefs: () => [], batch });
    vm.runInContext(functionSource('getFutabaQuoteResolution') + functionSource('getPostReferences'), context);
    const result = vm.runInContext('batch.map(p => getPostReferences(p)[0].from)', context, { timeout: 1000 });
    assert.equal(result.length, 5000);
    assert.ok(result.every(n => n === 1432656554));
});

test('legacy batches resolve once before drawing, including attachment-only replacements', () => {
    const owner = post(100, 'owner', 0, ['https://example.com/a.jpg']);
    const child = post(200, 'a.jpg', 1);
    let results = quotes.prepareBatch([], [owner, child], new Map());
    assert.equal(results.get(200).state, 'resolved');
    const replacement = post(100, 'owner', 0, ['https://example.com/b.jpg']);
    results = quotes.prepareBatch([owner, child], [replacement], results);
    assert.equal(results.get(200).state, 'unresolved');
});

test('fresh resync payload replaces an old resolution with the same post number', () => {
    const p = post(200, 'quote', 1);
    const context = vm.createContext({ futabaQuoteResolutions: new Map([[200, { state: 'resolved', parentNumbers: [100] }]]), p });
    vm.runInContext(functionSource('getFutabaQuoteResolution'), context);
    p.futabaQuoteResolution = { state: 'unresolved', parentNumbers: [] };
    assert.equal(vm.runInContext('getFutabaQuoteResolution(p).state', context), 'unresolved');
});

test('missing/NG parent keeps original quote text despite persisted full-thread resolution', () => {
    const p = post(200, 'quoted original', 1);
    p.futabaQuoteResolution = { state: 'resolved', parentNumbers: [100] };
    const context = vm.createContext({ p, viewMode: 'dedupTree2', window: { FutabaQuotes: quotes },
        futabaQuoteResolutions: new Map(), postsByNumber: new Map([[200, p]]) });
    vm.runInContext(functionSource('getFutabaQuoteResolution') + functionSource('buildFutabaDisplayBody'), context);
    assert.equal(vm.runInContext("buildFutabaDisplayBody(p, '', 'original quote HTML', [], true)", context), 'original quote HTML');
});

test('5ch still uses the existing anchor extractor', () => {
    let read;
    const context = vm.createContext({ extractAnchorRefs(body) { read = body; return [{ from: 1, to: 3 }]; } });
    vm.runInContext(functionSource('getPostReferences'), context);
    assert.equal(vm.runInContext("getPostReferences({number:4,body:'>>1-3'})[0].to", context), 3);
    assert.equal(read, '>>1-3');
});
