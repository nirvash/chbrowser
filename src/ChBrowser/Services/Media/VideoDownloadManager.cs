using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Services.Image;

namespace ChBrowser.Services.Media;

/// <summary>動画本体のバックグラウンドダウンロード管理 (Phase 4)。
///
/// <para>役割:
/// <list type="bullet">
/// <item><description>URL ベースでのコアレス: 同じ URL に対する複数 Request 呼び出しは
///   1 本の HttpClient ダウンロードに集約される (= スレッド側クリック + ビューワ起動 が同 URL の場合も帯域 1 本)</description></item>
/// <item><description>完了 / 失敗イベント: <see cref="DownloadCompleted"/> / <see cref="DownloadFailed"/> で
///   購読者 (= スレッド側 / ビューワ側) に通知 → 「未DL」バッジ除去や再描画のトリガ</description></item>
/// <item><description>キャッシュ済 URL は即時 <see cref="DownloadCompleted"/> 発火 (= 呼び出し側は
///   キャッシュ存否を気にせず Request() するだけで良い、というシンプル契約)</description></item>
/// </list></para>
///
/// <para>ダウンロードした bytes は <see cref="ImageCacheService.SaveAsync"/> 経由で
/// Kind=Video として永続化される (= .tmp に書いて atomic rename、size 上限チェック等は既存 SaveAsync 任せ)。</para>
///
/// <para>失敗追跡 (DL 失敗 / サムネ抽出失敗) は <see cref="MediaAcquisitionTracker"/> に委譲する (Step B 統合)。
/// 公開 API <see cref="IsFailed"/> / <see cref="MarkThumbFailed"/> 等はそのまま維持して呼び出し側を壊さない。</para></summary>
public sealed class VideoDownloadManager
{
    /// <summary>動画 DL 専用 HttpClient (ブラウザ UA)。
    /// 5ch 用の <c>MonazillaClient.Http</c> (= UA: Monazilla/1.00 ChBrowser/x.x) を流用すると、
    /// 外部 CDN (tadaup.jp 等) が UA で挙動を変えて Chrome とは異なるエンコード/ビットレートの
    /// ファイルを返す事例があるため (= 黒画面再生 / コーデック非対応の見かけ症状)、
    /// 通常のブラウザ UA を使う独自インスタンスを持つ。</summary>
    private readonly HttpClient                _http;
    private readonly ImageCacheService         _cache;
    private readonly MediaAcquisitionTracker   _tracker;

    /// <summary>進行中ダウンロード: URL → 完了 Task (= TrySetResult されるまで Pending)。
    /// 同じ URL の Request() は同じ Task を共有するためコアレスが成立する。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _inFlight = new(StringComparer.Ordinal);

    // ---- 確定的 DL 失敗 (HTTP 404 / 410) の永続ストア ----
    // セッションをまたいで「削除済み動画」を記憶する (= スレ再オープン時に 未DL ではなく
    // 取得失敗として即表示するため)。タイムアウト / 5xx 等の一時失敗は従来どおり in-memory
    // tracker のみに留め、再起動後の自動再試行余地を残す。ユーザの明示再試行 (クリック →
    // mediaSlotRetry → Request) や DL 成功時にストアから除去する (= 自己修復)。
    private readonly string? _failureStorePath;
    private readonly object _failureStoreLock = new();
    private HashSet<string> _persistedFailures = new(StringComparer.Ordinal);
    private bool _failureStoreLoaded;

    /// <summary>ダウンロードが正常完了したとき発火 (URL のみペイロード)。
    /// 引数は <see cref="ImageCacheService"/> にコミット済の状態で渡される (= 直後の Contains/TryGet で即取得可)。
    /// 既にキャッシュ済の URL に対する Request() でも同じイベントが (Task.Run 経由で) 発火する。</summary>
    public event EventHandler<VideoDownloadEventArgs>? DownloadCompleted;

    /// <summary>ダウンロードに失敗したとき発火 (URL のみペイロード)。
    /// HTTP エラー / ストリームエラー / SaveAsync 失敗のすべてで発火する。</summary>
    public event EventHandler<VideoDownloadEventArgs>? DownloadFailed;

    public VideoDownloadManager(ImageCacheService cache, MediaAcquisitionTracker tracker, string? failureStorePath = null)
    {
        _cache            = cache;
        _tracker          = tracker;
        _failureStorePath = failureStorePath;
        // 動画は数 MB 〜数十 MB あり得るので 5 分タイムアウト (= MonazillaClient の 30 秒では切れる)。
        // AutomaticDecompression は動画 (.mp4) 用途では不要だが、サーバが万一 Transfer-Encoding で
        // 圧縮を入れてくる場合に備えて有効化。
        var handler = new System.Net.Http.HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            AllowAutoRedirect      = true,
            MaxAutomaticRedirections = 5,
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        // ブラウザ UA で外部 CDN にアクセス (= Chrome と同じファイルを取得する)。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36 ChBrowser");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
    }

