const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const source = fs.readFileSync(path.join(__dirname, '../src/ChBrowser/Resources/thread.js'), 'utf8');
const start = source.indexOf('    function findBottommostPrimaryInRange(N)');
assert.ok(start >= 0);
const end = source.indexOf('\n    }', start) + 6;
const functionSource = source.slice(start, end);

function restore(numbers, positions, target) {
    const lookups = [];
    const context = vm.createContext({
        allPosts: numbers.map(number => ({ number })),
        window: { scrollY: 1400 },
        document: {
            getElementById(id) {
                lookups.push(id);
                // Fail fast if sparse IDs are accidentally expanded into a numeric range.
                assert.ok(lookups.length <= numbers.length, 'DOM lookups must be bounded by post count');
                return Object.hasOwn(positions, id)
                    ? { id, getBoundingClientRect: () => ({ top: positions[id] }) }
                    : null;
            },
        },
    });
    vm.runInContext(functionSource, context);
    const result = vm.runInContext(`findBottommostPrimaryInRange(${target})`, context, { timeout: 1000 });
    return { id: result?.id ?? null, lookups };
}

test('Futaba: sparse billion-scale IDs select the visually lowest read post', () => {
    const result = restore([1432658445, 1432660312, 1432673460], {
        r1432658445: 200, r1432660312: 100, r1432673460: 500,
    }, 1432660312);
    assert.equal(result.id, 'r1432658445');
    assert.deepEqual(result.lookups, ['r1432658445', 'r1432660312']);
});

test('5ch: contiguous IDs preserve DOM-order selection', () => {
    assert.equal(restore([1, 2, 3, 4], { r1: 0, r2: 300, r3: 100, r4: 500 }, 3).id, 'r2');
});

test('Missing or filtered posts and an absent target are skipped', () => {
    assert.equal(restore([100, 200, 400], { r100: 10, r400: 400 }, 300).id, 'r100');
    assert.equal(restore([100, 200], {}, 200).id, null);
});

test('Empty posts or target before the first post need no DOM lookups', () => {
    assert.deepEqual(restore([], {}, 1432660312), { id: null, lookups: [] });
    assert.deepEqual(restore([1432658445], {}, 1), { id: null, lookups: [] });
});

test('Equal vertical positions preserve the first post', () => {
    assert.equal(restore([1, 2], { r1: 100, r2: 100 }, 2).id, 'r1');
});
