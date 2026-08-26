# AGENTS.md

コード変更時のための実装メモ。利用者向け情報は README.md を参照。
本書はコードリーディングで判明した基本設計・内部構造の知識ベースであり、後続の改修で再利用することを意図する。

## LOCAL.md について

リポジトリ直下の `LOCAL.md` は**このマシン固有の情報を置くファイル**で、`.gitignore` 対象
(commit 禁止)。デイリー利用 exe の配置先など、環境依存の作業メモが書かれる。
エージェントは存在すれば読んで作業の参考にしてよいが、内容を main へ commit してはならない。
本書 (AGENTS.md) に書くのは「このプロジェクト共通で永続したい知識」のみとし、
特定マシンにしか当てはまらない情報 (絶対パス等) は LOCAL.md の方へ置く。

## 実装手順

機能追加・改修は次の流れで行う:

1. **仕様を確認** — 実装前にユーザと仕様を合意する。曖昧な点は質問して潰す。
   類似機能が既に無いかコードを調査してから着手する (重複実装の防止)
2. **作業ブランチ作成** — main から feature ブランチを切ってから作業を始める
   (例: `git checkout -b feature/video-loop`)
3. **実装** — 本書の設計知識に基づいて変更する。完了時に `dotnet build` を通し
   0 警告 0 エラーを確認する
4. **ユーザによる動作確認** — 動作の良し悪しの判定はユーザが実際にアプリを実行して行う。
   エージェント側で「動くはず」と判断して次へ進めない。確認依頼 → 指示待ち
5. **main への反映** — 動作確認が取れたら、次のいずれかで main へ反映する
   (**どちらを使うかはユーザが判断する**):
   - **PR レビュー** — ブランチを push して `gh pr create` で PR を出し
     (base = 自フォーク `nirvash/chbrowser` の main)、レビュー指摘に対応してからマージする
   - **ローカルマージ** — 先に `git fetch origin` して main を最新化してから
     (`git pull --ff-only`。ローカル main が古いと squash 差分に他変更が混入する
     — 実害が発生済み)、ローカルで `git merge --squash <branch>` + 単一コミットで
     main に取り込んで push する
   いずれの経路でも Squash and merge 相当とし、マージコミットを履歴に残さない
   (= main のコミット履歴は機能 1 件 = コミット 1 件の一直線を保つ)

直接 main にコミットしない。

## ビルド

