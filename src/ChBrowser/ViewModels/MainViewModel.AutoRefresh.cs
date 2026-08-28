using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

public sealed partial class MainViewModel
{
    private readonly DispatcherTimer _autoRefreshTimer = new();
    private ThreadTabViewModel? _autoRefreshRoot;
    private ThreadTabViewModel? _autoRefreshNext;
    private bool _autoRefreshRunning;

    [ObservableProperty] private bool _isAutoRefreshEnabled;

    [RelayCommand]
    private void ToggleAutoRefresh()
    {
        if (!IsAutoRefreshEnabled)
        {
            _autoRefreshTimer.Stop();
            _autoRefreshRoot = null;
            _autoRefreshNext = null;
            return;
        }

        _autoRefreshRoot = SelectedThreadTab;
        _autoRefreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, CurrentConfig.ThreadAutoRefreshIntervalMinutes));
        _autoRefreshTimer.Tick -= AutoRefreshTimer_Tick;
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        _autoRefreshTimer.Start();
        _ = RunAutoRefreshAsync();
    }

    private void AutoRefreshTimer_Tick(object? sender, EventArgs e) => _ = RunAutoRefreshAsync();

    private async Task RunAutoRefreshAsync()
    {
        if (_autoRefreshRunning || !IsAutoRefreshEnabled || _autoRefreshRoot is null) return;
        _autoRefreshRunning = true;
        try
        {
            var root = _autoRefreshRoot;
            if (!AllThreadTabs.Contains(root)) { IsAutoRefreshEnabled = false; return; }

            await RefreshThreadAsync(root, scrollToFirstNewPost: false, preserveNewPostsMarker: true).ConfigureAwait(true);
            if (_autoRefreshNext is { } nextTarget && AllThreadTabs.Contains(nextTarget))
                await RefreshThreadAsync(nextTarget, scrollToFirstNewPost: false, preserveNewPostsMarker: true).ConfigureAwait(true);
            await ResolveThreadChainAsync(root, force: true).ConfigureAwait(true);

            var nextKey = root.NextNavKey;
            if (string.IsNullOrEmpty(nextKey)) return;
            var next = AllThreadTabs.FirstOrDefault(t => t.Board.Host == root.Board.Host &&
                t.Board.DirectoryName == root.Board.DirectoryName && t.ThreadKey == nextKey);
            if (next is null)
            {
                await OpenThreadAsync(root.Board,
                    new ChBrowser.Models.ThreadInfo(nextKey, root.NextNavTitle ?? "", 0, 0),
                    activate: false, fetchRemote: true).ConfigureAwait(true);
            }
            _autoRefreshNext = AllThreadTabs.FirstOrDefault(t => t.Board.Host == root.Board.Host &&
                t.Board.DirectoryName == root.Board.DirectoryName && t.ThreadKey == nextKey);
        }
        finally { _autoRefreshRunning = false; }
    }

    private void OnSelectedThreadTabForAutoRefresh(ThreadTabViewModel? value)
    {
        if (IsAutoRefreshEnabled && !ReferenceEquals(value, _autoRefreshRoot))
        {
            IsAutoRefreshEnabled = false;
            _autoRefreshTimer.Stop();
            _autoRefreshRoot = null;
            _autoRefreshNext = null;
        }
    }
}
