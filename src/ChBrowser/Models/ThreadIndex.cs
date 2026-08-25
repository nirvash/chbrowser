namespace ChBrowser.Models;

/// <summary>
/// スレッドごとの読了/位置情報。idx.json として板ディレクトリに永続化される。
/// </summary>
/// <param name="LastReadPostNumber">「読了 prefix」の最大レス番号。
/// JS 側 <c>findReadProgressMaxNumber</c> が「先頭から連番が途切れず下端まで見終えた最大番号」を算定する。
/// 次回スレオープン時、JS は <c>el.scrollIntoView({ block: 'end' })</c> で対象レスを viewport 下端に揃える
/// (= 表示モード変更でレスの DOM 並び順が変わっても、「読了番号」という意味付けで安定して復元できる)。</param>
/// <param name="LastFetchedPostCount">前回取得完了時のレス総数。subject.txt の件数と比較して新着の有無を判定する。</param>
/// <param name="OwnPostNumbers">「自分の書き込み」としてマークされているレス番号集合。
/// レス番号メニュー (post.html の data-number 経由) からトグルできる。null は空集合と同義。
/// dat 削除で idx.json ごと消えるため自動リセットされる。</param>
///
/// 注 1: 「以降新レス」ラベルの位置 (旧 <c>MarkPostNumber</c>) は **永続化しない** 設計に変更 (Phase: ラベルの session-local 化)。
/// ラベルは「いまアプリが起動してから以降に差分取得で来た新着の先頭番号」を意味し、タブ閉じ / アプリ再起動でリセットされる。
/// これにより「現セッションで 1 件も新着を取っていないのにラベルが出る」という違和感を回避する。
///
/// 注 2: 「自分のレスへの返信検知」(旧 <c>HasReplyToOwn</c>) も **永続化しない** 設計に変更。
/// 「直前の差分取得が自分への返信を含むか」のフラグであり、cache load では立てず、ToggleOwnPost でも発火しない。
/// 次の取得 / 一覧更新が来たら自由に他マークで上書きされる (= 短期 alert 用途)。
public sealed record ThreadIndex(
    int?   LastReadPostNumber,
    int?   LastFetchedPostCount,
    int[]? OwnPostNumbers = null,
    /// <summary>復元時の追加スクロール量 (px)。LastReadPostNumber の下端を viewport 下端に
    /// 揃えたあと、さらに下へスクロールしていた量 (= 投稿内の細かい読み位置)。
    /// null / 0 は従来どおり境界ぴったりの復元。ズームやレイアウト変化で多少ずれうる近似値。</summary>
    double? ScrollOffsetPx = null,
    /// <summary>保存時の絶対 scrollY (px)。保存時とスロットスケール / ページズーム / ドキュメント高さが
    /// すべて一致する場合にのみ使われる完全復元用の値。</summary>
    double? ScrollY = null,
    double? DocHeight = null,
    double? EnvSlotScale = null,
    double? EnvPageZoom = null,
    /// <summary>前スレナビゲーションの確定値 (= ユーザが ✅ 確定 / 候補メニューで選択 / 手動 URL 設定した key)。
    /// null は「未確定」で、自動推測 (<see cref="ViewModels.MainViewModel"/>) の結果に従う。
    /// スレ key はスレ立て epoch 秒なので「本 key &lt; 自スレ key」が前スレの方向条件。</summary>
    string? PrevThreadKey = null,
    /// <summary>次スレナビゲーションの確定値。意味は <see cref="PrevThreadKey"/> の次スレ版
    /// (「本 key &gt; 自スレ key」が方向条件)。</summary>
    string? NextThreadKey = null,
    /// <summary>前後スレ候補から「荒らし / 間違いリンク」としてユーザが除外登録したスレ key 集合。
    /// 自動推測の候補 ranking から永久に外される (🚫 除外ボタンで追加)。null は空集合と同義。</summary>
    string[]? NavExcludedKeys = null);
