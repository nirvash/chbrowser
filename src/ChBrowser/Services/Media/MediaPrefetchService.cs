using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using ChBrowser.Models;
using ChBrowser.Services.Image;

namespace ChBrowser.Services.Media;

/// <summary>お気に入りスレのメディア先読みキュー。
///
/// <para>スレ本文から抽出した画像 / 動画 URL をバックグラウンドで取得して
/// <see cref="ImageCacheService"/> (Kind=Image / Kind=Video) に永続化する。
/// 対象は <see cref="IsThreadFavorited"/> デリゲートが true を返すスレのみ
/// (= 判定は App 起動時に MainViewModel.Favorites へ配線される単一箇所)。</para>
///
/// <para>呼び出し経路は 1 本化されている:
/// スレオープン時 / 差分取得時 / 将来の自動巡回が、いずれも
/// <see cref="EnqueueForPosts"/> を呼ぶだけ。UI (WebView2 / thread.js) は関与しないため
/// バックグラウンド差分取得 (= タブ未オープン) でも先読みが走る。</para>
///
/// <para>帯域設計: 画像は同時 2 本まで (<see cref="_imageGate"/>)、動画は 1 本ずつ逐次。
/// ユーザ起因の取得 (近接 &lt;img&gt; GET / クリック DL) はこのキューを通らないため
/// 先読みが飢えさせることはない。動画は <see cref="VideoDownloadManager.Request"/> 経由なので、
/// 先読み中 URL のクリック再生は既存 DL にコアレスされ、確定失敗ストア (404/410/403) も共有される。</para>
///
/// <para>失敗時は <see cref="MediaAcquisitionTracker"/> に記録する (= 次回表示時に既存 UI が
/// 「クリックで再試行」扱いにする。自動再試行はしない)。</para></summary>
public sealed class MediaPrefetchService : IDisposable
{
    /// <summary>(host, directoryName, threadKey) → お気に入り登録済みか。
    /// App.OnStartup で MainViewModel.Favorites.IsThreadFavorited へ配線される。
    /// 未配線 (= null) の間は何も先読みしない。</summary>
    public Func<string, string, string, bool>? IsThreadFavorited { get; set; }

    private readonly ImageCacheService       _cache;
    private readonly MediaAcquisitionTracker _tracker;
    private readonly VideoDownloadManager    _videoDl;
    private readonly ImageMetaService        _imageMeta;
    private readonly UrlExpander             _expander;

    /// <summary>画像取得専用クライアント (ブラウザ UA)。VideoDownloadManager と同じ理由で
    /// Monazilla UA は使わない (= 外部 CDN が UA で挙動を変える対策)。</summary>
    private readonly HttpClient _http;

    private sealed record PrefetchItem(bool IsVideo, string Url);

