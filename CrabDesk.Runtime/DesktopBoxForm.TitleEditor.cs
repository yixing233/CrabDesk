using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices.ComTypes;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;
using FormsIntegration = System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm : Forms.Form
{

    private bool IsEffectivelyCollapsed(DesktopBox box) =>
        box.ExpandOnHover && !_hoverExpandedBoxes.Contains(box.Id);

    private void ExpandHoveredBox(Guid boxId, bool updateRegion = true)
    {
        var box = DesktopBoxes.FirstOrDefault(candidate => candidate.Id == boxId);
        if (box is null || _hoverExpandedBoxes.Contains(boxId))
        {
            return;
        }
        var fromHeight = GetVisualBoxHeight(box);
        _hoverExpandedBoxes.Clear();
        _hoverExpandedBoxes.Add(boxId);
        _geometryDirty = true;
        StartBoxHeightAnimation(box, fromHeight);
        if (updateRegion)
        {
            UpdateWindowRegion();
        }
    }

    private void CollapseHoverExpandedBox(Guid boxId, bool updateRegion = true)
    {
        var box = DesktopBoxes.FirstOrDefault(candidate => candidate.Id == boxId);
        if (box is null || !_hoverExpandedBoxes.Contains(boxId))
        {
            _hoverExpandedBoxes.Remove(boxId);
            return;
        }
        var fromHeight = GetVisualBoxHeight(box);
        _hoverExpandedBoxes.Remove(boxId);
        _geometryDirty = true;
        StartBoxHeightAnimation(box, fromHeight);
        if (updateRegion)
        {
            UpdateWindowRegion();
        }
    }

    private double GetMinimumBoxWidth(DesktopBox box) =>
        DesktopItemLayoutEngine.GetMinimumBoxWidth(
            box.ViewMode,
            box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(_runtime.State.Settings.Appearance.IconHorizontalSpacing, box.Appearance.IconSize));

    private void InvalidateBoxVisualArea(Guid? boxId)
    {
        if (boxId is not { } id || DesktopBoxes.FirstOrDefault(box => box.Id == id) is not { } box)
        {
            return;
        }
        InvalidateDip(new RectangleF(
            (float)box.Bounds.X,
            (float)box.Bounds.Y,
            (float)box.Bounds.Width,
            (float)Math.Max(box.Bounds.Height, box.Appearance.TitleBarHeight)));
    }

    private void ClearAutoExpandHover()
    {
        var autoExpandBoxId = _hoveredAutoExpandBoxId;
        var searchBoxId = _hoveredSearchBoxId;
        var menuBoxId = _hoveredMenuBoxId;
        if (autoExpandBoxId is null && searchBoxId is null && menuBoxId is null)
        {
            return;
        }
        _hoveredSearchBoxId = null;
        _hoveredAutoExpandBoxId = null;
        _hoveredMenuBoxId = null;
        _headerToolTip.SetToolTip(this, null);
        RequestHeaderActionVisualUpdate();
    }

    private void BeginTitleEdit(DesktopBox box)
    {
        CloseBoxSearch(clearFilter: true);
        if (_editingBox is not null)
        {
            FinishTitleEdit(true);
        }
        RebuildGeometry();
        var geometry = _boxes.FirstOrDefault(candidate => candidate.Box.Id == box.Id);
        if (geometry is null)
        {
            return;
        }

        _editingBox = box;
        // GDI+ scales the drawn title with the surface transform. WinForms
        // already resolves point fonts against the monitor DPI, so applying
        // _scale here as well makes the editor text render at a different
        // size and baseline from the title it replaces.
        _titleEditorFont?.Dispose();
        _titleEditorFont = CreateFont(
            box.Appearance.TitleFontFamily,
            (float)box.Appearance.TitleFontSize,
            box.Appearance.TitleFontBold ? FontStyle.Bold : FontStyle.Regular,
            GraphicsUnit.Point);
        _titleEditor.FontFamily = new WpfMedia.FontFamily(
            ResolveTitleEditorFontFamily(box.Appearance.TitleFontFamily, box.Title));
        _titleEditor.FontSize = box.Appearance.TitleFontSize * 96d / 72d;
        _titleEditor.FontWeight = box.Appearance.TitleFontBold
            ? Wpf.FontWeights.Bold
            : Wpf.FontWeights.Regular;
        EnsureTitleEditorHandle();
        // Editing controls deliberately sit outside the box material: a box's
        // background color and opacity must never wash into typed text or its
        // selection state.
        var boxBackground = ParseOpaqueColor(box.Appearance.Background);
        _titleEditor.Background = CreateOpaqueWpfBrush(GetOpaqueTitleEditorBackColor(boxBackground));
        _titleEditor.Foreground = CreateOpaqueWpfBrush(ResolveTitleColor(box.Appearance.TitleColor, boxBackground));
        _titleEditor.Text = box.Title;
        _titleEditor.TextAlignment = Wpf.TextAlignment.Center;
        ShowTitleEditor(geometry);
        Invalidate();
    }

    private void OnTitleEditorTextChanged(object? sender, WpfControls.TextChangedEventArgs eventArgs)
    {
        if (_editingBox is null || _titleEditorFont is null)
        {
            return;
        }

        var geometry = _boxes.FirstOrDefault(candidate => candidate.Box.Id == _editingBox.Id);
        if (geometry is not null)
        {
            LayoutTitleEditor(geometry);
        }
    }

    private void LayoutTitleEditor(BoxGeometry geometry)
    {
        if (_titleEditorFont is null)
        {
            return;
        }

        var titleBounds = CalculateTitleTextBounds(geometry.Header, centered: true);
        var left = ToPixel(titleBounds.X);
        var availableWidth = Math.Max(ToPixel(48), ToPixel(titleBounds.Width));
        var minimumWidth = Math.Min(ToPixel(40), availableWidth);
        var text = string.IsNullOrEmpty(_titleEditor.Text) ? "M" : _titleEditor.Text;
        var measuredWidth = Forms.TextRenderer.MeasureText(
            text,
            _titleEditorFont,
            Size.Empty,
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.SingleLine).Width + ToPixel(8);
        var editorWidth = Math.Clamp(measuredWidth, minimumWidth, availableWidth);
        left += (availableWidth - editorWidth) / 2;

        var editorHeight = Math.Min(
            Math.Max(20, ToPixel(geometry.Header.Height) - 10),
            Math.Max(22, _titleEditorFont.Height + 4));
        var clientBounds = new Rectangle(
            left,
            ToPixel(geometry.Header.Y + geometry.Header.Height / 2) - editorHeight / 2,
            editorWidth,
            editorHeight);
        var screenLocation = PointToScreen(clientBounds.Location);
        _titleEditorWindow.Bounds = new Rectangle(screenLocation, clientBounds.Size);
    }

    private static Color GetOpaqueTitleEditorBackColor(Color boxBackground) => boxBackground;

    private static WpfMedia.SolidColorBrush CreateOpaqueWpfBrush(Color color)
    {
        var brush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static string ResolveTitleEditorFontFamily(string? configuredFamily, string title)
    {
        // GDI+ resolves Chinese glyphs in Segoe UI through Microsoft YaHei.
        // WPF otherwise picks the UI fallback, whose metrics and strokes differ
        // visibly from the title that the editor replaces.
        if (string.Equals(configuredFamily, "Segoe UI", StringComparison.OrdinalIgnoreCase) &&
            title.Any(character => character is >= '\u3400' and <= '\u9FFF'))
        {
            return "Microsoft YaHei";
        }

        return string.IsNullOrWhiteSpace(configuredFamily) ? "Segoe UI" : configuredFamily;
    }

    private void ResetTitleEditorHighlight()
    {
        // Start with a caret at the end, matching the desktop rename field.
        _titleEditor.Select(_titleEditor.Text.Length, 0);
    }

    private void EnsureTitleEditorHandle()
    {
        if (!_titleEditorWindow.IsDisposed && !_titleEditorWindow.IsHandleCreated)
        {
            _titleEditorWindow.CreateControl();
        }
    }

    private void ShowTitleEditor(BoxGeometry geometry)
    {
        if (_titleEditorWindow.IsDisposed)
        {
            return;
        }

        // Creating the handle before assigning the first bounds prevents
        // WinForms from reinterpreting our monitor-pixel bounds during the
        // initial per-monitor DPI negotiation.
        EnsureTitleEditorHandle();
        LayoutTitleEditor(geometry);
        _titleEditorWindow.Show();
        LayoutTitleEditor(geometry);
        _titleEditorWindow.Activate();
        _titleEditorHost.Focus();
        _titleEditor.Focus();
        DiagnosticLog.Info(
            $"Title editor shown bounds={_titleEditorWindow.Bounds} " +
            $"editorFocused={_titleEditor.IsKeyboardFocused}");

        // ElementHost can finish its first WPF measure pass after Show().
        // Reapply the bounds on the UI queue so the first edit has the same
        // compact geometry as every subsequent edit.
        if (IsHandleCreated)
        {
            var editingBoxId = geometry.Box.Id;
            BeginInvoke((Action)(() =>
            {
                if (_resourcesDisposed || _editingBox?.Id != editingBoxId || !_titleEditorWindow.Visible)
                {
                    return;
                }

                var currentGeometry = _boxes.FirstOrDefault(candidate => candidate.Box.Id == editingBoxId);
                if (currentGeometry is not null)
                {
                    LayoutTitleEditor(currentGeometry);
                }
            }));
        }
    }

    private void OnTitleEditorWindowDeactivate(object? sender, EventArgs eventArgs)
    {
        if (_resourcesDisposed || _editingBox is null || !_titleEditorWindow.Visible)
        {
            return;
        }

        FinishTitleEdit(true);
    }

    private void OnTitleEditorKeyDown(object? sender, WpfInput.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == WpfInput.Key.Enter)
        {
            eventArgs.Handled = true;
            FinishTitleEdit(true);
        }
        else if (eventArgs.Key == WpfInput.Key.Escape)
        {
            eventArgs.Handled = true;
            FinishTitleEdit(false);
        }
    }

    private void FinishTitleEdit(bool commit)
    {
        if (_editingBox is not { } box)
        {
            return;
        }
        var title = _titleEditor.Text.Trim();
        _editingBox = null;
        _titleEditorWindow.Hide();
        if (commit && title.Length > 0 && !string.Equals(title, box.Title, StringComparison.Ordinal))
        {
            box.Title = title;
            _runtime.BoxChanged(box, true);
        }
        else
        {
            Invalidate();
        }
    }

    private void ToggleBoxDisplayMode(DesktopBox box) =>
        SetBoxDisplayMode(box, !box.ExpandOnHover);

    private void SetBoxDisplayMode(DesktopBox box, bool expandOnHover)
    {
        CloseBoxSearch(clearFilter: true);
        FinishTitleEdit(true);
        if (box.ExpandOnHover == expandOnHover && box.IsCollapsed == expandOnHover)
        {
            return;
        }

        var fromHeight = GetVisualBoxHeight(box);
        var previouslyExpandedBoxIds = _hoverExpandedBoxes.ToArray();
        _hoverExpansion.Reset();
        foreach (var expandedBoxId in previouslyExpandedBoxIds.Where(id => id != box.Id))
        {
            CollapseHoverExpandedBox(expandedBoxId, updateRegion: false);
        }
        _hoverExpandedBoxes.Remove(box.Id);

        box.ExpandOnHover = expandOnHover;
        // IsCollapsed is retained in the persisted shape for compatibility,
        // but it is derived from the display mode rather than user-controlled.
        box.IsCollapsed = expandOnHover;
        _geometryDirty = true;
        StartBoxHeightAnimation(box, fromHeight);
        UpdateWindowRegion();
        _runtime.BoxChanged(box);

        foreach (var boxId in previouslyExpandedBoxIds)
        {
            InvalidateBoxVisualArea(boxId);
        }
        InvalidateBoxVisualArea(box.Id);
    }

    private void PrepareBoxTransform(DesktopBox box)
    {
        CloseBoxSearch(clearFilter: true);
        ReleaseMovingBoxVisualCache();
        ReleaseHeightAnimationVisualCache(box.Id);
        _transformDirtyBounds = ToVisualBounds(box, box.Bounds);
        _heightAnimations.Remove(box.Id);
        _geometryDirty = true;
        _animationFrameClock.StopWhenIdle(_heightAnimations.Count > 0 || IsScrollAnimationActive);
    }

    private void StartBoxHeightAnimation(DesktopBox box, double fromHeight)
    {
        var targetHeight = GetSettledBoxHeight(box);
        if (!_runtime.State.Settings.Appearance.AnimationEnabled || Math.Abs(targetHeight - fromHeight) < 0.5)
        {
            _heightAnimations.Remove(box.Id);
            ReleaseHeightAnimationVisualCache(box.Id);
            return;
        }
        EnsureHeightAnimationVisualCache(box);
        _heightAnimations[box.Id] = new BoxHeightAnimation(
            fromHeight,
            targetHeight,
            Stopwatch.GetTimestamp(),
            CalculateBoxHeightAnimationDuration(
                Math.Abs(targetHeight - fromHeight),
                Math.Abs(box.Bounds.Height - box.Appearance.TitleBarHeight)));
        _dynamicVisualVersion++;
        _animationFrameClock.RequestFrames();
    }

    private double GetSettledBoxHeight(DesktopBox box) =>
        IsEffectivelyCollapsed(box)
            ? box.Appearance.TitleBarHeight
            : box.Bounds.Height;

    private double GetVisualBoxHeight(DesktopBox box)
    {
        var targetHeight = GetSettledBoxHeight(box);
        if (!_runtime.State.Settings.Appearance.AnimationEnabled)
        {
            _heightAnimations.Remove(box.Id);
            return targetHeight;
        }
        if (!_heightAnimations.TryGetValue(box.Id, out var animation))
        {
            return targetHeight;
        }
        if (Math.Abs(animation.ToHeight - targetHeight) > 0.5)
        {
            _heightAnimations.Remove(box.Id);
            ReleaseHeightAnimationVisualCache(box.Id);
            return targetHeight;
        }
        var progress = Stopwatch.GetElapsedTime(animation.StartedTimestamp).TotalMilliseconds /
            animation.Duration.TotalMilliseconds;
        return progress >= 1
            ? animation.ToHeight
            : AnimationMath.Interpolate(animation.FromHeight, animation.ToHeight, progress);
    }

    private double GetInteractionBoxHeight(DesktopBox box) =>
        _heightAnimations.TryGetValue(box.Id, out var animation)
            ? Math.Max(animation.FromHeight, animation.ToHeight)
            : GetVisualBoxHeight(box);

    private void OnAnimationFrame(object? sender, EventArgs eventArgs)
    {
        var hadScrollAnimation = _scrollAnimationKey is not null;
        var scrollCompleted = AdvanceScrollAnimation(requestRender: false);
        var animatedBoxIds = _animationFrameBoxIds;
        var completedBoxIds = _animationFrameCompletedBoxIds;
        animatedBoxIds.Clear();
        completedBoxIds.Clear();
        foreach (var pair in _heightAnimations)
        {
            animatedBoxIds.Add(pair.Key);
            if (Stopwatch.GetElapsedTime(pair.Value.StartedTimestamp) >= pair.Value.Duration)
            {
                completedBoxIds.Add(pair.Key);
            }
        }
        RectangleF? completedAnimationBounds = null;
        foreach (var id in completedBoxIds)
        {
            _heightAnimations.Remove(id);
            var box = DesktopBoxes.FirstOrDefault(candidate => candidate.Id == id);
            if (box is null ||
                !ShouldRetainHeightAnimationVisualCache(IsEffectivelyCollapsed(box)))
            {
                ReleaseHeightAnimationVisualCache(id);
            }
            if (box is null)
            {
                continue;
            }
            var settledBounds = new RectangleF(
                (float)box.Bounds.X,
                (float)box.Bounds.Y,
                (float)box.Bounds.Width,
                (float)box.Bounds.Height);
            completedAnimationBounds = completedAnimationBounds is { } union
                ? RectangleF.Union(union, settledBounds)
                : settledBounds;
        }
        var otherDynamicVisualActive =
            IsTransformActive || _dragStarted || _dropPreview is not null || _selectionBox is not null;
        var animationStillActive = _heightAnimations.Count > 0 || IsScrollAnimationActive;
        var commitCompletionPartially = ShouldCommitCompletedHeightAnimationPartially(
            _isCompositedByIconSurface,
            completedBoxIds.Count > 0,
            animationStillActive,
            otherDynamicVisualActive,
            _iconLayerPartialRenderRequest is not null);
        if (completedBoxIds.Count > 0 && !commitCompletionPartially)
        {
            // The shared icon layer caches a settled base that excludes every
            // animated box. When one of multiple overlapping height animations
            // finishes, rebuild that base so the completed box is immediately
            // restored while the remaining box continues animating.
            _dynamicVisualVersion++;
        }
        if (completedBoxIds.Count > 0 && !animationStillActive)
        {
            _pendingHeightAnimationFrameDirtyBounds = null;
        }
        _animationFrameClock.StopWhenIdle(_heightAnimations.Count > 0 || IsScrollAnimationActive);
        if (ShouldRebuildHeightAnimationGeometry(
                _isCompositedByIconSurface,
                completedBoxIds.Count > 0))
        {
            _geometryDirty = true;
        }
        else
        {
            UpdateHeightAnimationGeometry(animatedBoxIds);
        }
        if (!_isCompositedByIconSurface)
        {
            UpdateWindowRegion();
            RequestLayerRender();
        }
        else if (completedBoxIds.Count > 0)
        {
            UpdateWindowRegion();
            if (commitCompletionPartially && completedAnimationBounds is { } dirtyBounds)
            {
                _iconLayerPartialRenderRequest!(dirtyBounds);
            }
            else
            {
                RequestLayerRender();
            }
        }
        else if (animatedBoxIds.Count > 0 || hadScrollAnimation)
        {
            RequestVisualLayerRender();
        }
        if (completedBoxIds.Count > 0 || scrollCompleted)
        {
            RequestHeaderActionVisualUpdate();
        }
    }

}

