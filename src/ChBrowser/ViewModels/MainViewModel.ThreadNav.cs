using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Llm;
using ChBrowser.Services.Storage;
using ChBrowser.Services.Url;

namespace ChBrowser.ViewModels;

/// <summary>前スレ / 次スレナビゲーション (ツールバー 前 / 次 / 🚫 / ✅)。
///
/// 解決の流れ (<see cref="ResolveThreadChainAsync"/>= スレオープン完了 / 差分取得完了のたびに自動実行):
///   1. 自レス本文から same-board のスレリンクを抽出 (前 = レス 1〜5、次 = 後半 50%)
///   2. 候補を検証して ranking:
///      - 方向条件 (スレ key = スレ立て epoch 秒なので「前 = key 小 / 次 = key 大」)
///      - タイトル類似 (最長共通部分文字列)。subject.txt に無い候補 (= dat 落ち等) でも
///        ローカル dat / ネットワーク dat 先頭 probe で実際のタイトルを取って検証する
///      - 検証材料: タイトル類似 or ローカルログ有無 or リンク被回数。単発リンク×証拠なしは棄却
///   3. リンク候補が無ければ subject.txt 全体からのタイトル推測 fallback (類似度は厳しめ)
///   4. idx.json の確定値 (PrevThreadKey / NextThreadKey) があればそれを最優先
///
/// ユーザ操作:
///   - 前 / 次 左クリック : 採用中 target を開く。間違っていたら…
///   - 右クリック         : 有力順候補メニュー → 選択で採用 + オープン (繰り返して正解へ)
///   - 🚫                 : 荒らし / 間違い候補を除外登録 (NavExcludedKeys に永続化、以降の ranking 対象外)
///   - ✅                 : 現採用 target を「本物」として確定 (永続化、以降の自動解決で不変)
/// </summary>
public sealed partial class MainViewModel
{
    // ---- tuning 定数 ----

    /// <summary>前スレリンクを探す先頭レス数。</summary>
    private const int NavPrevScanPostLimit = 5;

    /// <summary>次スレリンクを探す後半範囲の開始位置 (全レス数に対する比率)。</summary>
    private const double NavNextScanStartRatio = 0.5;

    /// <summary>候補メニューに載せる最大件数 (片側)。</summary>
    private const int NavMaxCandidates = 8;

    /// <summary>リンク由来候補が「subject.txt 上のスレ」と同一系列とみなすタイトル類似度
    /// (最長共通部分文字列長)。これ未満でもログあり / 複数回リンクなら通す。</summary>
    private const int NavLinkSimMin = 8;

    /// <summary>subject.txt からの推測 fallback に要求するタイトル類似度。
    /// リンクの手掛かりが無い純推測なので、誤爆防止に <see cref="NavLinkSimMin"/> より厳しい。</summary>
    private const int NavInferSimMin = 12;

    /// <summary>subject.txt 取得結果の板単位メモリキャッシュ TTL。乱立板で連続 resolve しても
    /// ネットワークを連打しないための上限。</summary>
    private static readonly TimeSpan NavSubjectCacheTtl = TimeSpan.FromMinutes(3);

    /// <summary>subject.txt ネットワーク取得の打ち切り時間。起動直後の遅い回線で解決全体が
    /// 長時間ブロックされるのを避けるための短め上限。</summary>
    private static readonly TimeSpan NavSubjectFetchTimeout = TimeSpan.FromSeconds(8);

    /// <summary>タイトル probe (dat 先頭 4KB) のネットワーク打ち切り時間。</summary>
    private static readonly TimeSpan NavNetFetchTimeout = TimeSpan.FromSeconds(8);

    /// <summary>連鎖検証の全文取得 (= 候補スレ dat 全体) のネットワーク打ち切り時間。
    /// 本文走査が目的なので完全性より速さ優先。失敗時は候補減点なしの通常扱いにフォールバックする。</summary>
    private static readonly TimeSpan NavChainFetchTimeout = TimeSpan.FromSeconds(20);

