using System;
using System.Drawing;
using System.Linq;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using FormsIntegration = System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private readonly Forms.Form _searchWindow = new();
    private readonly WpfControls.TextBox _searchInput = new();
    private readonly WpfControls.TextBlock _searchPlaceholder = new();
    private readonly WpfControls.Border _searchInputBorder = new();
    private readonly FormsIntegration.ElementHost _searchInputHost = new();
    private Guid? _searchingBoxId;
    private string _searchQuery = string.Empty;
    private bool _updatingSearchInput;

    private void InitializeBoxSearch()
    {
        _searchWindow.FormBorderStyle = Forms.FormBorderStyle.None;
        _searchWindow.ShowInTaskbar = false;
        _searchWindow.StartPosition = Forms.FormStartPosition.Manual;
        _searchWindow.AutoScaleMode = Forms.AutoScaleMode.None;
        _searchWindow.Padding = Forms.Padding.Empty;
        _searchWindow.Margin = Forms.Padding.Empty;

        _searchInput.BorderThickness = new Wpf.Thickness(0);
        _searchInput.Padding = new Wpf.Thickness(10, 0, 10, 0);
        _searchInput.VerticalContentAlignment = Wpf.VerticalAlignment.Center;
        _searchInput.AcceptsReturn = false;
        _searchInput.TextWrapping = Wpf.TextWrapping.NoWrap;
        _searchInput.Background = WpfMedia.Brushes.Transparent;
        WpfMedia.TextOptions.SetTextFormattingMode(_searchInput, WpfMedia.TextFormattingMode.Display);
        WpfMedia.TextOptions.SetTextRenderingMode(_searchInput, WpfMedia.TextRenderingMode.Grayscale);

        _searchPlaceholder.Text = "搜索盒子内容";
        _searchPlaceholder.Margin = new Wpf.Thickness(10, 0, 10, 0);
        _searchPlaceholder.VerticalAlignment = Wpf.VerticalAlignment.Center;
        _searchPlaceholder.IsHitTestVisible = false;

        var content = new WpfControls.Grid();
        content.Children.Add(_searchPlaceholder);
        content.Children.Add(_searchInput);
        _searchInputBorder.BorderThickness = new Wpf.Thickness(1);
        _searchInputBorder.CornerRadius = new Wpf.CornerRadius(6);
        _searchInputBorder.Child = content;

        _searchInputHost.Dock = Forms.DockStyle.Fill;
        _searchInputHost.Margin = Forms.Padding.Empty;
        _searchInputHost.Child = _searchInputBorder;
        _searchInput.TextChanged += OnBoxSearchTextChanged;
        _searchInput.KeyDown += OnBoxSearchKeyDown;
        _searchWindow.Controls.Add(_searchInputHost);
    }

    private void DisposeBoxSearch()
    {
        _updatingSearchInput = true;
        _searchingBoxId = null;
        _searchQuery = string.Empty;
        _searchWindow.Dispose();
    }

    private void ToggleBoxSearch(DesktopBox box)
    {
        if (_searchingBoxId == box.Id)
        {
            CloseBoxSearch(clearFilter: true);
            return;
        }

        BeginBoxSearch(box);
    }

    private void BeginBoxSearch(DesktopBox box)
    {
        CloseBoxSearch(clearFilter: true);
        FinishTitleEdit(true);

        _searchingBoxId = box.Id;
        _searchQuery = string.Empty;
        _updatingSearchInput = true;
        _searchInput.Clear();
        _updatingSearchInput = false;

        if (box.ExpandOnHover && !_hoverExpandedBoxes.Contains(box.Id))
        {
            var previouslyExpandedBoxIds = _hoverExpandedBoxes.ToArray();
            _hoverExpansion.Reset();
            foreach (var expandedBoxId in previouslyExpandedBoxIds.Where(id => id != box.Id))
            {
                CollapseHoverExpandedBox(expandedBoxId, updateRegion: false);
            }
            _hoverExpansion.AdoptExpanded(box.Id);
            ExpandHoveredBox(box.Id, updateRegion: false);
            UpdateWindowRegion();
        }

        _geometryDirty = true;
        RebuildGeometry();
        EnsureBoxSearchHandle();
        LayoutBoxSearch();
        _searchWindow.Show(this);
        LayoutBoxSearch();
        _searchWindow.Activate();
        _searchInputHost.Focus();
        _searchInput.Focus();
        RequestVisualLayerRender();
        RequestHeaderActionVisualUpdate();
    }

    private void CloseBoxSearch(bool clearFilter)
    {
        if (_searchingBoxId is null && !_searchWindow.Visible)
        {
            return;
        }

        var previousBoxId = _searchingBoxId;
        _searchingBoxId = null;
        _searchWindow.Hide();
        if (clearFilter)
        {
            _searchQuery = string.Empty;
            _updatingSearchInput = true;
            _searchInput.Clear();
            _updatingSearchInput = false;
        }

        if (previousBoxId is { } boxId)
        {
            RemoveSearchScrollOffsets(boxId);
        }
        _geometryDirty = true;
        RebuildGeometry();
        RequestVisualLayerRender();
        RequestHeaderActionVisualUpdate();
        QueueHoverReconcile();
    }

    private void OnBoxSearchTextChanged(object? sender, WpfControls.TextChangedEventArgs eventArgs)
    {
        _searchPlaceholder.Visibility = string.IsNullOrEmpty(_searchInput.Text)
            ? Wpf.Visibility.Visible
            : Wpf.Visibility.Collapsed;
        if (_updatingSearchInput || _searchingBoxId is null)
        {
            return;
        }

        _searchQuery = _searchInput.Text;
        ApplyBoxSearchFilter();
    }

    private void OnBoxSearchKeyDown(object? sender, WpfInput.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != WpfInput.Key.Escape)
        {
            return;
        }

        eventArgs.Handled = true;
        CloseBoxSearch(clearFilter: true);
    }

    private void ApplyBoxSearchFilter()
    {
        if (_searchingBoxId is not { } boxId)
        {
            return;
        }

        var previousBounds = _boxes.FirstOrDefault(box => box.Box.Id == boxId)?.Bounds ?? RectangleF.Empty;
        RemoveSearchScrollOffsets(boxId);
        _geometryDirty = true;
        RebuildGeometry();

        var geometry = _boxes.First(box => box.Box.Id == boxId);
        var visibleKeys = GetVisibleItemsForBox(geometry)
            .Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boxKeys = GetCachedItemsForBox(boxId)
            .Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selection.RemoveWhere(key => boxKeys.Contains(key) && !visibleKeys.Contains(key));
        if (_hoveredItemKey is not null && boxKeys.Contains(_hoveredItemKey) && !visibleKeys.Contains(_hoveredItemKey))
        {
            ClearItemHover();
        }

        RequestFocusVisualUpdate(RectangleF.Union(previousBounds, geometry.Bounds));
    }

    private void RemoveSearchScrollOffsets(Guid boxId)
    {
        foreach (var key in _scrollOffsets.Keys.Where(key => key.BoxId == boxId).ToArray())
        {
            _scrollOffsets.Remove(key);
        }
    }

    private void ReconcileBoxSearch()
    {
        if (_searchingBoxId is not { } boxId)
        {
            return;
        }

        if (DesktopBoxes.All(box => box.Id != boxId))
        {
            _searchingBoxId = null;
            _searchQuery = string.Empty;
            _updatingSearchInput = true;
            _searchInput.Clear();
            _updatingSearchInput = false;
            _searchWindow.Hide();
            return;
        }

        LayoutBoxSearch();
    }

    private void LayoutBoxSearch()
    {
        if (_searchingBoxId is not { } boxId ||
            _boxes.FirstOrDefault(box => box.Box.Id == boxId) is not { } geometry)
        {
            return;
        }

        var background = ParseOpaqueColor(geometry.Box.Appearance.Background);
        var foreground = ResolveTitleColor(geometry.Box.Appearance.TitleColor, background);
        var accent = ParseOpaqueColor(geometry.Box.Appearance.Accent);
        var fieldBackground = UsesLightText(background)
            ? DesktopItemVisualStyle.Brighten(background, 0.14f)
            : DesktopItemVisualStyle.Brighten(background, 0.52f);
        _searchWindow.BackColor = background;
        _searchInputHost.BackColor = background;
        _searchInputBorder.Background = CreateSearchBrush(fieldBackground);
        _searchInputBorder.BorderBrush = CreateSearchBrush(Color.FromArgb(176, accent));
        _searchInput.Foreground = CreateSearchBrush(foreground);
        _searchInput.CaretBrush = CreateSearchBrush(accent);
        _searchPlaceholder.Foreground = CreateSearchBrush(Color.FromArgb(144, foreground));
        var searchText = string.IsNullOrEmpty(_searchInput.Text)
            ? _searchPlaceholder.Text
            : _searchInput.Text;
        var fontFamily = new WpfMedia.FontFamily(ResolveTitleEditorFontFamily(
            geometry.Box.Appearance.TitleFontFamily,
            searchText));
        var fontSize = geometry.Box.Appearance.TitleFontSize * 96d / 72d;
        var fontWeight = geometry.Box.Appearance.TitleFontBold
            ? Wpf.FontWeights.Bold
            : Wpf.FontWeights.Regular;
        _searchInput.FontFamily = fontFamily;
        _searchInput.FontSize = fontSize;
        _searchInput.FontWeight = fontWeight;
        _searchPlaceholder.FontFamily = fontFamily;
        _searchPlaceholder.FontSize = fontSize;
        _searchPlaceholder.FontWeight = fontWeight;

        var left = ToPixel(geometry.Search.Right + 8);
        var right = ToPixel(geometry.AutoExpand.Left - 8);
        var height = Math.Max(ToPixel(24), Math.Min(ToPixel(28), ToPixel(geometry.Header.Height - 10)));
        var clientBounds = new Rectangle(
            left,
            ToPixel(geometry.Header.Y + geometry.Header.Height / 2) - height / 2,
            Math.Max(ToPixel(72), right - left),
            height);
        _searchWindow.Bounds = new Rectangle(PointToScreen(clientBounds.Location), clientBounds.Size);
    }

    private void EnsureBoxSearchHandle()
    {
        if (!_searchWindow.IsDisposed && !_searchWindow.IsHandleCreated)
        {
            _searchWindow.CreateControl();
        }
    }

    private static WpfMedia.SolidColorBrush CreateSearchBrush(Color color)
    {
        var brush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(
            color.A,
            color.R,
            color.G,
            color.B));
        brush.Freeze();
        return brush;
    }
}