```pwsh
dotnet build src/ChBrowser/ChBrowser.csproj -c Debug
dotnet publish src/ChBrowser/ChBrowser.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

出力: `src/ChBrowser/bin/Release/net8.0-windows/win-x64/publish/ChBrowser.exe`

## 配布テーマフォルダ (`themes/`)

- リポジトリ直下 `themes/` は**配布用テーマファイルの置き場所**。埋め込みリソース外で version 管理
  し、`data/themes/` と同じ構造をミラーする (現状 `themes/default/image-404.png` のみ)
- `image-404.png` のオリジナル (1254x1254) はリポジトリ外で管理し、配布には 512x512 縮小版を使う
- アプリはこのフォルダを参照しない (= 純配布物)。不足時の自動補充等も無く、
  反映は手動で `<配置先>\data\themes\default\` へコピーする (配置先パスは LOCAL.md 参照)

デイリー利用環境への反映手順 (手動):

1. ChBrowser プロセスが起動していないことを確認する
2. publish 出力の `ChBrowser.exe` を配置先へ上書きコピー
3. `themes/default/` 配下を `<配置先>\data\themes\default\` へコピー

## 全体構成

- WPF (.NET 8) + CommunityToolkit.Mvvm。`ObservableProperty` ソースジェネレータ主体の MVVM
- **WPF 側はシェル兼メッセージ仲介のみ**。スレ表示 / スレ一覧 / 板一覧 / お気に入り / AI チャット /
  画像ビューアの UI 実体はすべて WebView2 上の埋め込み HTML/CSS/JS
  (`src/ChBrowser/Resources/*.html|css|js`) で描かれる
- 埋め込みアセットは csproj の `EmbeddedResource`。`Services/Render/EmbeddedAssets` が読み出しを担い、
  CSS だけは `data/themes/default/` 配下に同名ファイルがあればそちらを優先する (ユーザテーマ上書き)
- WebView2 への HTML 注入・メッセージ push は `Controls/WebView2Helper*.cs` の添付プロパティ群に集約
  (`ImageUrl` / `ViewMode` / `ThreadConfigJson` / `PaneConfigJson` / 各ショートカット JSON 等)

## データ保存 (`data/`、ポータブル。exe と同じフォルダ)

| パス | 内容 |
|---|---|
| `app/config.json` | AppConfig (camelCase JSON)。**既存キーの rename 禁止** (互換) |
| `app/favorites.json` / `app/layout.json` / `app/open_tabs.json` | お気に入り / ウィンドウ+ペインレイアウト / 開いていたタブ |
| `app/ng/rules.json` | NG ルール |
| `5ch.io/<板dir>/` | `subject.txt` / `<key>.dat` / `<key>.idx.json` (既読位置・自分レス番号等) |
| `cache/images/` | 画像キャッシュ (上限 `CacheMaxMb`)。動画キャッシュも同系統 |
| `donguri/` | どんぐり Cookie / 推定 Lv |

開発時は環境変数 `CHBROWSER_DATA_DIR` でデータディレクトリを差し替え可能。

## タブ / ペイン / セッション復元

- タブ集合は `PaneGroupViewModel<TTab>` 系。スレ表示 = `ThreadPaneGroups`、スレ一覧 =
  `ThreadListPaneGroups` で複数ペイン対応。`MainViewModel.ThreadTabs` 等は「アクティブグループへの facade」
- 終了時に `SaveOpenTabsToDisk()` がペイン別のタブ並び + **選択インデックス + アクティブペインキー** を
  open_tabs.json へ保存し、起動時に `RestoreOpenTabs()` が同順で再オープンする。
  ON/OFF は `AppConfig.RestoreOpenTabsOnStartup` (既定 ON)。OFF でも保存は常に行われる
- スクロール位置は JS からの `scrollPosition` メッセージを in-memory 保持し、終了時に
  `FlushScrollPositionToDisk` で idx.json へ一括書き出し。再オープン時 `ScrollTargetPostNumber` として復帰
- スレオープンの本体は `MainViewModel.OpenThreadAsync`: ディスクキャッシュ dat を即表示 → サーバ差分取得
  (`FetchedPostCount` を境界に管理) → `ComputeMarkState` でログ状態算定 (Dropped > RepliedToOwn > Cached)

## スレ表示 (thread.js) と C# ↔ JS プロトコル

- 各 JS ファイル冒頭ヘッダコメントにプロトコル (受信 type / 送信 type) を文書化する文化。
  新規メッセージ追加時はここも更新する
- JS → C#: `window.chrome.webview.postMessage({ type: '...' })`。受信は各ペインのコードビハインドの switch
  (`Views/Panes/ThreadDisplayPane.xaml.cs` の `HandleOpenUrl` / `HandleScrollPosition` 等)
- C# → JS: 上記添付プロパティ経由の `PostWebMessageAsJson` push。**初回 bind (= null からの変化) でも
  発火するため新しく開いたタブにも現在値が届く**。新規タブ向けの個別配慮は不要
- 本文内の `/test/read.cgi/<dir>/<key>(/<postNo>)?` URL は JS 側 `FIVECH_THREAD_RE` で検出され
  `.thread-link` になる。クリック → `{type:'openUrl'}` → `HandleOpenUrl` が `AddressBarParser.Parse`
  (C# 側の純粋関数パーサ) で判定してアプリ内オープン or 外部ブラウザ。ホバーでタイトル+レス数プレビュー
- 本文 linkify の URL 検出は `URL_OR_ANCHOR_RE` 1 本で行い、prefix セットは 4 alt =
  sssp:// / フル形 (https?://) / 省略形 (ttp:// 等。直前が英字なら不成立) /
  **裸ドメイン形** (`imgur.com/a/X` 等スキーム完全省略。capture group 4 = ドメイン部)。
  裸ドメイン形の規則 (= issue #6「本文記載の URL っぽい文字列のリンク化」):
  実体は常に `https://` 固定で正規化し表示テキストは元文字列のまま (= 省略形と同じ方針)。
  パス (`/` 以降) 必須 (= パス無し "英字.英字" はテンプレ FAQ の `Q.NovelAI` や箇条書き
  `1.user` 等の誤検出が支配的のため対象外。スレに貼られる裸 URL は実質すべてパス付きなので
  取りこぼしはない)、TLD は英字 2+ 文字限定 (`0.774` / IP アドレス / `v1.2.3` を排除)、
  ドメイン部最終ラベルが拡張子っぽいトークン (`read.cgi/JNVA/<key>` 等) は `BARE_DOMAIN_FILE_EXT`
  拒否リストで除外 (= 判定はドメイン部のみ。パス末尾の拡張子は対象外なので
  `example.com/files/x.pdf` は正しくリンク化される)。既知限界として `cat～box.moe` のように
  ドメイン内部へ文字を挿入する NG 回避記法は残滓 (`box.moe/`) が別リンク化される = 汎用ルールでは
  区別不可能なため許容。同期対象: `BODY_URL_RE` / `HAS_URL_RE` (リッチスクロールバー判定)、
  C# 側 `MediaUrlExtractor.BodyUrlRe` (先読み抽出)
- スレ内動画の再生要素生成は `playMedia()` の 1 箇所のみ (クリックで `<video controls autoplay>` 生成)。
  サムネ抽出用の非表示 `<video>` は別経路 (`extractAndCacheVideoThumbnail`, canvas から 240px JPEG を
  `videoThumbnailCache` メッセージで C# へ)

## メディア処理

- 画像は URL 単位で `cache/images/` にキャッシュ。スロットには `data-cache-state` が付き、
  cached / deferred (サイズしきい値 `ImageSizeThresholdMb` 以上はクリックまで取得しない) を区別
- サムネイル右クリック「保存」(`ThreadDisplayPane.SaveMediaToConfiguredDirAsync`) は設定の
  `ImageSaveDir` / `VideoSaveDir` へ無ダイアログ保存 (同名は `_1` 連番退避)。未設定時は
  「名前を付けて保存」ダイアログ (= 従来動作、選択結果は記憶しない)。キャッシュ済みはコピー、
  未 DL は SaveDirectAsync で直接 DL
- 動画はクリック時に `VideoDownloadManager` が並列 DL を kick。ヒット時は仮想ホスト
  `https://chbrowser-cache.local/videos/...` 経由でローカル再生 (`PlaybackUrl`)
- お気に入りスレのメディア先読みは `Services/Media/MediaPrefetchService.cs`
  (App シングル `MediaPrefetchServiceInstance`)。呼び出し経路は 1 本化されており、スレオープン /
  差分取得 (`ApplyFetchDelta`) / **お気に入り登録時** (`ToggleThreadFavorite` 追加ブランチ。
  既オープンタブの既読分を投入) / 将来の自動巡回が `EnqueueForPosts(board, key, posts)` を呼ぶだけ。
  お気に入り判定は App.OnStartup で `IsThreadFavorited` デリゲートに `mainVm.Favorites` を配線した単一箇所。
  URL 抽出は `MediaUrlExtractor` (= thread.js `buildMediaSlotForUrl` 系判定ルールの C# 移植。
  JS 側を変えたら同期)。画像 = 同時 2 本・Content-Length 判明時にしきい値事前判定、動画 =
  逐次ポンプで `VideoDownloadManager.Request` に委譲 (= クリック DL とコアレス)。
  設定は `PrefetchImagesOnThreadLoad` (既定 ON) / `PrefetchVideosOnThreadLoad` (既定 OFF)。
  DL 完了は `ThreadDisplayPane.WireVideoDownloadCompletionToPane` が全タブ WebView へ
  `videoCacheState` broadcast する (先読み起因完了には要求元記録が無いため要求元限定 push では反映されない)
- 非同期 URL 展開は `Services/Image/UrlExpander.cs`: x.com → api.fxtwitter.com JSON
  (引用 RT の quote 側メディアも探索)、pixiv → ajax/illust (Referer 必須)、imgur アルバム
  (`imgur.com/a/<id>`) → アルバムページ HTML の og:image (表紙サムネ。404/410 = NoMedia)。
  JS 側のパターン集合は thread.js `ASYNC_EXPANDER_RES` で同期して維持する。
  展開結果は 3 値 (Resolved / NoMedia = スロットごと削除 / Unavailable = クリック再試行可)
- 削除済みメディアの扱い: imgur は削除済み画像を **removed.png へのリダイレクト (HTTP 200)** で返すため、
  `ImageMetaService` が HEAD 最終 URI (`IsImgurRemovedPlaceholder`) で検出して tracker 記録 +
  imageLoadFailed=true (= スロットはテーマの image-404.png 表示。先読みもキャッシュしない)。
  削除済み動画は imgur トップ HTML へのリダイレクト (200 text/html) になるため
  `VideoDownloadManager.DownloadAsync` が video/* / octet-stream 以外の content-type を失敗扱いにする
  (= HTML を .mp4 としてキャッシュする「キャッシュ済みなのに再生不能」事故の防止)。 Gone (404/410/403)
  判定はしない (= 一時的エラーページの可能性を残す)。サムネ抽出失敗済み動画
  (`thumbExtractFailed=true` & サムネ無し) のスロットも load-failed (404 アート) 表示になり、
  展開失敗スロット (expand-failed) も load-failed と見た目を統一 (= 意味クラスとしてのみ保持)
- 前後スレナビ (`OpenNavTargetAsync`) は**移動元スレがお気に入りの場合のみ**移動先を自動でお気に入り追加する
  (= 連番チェーンを読み進めた先をすべて先読み対象に揃える。移動元未登録なら連鎖も自動登録も起きない)
- AI 生成画像メタデータは `Services/Image/AiImageMetadataService.cs` (NuGet 依存ゼロの手製パーサ):
  PNG tEXt/XMP/LSB ステルス、JPEG EXIF、WebP XMP、MP4/WebM コンテナ (未キャッシュ動画は HTTP Range で
  メタ部のみ取得)。ComfyUI workflow グラフ解析込み。結果はサムネイル左上バッジ行の
  「P」ボタン (プロンプトがあるときのみ表示) クリックでポップアップ表示 / ビューア詳細ペインに表示

- スレ上部ツールバーの 🖼 スライダ (`ThreadDisplayPane.xaml` / `MainViewModel.ThreadSlotScaleUi`) は
  メディアスロットの全体既定スケールを調整する。ドラッグ中は 100ms 間隔で軽量 push
  (`PushThreadSlotScaleLive`、VM 非接触)、放した時点で `UpdateAndPersistConfig`
  (`AppConfig.ThreadSlotScale` 永続化)。JS 側 `applyGlobalSlotScale` はスケール変更の前後で
  「ビューポート上端付近の投稿」をアンカーに scrollY を補正する (= 視線位置を固定)。
  個別ドラッグリサイズ (inline style) が優先される
- スレ表示のスクロール復元: idx.json の読了レス番号 + 投稿内オフセット (px) を基本とし、
  保存環境 (スロットスケール / ページズーム / ドキュメント高さ) が一致する場合は
  絶対 scrollY による完全復元を優先する (thread.js tryScrollToTarget の exact パス)
- スレ表示の Ctrl+ホイールは WebView2 標準ズームではなく**ページズーム倍率**
  (`AppConfig.ThreadPageZoom` 0.5–3.0) の変更に割り当てて永続化する
  (thread.js が wheel を preventDefault → `threadPageZoomDelta` → ペインが `wv.ZoomFactor` 設定 +
  MainViewModel 経由で debounce 保存。ready 時に復元)

## 設定システムの流れ

`AppConfig` (init-only record) → `SettingsViewModel` (UI ミラー) → `MainViewModel.ApplyConfig` →
各ペイン向け setConfig JSON → JS ハンドラ。

- 設定 1 件のタッチポイント: ① `Models/AppConfig.cs` ② `ViewModels/SettingsViewModel.cs`
  ([ObservableProperty] 宣言 / 初期値流し込み / 保存用匿名オブジェクトの 3 箇所)
  ③ `Views/Settings/` の該当 Panel.xaml (スレ系=`ThreadPanel.xaml`、画像系=`ImagePanel.xaml`、
  AI 系=`AiPanel.xaml`/`AiNgPanel.xaml`、保存系=`SavePanel.xaml`) ④ 即時反映なら `ApplyConfig` の setConfig JSON へ追加
  ⑤ 対象 JS の setConfig ハンドラ。カテゴリ追加は `Categories.Add` + `SettingsWindow.xaml` の DataTemplate/DataTrigger も必要
- ApplyConfig が生成するのはペイン種別ごとの文字列プロパティ
  (`ThreadConfigJson` / `FavoritesConfigJson` / `BoardListConfigJson` / `ThreadListConfigJson`)。
  「次回起動時反映」項目は ApplyConfig に入れず起動時経路でのみ読む
- ビューアウィンドウ向けは別経路: App (`ApplyConfigImmediate`) が `ImageViewerViewModel.ConfigJson`
  を組み立て、ImageViewerWindow.xaml の各タブ WebView2 に bind された
  `WebView2Helper.ViewerConfigJson` 添付プロパティから viewer.js へ push される

## ショートカット / マウスジェスチャ

- 既定バインディングは `Services/Shortcuts/ShortcutRegistry.cs` にカテゴリ付きで定義
  (例: `thread.refresh`、ジェスチャ既定 `↓→`)。ユーザ上書きは `shortcuts.json`
- ディスパッチは `ShortcutManager.Dispatch(category, descriptor)` / `DispatchGesture`。
  カテゴリごとのアクション実装表は `App.xaml.cs` 内 (例: `"thread.refresh"` → `RefreshThread`)
- WebView2 内のキー/マウスイベントは `Resources/shortcut-bridge.js` ブリッジ経由で C# へ転送される。
  JS ローカルで完結するアクション (ズーム等) は bridge 初期化時の `localActions` に渡す

## LLM / エージェント / MCP

- `Services/Llm/LlmClient.cs`: OpenAI 互換 Chat Completions のみ (SSE ストリーム)。エンドポイントは
  base URL / 完全 URL 両方可。DeepSeek-R1 系の `reasoning_content` を `<think>` ブロック化
- AI チャットは 3 レイヤーエージェント `Services/Agent/NewAgentEngine.cs`
  (**Strategist** = plan 所有・重い操作は ask_user 承認ゲート / **Worker** = 使い捨て ReAct ループ /
  **ToolRuntime** = evidence id 付きツール実行)。UI 出力先は `IAgentHost`
  (`AiChatViewModel.AgentHost.cs` 実装)
- LLM 公開ツールの唯一の真実源は `Services/Llm/ToolCatalog.PublicToolsets()`。
  **内蔵エージェントと MCP サーバが同一表面を共有** (スレ読取・新着・ナビ・表示 21 + web_search/web_fetch の計 23)。
  `thread_url` 省略時は「現在選択中のスレ」が対象。URL 受理は 5ch.io / bbspink.com 系のみ。
  前後スレナビ系 (`get_nav_state` / `open_prev_thread` / `open_next_thread` /
  `find_next_thread_candidates` / `set_nav_target`) は attached 専用で、ナビ状態は
  `BuildToolsetForTab` がライブ読み取りデリゲート (`ReadNavSideForTool` / `ApplyNavMutationForToolAsync`,
  MainViewModel.ThreadNav.cs) として注入する (= スナップショットにしない。採用 → オープンの
  ツール連鎖中に状態が変わるため)。`get_new_posts` も同様にライブ状態デリゲート
  (`AttachedLiveState`) と差分取得デリゲート (`RefreshThreadViaToolAsync`, refresh=true 時のみ) を使う。
  `show_post_in_app` は `OpenThreadByUrlAsync(host, dir, key, postNumber)` の薄ラッパ
  (= 未オープンならオープン / 既存ならアクティブ化 + `PendingScrollToPost` 経由で JS scrollToPost)
- MCP サーバ (`Services/Mcp/McpHttpServer.cs`): Streamable HTTP 最小実装、**127.0.0.1 バインドのみ**
  (既定ポート 7393、`http://127.0.0.1:7393/mcp`)。設定で明示オフ (既定 OFF)、ON/OFF・ポート変更は即時反映。
  GET/SSE・バッチ・認証は非対応。tools/call は UI スレッドへマーシャリングされる
- AI NG (`ViewModels/MainViewModel.AiNg.cs` + `Services/Ng/AiNgJudge.cs`): レス単位で攻撃度 1..5 を
  別接続設定の LLM に採点させ、`NgAiThreshold` 以上を可逆非表示。スコアはスレ単位の `.aing.json` に
  **全件完了時のみ**保存。判定対象は選択中タブのみ

## 既知の制限・落とし穴

- **不具合対応の基本**: 修正の前に**ログを取って現象を再現・確認し、原因を特定する**。
  推測だけで修正を重ねると別問題を生み長時間溶ける (実害例: スライダ表示ジャンプは
  「JS 内未定義変数の例外」「PaneDragInitiator によるキャプチャ横取り」「Chromium ネイティブ
  スクロールアンカーとの二重補正」の 3 因子の重なりだった)。JS 側は
  `postMessage({ type:'jsDebug', text })` → C# の LogService 出力という計装が有効
- **ビルド成否は必ず `$LASTEXITCODE` / エラー出力で明示確認する**。失敗に気づかず古い exe を
  起動すると「修正したのに直らない」という別の不具合に見える (実害が発生済み)
- **プロセス停止は PID 指定のみ** (`Stop-Process -Id <pid>`)。自分が `Start-Process` で起動した
  インスタンスの PID を毎回記録し、再ビルド時はその PID だけを止める。
  `Get-Process -Name ChBrowser | Stop-Process` のような名前指定一括 kill は**禁止**。
  さらに「列挙して特定 PID だけ除外」する方式も**禁止** — ユーザが自分のインスタンスを
  再起動すると PID が変わり除外が無効化され、巻き込んで落とす (実害が発生済み)
  - **例外 (ユーザ許可済み, 2026-08)**: **Debug ビルドのプロセス** (= exe パスが
    `src\ChBrowser\bin\Debug` 配下のもの) はユーザが動作確認用に起動したものであり、
    停止してよい。exe パスで Debug 版と判定した個別 PID に対して
    `Stop-Process -Id <pid>` を使うこと (名前指定一括 kill の禁止は継続)。
    Release/publish 出力 (= デイリー利用 exe) は本例外の対象外
- `validate` 相当の機構は無し。JS 変更はビルドで埋め込まれるため、動作確認はビルド後の実行で行う
- 埋め込み JS の構文エラーは**ペイン全体の描画死**として表面化する (IIFE 全体が死ぬため「スレが真っ白」等)。
  ビルド前に `node --check src/ChBrowser/Resources/<file>.js` で構文検証できる
- WebView2 の OOM / クラッシュはノードエラーではなく接続断・タイムアウトとして表面化する
  (`get_logs` / ログウィンドウで確認)
- API キー類 (LLM / Worker / NG-AI / どんぐり) は config.json に平文保存 (DPAPI 化は未着手)
- チャット履歴・会話状態は永続化されない (ウィンドウを閉じると破棄)