    /// <summary>本文から 5ch 系スレ URL を抽出する正規表現 (ホスト省略形 / 相対形式も許容)。
    /// JS 側 thread.js の FIVECH_THREAD_RE と同じ意味範囲。</summary>
    private static readonly Regex NavThreadLinkRe = new(
        @"(?:https?://[A-Za-z0-9.\-]+)?/test/read\.cgi/(?<dir>[A-Za-z0-9]+)/(?<key>[0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>subject.txt の板単位キャッシュ。key = (host, dir)。TTL は <see cref="NavSubjectCacheTtl"/>。</summary>
    private readonly Dictionary<(string Host, string Dir), (DateTimeOffset FetchedAt, IReadOnlyList<ThreadInfo> Subjects)>
        _navSubjectCache = new();

    /// <summary>候補タイトル probe 結果の TTL (positive / negative 共通)。
    /// 死んだリンクへの dat probe を差分取得のたびに繰り返さないための上限。</summary>
    private static readonly TimeSpan NavTitleCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>候補 key ごとのタイトル probe 結果キャッシュ。Title が null = 「probe したが取れなかった」
    /// (= dat 落ち等) もキャッシュする (= negative cache)。</summary>
    private readonly Dictionary<(string Host, string Dir, string Key), (DateTimeOffset At, string? Title)>
        _navTitleCache = new();

    /// <summary>候補スレ本文内の「次スレリンク」走査結果のキャッシュ (連鎖検証用)。
    /// value は key → 出現回数の出現順リスト。TTL は <see cref="NavTitleCacheTtl"/> を共用。</summary>
    private readonly Dictionary<(string Host, string Dir, string Key), (DateTimeOffset At, List<KeyValuePair<string, int>> Nexts)>
        _navChainCache = new();

    /// <summary>ローカルログ key 集合の板単位キャッシュ (= <see cref="ChBrowser.Services.Api.DatClient.EnumerateExistingThreadKeys"/>)。
    /// dat ファイル全列挙は重いので、解決のたびにやり直さない。TTL は subject と同じ 3 分。</summary>
    private readonly Dictionary<(string Host, string Dir), (DateTimeOffset At, IReadOnlySet<string> Keys)>
        _navLogKeysCache = new();

    // -----------------------------------------------------------------
    // 自動解決
    // -----------------------------------------------------------------

    /// <summary>タブの前後スレ候補を解決して UI 状態を更新する。
    /// OpenThreadAsync / RefreshThreadAsync 完了フックから fire-and-forget される。
    /// 再入は <see cref="ThreadTabViewModel.NavResolving"/> で握り潰す (= 実行中の解決に任せる)。
    ///
    /// <paramref name="force"/>= false の通常呼び出しは「本文が前回解決から変化していない
    /// (<see cref="ThreadTabViewModel.NavResolvedAtPostCount"/> == Posts.Count) 場合スキップ」する
    /// (= 判定結果の実質キャッシュ。新着 0 件の差分取得では再解析しない)。</summary>
    public async Task ResolveThreadChainAsync(ThreadTabViewModel tab, bool force = false)
    {
        if (tab is null || tab.NavResolving) return;
        if (!force && tab.Posts.Count > 0 && tab.NavResolvedAtPostCount == tab.Posts.Count)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] resolve skip (cached, posts={tab.Posts.Count}): {tab.Header}");
            return;
        }
        tab.NavResolving = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[threadNav] resolve start: {tab.Header} posts={tab.Posts.Count}{(force ? " (force)" : "")}");
        try
        {
            await ResolveThreadChainCoreAsync(tab).ConfigureAwait(true);
            tab.NavResolvedAtPostCount = tab.Posts.Count; // 完走時のみキャッシュ (= 失敗は再解析対象)
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] resolve end ({sw.ElapsedMilliseconds}ms): {tab.Header} " +
                $"prev={(tab.PrevNavKey ?? "-")}{(tab.IsPrevNavConfirmed ? "*" : "")} " +
                $"next={(tab.NextNavKey ?? "-")}{(tab.IsNextNavConfirmed ? "*" : "")}");
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] {tab.Header}: 解決エラー ({sw.ElapsedMilliseconds}ms) {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            tab.NavResolving = false;
        }
    }

    /// <summary><see cref="ResolveThreadChainAsync"/> の本体。UI スレッド前提
    /// (ObservableProperty 更新とディスク列挙を含む)。</summary>
    private async Task ResolveThreadChainCoreAsync(ThreadTabViewModel tab)
    {
        var board  = tab.Board;
        var curKey = tab.ThreadKey;

        // スレ key が数値でない形式 (= epoch 秒でない) は方向判定ができないため対象外。
        if (!ulong.TryParse(curKey, out var curNum))
        {
            ApplyNavResult(tab, isPrev: true,  key: null, title: "", confirmed: false, candidates: Array.Empty<ThreadNavCandidate>());
            ApplyNavResult(tab, isPrev: false, key: null, title: "", confirmed: false, candidates: Array.Empty<ThreadNavCandidate>());
            return;
        }

        // idx.json の確定値 / 除外集合を読む (ユーザ操作の永続値)。
        var index       = _threadIndex.Load(board.Host, board.DirectoryName, curKey);
        var excluded    = index?.NavExcludedKeys is { Length: > 0 } arr ? new HashSet<string>(arr, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        var lockedPrev  = index?.PrevThreadKey;
        var lockedNext  = index?.NextThreadKey;

        var curTitleLower = (tab.Title ?? "").ToLowerInvariant();
        var subjects      = await GetSubjectsForNavAsync(board).ConfigureAwait(true);
        var subjByKey     = new Dictionary<string, ThreadInfo>(subjects.Count, StringComparer.Ordinal);
        foreach (var s in subjects) subjByKey[s.Key] = s;
        var logKeys = GetLogKeysCached(board);

        // 本文からのリンク抽出 (前 = 先頭 N レス、次 = 後半 50%)。
        var prevLinks = ScanThreadLinks(tab.Posts, board.DirectoryName, 0,
            Math.Min(NavPrevScanPostLimit, tab.Posts.Count));
        var nextStart = (int)(tab.Posts.Count * NavNextScanStartRatio);
        var nextLinks = ScanThreadLinks(tab.Posts, board.DirectoryName, nextStart, tab.Posts.Count);

        var prevCands = await BuildNavSideAsync(isPrev: true,  board, curKey, curNum, curTitleLower, prevLinks,
            subjByKey, logKeys, excluded, allowUnverified: true).ConfigureAwait(true);
        var nextCands = await BuildNavSideAsync(isPrev: false, board, curKey, curNum, curTitleLower, nextLinks,
            subjByKey, logKeys, excluded, allowUnverified: false).ConfigureAwait(true);

        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[threadNav] scan {tab.Header}: posts={tab.Posts.Count} " +
            $"prevLinks=[{FmtLinks(prevLinks)}] nextLinks=[{FmtLinks(nextLinks)}]");
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[threadNav] {tab.Header}: prev cands={prevCands.Count}{FmtTop(prevCands)}, next cands={nextCands.Count}{FmtTop(nextCands)}, " +
            $"locked=({lockedPrev ?? "-"}, {lockedNext ?? "-"}), excluded={excluded.Count}");

        // 確定値があれば最優先 (title は subject / ローカル dat からベストエフォートで引く)。
        await ApplySideWithLockAsync(tab, isPrev: true,  lockedPrev, prevCands, subjByKey, board).ConfigureAwait(true);
        await ApplySideWithLockAsync(tab, isPrev: false, lockedNext, nextCands, subjByKey, board).ConfigureAwait(true);

        // 兄弟タブ逆引き (短命スレ落ちスレの救済): 本文に次スレリンクが無くても、
        // 隣接スレのテンプレが自スレを前スレとして宣言していればそこから前後関係が分かる。
        InferNavFromSiblingTabs(tab);

    }

    /// <summary>開いている他タブの解決結果を逆参照して、自スレの前後候補を補完する。
    ///
    /// 主目的は短命スレ落ちスレの救済: 10 レス程度で死んだスレは本文に次スレリンクが存在しないが、
    /// 翌日立て直された同系列スレ (= 後継) のテンプレ 1 が「前スレ: 自スレ」を宣言しているので、
    /// その宣言を逆に辿れば自スレの次スレが分かる。ユーザは通常、隣接スレを開いた状態で
    /// 前 / 次 を辿るため、兄弟タブはほぼ常に解決済みとして機能する。
    ///
    /// 注意: 兄弟タブがまだ未解決のときは拾えない (= 兄弟の解決完了後に自タブが再解決されれば拾う)。</summary>
    private void InferNavFromSiblingTabs(ThreadTabViewModel tab)
    {
        if (!ulong.TryParse(tab.ThreadKey, out var myNum)) return;

        foreach (var other in AllThreadTabs)
        {
            if (ReferenceEquals(other, tab)) continue;
            // 同一板のみ (root domain + dir)。
            if (!string.Equals(other.Board.DirectoryName, tab.Board.DirectoryName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(DataPaths.ExtractRootDomain(other.Board.Host),
                               DataPaths.ExtractRootDomain(tab.Board.Host), StringComparison.OrdinalIgnoreCase)) continue;
            if (!ulong.TryParse(other.ThreadKey, out var otherNum)) continue;

            var otherPrevKeys = CollectNavKeys(other.PrevNavKey, other.PrevNavCandidates);
            var otherNextKeys = CollectNavKeys(other.NextNavKey, other.NextNavCandidates);

            // other が自スレより新しく、other が「前スレ = 自スレ」を指している → other は自スレの後継。
            if (otherNum > myNum && otherPrevKeys.Contains(tab.ThreadKey))
                MergeSiblingCandidate(tab, isPrev: false, other);

            // other が自スレより古く、other が「次スレ = 自スレ」を指している → other は自スレの前身。
            if (otherNum < myNum && otherNextKeys.Contains(tab.ThreadKey))
                MergeSiblingCandidate(tab, isPrev: true, other);
        }
    }

    private static HashSet<string> CollectNavKeys(string? adoptedKey, IReadOnlyList<ThreadNavCandidate>? candidates)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(adoptedKey)) set.Add(adoptedKey);
        if (candidates is not null)
        {
            foreach (var c in candidates) set.Add(c.Key);
        }
        return set;
    }

    /// <summary>兄弟タブ由来の推論候補を自タブへ統合する。既存候補に無ければ先頭に高スコアで追加し、
    /// 該当側の採用 key が未決の場合だけ採用する (= 通常解決が見つけられなかった場合の救済)。
    /// ただしユーザが除外登録 (🚫 / <c>NavExcludedKeys</c>) した key は採り込まない
    /// (= 除外の恒久性を保証。さもないと再解決のたびに除外候補が蘇る)。</summary>
    private void MergeSiblingCandidate(ThreadTabViewModel tab, bool isPrev, ThreadTabViewModel other)
    {
        var index = _threadIndex.Load(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
        if (index?.NavExcludedKeys is { Length: > 0 } excludedArr &&
            excludedArr.Contains(other.ThreadKey))
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] sibling infer skip (excluded): {tab.ThreadKey} {(isPrev ? "prev" : "next")} = {other.ThreadKey}");
            return;
        }

        IReadOnlyList<ThreadNavCandidate> cur = isPrev
            ? tab.PrevNavCandidates ?? Array.Empty<ThreadNavCandidate>()
            : tab.NextNavCandidates ?? Array.Empty<ThreadNavCandidate>();
        if (cur.Any(c => c.Key == other.ThreadKey)) return;

        var cand = new ThreadNavCandidate(other.ThreadKey, other.Title ?? "", 0, 30);
        var list = new List<ThreadNavCandidate>(cur.Count + 1) { cand };
        list.AddRange(cur);

        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[threadNav] sibling infer: {tab.ThreadKey} {(isPrev ? "prev" : "next")} = {other.ThreadKey} \"{other.Title}\"");

        if (isPrev)
        {
            tab.PrevNavCandidates = list;
            if (!tab.HasPrevNav)
            {
                tab.PrevNavKey   = other.ThreadKey;
                tab.PrevNavTitle = other.Title ?? "";
            }
        }
        else
        {
            tab.NextNavCandidates = list;
            if (!tab.HasNextNav)
            {
                tab.NextNavKey   = other.ThreadKey;
                tab.NextNavTitle = other.Title ?? "";
            }
        }
    }

    /// <summary>片側の解決結果を tab へ適用する。idx.json の確定値 (<paramref name="lockedKey"/>) があれば
    /// 候補 ranking を無視してそれを採用 (confirmed=true)。無ければ最有力候補 (自動推測)。</summary>
    private async Task ApplySideWithLockAsync(
        ThreadTabViewModel tab, bool isPrev, string? lockedKey,
        IReadOnlyList<ThreadNavCandidate> candidates,
        IReadOnlyDictionary<string, ThreadInfo> subjByKey, Board board)
    {
        if (!string.IsNullOrEmpty(lockedKey))
        {
            var subjTitle = subjByKey.TryGetValue(lockedKey, out var lockedInfo) ? lockedInfo.Title : null;
            var title = await ResolveNavTitleWithFetchAsync(board, lockedKey, subjTitle).ConfigureAwait(true) ?? "";
            ApplyNavResult(tab, isPrev, lockedKey, title, confirmed: true, candidates);
            return;
        }
        var best = candidates.Count > 0 ? candidates[0] : null;
        ApplyNavResult(tab, isPrev, best?.Key, best?.Title ?? "", confirmed: false, candidates);
    }

    /// <summary>解決結果を tab の ObservableProperty 群へ流し込む。</summary>
    private void ApplyNavResult(
        ThreadTabViewModel tab, bool isPrev, string? key, string title, bool confirmed,
        IReadOnlyList<ThreadNavCandidate> candidates)
    {
        if (isPrev)
        {
            tab.PrevNavKey         = key;
            tab.PrevNavTitle       = title;
            tab.IsPrevNavConfirmed = confirmed;
            tab.PrevNavCandidates  = candidates;
        }
        else
        {
            tab.NextNavKey         = key;
            tab.NextNavTitle       = title;
            tab.IsNextNavConfirmed = confirmed;
            tab.NextNavCandidates  = candidates;
        }
    }

    /// <summary>片側分の候補 ranking を構築する。リンク由来候補を検証 + 採点したあと、
    /// 手掛かり不足なら subject.txt 全体からのタイトル推測 fallback で水増しする。
    ///
    /// 検証は「実際にそのスレのタイトルを見る」: subject.txt に無い候補 (= dat 落ち等) でも
    /// ローカル dat → ネットワーク dat 先頭 probe の順にタイトルを取得して自スレタイトルとの
    /// 類似を判定する (<see cref="ResolveNavTitleWithFetchAsync"/>)。
    ///
    /// 前スレ側はさらに連鎖検証を行う: 候補スレ自身の「次スレリンク」に自スレに近い中継世代が
    /// いればテンプレ更新忘れとみなし、候補を減点して中継世代を本命に繰り上げる
    /// (<see cref="PromoteChainIntermediateAsync"/>)。
    ///
    /// <paramref name="allowUnverified"/>= true (前スレ側のみ) は「probe してもタイトルが取れず、
    /// 他の証拠も無い単発リンク」を最下位スコアで保険採用する (完全 dat 落ち前スレの救済)。</summary>
    private async Task<List<ThreadNavCandidate>> BuildNavSideAsync(
        bool isPrev, Board board, string curKey, ulong curNum, string curTitleLower,
        List<KeyValuePair<string, int>> links,
        IReadOnlyDictionary<string, ThreadInfo> subjByKey,
        IReadOnlySet<string> logKeys,
        HashSet<string> excluded,
        bool allowUnverified = false)
    {
        // score 部分一致比較用にタイトル類似を 20 文字で頭打ち (= 極端な長文一致の支配を防ぐ)。
        var pool = new Dictionary<string, (double Score, string Title, int PostCount)>(StringComparer.Ordinal);

        void AddOrUpdate(string key, double scoreAdd, string title, int postCount)
        {
            if (pool.TryGetValue(key, out var cur))
                pool[key] = (cur.Score + scoreAdd,
                             cur.Title.Length > 0 ? cur.Title : title,
                             Math.Max(cur.PostCount, postCount));
            else
                pool[key] = (scoreAdd, title, postCount);
        }

        // 連鎖検証は候補スレの dat 全文取得を伴いうるので、片側あたり上位 2 候補までに制限。
        var chainWalkBudget = isPrev ? 2 : 0;

        // 落選理由の診断ログ (上限件数で打ち切り。次スレが検出できない原因特定用)。
        var rejects    = new List<string>();
        var rejectSide = isPrev ? "prev" : "next";

        void Reject(string candKey, string reason)
        {
            if (rejects.Count < 20) rejects.Add($"{candKey}:{reason}");
        }

        // ---- 1) リンク由来候補 ----
        foreach (var link in links)
        {
            var key = link.Key;
            if (key == curKey)
            {
                Reject(key, "self"); continue; // 自スレ自身へのリンク
            }
            if (excluded.Contains(key))
            {
                Reject(key, "excluded"); continue;
            }
            if (!ulong.TryParse(key, out var k))
            {
                Reject(key, "nonnum"); continue;
            }
            if (isPrev ? !(k < curNum) : !(k > curNum))
            {
                Reject(key, "dir"); continue; // 方向不一致
            }

            var inSubj = subjByKey.TryGetValue(key, out var info);
            string title;
            int postCount;
            if (inSubj)
            {
                title     = info!.Title;
                postCount = info.PostCount;
            }
            else
            {
                // subject に無い (= dat 落ち等) 場合も実際のタイトルを取得して検証する。
                title     = (await ResolveNavTitleWithFetchAsync(board, key, null).ConfigureAwait(true)) ?? "";
                postCount = 0;
            }
            var lcs    = LongestCommonSubstringLength(curTitleLower, title.ToLowerInvariant());
            var hasLog = logKeys.Contains(key);
            var links2 = Math.Min(link.Value, 3);

            // 検証可能性ゲート: タイトル類似 (subject 由来 / probe 由来を問わない) /
            // ローカルログ / 複数回リンク のどれかが必要。
            // ゲートを落とした候補は、前スレ側のみ最下位スコア (= 検証済みが無いときの保険) で採用。
            var verifiedByTitle = title.Length > 0 && lcs >= NavLinkSimMin;
            var trusted         = verifiedByTitle || hasLog || link.Value >= 2;
            if (!trusted)
            {
                Reject(key, $"unverified(lcs={lcs},log={hasLog},n={link.Value})");
                if (allowUnverified) AddOrUpdate(key, 1.0, title, postCount);
                continue;
            }

            var score = Math.Min(lcs, 20) * 1.0
                      + (hasLog ? 4.0 : 0)
                      + links2 * 2.0;

            // ---- 連鎖検証 (前スレ側のみ) ----
            // 候補スレ自身の本文に張られた「次スレリンク」の中に、候補より新しく自スレより古い key
            // (= 自スレと候補の間に割り込む世代) があれば、このリンクはテンプレ更新忘れの可能性が
            // 高い。候補を減点し、代わりに中継世代を「連鎖確定」として高スコアで追加する。
            if (chainWalkBudget > 0)
            {
                chainWalkBudget--;
                var promoted = await PromoteChainIntermediateAsync(
                    board, curKey, curNum, curTitleLower,
                    candKey: key, candNum: k, candScore: score, candTitle: title, candPostCount: postCount,
                    subjByKey, logKeys, excluded, AddOrUpdate).ConfigureAwait(true);
                if (promoted) continue; // 候補は減点済みで pool 登録済み → 通常スコアでの追加はしない
            }

            AddOrUpdate(key, score, title, postCount);
        }

        // ---- 2) subject.txt からの推測 fallback (リンク手掛かりゼロ or 少ないときの補完) ----
        foreach (var info in subjByKey.Values)
        {
            if (!ulong.TryParse(info.Key, out var k)) continue;
            if (isPrev ? !(k < curNum) : !(k > curNum)) continue;
            if (pool.ContainsKey(info.Key) || excluded.Contains(info.Key)) continue;

            var lcs = LongestCommonSubstringLength(curTitleLower, info.Title.ToLowerInvariant());
            if (lcs < NavInferSimMin) continue;
            AddOrUpdate(info.Key, Math.Min(lcs, 20) * 1.0, info.Title, info.PostCount);
        }

        if (rejects.Count > 0)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] {rejectSide} rejects ({rejects.Count}): {string.Join(", ", rejects)}");
        }

        // 同スコア時の並び: 前 = 自スレに最も近い過去 (key 降順) / 次 = 最も近い後継 (key 昇順)。
        // 次を降順 (= 最新優先) にすると、推測 fallback が世代を飛ばして最新スレを採用する事故になるため。
        return pool
            .OrderByDescending(kv => kv.Value.Score)
            .ThenBy(kv => isPrev
                ? ulong.MaxValue - (ulong.TryParse(kv.Key, out var n) ? n : 0UL)
                : (ulong.TryParse(kv.Key, out var m) ? m : ulong.MaxValue))
            .Take(NavMaxCandidates)
            .Select(kv => new ThreadNavCandidate(kv.Key, kv.Value.Title, kv.Value.PostCount, kv.Value.Score))
            .ToList();
    }

    /// <summary>posts[start,end) の本文から same-board スレリンクを抽出して key ごとに集計する
    /// (出現順保持)。他板 / 自スレ自身へのリンクは除外する。</summary>
    private static List<KeyValuePair<string, int>> ScanThreadLinks(
        IReadOnlyList<Post> posts, string directoryName, int start, int endExclusive)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = start; i < endExclusive && i < posts.Count; i++)
        {
            var body = posts[i].Body;
            if (string.IsNullOrEmpty(body)) continue;
            foreach (Match m in NavThreadLinkRe.Matches(body))
            {
                if (!string.Equals(m.Groups["dir"].Value, directoryName, StringComparison.OrdinalIgnoreCase)) continue;
                counts[m.Groups["key"].Value] = counts.TryGetValue(m.Groups["key"].Value, out var c) ? c + 1 : 1;
            }
        }
        return counts.ToList();
    }

    /// <summary>前スレ候補 <paramref name="candKey"/> の本文を走査し、その「次スレリンク」の中に
    /// candKey より新しく自スレより古い中継世代 (= テンプレ更新忘れが飛ばした世代) が居たら:
    ///   - 候補を減点して登録 (メニュー下位に残る)
    ///   - 中継世代のうち最も自スレに近い (= 最大 key) をタイトル検証のうえ連鎖確定として高スコアで追加
    /// 中継を採用できたかどうかを返す。</summary>
    private async Task<bool> PromoteChainIntermediateAsync(
        Board board, string curKey, ulong curNum, string curTitleLower,
        string candKey, ulong candNum, double candScore, string candTitle, int candPostCount,
        IReadOnlyDictionary<string, ThreadInfo> subjByKey,
        IReadOnlySet<string> logKeys,
        HashSet<string> excluded,
        Action<string, double, string, int> addOrUpdate)
    {
        var nexts = await GetNavChainNextsAsync(board, candKey).ConfigureAwait(true);

        // 中継世代の抽出: candNum < n < curNum。
        var mids = new List<ulong>();
        foreach (var kv in nexts)
        {
            if (!ulong.TryParse(kv.Key, out var n)) continue;
            if (!(n > candNum && n < curNum)) continue;
            if (excluded.Contains(kv.Key)) continue;
            mids.Add(n);
        }
        if (mids.Count == 0) return false;

        // 最も自スレに近い (= 最大 key) の中継から順にタイトル検証し、通ったものを採用する。
        foreach (var mid in mids.OrderByDescending(x => x))
        {
            var midKey    = mid.ToString();
            var subjTitle = subjByKey.TryGetValue(midKey, out var mi) ? mi.Title : null;
            var title     = await ResolveNavTitleWithFetchAsync(board, midKey, subjTitle).ConfigureAwait(true) ?? "";
            var lcs       = LongestCommonSubstringLength(curTitleLower, title.ToLowerInvariant());
            if (title.Length == 0 || lcs < NavLinkSimMin) continue; // 検証不能な中継は本命にできない

            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] chain: 前スレ候補 {candKey} の次スレリンクに中継世代 {midKey} を検出 → 更新忘れとみなし減点、{midKey} を本命に");

            // 元候補は「更新忘れ」として大幅減点して残す (= メニューには下位に並ぶ。保険候補(1.0)は下回らない)。
            addOrUpdate(candKey, Math.Max(2.0, candScore * 0.3), candTitle, candPostCount);
            addOrUpdate(midKey,
                Math.Min(lcs, 20) * 1.0 + 8.0 + (logKeys.Contains(midKey) ? 4.0 : 0),
                title,
                subjByKey.TryGetValue(midKey, out var mi2) ? mi2.PostCount : 0);
            return true;
        }
        return false;
    }

    /// <summary>指定スレ本文内の same-board スレリンク (後半走査 = 次スレリンク抽出) を key → 出現回数で返す。
    /// ローカル dat を優先し、無ければネットワークからメモリのみ取得 (<see cref="ChBrowser.Services.Api.DatClient.FetchInMemoryAsync"/>)。
    /// 結果は TTL キャッシュされ、解決のたびに同じ候補の dat を取りに行かない。</summary>
    private async Task<List<KeyValuePair<string, int>>> GetNavChainNextsAsync(Board board, string key)
    {
        var cacheKey = (board.Host, board.DirectoryName, key);
        if (_navChainCache.TryGetValue(cacheKey, out var hit) &&
            DateTimeOffset.UtcNow - hit.At < NavTitleCacheTtl)
            return hit.Nexts;

        IReadOnlyList<Post>? posts = null;
        try
        {
            var local = await _datClient.LoadFromDiskAsync(board, key).ConfigureAwait(true);
            if (local is { Posts.Count: > 0 }) posts = local.Posts;
        }
        catch (Exception ex)
        {
            // 起動時の同時復元などでファイルが掴まれている場合はネットワーク取得へフォールバック。
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] chain local read failed ({key}): {ex.Message} → network fallback");
        }
        if (posts is null)
        {
            using var cts = new System.Threading.CancellationTokenSource(NavChainFetchTimeout);
            var net = await _datClient.FetchInMemoryAsync(board, key, cts.Token).ConfigureAwait(true);
            if (net is { Posts.Count: > 0 }) posts = net.Posts;
        }

        List<KeyValuePair<string, int>> nexts = new();
        if (posts is { Count: > 0 })
        {
            var start = (int)(posts.Count * NavNextScanStartRatio);
            nexts = ScanThreadLinks(posts, board.DirectoryName, start, posts.Count)
                .Where(kv => kv.Key != key) // 自分自身へのリンクは除外
                .ToList();
        }
        _navChainCache[cacheKey] = (DateTimeOffset.UtcNow, nexts);
        return nexts;
    }

    /// <summary>候補 key のタイトルを取得する。優先順:
    /// ① 既知の subject タイトル (<paramref name="subjectTitle"/>) → ② ローカル dat 1 レス目 →
    /// ③ ネットワーク dat 先頭 probe (<see cref="ChBrowser.Services.Api.DatClient.FetchThreadTitleFromNetworkAsync"/>)。
    /// 取得結果 (null 含む) を TTL キャッシュし、差分取得のたびに同じ死 link へ probe しに行かない。
    /// 戻り値 null = 「どこからもタイトルを取れなかった」。</summary>
    private async Task<string?> ResolveNavTitleWithFetchAsync(Board board, string key, string? subjectTitle)
    {
        if (!string.IsNullOrEmpty(subjectTitle)) return subjectTitle;

        var cacheKey = (board.Host, board.DirectoryName, key);
        if (_navTitleCache.TryGetValue(cacheKey, out var hit) &&
            DateTimeOffset.UtcNow - hit.At < NavTitleCacheTtl)
            return hit.Title;

        string? title = null;
        try
        {
            title = await _datClient.ReadThreadTitleFromDiskAsync(board, key).ConfigureAwait(true);
        }
        catch { /* ログ読み失敗はネットワーク probe へフォールバック */ }
        if (string.IsNullOrEmpty(title))
        {
            using var cts = new System.Threading.CancellationTokenSource(NavNetFetchTimeout);
            title = await _datClient.FetchThreadTitleFromNetworkAsync(board, key, cts.Token).ConfigureAwait(true);
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] title probe (net): {board.DirectoryName}/{key} -> \"{title ?? "(取得不可)"}\"");
        }
        _navTitleCache[cacheKey] = (DateTimeOffset.UtcNow, title);
        return title;
    }

    /// <summary>ナビ解決用の subject.txt を取得する (メモリ TTL キャッシュ付き)。
    /// ネットワーク取得は 8 秒で打ち切り (= 起動直後など回線が遅くてもボタン状態を長時間
    /// ブランクにしない)。失敗 / タイムアウト時はローカルキャッシュへフォールバック。</summary>
    private async Task<IReadOnlyList<ThreadInfo>> GetSubjectsForNavAsync(Board board)
    {
        var cacheKey = (board.Host, board.DirectoryName);
        if (_navSubjectCache.TryGetValue(cacheKey, out var hit) &&
            DateTimeOffset.UtcNow - hit.FetchedAt < NavSubjectCacheTtl)
            return hit.Subjects;

        IReadOnlyList<ThreadInfo> subjects;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(NavSubjectFetchTimeout);
            subjects = await _subjectClient.FetchAndSaveAsync(board, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] subject fetch 失敗 ({board.BoardName}), disk fallback: {ex.Message}");
            try { subjects = await _subjectClient.LoadFromDiskAsync(board).ConfigureAwait(true); }
            catch { subjects = Array.Empty<ThreadInfo>(); }
        }
        _navSubjectCache[cacheKey] = (DateTimeOffset.UtcNow, subjects);
        return subjects;
    }

    private static string FmtTop(IReadOnlyList<ThreadNavCandidate> cands)
        => cands.Count == 0 ? "" : $" top=[{cands[0].Key} {cands[0].Score:F0}]";

    /// <summary>リンク抽出結果のコンパクト表記 (先頭 6 件 + 残数)。</summary>
    private static string FmtLinks(List<KeyValuePair<string, int>> links)
    {
        if (links.Count == 0) return "-";
        var head = string.Join(",", links.Take(6).Select(kv => $"{kv.Key}x{kv.Value}"));
        return links.Count > 6 ? $"{head}+{links.Count - 6}" : head;
    }

    /// <summary>ローカルログ key 集合を板単位 TTL キャッシュ付きで返す
    /// (= <see cref="ChBrowser.Services.Api.DatClient.EnumerateExistingThreadKeys"/> の薄ラッパ)。</summary>
    private IReadOnlySet<string> GetLogKeysCached(Board board)
    {
        var cacheKey = (board.Host, board.DirectoryName);
        if (_navLogKeysCache.TryGetValue(cacheKey, out var hit) &&
            DateTimeOffset.UtcNow - hit.At < NavSubjectCacheTtl)
            return hit.Keys;
        var keys = _datClient.EnumerateExistingThreadKeys(board);
        _navLogKeysCache[cacheKey] = (DateTimeOffset.UtcNow, keys);
        return keys;
    }

    // -----------------------------------------------------------------
    // ユーザ操作 (前 / 次 / 🚫 / ✅ / 候補メニュー / 手動 URL)
    // -----------------------------------------------------------------

    /// <summary>前 / 次 クリック。採用中 target を開く (既存タブがあればアクティブ化 + 差分取得)。
    /// target 未決 (= ボタン無効状態) では何もしない。
    ///
    /// <para>移動元スレがお気に入り登録済みの場合、移動先スレを未登録なら自動でお気に入りに追加する
    /// (= 「連番チェーンを読み進めた先はすべてお気に入り = メディア先読み対象」に揃える設計)。
    /// 移動元の判定は <see cref="OpenThreadAsync"/> の前に取る
    /// (オープン後は tab.ThreadKey が移動先に差し替わり得るため)。</para></summary>
    public async Task OpenNavTargetAsync(ThreadTabViewModel tab, bool isPrev)
    {
        var key = isPrev ? tab.PrevNavKey : tab.NextNavKey;
        if (string.IsNullOrEmpty(key)) return;
        var title = isPrev ? tab.PrevNavTitle : tab.NextNavTitle;
        // 未オープンの遷移先は、次なら現在の直左、前なら直右に挿入する。
        // 既存タブの場合は OpenThreadAsync 側でそのタブをアクティブ化するだけで、並びは変えない。
        var sourceGroup = GroupOf(tab);
        var sourceIndex = sourceGroup?.Tabs.IndexOf(tab) ?? -1;
        var insertAtIndex = sourceIndex >= 0
            ? (isPrev ? sourceIndex + 1 : sourceIndex)
            : (int?)null;
        var srcFavorited = Favorites.IsThreadFavorited(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[threadNav] open {(isPrev ? "prev" : "next")}: {key} \"{title}\" from {tab.ThreadKey}");
        await OpenThreadAsync(
            tab.Board,
            new ThreadInfo(key, title ?? "", 0, 0),
            insertAtIndex: insertAtIndex).ConfigureAwait(true);

        // 移動元がお気に入りなら移動先を自動登録 (既存登録 / オープン失敗でタブ削除済みのときは何もしない)。
        if (srcFavorited && Favorites.FindThread(tab.Board.Host, tab.Board.DirectoryName, key) is null)
        {
            ToggleThreadFavorite(tab.Board, key, title ?? "");
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] auto-favorite (source was favorited): {key} \"{title}\"");
        }

        // 移動元スレがルートにお気に入りされていて、次スレが解決済みなら
        // シリーズフォルダへ整理する (= 前スレがお気に入りのままルートに残る問題の修正)。
        if (srcFavorited && tab.HasNextNav)
        {
            var srcFav = Favorites.FindThread(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
            if (srcFav is { Parent: null })
            {
                var folder = Favorites.GetOrCreateRootFolder(DeriveSeriesFolderName(tab.Title));
                Favorites.MoveIntoFolder(srcFav, folder);
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    $"[threadNav] reorganized source into folder: {tab.ThreadKey} \"{tab.Title}\" → \"{folder.Name}\"");
            }
        }
    }

    /// <summary>✅ 確定ボタン。両サイドの現採用 target を idx.json へ永続化して confirmed にする。
    /// 未採用側は既存確定値 (あれば) を維持。</summary>
    public void ConfirmNavigation(ThreadTabViewModel tab)
    {
        UpdateNavIndex(tab, existing => existing with
        {
            PrevThreadKey = tab.PrevNavKey ?? existing.PrevThreadKey,
            NextThreadKey = tab.NextNavKey ?? existing.NextThreadKey,
        });
        tab.IsPrevNavConfirmed = tab.HasPrevNav;
        tab.IsNextNavConfirmed = tab.HasNextNav;
        StatusMessage = $"前後スレを確定しました (前={tab.PrevNavKey ?? "-"} / 次={tab.NextNavKey ?? "-"})";
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[threadNav] confirm: src={tab.ThreadKey} prev={tab.PrevNavKey ?? "-"} next={tab.NextNavKey ?? "-"}");
    }

    /// <summary>右クリック候補メニューからの選択。その場で採用 + 確定 + オープンする
    /// (= 「まちがってたら次候補を選択できるフローを繰り返す」の 1 ステップ)。
    /// UI 経路は従来どおりオープンの完了を待たない。完了を待って結果を取得したい経路
    /// (set_nav_target ツール等) は <see cref="AdoptNavCandidateAndOpenAsync"/> を直接 await する。</summary>
    public void AdoptNavCandidate(ThreadTabViewModel tab, bool isPrev, ThreadNavCandidate candidate)
        => _ = AdoptNavCandidateAndOpenAsync(tab, isPrev, candidate);

    /// <summary>候補の「採用 + 確定 + オープン」の本体。右クリックメニュー / 手動 URL /
    /// set_nav_target(action="adopt") の共通経路。オープンの完了を待ち、成功 / 失敗を
    /// メッセージ文字列として返す (= ツール経路では開くのに失敗したことが呼び出し元に伝わる)。
    /// 採用と確定はオープンの成否に関わらず先に行う (= 従来動作)。</summary>
    internal async Task<string> AdoptNavCandidateAndOpenAsync(ThreadTabViewModel tab, bool isPrev, ThreadNavCandidate candidate)
    {
        UpdateNavIndex(tab, existing => existing with
        {
            PrevThreadKey = isPrev ? candidate.Key : existing.PrevThreadKey,
            NextThreadKey = !isPrev ? candidate.Key : existing.NextThreadKey,
        });
        ApplyNavResult(tab, isPrev, candidate.Key, candidate.Title, confirmed: true,
            isPrev ? tab.PrevNavCandidates ?? Array.Empty<ThreadNavCandidate>()
                   : tab.NextNavCandidates ?? Array.Empty<ThreadNavCandidate>());
        try
        {
            // ApplyNavResult 済みなので OpenNavTargetAsync が候補 key を開く (ログも共通経路で出る)。
            await OpenNavTargetAsync(tab, isPrev).ConfigureAwait(true);
            return $"{(isPrev ? "前" : "次")}スレとして採用して開きました: [{candidate.Key}]";
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[threadNav] adopt open failed: src={tab.ThreadKey} key={candidate.Key}: {ex.Message}");
            return $"{(isPrev ? "前" : "次")}スレとして採用しましたが、オープンに失敗しました: [{candidate.Key}] ({ex.Message})";
        }
    }

    /// <summary>🚫 除外ボタン。指定 key を NavExcludedKeys に追加して永続化し、再解決する。
    /// 除外 key が現採用 (= 確定済含む) だった場合は確定値も取り消す。</summary>
    public async Task ExcludeNavCandidateAsync(ThreadTabViewModel tab, string key)
    {
        UpdateNavIndex(tab, existing =>
        {
            var set = new HashSet<string>(existing.NavExcludedKeys ?? Array.Empty<string>(), StringComparer.Ordinal)
                { key };
            return existing with { NavExcludedKeys = set.ToArray() };
        });

        // 確定値として採用中の key を除外した場合は確定も取り消す (= 自動解決に戻す)。
        if ((tab.IsPrevNavConfirmed && tab.PrevNavKey == key) ||
            (tab.IsNextNavConfirmed && tab.NextNavKey == key))
        {
            UpdateNavIndex(tab, existing => existing with
            {
                PrevThreadKey = tab.PrevNavKey == key ? null : existing.PrevThreadKey,
                NextThreadKey = tab.NextNavKey == key ? null : existing.NextThreadKey,
            });
        }

        StatusMessage = $"候補を除外しました [{key}] — 再解決します";
        ChBrowser.Services.Logging.LogService.Instance.Write($"[threadNav] exclude: src={tab.ThreadKey} key={key}");
        await ResolveThreadChainAsync(tab, force: true).ConfigureAwait(true);
    }

    /// <summary>手動 URL 設定。5ch 系スレ URL 以外 / 自スレ自身 / 他板は拒否して false。
    /// 成功時は採用 + 確定 + オープンまで一気に行う。</summary>
    public bool SetManualNavTarget(ThreadTabViewModel tab, bool isPrev, string urlInput)
    {
        var parsed = AddressBarParser.Parse(urlInput);
        if (parsed.Kind != AddressBarTargetKind.Thread)
        {
            StatusMessage = "手動設定エラー: スレッド URL として認識できません";
            return false;
        }

        // 同一板チェック (root domain + dir)。他板への移動はナビの意味ではないので弾く。
        var sameBoard =
            parsed.Directory.Equals(tab.Board.DirectoryName, StringComparison.OrdinalIgnoreCase) &&
            DataPaths.ExtractRootDomain(parsed.Host).Equals(DataPaths.ExtractRootDomain(tab.Board.Host), StringComparison.OrdinalIgnoreCase);
        if (!sameBoard)
        {
            StatusMessage = $"手動設定エラー: 同じ板のスレ URL を指定してください ({parsed.Host}/{parsed.Directory})";
            return false;
        }
        if (parsed.ThreadKey == tab.ThreadKey)
        {
            StatusMessage = "手動設定エラー: 自分自身のスレです";
            return false;
        }

        var cand = new ThreadNavCandidate(parsed.ThreadKey, "", 0, 0);
        AdoptNavCandidate(tab, isPrev, cand);
        // 手動入力はタイトル不明 (= 空) で採用されるので、subject / ローカル dat から title を引く再解決をかける。
        KickNavResolve(tab, force: true);
        return true;
    }

    /// <summary>確定値の取り消し (候補メニュー「設定をクリア」)。該当側のみ null 化して再解決する。</summary>
    public async Task ClearNavOverrideAsync(ThreadTabViewModel tab, bool isPrev)
    {
        UpdateNavIndex(tab, existing => existing with
        {
            PrevThreadKey = isPrev ? null : existing.PrevThreadKey,
            NextThreadKey = !isPrev ? null : existing.NextThreadKey,
        });
        if (isPrev) tab.IsPrevNavConfirmed = false; else tab.IsNextNavConfirmed = false;
        StatusMessage = $"{(isPrev ? "前" : "次")}スレの確定を解除しました";
        await ResolveThreadChainAsync(tab, force: true).ConfigureAwait(true);
    }

    /// <summary>idx.json のナビ関連フィールドを更新する共通ヘルパ (load → mutate → save)。</summary>
    private void UpdateNavIndex(ThreadTabViewModel tab, Func<ThreadIndex, ThreadIndex> mutate)
    {
        var existing = _threadIndex.Load(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey)
                        ?? new ThreadIndex(null, null);
        _threadIndex.Save(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey, mutate(existing));
    }

    /// <summary>アクティブタブの OpenThreadAsync / RefreshThreadAsync 完了フックや選択時セーフティネットから呼ぶ
    /// fire-and-forget 解決起動。非アクティブタブは選択されるまで解決しない。ユーザ操作由来 (手動設定等) は force=true で本文キャッシュを無視する。
    /// 併せて板のローカルログ key キャッシュを無効化する (= 直前の取得で新規 dat が落ちているかもしれない)。</summary>
    private void KickNavResolve(ThreadTabViewModel tab, bool force = false)
    {
        if (!ReferenceEquals(_activeThreadGroup.SelectedTab, tab)) return;
        _navLogKeysCache.Remove((tab.Board.Host, tab.Board.DirectoryName));
        _ = ResolveThreadChainAsync(tab, force);
    }

    // -----------------------------------------------------------------
    // AI / MCP ツール公開 (ThreadToolset の get_nav_state / open_next_thread /
    // open_prev_thread / set_nav_target から呼ばれる)
    // -----------------------------------------------------------------

    /// <summary>ツール層 (<see cref="ThreadToolset"/>) 向けに片側ナビ状態のライブ読み取りを行う。
    /// スナップショットでなく都度読みにするのは、ツール連鎖 (採用 → オープン等) の間に
    /// 状態が更新されても常に最新を見せるため。UI スレッド前提 (= ObservableProperty 直読み)。</summary>
    internal NavSideState ReadNavSideForTool(ThreadTabViewModel tab, bool isPrev)
        => isPrev
            ? new NavSideState(tab.PrevNavKey, tab.PrevNavTitle ?? "", tab.IsPrevNavConfirmed,
                               ToNavCandidateInfos(tab.PrevNavCandidates))
            : new NavSideState(tab.NextNavKey, tab.NextNavTitle ?? "", tab.IsNextNavConfirmed,
                               ToNavCandidateInfos(tab.NextNavCandidates));

    private static IReadOnlyList<NavCandidateInfo> ToNavCandidateInfos(IReadOnlyList<ThreadNavCandidate>? cands)
        => cands is null || cands.Count == 0
            ? Array.Empty<NavCandidateInfo>()
            : cands.Select(c => new NavCandidateInfo(c.Key, c.Title, c.PostCount, c.Score)).ToArray();

    /// <summary>set_nav_target の実体。action は "adopt" / "exclude" / "clear" / "confirm"
    /// (adopt / exclude の key・方向検証は ThreadToolset 側で済んでいる前提)。
    /// ユーザ向けメッセージ文字列を返す。UI スレッド前提。</summary>
    internal async Task<string> ApplyNavMutationForToolAsync(ThreadTabViewModel tab, NavMutationRequest req)
    {
        switch (req.Action)
        {
            case "confirm":
                ConfirmNavigation(tab);
                return $"前後スレを確定しました (前={tab.PrevNavKey ?? "-"} / 次={tab.NextNavKey ?? "-"})";

            case "exclude":
                await ExcludeNavCandidateAsync(tab, req.Key!).ConfigureAwait(true);
                return $"候補 [{req.Key}] を除外登録して再解決しました";

            case "clear":
                await ClearNavOverrideAsync(tab, req.IsPrev).ConfigureAwait(true);
                return $"{(req.IsPrev ? "前" : "次")}スレの確定を解除しました (自動推測に戻します)";

            case "adopt":
            {
                // 候補メニュー選択と同じ経路: 採用 + 確定 + オープンし、タイトル不明分を再解決で補う。
                // オープン完了を待ってから応答する (= 失敗時はその旨が LLM/MCP 呼び出し元へ伝わる)。
                var openMsg = await AdoptNavCandidateAndOpenAsync(
                    tab, req.IsPrev, new ThreadNavCandidate(req.Key!, "", 0, 0)).ConfigureAwait(true);
                KickNavResolve(tab, force: true);
                return openMsg;
            }

            default:
                return $"未知の action: {req.Action}";
        }
    }
}

