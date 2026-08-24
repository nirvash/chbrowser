using System.Windows;
using System.Windows.Controls;

namespace ChBrowser.Views.Settings;

/// <summary>「保存」カテゴリ。画像 / 動画の保存先フォルダを設定する。
/// 「参照...」でフォルダ選択ダイアログ (.NET 8 OpenFolderDialog) を開き、
/// 選択結果を SettingsViewModel の対応プロパティへ反映する (即時保存されるのは設定ウィンドウ経由)。</summary>
public partial class SavePanel : UserControl
{
    public SavePanel() => InitializeComponent();

    private void BrowseImageDir_Click(object sender, RoutedEventArgs e) => BrowseDir(vm => vm.ImageSaveDir, (vm, v) => vm.ImageSaveDir = v);

    private void BrowseVideoDir_Click(object sender, RoutedEventArgs e) => BrowseDir(vm => vm.VideoSaveDir, (vm, v) => vm.VideoSaveDir = v);

    private void ClearImageDir_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm) vm.ImageSaveDir = "";
    }

    private void ClearVideoDir_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm) vm.VideoSaveDir = "";
    }

    private void BrowseDir(Func<ViewModels.SettingsViewModel, string> get, Action<ViewModels.SettingsViewModel, string> set)
    {
        if (DataContext is not ViewModels.SettingsViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title      = "保存先フォルダを選択",
            FolderName = get(vm),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            set(vm, dlg.FolderName);
        }
    }
}
