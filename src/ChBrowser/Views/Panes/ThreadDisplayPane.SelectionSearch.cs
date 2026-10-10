using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using ChBrowser.Services;

namespace ChBrowser.Views.Panes;

public partial class ThreadDisplayPane
{
    private static void AttachSelectionGoogleSearch(CoreWebView2 core)
    {
        core.ContextMenuRequested -= ThreadWebView_ContextMenuRequested;
        core.ContextMenuRequested += ThreadWebView_ContextMenuRequested;
    }

    private static void ThreadWebView_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var target = e.ContextMenuTarget;
        if (!target.HasSelection || !GoogleSearchUrl.TryCreate(target.SelectionText, out var url)) return;

        if (sender is not CoreWebView2 core) return;
        var item = core.Environment.CreateContextMenuItem("Googleで検索", null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => LaunchGoogleSearch(url);
        e.MenuItems.Add(item);
    }

    private static void LaunchGoogleSearch(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SelectionGoogleSearch] Browser launch failed: {ex.Message}");
        }
    }
}
