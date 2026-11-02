const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const source = fs.readFileSync(path.join(__dirname, '../src/ChBrowser/Resources/thread.js'), 'utf8');
const start = source.indexOf("document.addEventListener('contextmenu', function(e) {");
assert.ok(start >= 0, 'contextmenu listener exists');
const endMarker = '\n    });';
const end = source.indexOf(endMarker, start) + endMarker.length;
assert.ok(end > start, 'contextmenu listener closes');
const listenerSource = source.slice(start, end);

function dispatch({ selection = '', postNo = null, url = null } = {}) {
    const messages = [];
    const calls = [];
    let prevented = false;
    const target = { closest(selector) {
        if (selector === '.post-no' && postNo !== null) return { dataset: { number: String(postNo) } };
        if (selector === 'a[data-url], .image-slot[data-url]' && url !== null) return { dataset: url, classList: { contains: c => c === 'image-slot' && !!url.isSlot } };
        return null;
    } };
    const context = vm.createContext({
        document: { addEventListener(type, callback) { assert.equal(type, 'contextmenu'); context.callback = callback; } },
        window: { getSelection: () => ({ toString: () => selection }), chrome: { webview: { postMessage: m => messages.push(m) } } },
        postPostNoContextMenu: n => calls.push(['postNo', n]),
        postUrlContextMenu: (...args) => calls.push(['url', ...args]),
        parseInt, isNaN,
    });
    vm.runInContext(listenerSource, context);
    const event = { target, preventDefault() { prevented = true; }, stopPropagation() {} };
    context.callback(event);
    return { messages, calls, prevented };
}

test('selected text does not steal image or video URL context menus', () => {
    const image = dispatch({ selection: 'sticky', url: { url: 'https://image.test', isSlot: true, src: 'https://cdn.test/a.png' } });
    assert.deepEqual(image.calls, [['url', 'https://image.test', 'image', 'https://cdn.test/a.png']]);
    assert.equal(image.prevented, true);
    const video = dispatch({ selection: 'sticky', url: { url: 'https://video.test', isSlot: true, mediaType: 'video', src: 'https://cdn.test/a.mp4' } });
    assert.deepEqual(video.calls, [['url', 'https://video.test', 'video', 'https://cdn.test/a.mp4']]);
    assert.equal(video.prevented, true);
});

test('selected text does not steal post-number context menu', () => {
    const r = dispatch({ selection: 'sticky', postNo: 42 });
    assert.deepEqual(r.calls, [['postNo', 42]]);
    assert.equal(r.prevented, true);
});

test('ordinary selected text falls through to native context menu', () => {
    const r = dispatch({ selection: '  日本語 & + #\n query  ' });
    assert.equal(r.messages.length, 0);
    assert.equal(r.prevented, false);
});

test('without selection, post number and URL context menus keep their existing routes', () => {
    assert.deepEqual(dispatch({ postNo: 42, url: { url: 'https://example.test' } }).calls, [['postNo', 42]]);
    assert.deepEqual(dispatch({ url: { url: 'https://example.test' } }).calls, [['url', 'https://example.test', '', '']]);
    assert.equal(dispatch().prevented, false);
});

test('whitespace selection falls through to existing post-number and media routes', () => {
    assert.deepEqual(dispatch({ selection: ' \n\t ', postNo: 7 }).calls, [['postNo', 7]]);
    assert.deepEqual(dispatch({ selection: '  ', url: { url: 'https://image.test', isSlot: true, src: 'https://cdn.test/a.png' } }).calls,
        [['url', 'https://image.test', 'image', 'https://cdn.test/a.png']]);
    assert.equal(dispatch({ selection: '  ' }).prevented, false);
});
