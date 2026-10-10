const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');
const source = fs.readFileSync(path.join(__dirname, '../src/ChBrowser/Resources/thread.js'), 'utf8');
const rendering = source.slice(source.indexOf('    const TAG_BLOCK_RE'), source.indexOf('    // ---------- post HTML builders'));
const classify = source.slice(source.indexOf('    function isVideoMediaSlot('), source.indexOf('    function postHasNon404Media('));
const apply = source.slice(source.indexOf('    function applyImageMeta('), source.indexOf('    // ============================================================', source.indexOf('    function applyImageMeta(')));

function fixture() {
    const applied = [];
    const slots = [];
    const context = vm.createContext({
        escapeHtml: value => String(value).replace(/&/g, '&amp;').replace(/"/g, '&quot;'),
        imageMetaCache: new Map(),
        currentFilter: { mediaOnly: false },
        document: { querySelectorAll: () => slots },
        applyMetaToSlot: (slot, meta) => applied.push({ slot, meta }),
    });
    vm.runInContext(rendering + classify + apply + '\nthis.render = buildBodyAndMedia; this.apply = applyImageMeta;', context);
    return { context, slots, applied };
}
function slot(mediaType, src, classes) {
    const set = new Set(classes);
    return { dataset: { mediaType, src }, classList: { contains: name => set.has(name) } };
}

test('short YouTube URL reaches thumbnail loading after image metadata response', () => {
    const { context, slots, applied } = fixture();
    const url = 'https://youtu.be/0KTjdndbDHw';
    const thumb = 'https://i.ytimg.com/vi/0KTjdndbDHw/hqdefault.jpg';
    const rendered = context.render(' ' + url + ' ');
    assert.ok(rendered.media.includes('data-video-id="0KTjdndbDHw"'));
    assert.ok(rendered.media.includes('data-src="' + thumb + '"'));
    assert.ok(rendered.media.includes('data-url="' + url + '"'));
    const youtube = slot('youtube', thumb, ['image-slot', 'youtube', 'deferred']);
    slots.push(youtube);
    context.apply(thumb, { ok: true, size: 50000 });
    assert.equal(applied.length, 1, 'YouTube thumbnail must enter the image loading path');
    assert.equal(applied[0].slot, youtube);
    assert.equal(youtube.dataset.mediaReachable, 'true');
});

test('direct videos remain excluded from image metadata handling', () => {
    const { context, slots, applied } = fixture();
    const url = 'https://example.test/movie.mp4';
    slots.push(slot('video', url, ['image-slot', 'video', 'deferred']));
    context.apply(url, { ok: true });
    assert.equal(applied.length, 0);
});

test('image metadata applies only to matching image slots', () => {
    const { context, slots, applied } = fixture();
    const image = slot('image', 'https://example.test/image.jpg', ['image-slot', 'deferred']);
    slots.push(image, slot('youtube', 'https://i.ytimg.com/vi/other-ID123/hqdefault.jpg', ['image-slot', 'youtube', 'deferred']));
    context.apply(image.dataset.src, { ok: false });
    assert.equal(applied.length, 1);
    assert.equal(applied[0].slot, image);
    assert.equal(image.dataset.mediaReachable, 'false');
});
