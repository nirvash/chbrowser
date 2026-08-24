# AGENTS.md

コード変更時のための実装メモ。利用者向け情報は README.md を参照。
本書はコードリーディングで判明した基本設計・内部構造の知識ベースであり、後続の改修で再利用することを意図する。

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
   - **ローカルマージ** — ローカルで `git merge --squash <branch>` + 単一コミットで
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
- スレ内動画の再生要素生成は `playMedia()` の 1 箇所のみ (クリックで `<video controls autoplay>` 生成)。
  サムネ抽出用の非表示 `<video>` は別経路 (`extractAndCacheVideoThumbnail`, canvas から 240px JPEG を
  `videoThumbnailCache` メッセージで C# へ)

## メディア処理

- 画像は URL 単位で `cache/images/` にキャッシュ。スロットには `data-cache-state` が付き、
  cached / deferred (サイズしきい値 `ImageSizeThresholdMb` 以上はクリックまで取得しない) を区別
- 動画はクリック時に `VideoDownloadManager` が並列 DL を kick。ヒット時は仮想ホスト
  `https://chbrowser-cache.local/videos/...` 経由でローカル再生 (`PlaybackUrl`)
- AI 生成画像メタデータは `Services/Image/AiImageMetadataService.cs` (NuGet 依存ゼロの手製パーサ):
  PNG tEXt/XMP/LSB ステルス、JPEG EXIF、WebP XMP、MP4/WebM コンテナ (未キャッシュ動画は HTTP Range で
  メタ部のみ取得)。ComfyUI workflow グラフ解析込み。結果はホバーポップアップ / ビューア詳細ペインに表示

## 設定システムの流れ

`AppConfig` (init-only record) → `SettingsViewModel` (UI ミラー) → `MainViewModel.ApplyConfig` →
各ペイン向け setConfig JSON → JS ハンドラ。

- 設定 1 件のタッチポイント: ① `Models/AppConfig.cs` ② `ViewModels/SettingsViewModel.cs`
  ([ObservableProperty] 宣言 / 初期値流し込み / 保存用匿名オブジェクトの 3 箇所)
  ③ `Views/Settings/` の該当 Panel.xaml (スレ系=`ThreadPanel.xaml`、画像系=`ImagePanel.xaml`、
  AI 系=`AiPanel.xaml`/`AiNgPanel.xaml`) ④ 即時反映なら `ApplyConfig` の setConfig JSON へ追加
  ⑤ 対象 JS の setConfig ハンドラ
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
  **内蔵エージェントと MCP サーバが同一表面を共有** (スレ読取 14 + web_search/web_fetch の計 16)。
  `thread_url` 省略時は「現在選択中のスレ」が対象。URL 受理は 5ch.io / bbspink.com 系のみ
- MCP サーバ (`Services/Mcp/McpHttpServer.cs`): Streamable HTTP 最小実装、**127.0.0.1 バインドのみ**
  (既定ポート 7393、`http://127.0.0.1:7393/mcp`)。設定で明示オフ (既定 OFF)、ON/OFF・ポート変更は即時反映。
  GET/SSE・バッチ・認証は非対応。tools/call は UI スレッドへマーシャリングされる
- AI NG (`ViewModels/MainViewModel.AiNg.cs` + `Services/Ng/AiNgJudge.cs`): レス単位で攻撃度 1..5 を
  別接続設定の LLM に採点させ、`NgAiThreshold` 以上を可逆非表示。スコアはスレ単位の `.aing.json` に
  **全件完了時のみ**保存。判定対象は選択中タブのみ

## 既知の制限・落とし穴

- `validate` 相当の機構は無し。JS 変更はビルドで埋め込まれるため、動作確認はビルド後の実行で行う
- 埋め込み JS の構文エラーは**ペイン全体の描画死**として表面化する (IIFE 全体が死ぬため「スレが真っ白」等)。
  ビルド前に `node --check src/ChBrowser/Resources/<file>.js` で構文検証できる
- WebView2 の OOM / クラッシュはノードエラーではなく接続断・タイムアウトとして表面化する
  (`get_logs` / ログウィンドウで確認)
- API キー類 (LLM / Worker / NG-AI / どんぐり) は config.json に平文保存 (DPAPI 化は未着手)
- チャット履歴・会話状態は永続化されない (ウィンドウを閉じると破棄)
