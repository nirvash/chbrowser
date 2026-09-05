// Diagnostic characterization: runs production section rendering and scrollbar code.
// DOM/layout and post-template rendering are simulated; no live app is modified.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require('node:path').join(__dirname, '../src/ChBrowser/Resources/thread.js'), 'utf8');
function fn(name) {
    const start = source.indexOf('    function ' + name + '(');
    assert.ok(start >= 0, name);
    return source.slice(start, source.indexOf('\n    }', start) + 6);
}
const old = [1432811935];
const delta = [1432834499,1432834557,1432834676,1432835212,1432835797,
    1432835858,1432835920,1432836304,1432836335,1432836427];
const next = [1432837704];
function harness() {
    let sections = [];
    const tracks = Object.fromEntries(['popular','url','image','video'].map(k => [k, {
        markers: [], set innerHTML(v) { this.markers = []; },
        appendChild(m) { this.markers.push(m); }
    }]));
    const rows = () => sections.flatMap(s => s.rows);
    const root = { querySelector() { return sections.find(s => s.live) || null; } };
    const c = vm.createContext({ allPosts: [], postsByNumber: new Map(),
        sessionNewPostNumbers: new Set(), markPostNumber: null,
        getValidForwardAnchors: () => [],
        postDataFor: (p, embedded, omitId, children) => ({p, omitId, children}),
        renderPost: ({p, omitId, children}) => `<post number="${p.number}" canonical="${!omitId}">${children}</post>`,
        insertHtmlIntoContainer(_root, html) {
            const section = { live: true, rows: [...html.matchAll(/<post number="(\d+)" canonical="(true|false)">/g)]
                .map(m => ({number: Number(m[1]), canonical: m[2] === 'true'})),
                remove() { sections = sections.filter(s => s !== section); },
                classList: { remove() { section.live = false; } } };
            sections.push(section);
        },
        document: { documentElement: { get scrollHeight() { return rows().length * 100; } },
            getElementById(id) {
                if (id === 'posts') return root;
                if (id === 'richScrollbar') return {querySelector: s => tracks[s.replace('.track-', '')]};
                const index = rows().findIndex(r => r.canonical && 'r' + r.number === id);
                return index < 0 ? null : {offsetTop: index * 100};
            }, createElement: () => ({style: {}, classList: {add() {}}}) },
        buildReverseIndex: () => new Map(), POPULAR_THRESHOLD: 3,
        HAS_URL_RE: /https:/, bodyContainsImage: () => true, bodyContainsVideo: () => false,
        updateScrollThumb() {}
    });
    vm.runInContext(['renderIncrementalForestNode','buildDedupTree2Chain','buildDedupTree2Forest',
        'renderDedupTree2SectionB','rebuildDedupTree2SectionBDelta','rebuildDedupTree2SectionBFull',
        'updateRichScrollbar'].map(fn).join('\n'), c);
    // Use the actual strategy's freeze implementation.
    const strategy = source.slice(source.indexOf('        dedupTree2: {'));
    const freeze = strategy.match(/promoteOnNewRefresh\(root\) \{([\s\S]*?)\n            \}/)[1];
    c.freeze = () => vm.runInContext('(function(root) {' + freeze + '})(document.getElementById("posts"))', c);
    const add = ns => ns.forEach(number => { const p = {number, body: 'https://example.com/image.png'};
        c.allPosts.push(p); c.postsByNumber.set(number,p); });
    function full() {
        sections = [{live:false, rows:[]}];
        c.appendPrimaryAtEnd = p => sections[0].rows.push({number:p.number,canonical:true});
        c.embedUnderParentReverse = () => { throw new Error('unexpected reference'); };
        c.testRoot = root;
        const strategySource = source.slice(source.indexOf('        dedupTree2: {'));
        const arrival = strategySource.slice(strategySource.indexOf('insertOnArrival(p, root) {') + 'insertOnArrival(p, root) {'.length,
            strategySource.indexOf('\n            },'));
        for (const p of c.allPosts) {
            c.testPost = p;
            vm.runInContext('(function(p, root) {' + arrival + '})(testPost, testRoot)', c);
        }
        vm.runInContext('rebuildDedupTree2SectionBFull()', c);
    }
    function append(ns) {
        c.freeze(); add(ns); c.markPostNumber = ns[0]; c.sessionNewPostNumbers = new Set(ns);
        vm.runInContext('rebuildDedupTree2SectionBDelta()', c);
    }
    add(old); full();
    return {c, rows, full, append, tracks};
}
test('ordinary consecutive updates keep unquoted new posts unique', () => {
    const h = harness(); h.append(delta); h.append(next);
    assert.equal(h.rows().length, 12);
    assert.equal(h.rows().filter(r => !r.canonical).length, 0);
});
test('full redraw after first update keeps its ten posts unique on next update', () => {
    const h = harness(); h.append(delta); h.full(); h.append(next);
    assert.deepEqual(h.rows().map(r => r.number), [...old,...delta,...next]);
    assert.equal(h.c.allPosts.length, 12);
    assert.equal(h.rows().length, 12);
    vm.runInContext('updateRichScrollbar()', h.c);
    assert.equal(h.tracks.image.markers.length, 12);
    const markedRows = h.tracks.image.markers.map(m => Math.round(parseFloat(m.style.top) / 100 * 12));
    assert.deepEqual(markedRows, Array.from({length:12}, (_,i)=>i));
});
test('subsequent full redraw also keeps current new post unique', () => {
    const h = harness(); h.append(delta); h.full(); h.append(next); h.full();
    assert.deepEqual(h.rows().map(r => r.number), [...old,...delta,...next]);
});
