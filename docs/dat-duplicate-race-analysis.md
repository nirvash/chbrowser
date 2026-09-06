# 5ch dat 重複レス調査

## 問題

5ch の「なんJNVA部★695」で、`get_posts` の取得結果にレス154/155付近の重複が現れた。

当初は `get_posts(start=300,end=309)` の範囲解釈変更による副作用も疑ったが、5ch の `DatParser` は dat の行番号を1始まりで `Post.Number` にするため、相対番号と実レス番号は通常一致する。`get_posts` の範囲変更だけで dat の内容が重複することはない。

## 調査結果

対象ローカルファイル:

`C:\Users\nirva\Documents\Apps\ChBrowser\data\5ch.io\liveuranus\1788663727.dat`

- `subject.txt` の表示件数: 154
- ローカル dat の行数: 155
- ローカル dat の154行目と155行目はバイト列・内容が完全一致
- 現在のサーバー dat は155行で、サーバーの154行目と155行目は別内容
- ローカル dat はサーバーの現在データと58854バイト目から差分がある
- したがって、重複は現在の5chサーバーが返しているものではなく、ローカル保存時に混入した

## コード上の原因候補

`src/ChBrowser/Services/Api/DatClient.cs` の `FetchStreamingAsync` は、同一スレッドに対する同時実行を直列化していない。

差分取得は次の流れになっている。

1. 保存済みファイルのサイズを読む
2. `Range: bytes=<existing>-` を付けて取得する
3. `206 Partial Content` の場合、保存済みレスを読み込む
4. `FileMode.Append` で新しいレスを追記する

この間に同じスレッドの fetch が2本走ると、両方が同じ `existing` サイズを読み、同じ Range 応答を受け、同じ新着行を append できる。

```text
fetch A: existing = N -> Range N- -> append response
fetch B: existing = N -> Range N- -> append same response
```

`DatClient` には、`host/board/threadKey` 単位の fetch 排他も、206応答の `Content-Range` 開始位置と `existing` の照合もない。

これは今回のローカル dat に「同じ154行目が154/155として2回保存されている」事実と整合する。過去の取得ログが残っていないため、当時実際に2本の fetch が同時実行されたことまでは直接記録できないが、現実的な混入経路として最有力である。

## `get_posts` との関係

`get_posts` は `ctx.Posts` を返しているだけで、dat やサーバーには書き込まない。

重複行がパースされると、`DatParser` は各行を別レスとして扱うため、同一内容が異なる番号で返る。

```text
dat line 154 -> Post.Number = 154
dat line 155 -> Post.Number = 155
```

そのため、`get_posts` は重複を発生させたのではなく、ローカル dat に既に入っていた重複を可視化したものと判断できる。

## 対応済みの修正

### 1. 同一スレッドの fetch を直列化する

`DatClient` にキャッシュファイルの実パス単位の `SemaphoreSlim` を追加した。

- キー: `DataPaths.DatPath(...)` が返すキャッシュファイルの実パス
- `FetchStreamingAsync` の Range 判定からファイル書き込み完了までをロック内で実行
- 異なるスレッドの取得は並列性を維持
- `DatClient` の寿命中は lock キーを保持する。待機者がいる間に単純にキーを回収すると別の lock を作る競合になるため、回収は行わない

後続の fetch は先行 fetch の書き込み完了後に `existing` を読み直すため、同じ Range 応答を二重に append しない。

### 2. `Content-Range` を検証する

206応答時に次を必須検証する。

- `Content-Range.From == existing`（.NET のプロパティ名は `Start` ではなく `From`）
- `To` / 総長 / `Content-Length` が矛盾していない
- append 前にローカルファイルの実長が `existing` と一致している

不一致なら append せずエラーにする。異常なサーバー応答時に既存キャッシュを失わないよう、ここで自動全件再取得はしない。

### 3. 本文一致による自動削除は行わない

本文が同じでもレス番号が異なるなら通常の同文連投であり、重複ではない。

- dat は行位置から `Post.Number` を付与するため、破損後のキャッシュだけを本文比較で自動修復することはできない
- 同文の先頭・末尾行を比較して破棄すると、正常なレスを失うため実装しない
- 同一 Range を並行 append させないことと、byte-range 検証を正しい防御層とする

### 4. 既存キャッシュを修復する

修正後に、既存 dat を無条件に信用しない。

- まず対象 dat を削除して再取得する、または
- サーバー全件との差分検証後に重複行を修復する

今回の対象はローカルキャッシュを削除して再取得すれば、少なくとも154/155の重複は解消できる。ただし、削除・再取得はユーザーのキャッシュ状態を変更するため、実施時は対象ファイルを明示する。

## 検証項目

- `tests/ChBrowser.Api.Tests` に同一 thread key への `FetchAsync` 同時呼び出しを再現する回帰テストを追加
- 先行 fetch 中に後続 fetch が同じ Range を発行しないことを確認
- 後続 fetch が書き込み後のサイズで Range を再計算することを確認
- 本文が同じでもレス番号 1 / 2 が異なる2レスは両方保持されることを確認
- `dotnet run --project tests/ChBrowser.Api.Tests/ChBrowser.Api.Tests.csproj` が成功
- `dotnet build src/ChBrowser/ChBrowser.csproj -c Debug` が警告0・エラー0で成功

## 現時点の判定

- 5chサーバーへの書き込み副作用: 確認されていない
- `get_posts` が重複を生成した: 否定的
- ローカル dat に重複行が存在する: 確認済み
- 混入原因: 同一スレッド差分取得の競合が最有力
- 同時 fetch の実行履歴: 既存ログ不足で未確認
- 修正実装: 対応済み（`147f541 Prevent concurrent dat cache appends`）
- ローカル配布: 対応済み（Release publish、配置先 exe とテーマの SHA-256 一致を確認）
- 既に破損している対象 dat の修復: 未実施。対象ファイルを明示して削除・再取得またはサーバー全件比較を行う必要がある
