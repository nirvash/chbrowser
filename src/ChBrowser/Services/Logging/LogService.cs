using System;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.Services.Logging;

/// <summary>アプリ内ログ (= Log ペインに表示) のサービス。
/// 静的シングルトン <see cref="Instance"/> 経由でどこからでも書き込める。
///
/// <para>用途:
/// <list type="bullet">
/// <item><description>StatusMessage 変更を流して履歴として保持 (= MainViewModel が partial method で hook)</description></item>
/// <item><description>リリース版でデバッグ情報を出したいとき (= <c>Debug.WriteLine</c> 代替) — DevTools が無くても確認できる</description></item>
/// </list></para>
///
/// <para>UI バインドには <see cref="Text"/> を使う (= ObservableProperty なので変化通知される)。
/// LogPane の <c>TextBox</c> がこれにバインドされ、追記のたびに表示が更新される。</para></summary>
public sealed partial class LogService : ObservableObject
{
    public static LogService Instance { get; } = new();

    /// <summary>ログ全体の上限文字数。超えたら古い分から半分に詰める (= rolling buffer)。</summary>
    private const int MaxChars = 200_000;

    private readonly StringBuilder _sb = new();
    private readonly object _lock = new();

    /// <summary>ファイルシンクの書き込み先。App 起動時に <see cref="InitFileSink"/> で設定されるまで null。</summary>
    private string? _filePath;

    /// <summary>連続失敗でファイル出力を諦めるまでの回数 (= アプリをログ I/O で落とさないための回路遮断)。</summary>
    private int _fileFailCount;
    private bool _fileDisabled;

    /// <summary>UI バインド用の集約文字列。タイムスタンプ + 本文 + 改行 を行単位で並べる。</summary>
    [ObservableProperty]
    private string _text = "";

    private LogService() { }

    /// <summary>ログのファイル出力を有効化する (data/chbrowser.log、UTF-8 append)。
    /// サイズ上限 (<see cref="LogRotateBytes"/>) 超過で 1 世代前の内容を <c>.old</c> に退避して
    /// 新規開始する簡易ローテーション (= 常時ディスク占有は最大約 2 世代分)。長期連続運転
    /// (= リリース版) でも肥大化しない。App の起動処理から 1 度だけ呼ぶことを想定。</summary>
    public void InitFileSink(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            RotateIfNeeded(path);
        }
        catch { /* 削除/ディレクトリ作成に失敗してもアプリは継続 */ }
        lock (_lock)
        {
            _filePath      = path;
            _fileFailCount = 0;
            _fileDisabled  = false;
        }
    }

    /// <summary>1 行追加。UI スレッド外から呼ばれた場合は UI スレッドに marshal される。
    /// 空 / null は no-op。</summary>
    public void Write(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";

        AppendToFile(line);

        var app = Application.Current;
        if (app is { Dispatcher: { } d } && !d.CheckAccess())
        {
            d.BeginInvoke(new Action(() => Append(line)));
            return;
        }
        Append(line);
    }

    /// <summary>1 行をファイルへ追記する。失敗は黙って飲み、5 回連続したら出力自体を停止する
    /// (= ログ I/O が原因で本体を遅延 / クラッシュさせない)。open → 即 close なので
    /// テキストエディタでの同時閲覧も妨げない。
    /// サイズ チェックは <see cref="LogWriteCheckInterval"/> 行ごとに間引いて実施。</summary>
    private void AppendToFile(string line)
    {
        string? path;
        var doRotateCheck = false;
        lock (_lock)
        {
            if (_filePath is null || _fileDisabled) return;
            path          = _filePath;
            doRotateCheck = (++_writeCount % LogWriteCheckInterval) == 0;
        }
        try
        {
            if (doRotateCheck) RotateIfNeeded(path);
            File.AppendAllText(path, line, new UTF8Encoding(false));
            lock (_lock) _fileFailCount = 0;
        }
        catch
        {
            lock (_lock)
            {
                if (++_fileFailCount >= 5) _fileDisabled = true;
            }
        }
    }

    /// <summary>ファイル出力のサイズ上限 (超えたら .old へ退避して新規開始)。</summary>
    private const long LogRotateBytes = 2 * 1024 * 1024;

    /// <summary>ファイルサイズをチェックする行間隔 (毎行 FileInfo を見ないための間引き)。</summary>
    private const int LogWriteCheckInterval = 128;

    private int _writeCount;

    /// <summary><paramref name="path"/> が <see cref="LogRotateBytes"/> を超えていたら
    /// 既存の .old を消したうえで現行を .old へリネームする (= 常に最大 2 世代分だけ保持)。</summary>
    private static void RotateIfNeeded(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < LogRotateBytes) return;
        var old = path + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(path, old);
    }

    private void Append(string line)
    {
        lock (_lock)
        {
            _sb.Append(line);
            if (_sb.Length > MaxChars)
            {
                // 半分まで縮める (= 直近のログを優先して保持)
                var keep = MaxChars / 2;
                _sb.Remove(0, _sb.Length - keep);
            }
            Text = _sb.ToString();
        }
    }

    /// <summary>ログをすべて消す (= LogPane の「クリア」ボタンから呼ばれる)。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _sb.Clear();
            Text = "";
        }
    }
}
