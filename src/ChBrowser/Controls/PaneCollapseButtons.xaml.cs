using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChBrowser.Models;

namespace ChBrowser.Controls;

public partial class PaneCollapseButtons : UserControl
{
    private PaneLayoutPanel? _layoutPanel;

    public static readonly DependencyProperty PaneKindProperty = DependencyProperty.Register(
        nameof(PaneKind), typeof(PaneId), typeof(PaneCollapseButtons), new PropertyMetadata(PaneId.ThreadDisplay));

    public PaneId PaneKind { get => (PaneId)GetValue(PaneKindProperty); set => SetValue(PaneKindProperty, value); }

    public PaneCollapseButtons()
    {
        InitializeComponent();
        Loaded += PaneCollapseButtons_Loaded;
        Unloaded += PaneCollapseButtons_Unloaded;
    }

    private void PaneCollapseButtons_Loaded(object sender, RoutedEventArgs e)
    {
        _layoutPanel = FindParent<PaneLayoutPanel>();
        if (_layoutPanel is not null) _layoutPanel.LayoutChanged += LayoutPanel_LayoutChanged;
        UpdateParentButton();
    }

    private void PaneCollapseButtons_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_layoutPanel is not null) _layoutPanel.LayoutChanged -= LayoutPanel_LayoutChanged;
        _layoutPanel = null;
    }

    private void LayoutPanel_LayoutChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(UpdateParentButton);
    }

    private void UpdateParentButton()
    {
        var panel = FindParent<PaneLayoutPanel>();
        var key = panel is null ? null : PaneLayoutPanel.GetPaneKey(this, PaneKind);
        if (panel is not null && key is not null)
        {
            SelfButton.Content = panel.GetCollapseGlyph(key, parent: false);
            ParentButton.Content = panel.GetCollapseGlyph(key, parent: true);
        }
        ParentButton.Visibility = panel is not null && key is not null && panel.HasParentSplit(key)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelfButton_Click(object sender, RoutedEventArgs e)
    {
        var panel = FindParent<PaneLayoutPanel>();
        if (panel is not null && PaneLayoutPanel.GetPaneKey(this, PaneKind) is { } key) panel.CollapseSelf(key);
    }

    private void ParentButton_Click(object sender, RoutedEventArgs e)
    {
        var panel = FindParent<PaneLayoutPanel>();
        if (panel is not null && PaneLayoutPanel.GetPaneKey(this, PaneKind) is { } key) panel.CollapseParent(key);
    }

    private T? FindParent<T>() where T : DependencyObject
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is T found) return found;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
