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
        if (selector === 'a[data-url], .image-slot[data-url]' && url !== null) return { dataset: url, classList: { contains: c => c === 'image-slot' && !!url.mediaType } };
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

test('selected text is snapshotted at context-menu time before native focus changes', () => {
    const r = dispatch({ selection: '  日本語 & + #\n query  ' });
    assert.equal(r.messages.length, 1);
    assert.equal(r.messages[0].type, 'selectionContextMenu');
    assert.equal(r.messages[0].text, '  日本語 & + #\n query  ');
    assert.equal(r.prevented, true);
});

test('without selection, post number and URL context menus keep their existing routes', () => {
    assert.deepEqual(dispatch({ postNo: 42, url: { url: 'https://example.test' } }).calls, [['postNo', 42]]);
    assert.deepEqual(dispatch({ url: { url: 'https://example.test' } }).calls, [['url', 'https://example.test', '', '']]);
    assert.equal(dispatch().prevented, false);
});

test('whitespace selection falls through to existing post-number and media routes', () => {
    assert.deepEqual(dispatch({ selection: ' \n\t ', postNo: 7 }).calls, [['postNo', 7]]);
    assert.deepEqual(dispatch({ selection: '  ', url: { url: 'https://image.test', mediaType: 'image', src: 'https://cdn.test/a.png' } }).calls,
        [['url', 'https://image.test', 'image', 'https://cdn.test/a.png']]);
    assert.equal(dispatch({ selection: '  ' }).prevented, false);
});
