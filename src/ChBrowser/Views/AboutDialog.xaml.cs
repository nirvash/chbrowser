using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ChBrowser.Views;

/// <summary>ヘルプ → バージョン情報 で開くダイアログ。
/// バージョン文字列は <see cref="AssemblyInformationalVersionAttribute"/> から読み出す
/// (= csproj の StampGitCommitToVersion ターゲットが "vYYYYMMDD (<sha>[-dirty])" 形式で
/// ビルド日付 + commit を焼きこんでいる)。</summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = ReadInformationalVersion();
        IconImage.Source = LoadLargestIconFrame();
    }

    /// <summary>app.ico に含まれる複数フレーム (16/24/32/48/64/128/256 px) のうち最大解像度を返す。
    /// XAML の <c>&lt;Image Source="..."/&gt;</c> 直書きでは小さいフレームが拾われて拡大表示され
    /// ぼやけるため、code-behind で明示的に選択する。</summary>
    private static BitmapFrame LoadLargestIconFrame()
    {
        var uri = new Uri("pack://application:,,,/Resources/icon/app.ico", UriKind.Absolute);
        var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    private static string ReadInformationalVersion()
    {
        var attr = typeof(AboutDialog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        // "vYYYYMMDD (<sha>[-dirty])" をそのまま表示する (= どの commit のビルドか判別できるのが目的)。
        // git 不在環境で焼きこみに失敗した場合は日付のみの従来形式になる。
        return attr?.InformationalVersion ?? "(version unknown)";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}
