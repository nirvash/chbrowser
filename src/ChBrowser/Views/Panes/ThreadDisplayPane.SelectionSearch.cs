using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Wpf;
using ChBrowser.Services;

namespace ChBrowser.Views.Panes;

public partial class ThreadDisplayPane
{
    private void HandleSelectionContextMenu(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (!payload.TryGetProperty("text", out var textProperty)
            || textProperty.ValueKind != JsonValueKind.String) return;
        var selectedText = textProperty.GetString();
        if (string.IsNullOrWhiteSpace(selectedText)) return;

        var menu = new ContextMenu
        {
            PlacementTarget = wv,
            Placement = PlacementMode.MousePoint,
            DataContext = selectedText,
        };
        var searchItem = new MenuItem { Header = "Googleで検索" };
        searchItem.Click += SelectionGoogleSearch_Click;
        var copyItem = new MenuItem { Header = "コピー" };
        copyItem.Click += SelectionCopy_Click;
        menu.Items.Add(searchItem);
        menu.Items.Add(copyItem);
        menu.IsOpen = true;
    }

    private void SelectionGoogleSearch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectionTextOf(sender) is not { } text || !GoogleSearchUrl.TryCreate(text, out var url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SelectionGoogleSearch] Browser launch failed: {ex.Message}");
        }
    }

    private static void SelectionCopy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectionTextOf(sender) is not { } text) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Debug.WriteLine($"[SelectionCopy] Clipboard.SetText failed: {ex.Message}"); }
    }

    private static string? SelectionTextOf(object sender)
        => (sender as MenuItem)?.DataContext as string;
}
