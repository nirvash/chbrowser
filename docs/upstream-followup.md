# upstream 追従履歴

フォーク元 `pawapawa041950/chbrowser` の `upstream/main` を確認し、フォーク側の独自実装へ必要なロジックだけを移植した履歴です。

## 2026-09-05

### `d1d83d6` 絵文字が化けて書き込まれてしまっていたのを修正

- 対応コミット: `b97be58` (`main`)
- `PostClient` の投稿フォームエンコードで、Shift_JIS にない文字を `?` に置換しないよう修正。
- 共有の `HtmlEntityFallbackEncoder` を使い、絵文字などを `&#xNNNN;` の数値文字参照へ変換。
- `tests/ChBrowser.Api.Tests` に投稿エンコードの回帰テストを追加。

### `c31d7ff` プロンプト取得に失敗する動画に新たに対応

- 対応コミット: `c28cca9` (`main`)
- `AiImageMetadataService` のComfyUIノード入力フォールバックに `string` フィールドを追加。
- `string1` / `string2` などに分割されたカスタムノードのプロンプトを取得可能にした。
- `system_prompt` と `negative_prompt` は正のプロンプト候補から除外。
- 回帰テストで `string` の取得と `system` / `negative` の除外を確認。
- これは暫定対応であり、将来的にはIllustra側の構造化されたメタデータパーサ移植を検討する。

### `3afa66f` 連鎖あぼーんが効かない条件の修正

- 対応コミット: `53c5b7d` (`main`)
- 差分取得時に、過去バッチでhiddenになったレス番号を新着バッチの連鎖あぼーん判定へ引き継ぐよう修正。
- `ThreadTabViewModel.HiddenPostNumbers` にhidden済み番号を累積し、`NgService` の判定へ渡す。
- `string` / `system` の暫定対応と同様、回帰テストを追加して確認。

### `e562e1a` scom-v製動画にラベルを付けるように変更

- 対応コミット: `cfaab97` (`main`)
- 動画メタデータの `software` タグから `scom-v` / `scom` を判定。
- ComfyUIグラフ解析結果を維持しつつ、生成アプリ名をGeneratorラベルへ反映。
- バージョン付き・JSON引用符付きのタグを正規化する回帰テストを追加。

## 未取り込み

現在なし。

upstreamコミットをそのままrebase/cherry-pickするのではなく、フォーク側の独自変更を維持したまま、必要なロジックを手動移植している。