    /// <summary>URL のダウンロードを要求する。
    /// 既にキャッシュ済 → <see cref="DownloadCompleted"/> を即時発火 (Task.Run 経由) して false を返す。
    /// 既に in-flight → 何もせず false を返す (既存タスクの完了で購読者にイベントが届く)。
    /// 新規開始 → true を返す。</summary>
    public bool Request(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        // すでにキャッシュ済 → 統一フローのため Completed イベントを発火 (= 呼び出し側は分岐不要)。
        // 発火は Task.Run に逃がして re-entrance を回避 (Request 呼び出し中の subscriber が同じ
        // ロックを取り直すような構造でもデッドロックしないようにする)。
        if (_cache.Contains(url, CacheKind.Video))
        {
            _ = Task.Run(() => DownloadCompleted?.Invoke(this, new VideoDownloadEventArgs(url)));
            return false;
        }

        // 過去のセッションで失敗済 → 再 DL は試みず、Failed イベントだけ再発火 (UI バッジ維持用)。
        if (_tracker.IsFailed(url, MediaAcquisitionKind.VideoDownload))
        {
            _ = Task.Run(() => DownloadFailed?.Invoke(this, new VideoDownloadEventArgs(url)));
            return false;
        }

        // TryAdd でアトミックに in-flight 登録。失敗 = 既に DL 中なので no-op (= 既存タスクに乗っかる)。
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(url, tcs)) return false;