    private readonly Channel<PrefetchItem> _channel =
        Channel.CreateUnbounded<PrefetchItem>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>キュー投入済み / 取得中 URL の重複排除セット。
    /// 同一スレの再オープン・全件 enqueue と差分 enqueue の重複をここで吸収する。</summary>
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);

    /// <summary>動画 1 本の完了待ち (Request 後にイベントで解く)。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _videoWaits =
        new(StringComparer.Ordinal);

    private readonly SemaphoreSlim           _imageGate = new(2);
    private readonly CancellationTokenSource _cts       = new();
    private readonly Task                    _consumer;

    public MediaPrefetchService(
        ImageCacheService cache, MediaAcquisitionTracker tracker, VideoDownloadManager videoDl,
        ImageMetaService imageMeta, UrlExpander expander)
    {
        _cache     = cache;
        _tracker   = tracker;
        _videoDl   = videoDl;
        _imageMeta = imageMeta;
        _expander  = expander;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression   = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 5,
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36 ChBrowser");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");

        // 動画 DL 完了 / 失敗イベントを「逐次ポンプの待ち解放」に使う。
        _videoDl.DownloadCompleted += OnVideoDownloadSettled;
        _videoDl.DownloadFailed    += OnVideoDownloadSettled;

        _consumer = Task.Run(() => ConsumeLoopAsync(_cts.Token));
    }

    // ---- 公開 API ---------------------------------------------------------------

    /// <summary>スレ投稿列から先読み対象 URL を抽出してキューへ積む。
    /// お気に入り判定・設定 ON/OFF・キャッシュ済み/失敗済みフィルタはすべてここで行うので、
    /// 呼び出し側は「対象投稿列を渡す」だけでよい (冪等・非ブロッキング)。</summary>
    public void EnqueueForPosts(string host, string directoryName, string threadKey, IReadOnlyList<Post>? posts)
    {
        if (posts is null || posts.Count == 0) return;
        var fav = IsThreadFavorited;
        if (fav is null || !fav(host, directoryName, threadKey)) return;

        var app = Application.Current as App;
        var cfg = app?.CurrentConfig;
        if (cfg is null) return;

        var (images, videos) = MediaUrlExtractor.Extract(posts.Select(p => p.Body));

        var enqImages = EnqueueImages(images, cfg.PrefetchImagesOnThreadLoad);
        var enqVideos = EnqueueVideos(videos, cfg.PrefetchVideosOnThreadLoad);
        if (enqImages + enqVideos > 0)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[mediaPrefetch] enqueue: {enqImages} images / {enqVideos} videos ({host}/{directoryName}/{threadKey})");
        }
    }

    private int EnqueueImages(IEnumerable<string> urls, bool enabled)
    {
        if (!enabled) return 0;
        var n = 0;
        foreach (var url in urls)
        {
            if (_cache.Contains(url)) continue;
            if (_tracker.IsFailed(url, MediaAcquisitionKind.Image)) continue;
            if (!_queued.TryAdd(url, 0)) continue;
            if (_channel.Writer.TryWrite(new PrefetchItem(IsVideo: false, url))) n++;
            else _queued.TryRemove(url, out _);
        }
        return n;
    }

    private int EnqueueVideos(IEnumerable<string> urls, bool enabled)
    {
        if (!enabled) return 0;
        var n = 0;
        foreach (var url in urls)
        {
            if (_cache.Contains(url, CacheKind.Video)) continue;
            if (_videoDl.IsFailed(url)) continue;
            if (!_queued.TryAdd(url, 0)) continue;
            if (_channel.Writer.TryWrite(new PrefetchItem(IsVideo: true, url))) n++;
            else _queued.TryRemove(url, out _);
        }
        return n;
    }

    // ---- consumer ----------------------------------------------------------------

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item.IsVideo)
                {
                    try { await PrefetchVideoAsync(item.Url).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[mediaPrefetch] video task failed url={item.Url}: {ex.Message}");
                    }
                }
                else
                {
                    // 画像は同時 2 本まで内部ゲートで絞って fire-and-forget
                    // (= 動画の直前に入った画像群でポンプが詰まらないように)。
                    _ = PrefetchImageAsync(item.Url);
                }
            }
        }
        catch (OperationCanceledException) { /* Dispose 時の正常停止 */ }
    }

    /// <summary>画像 1 枚の取得。失敗時は tracker に記録して次回表示時の自動 GET 抑止に任せる。</summary>
    private async Task PrefetchImageAsync(string url)
    {
        try
        {
            await _imageGate.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                var actual = await ResolveActualUrlAsync(url).ConfigureAwait(false);
                if (actual is null) return;

                // dequeue 直前の再確認。近接 <img> GET / 前回分との二重取得の大半をここで消す。
                if (_cache.Contains(actual)) return;
                if (_tracker.IsFailed(actual, MediaAcquisitionKind.Image)) return;

                using var resp = await _http.GetAsync(actual, HttpCompletionOption.ResponseHeadersRead, _cts.Token)
                                            .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[mediaPrefetch] image HTTP {(int)resp.StatusCode} url={actual}");
                    _tracker.MarkFailed(actual, MediaAcquisitionKind.Image);
                    return;
                }

                // サイズしきい値: Content-Length が判明したときだけ事前判定する。
                // 不明な場合は従来 UX (= HEAD 失敗時も読む) に合わせて取得を続行。
                var len = resp.Content.Headers.ContentLength;
                if (len is long size && size > ThresholdBytes())
                {
                    Debug.WriteLine($"[mediaPrefetch] image over threshold ({size} bytes) url={actual}");
                    return;
                }

                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                await using var stream = await resp.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
                await _cache.SaveAsync(actual, stream, contentType, CacheKind.Image).ConfigureAwait(false);
                Debug.WriteLine($"[mediaPrefetch] image saved url={actual} ({len} bytes)");
            }
            finally
            {
                _imageGate.Release();
            }
        }
        catch (OperationCanceledException) { /* Dispose 時 */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mediaPrefetch] image task crashed url={url}: {ex.Message}");
        }
        finally
        {
            _queued.TryRemove(url, out _);
        }
    }

    /// <summary>x.com / pixiv 等の非同期展開対象なら実体 URL を解決して返す。
    /// 展開不要なら元 URL、媒体が無い/失敗なら null。</summary>
    private async Task<string?> ResolveActualUrlAsync(string url)
    {
        if (!UrlExpander.IsAsyncExpandable(url)) return url;

        // 過去に SNS 展開失敗済みの URL は自動再試行しない (= ユーザ明示クリックで Reset)。
        if (_tracker.IsFailed(url, MediaAcquisitionKind.SnsExpand)) return null;

        var expand = await _expander.ExpandAsync(url).ConfigureAwait(false);
        if (expand.IsNoMedia) return null;                       // 媒体なし確定 (= スロットも出ない)
        if (!expand.IsResolved || string.IsNullOrEmpty(expand.Url))
        {
            // 展開失敗 (API ダウン等)。確定的削除判定 (Unavailable) のときだけ tracker に記録する
            // (= ThreadDisplayPane.ReplyImageMetaAsync の表示経路と同じ基準)。
            if (expand.IsUnavailable) _tracker.MarkFailed(url, MediaAcquisitionKind.SnsExpand);
            return null;
        }
        return expand.Url;
    }

    /// <summary>動画 1 本の取得 (逐次ポンプから呼ばれる)。
    /// 本体は <see cref="VideoDownloadManager.Request"/> に委譲し、完了/失敗イベントで待ちを解く。
    /// キャッシュ済み / 失敗済み / 既に in-flight の場合も Request 側がイベントを発火するため
    /// 呼び出し側は分岐不要。</summary>
    private async Task PrefetchVideoAsync(string url)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _videoWaits[url] = tcs;
        try
        {
            _videoDl.Request(url);
            // マネージャのタイムアウト (5 分) + 余裕を見た上限。超過時は諦めて次項目へ
            // (= イベント取りこぼし等の異常系でポンプが止まることを防ぐ)。
            await tcs.Task.WaitAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Debug.WriteLine($"[mediaPrefetch] video wait timeout url={url}");
        }
        catch (OperationCanceledException) { /* Dispose 時 */ }
        finally
        {
            _videoWaits.TryRemove(url, out _);
            _queued.TryRemove(url, out _);
        }
    }

    private void OnVideoDownloadSettled(object? sender, VideoDownloadEventArgs e)
    {
        if (_videoWaits.TryRemove(e.Url, out var tcs)) tcs.TrySetResult(true);
    }

    // ---- helpers -----------------------------------------------------------------

    /// <summary>画像しきい値 (bytes)。設定変更を都度反映するため毎回読む。</summary>
    private static long ThresholdBytes()
    {
        var mb = (Application.Current as App)?.CurrentConfig.ImageSizeThresholdMb ?? 5;
        return Math.Max(0, mb) * 1024L * 1024L;
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _channel.Writer.TryComplete();
        }
        catch { /* 二重 Dispose 保護 */ }
        try { _consumer.Wait(TimeSpan.FromSeconds(3)); } catch { /* キャンセル後の残務は切り捨て */ }
        _http.Dispose();
    }
}
