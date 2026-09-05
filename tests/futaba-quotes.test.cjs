const assert = require('node:assert/strict');
const { test } = require('node:test');
const FutabaQuotes = require('../src/ChBrowser/Resources/futaba-quotes.js');
const info = (lines, attachments = []) => ({ lines: lines.map(([text, quoteDepth = 0]) => ({ text, quoteDepth, rawHtml: text })), attachmentUrls: attachments, rawHtml: '' });
const post = (number, lines, attachments = []) => ({ number, futabaQuoteInfo: info(lines, attachments) });
test('ordinary body after a quote resolves to the direct parent', () => {
  const result = FutabaQuotes.resolve([
    post(1432660797, [['そもそもビッくらポンが出てくるか出てこないかすらランダムなのどうなんだ']]),
    post(1432661031, [
      ['そもそもビッくらポンが出てくるか出てこないかすらランダムなのどうなんだ', 1],
      ['びっくらぼん当選抽選']
    ])
  ]);
  assert.equal(result[1].state, 'resolved');
  assert.deepEqual(result[1].parentNumbers, [1432660797]);
});
test('旅行の三段引用は各直前の投稿へ接続する', () => {
  const result = FutabaQuotes.resolve([post(4524, [['再来週から連休なので旅行に行く']]), post(4980, [['再来週から連休なので旅行に行く', 1], ['どこいくので？']]), post(5033, [['再来週から連休なので旅行に行く', 2], ['どこいくので？', 1], ['上に書いたけどディズニーので']]), post(5126, [['再来週から連休なので旅行に行く', 3], ['どこいくので？', 2], ['上に書いたけどディズニーので', 1]])]);
  assert.deepEqual(result.map(x => x.parentNumbers), [[], [4524], [4980], [5033]]);
});
test('deep No reference resolves its direct context parent only', () => {
  const result = FutabaQuotes.resolve([
    post(100, [['ancestor']]),
    post(200, [['No.100', 1], ['direct parent']]),
    post(300, [['No.100', 2], ['direct parent', 1], ['reply']])
  ]);
  assert.deepEqual(result[2].parentNumbers, [200]);
});
test('multiple quote blocks use the first resolved parent', () => {
  const result = FutabaQuotes.resolve([
    post(100, [['first quoted source']]), post(200, [['second quoted source']]),
    post(300, [['first quoted source', 1], ['reply'], ['unknown quoted source', 1]])
  ]);
  assert.equal(result[2].state, 'resolved');
  assert.deepEqual(result[2].parentNumbers, [100]);
});
test('同じ本文の複数投稿は直近固定せず未解決にする', () => {
  const result = FutabaQuotes.resolve([post(10, [['同じ短い文章です']]), post(20, [['同じ短い文章です']]), post(30, [['同じ短い文章です', 1]])]);
  assert.equal(result[2].state, 'unresolved');
});
test('No. と同名添付の曖昧性を扱う', () => {
  const attachment = 'https://may.2chan.net/b/src/1788556515329.jpg';
  const result = FutabaQuotes.resolve([post(100, [['画像']], [attachment]), post(200, [['画像']], [attachment]), post(300, [['>No.100', 1]]), post(400, [['1788556515329.jpg', 1]])]);
  assert.deepEqual(result[2].parentNumbers, [100]);
  assert.equal(result[3].state, 'unresolved');
});
test('短文・引用元なしは接続しない', () => {
  const result = FutabaQuotes.resolve([post(1, [['外部記事の引用']]), post(2, [['はい', 1]]), post(3, [['存在しない文章', 1]])]);
  assert.deepEqual(result.slice(1).map(x => x.parentNumbers), [[], []]);
});
test('一部だけ一致する引用ブロックは曖昧として親を返さない', () => {
  const result = FutabaQuotes.resolve([
    post(1, [['一致する本文']]), post(2, [['一致する本文', 1], ['外部記事の引用', 1], ['返答']])
  ]);
  assert.equal(result[1].state, 'ambiguous');
  assert.deepEqual(result[1].parentNumbers, [1]);
});
test('引用2行と本文1行は引用ブロック全体を解決できる', () => {
  const result = FutabaQuotes.resolve([
    post(100, [['店員が当たり抜くのがどこの店でも常態化してたんなら', 0], ['もうこれ企業ぐるみの詐欺みたいなもんでは', 0]]),
    post(200, [['店員が当たり抜くのがどこの店でも常態化してたんなら', 1], ['もうこれ企業ぐるみの詐欺みたいなもんでは', 1], ['どこの店でもってわけじゃないよ流石に', 0]])
  ]);
  assert.equal(result[1].state, 'resolved');
  assert.deepEqual(result[1].parentNumbers, [100]);
});