        _ = Task.Run(async () =>
        {
            bool ok = false;
            bool gone = false;
            try
            {
                (ok, gone) = await DownloadAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VideoDownload] task crashed url={url}: {ex.Message}");
            }
            finally
            {
                _inFlight.TryRemove(url, out _);
                tcs.TrySetResult(ok);
                if (ok)
                {
                    // 再試行が成功したら確定失敗記録を解消 (= 削除済み動画の復活に追従)。
                    ClearPersistedFailure(url);
                    DownloadCompleted?.Invoke(this, new VideoDownloadEventArgs(url));
                }
                else
                {
                    _tracker.MarkFailed(url, MediaAcquisitionKind.VideoDownload);
                    if (gone) MarkPersistedFailure(url);
                    DownloadFailed?.Invoke(this, new VideoDownloadEventArgs(url));
                }
            }
        });

        return true;
    }

    /// <summary>指定 URL の動画が現在ダウンロード進行中か。</summary>
    public bool IsDownloading(string url) => !string.IsNullOrEmpty(url) && _inFlight.ContainsKey(url);

    /// <summary>キャッシュを経由せず、URL を直接指定ファイルへダウンロード保存する
    /// (スレ表示の動画右クリック「保存」で、未キャッシュ動画を保存する用)。
    /// DL 用のブラウザ UA / タイムアウト設定を共有するためここに置く。失敗時は書きかけファイルを消して throw。</summary>
    public async Task SaveDirectAsync(string url, string destPath, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(destPath);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(destPath)) File.Delete(destPath); }
            catch { /* 書きかけ削除の失敗は無視 (元例外を優先) */ }
            throw;
        }
    }

    /// <summary>指定 URL が過去に DL 失敗済か (404 / 5xx / ネットワークエラー等)。
    /// in-memory tracker (セッション内) に加え、確定失敗 (404/410) の永続ストアも参照する
    /// (= アプリ再起動後も「削除済み動画」を取得失敗として即表示できる)。
    /// UI バッジ「取得失敗」/ 404 アート表示の判定に使う。</summary>
    public bool IsFailed(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (_tracker.IsFailed(url, MediaAcquisitionKind.VideoDownload)) return true;
        lock (_failureStoreLock) { return EnsureFailureStoreLoaded().Contains(url); }
    }

    /// <summary>指定 URL の失敗状態をクリアする (= キャッシュ削除メニュー等から呼ばれる)。
    /// 永続ストアの確定失敗記録も併せて解消する。次回 Request() で再 DL が試みられる。</summary>
    public void ResetFailedState(string url)
    {
        _tracker.Reset(url, MediaAcquisitionKind.VideoDownload);
        ClearPersistedFailure(url);
    }

    /// <summary>確定的削除 (HTTP 404 / 410) を DL 経路以外 (= CORS proxy で載るサムネ抽出の
    /// hidden <video> リクエスト) から記録する。セッション tracker と永続ストアの両方に載せる
    /// (= <see cref="IsFailed"/> が即ヒットし、スレッド表示時の先読み時点でクリックなしに
    /// 取得失敗 (404 アート) 表示になる)。ユーザの明示再試行や DL 成功で解消される。</summary>
    public void MarkGone(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        _tracker.MarkFailed(url, MediaAcquisitionKind.VideoDownload);
        MarkPersistedFailure(url);
    }

    /// <summary>サムネ抽出失敗状態を記憶する。
    /// thread.js / viewer.js の抽出失敗メッセージ受信時に呼ばれる。
    /// 次回スレッド表示時にこの URL の自動抽出をスキップさせる用途。</summary>
    public void MarkThumbFailed(string url) => _tracker.MarkFailed(url, MediaAcquisitionKind.VideoThumb);

    /// <summary>指定 URL がサムネ抽出失敗済か。状態 push の <c>thumbExtractFailed</c> フィールドに乗る。</summary>
    public bool IsThumbFailed(string url) => _tracker.IsFailed(url, MediaAcquisitionKind.VideoThumb);

    /// <summary>サムネ抽出失敗状態をクリアする。
    /// ユーザの明示クリック (= videoDownloadStart) で「再試行したい」意思があるとみなして呼ばれる。</summary>
    public void ResetThumbFailedState(string url) => _tracker.Reset(url, MediaAcquisitionKind.VideoThumb);

    /// <summary>DL 本体。戻り値は (成功, 確定的削除) の組。
    /// 確定的削除 (= HTTP 404 / 410) のみ永続ストアに記録する。その他の失敗
    /// (= 5xx / タイムアウト / ネットワーク断 / SaveAsync 上限超過) は一時的とみなし
    /// セッション内の tracker にのみ記録する (= 再起動後に自動再試行の余地を残す)。</summary>
    private async Task<(bool Ok, bool Gone)> DownloadAsync(string url)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                Debug.WriteLine($"[VideoDownload] HTTP {code} url={url}");
                // 確定判定: 404/410 (削除) + 403 (catbox litter 等は削除済みファイルに 403 を返す)。
                // 403 はホットリンク防止等の可能性もあるが、その場合もアプリからは取得不能なので
                // 取得失敗扱いで問題ない (= クリック再試行で再判定され、復活していれば記録は解消される)。
                return (false, code == 403 || code == 404 || code == 410);
            }
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(contentType))
            {
                // Content-Type 不明時は URL 拡張子から推定するため SaveAsync 側に任せる (= "video/mp4" でも可)。
                contentType = "video/mp4";
            }
            else if (!contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                     && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                // 動画でない応答 (= imgur の削除済み動画は imgur トップの text/html へリダイレクトされ
                // HTTP 200 が返る等)。HTML を .mp4 としてキャッシュすると「キャッシュ済みなのに再生不能」
                // の状態を作ってしまうため失敗扱いにする。確定的削除判定 (Gone) はしない
                // (= 一時的なエラーページの可能性も残るため、再試行で復活すれば記録は解消される)。
                Debug.WriteLine($"[VideoDownload] non-video content-type '{contentType}' url={url}");
                return (false, false);
            }

            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await _cache.SaveAsync(url, stream, contentType, CacheKind.Video).ConfigureAwait(false);

            // SaveAsync は MaxFileBytes 超過などで silently skip することがあるので、
            // 最終的にキャッシュに乗ったかを Contains で再確認 (= 上限超過時は失敗扱い)。
            return (_cache.Contains(url, CacheKind.Video), false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoDownload] failed url={url}: {ex.Message}");
            return (false, false);
        }
    }

    // ---- 確定失敗 (404/410) 永続ストアの読み書き ----
    // JSON 形式は単純な文字列配列 1 枚 ("[url, ...]")。書き込みは tmp + move のアトミック置換。

    /// <summary>永続ストアを lazy ロードする (_failureStoreLock 保持から呼ぶこと。lock は再入可能)。</summary>
    private HashSet<string> EnsureFailureStoreLoaded()
    {
        if (_failureStoreLoaded) return _persistedFailures;
        _failureStoreLoaded = true;
        if (_failureStorePath is null) return _persistedFailures;
        try
        {
            if (File.Exists(_failureStorePath))
            {
                var urls = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_failureStorePath));
                if (urls is not null) _persistedFailures = new HashSet<string>(urls, StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoDownload] failure store load failed: {ex.Message}");
        }
        return _persistedFailures;
    }

    /// <summary>ストアを現在の set 内容でアトミックに書き出す (_failureStoreLock 保持から呼ぶこと)。</summary>
    private void PersistFailureStore()
    {
        if (_failureStorePath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_failureStorePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _failureStorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new List<string>(_persistedFailures)));
            File.Move(tmp, _failureStorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoDownload] failure store save failed: {ex.Message}");
        }
    }

    /// <summary>確定失敗 (404/410) をストアに記録する。既に記録済みなら書き込まない (= 書き込み最小化)。</summary>
    private void MarkPersistedFailure(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        lock (_failureStoreLock)
        {
            if (EnsureFailureStoreLoaded().Add(url)) PersistFailureStore();
        }
    }

    /// <summary>確定失敗記録をストアから除去する (DL 成功 / 明示リセット時)。無ければ書き込まない。</summary>
    private void ClearPersistedFailure(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        lock (_failureStoreLock)
        {
            if (EnsureFailureStoreLoaded().Remove(url)) PersistFailureStore();
        }
    }
}

/// <summary>ダウンロード完了/失敗イベントのペイロード (Phase 4)。</summary>
public sealed class VideoDownloadEventArgs : EventArgs
{
    public string Url { get; }
    public VideoDownloadEventArgs(string url) { Url = url; }
}
