using System.Collections.Specialized;
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

    private void PaintRegularSurface(Graphics graphics)
    {
        if (IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        RebuildGeometry();
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.TextContrast = 4;
        graphics.ScaleTransform((float)_scale, (float)_scale);
        var clipBounds = new RectangleF(
            0,
            0,
            (float)(ClientSize.Width / _scale),
            (float)(ClientSize.Height / _scale));
        clipBounds.Inflate(8, 8);
        foreach (var box in _boxes.Where(box => box.Bounds.IntersectsWith(clipBounds)))
        {
            DrawBox(graphics, box, clipBounds);
        }
        graphics.ResetTransform();
    }

    private void PrepareMovingBoxVisualCache(DesktopBox box)
    {
        if (!_isCompositedByIconSurface || IsDisposed || _resourcesDisposed)
        {
            return;
        }

        EnsureGeometry();
        var geometry = _boxes.FirstOrDefault(candidate => candidate.Box.Id == box.Id);
        if (geometry is not null)
        {
            CreateMovingBoxVisualCache(GetTransformGeometry(geometry), box);
        }
    }

    private bool DrawMovingBoxVisualCache(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds)
    {
        if (_movingBoxVisualCache is null || _movingBoxVisualCacheBoxId != geometry.Box.Id)
        {
            CreateMovingBoxVisualCache(geometry, geometry.Box);
        }
        if (_movingBoxVisualCache is null || _movingBoxVisualCacheBoxId != geometry.Box.Id)
        {
            return false;
        }

        var destination = _movingBoxVisualCacheBounds;
        destination.Offset(
            (float)(geometry.Box.Bounds.X - _movingBoxVisualCacheAnchor.X),
            (float)(geometry.Box.Bounds.Y - _movingBoxVisualCacheAnchor.Y));
        if (!destination.IntersectsWith(clipBounds))
        {
            return true;
        }

        graphics.DrawImage(
            _movingBoxVisualCache,
            destination,
            new RectangleF(0, 0, _movingBoxVisualCache.Width, _movingBoxVisualCache.Height),
            GraphicsUnit.Pixel);
        return true;
    }

    private void CreateMovingBoxVisualCache(BoxGeometry geometry, DesktopBox box)
    {
        ReleaseMovingBoxVisualCache();
        var cacheBounds = CalculateMovingBoxVisualCacheBounds(geometry.Bounds, _scale);
        var pixelWidth = Math.Max(1, (int)Math.Round(cacheBounds.Width * _scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(cacheBounds.Height * _scale));
        var bitmap = DesktopLayerBitmapFactory.Create(pixelWidth, pixelHeight);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.TextContrast = 4;
            graphics.Transform = new Matrix(
                (float)_scale,
                0,
                0,
                (float)_scale,
                -(float)(cacheBounds.X * _scale),
                -(float)(cacheBounds.Y * _scale));
            graphics.SetClip(cacheBounds, CombineMode.Replace);
            DrawBox(
                graphics,
                geometry,
                cacheBounds,
                includeItemHoverFeedback: false,
                includeCompositedHeaderActions: true);
            graphics.ResetTransform();
        }

        _movingBoxVisualCache = bitmap;
        _movingBoxVisualCacheBoxId = box.Id;
        _movingBoxVisualCacheBounds = cacheBounds;
        _movingBoxVisualCacheAnchor = new PointF((float)box.Bounds.X, (float)box.Bounds.Y);
    }

    private void ReleaseMovingBoxVisualCache()
    {
        _movingBoxVisualCache?.Dispose();
        _movingBoxVisualCache = null;
        _movingBoxVisualCacheBoxId = null;
        _movingBoxVisualCacheBounds = RectangleF.Empty;
        _movingBoxVisualCacheAnchor = PointF.Empty;
    }

    private void PrepareHeightAnimationVisualCache(DesktopBox box)
    {
        if (!_isCompositedByIconSurface || IsDisposed || _resourcesDisposed)
        {
            return;
        }
        if (!AreRequiredBoxIconsLoaded(box))
        {
            _heightAnimationCacheRequestBoxIds.Add(box.Id);
            foreach (var key in GetRequiredBoxIconBitmapKeys(box))
            {
                QueueIconBitmapLoad(key);
            }
            return;
        }

        _heightAnimationCacheRequestBoxIds.Remove(box.Id);
        ReleaseHeightAnimationVisualCache(box.Id);

        var geometry = CreateBoxGeometry(box, (float)box.Bounds.Height, isCollapsed: false);
        var cacheBounds = CalculateMovingBoxVisualCacheBounds(geometry.Bounds, _scale);
        var pixelWidth = Math.Max(1, (int)Math.Round(cacheBounds.Width * _scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(cacheBounds.Height * _scale));
        var bitmap = DesktopLayerBitmapFactory.Create(pixelWidth, pixelHeight);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.TextContrast = 4;
            graphics.Transform = new Matrix(
                (float)_scale,
                0,
                0,
                (float)_scale,
                -(float)(cacheBounds.X * _scale),
                -(float)(cacheBounds.Y * _scale));
            graphics.SetClip(cacheBounds, CombineMode.Replace);
            DrawBox(
                graphics,
                geometry,
                cacheBounds,
                includeDropPreview: true,
                includeSelectionRectangle: false,
                includeItemHoverFeedback: false);
            graphics.ResetTransform();
        }

        _heightAnimationVisualCaches[box.Id] = new BoxHeightVisualCache(bitmap, cacheBounds);
    }

    private void EnsureHeightAnimationVisualCache(DesktopBox box)
    {
        if (!_heightAnimationVisualCaches.ContainsKey(box.Id))
        {
            PrepareHeightAnimationVisualCache(box);
        }
    }

    private bool DrawHeightAnimationVisualCache(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds)
    {
        if (!_heightAnimationVisualCaches.TryGetValue(geometry.Box.Id, out var cache))
        {
            EnsureHeightAnimationVisualCache(geometry.Box);
            if (!_heightAnimationVisualCaches.TryGetValue(geometry.Box.Id, out cache))
            {
                return false;
            }
        }

        if (!geometry.Bounds.IntersectsWith(clipBounds))
        {
            return true;
        }

        // Paint the cache *through* the rounded outline instead of clipping to
        // it. A GDI+ clip region is hard edged whatever the smoothing mode, so
        // the bottom edge the animation moves — the one edge the eye follows —
        // came out aliased on every frame and then snapped to a smooth edge the
        // moment the settled frame landed. Filling with the cache as a brush
        // antialiases that edge at ~0.36 ms per blit (vs ~0.18 ms for clip+blit,
        // well within the 15 ms frame budget) and keeps the moving edge clean.
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRectangle(
            RectangleF.Inflate(geometry.Bounds, -0.5f, -0.5f),
            (float)_runtime.State.Settings.Appearance.CornerRadius);
        using var brush = new TextureBrush(cache.Bitmap, WrapMode.Clamp)
        {
            // The cache holds device pixels with its own origin at
            // cache.Bounds, and CalculateMovingBoxVisualCacheBounds already
            // pinned that origin to a whole device pixel. Undoing the scale here
            // leaves an exact integer pixel translation once the layer's own
            // scale transform is applied, so the blit stays unresampled.
            Transform = new Matrix(
                1f / (float)_scale,
                0,
                0,
                1f / (float)_scale,
                cache.Bounds.X,
                cache.Bounds.Y),
        };
        graphics.FillPath(brush, path);
        graphics.Restore(state);
        return true;
    }

    private void ReleaseHeightAnimationVisualCache(Guid boxId)
    {
        _heightAnimationCacheRequestBoxIds.Remove(boxId);
        if (_heightAnimationVisualCaches.Remove(boxId, out var cache))
        {
            cache.Bitmap.Dispose();
        }
        if (_prewarmedHeightAnimationCacheBoxId == boxId)
        {
            _prewarmedHeightAnimationCacheBoxId = null;
        }
    }

    private void ClearHeightAnimationVisualCaches()
    {
        foreach (var cache in _heightAnimationVisualCaches.Values)
        {
            cache.Bitmap.Dispose();
        }
        _heightAnimationVisualCaches.Clear();
        _heightAnimationCacheRequestBoxIds.Clear();
        _pendingHeightAnimationCachePrewarmBoxId = null;
        _prewarmedHeightAnimationCacheBoxId = null;
    }

    private void DrawDropTargetFeedback(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds)
    {
        var preview = _dropPreview;
        if (preview is null || preview.BoxId != geometry.Box.Id ||
            !geometry.Bounds.IntersectsWith(clipBounds))
        {
            return;
        }

        var accent = ResolveAccentColor(
            ParseOpaqueColor(geometry.Box.Appearance.Background),
            ParseOpaqueColor(geometry.Box.Appearance.Accent));
        var outline = RectangleF.Inflate(geometry.Bounds, -1.5f, -1.5f);
        var alpha = preview.AcceptsDrop ? 220 : 132;
        using var border = new Pen(Color.FromArgb(alpha, accent), preview.AcceptsDrop ? 2 : 1)
        {
            DashStyle = preview.AcceptsDrop ? DashStyle.Solid : DashStyle.Dash
        };
        using var path = RoundedRectangle(
            outline,
            Math.Max(2, (float)_runtime.State.Settings.Appearance.CornerRadius - 1));
        graphics.DrawPath(border, path);

        if (preview.TargetManualTabIndex is not { } tabIndex ||
            geometry.ManualTabs.Count == 0 || geometry.TabBar.IsEmpty)
        {
            return;
        }

        var tabBounds = RectangleF.Inflate(
            GetBoxTabBounds(geometry, tabIndex, geometry.ManualTabs.Count),
            -3,
            -3);
        using var tabFill = new SolidBrush(Color.FromArgb(preview.AcceptsDrop ? 44 : 26, accent));
        using var tabBorder = new Pen(Color.FromArgb(preview.AcceptsDrop ? 210 : 118, accent), 1);
        using var tabPath = RoundedRectangle(tabBounds, 3);
        graphics.FillPath(tabFill, tabPath);
        graphics.DrawPath(tabBorder, tabPath);
    }

    private void DrawDropDragFeedback(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds)
    {
        var preview = _dropPreview;
        if (preview is null || preview.BoxId != geometry.Box.Id ||
            !preview.AcceptsDrop || !geometry.Body.IntersectsWith(clipBounds))
        {
            return;
        }

        var accent = ResolveAccentColor(
            ParseOpaqueColor(geometry.Box.Appearance.Background),
            ParseOpaqueColor(geometry.Box.Appearance.Accent));
        // External file drags and desktop-icon drags already carry their own
        // mouse-following ghost, so only box-item drags (which have no shell
        // drag image) draw the shared card here.
        if (preview.FloatingCard)
        {
            DrawBoxItemFloatingPreview(graphics, geometry, preview, accent);
        }
    }

    private void DrawBoxItemFloatingPreview(
        Graphics graphics,
        BoxGeometry geometry,
        DropPreviewState preview,
        Color accent)
    {
        // Normal boxes use a private virtual drag payload, so Explorer does
        // not reliably supply a shell drag image. The box draws the shared
        // ghost card while the pointer is inside a box; the desktop surface
        // takes over with the same card once the pointer leaves it.
        var previewItem = preview.ItemKeys
            .Select(key => _runtime.FindItemByKey(key))
            .FirstOrDefault(item => item is not null);
        // Ask for the box's own grid icon size: the grid caches those sizes
        // synchronously, so the ghost gets the real icon immediately instead
        // of falling back to the placeholder while an odd size loads async.
        var iconSize = Math.Clamp(
            (float)geometry.Box.Appearance.IconSize * 1.05f,
            32f,
            64f);
        var previewIcon = previewItem is null
            ? ShellIconProvider.GetGenericFileIcon()
            : GetIconBitmap(previewItem, iconSize) ?? ShellIconProvider.GetGenericFileIcon();
        using var font = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            9f,
            FontStyle.Regular,
            GraphicsUnit.Point);
        DragGhostRenderer.Draw(
            graphics,
            preview.Pointer,
            previewIcon,
            previewItem?.DisplayName ?? preview.ItemKeys.FirstOrDefault() ?? string.Empty,
            preview.ItemCount,
            font);
    }

    private void DrawBox(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds,
        bool includeDropPreview = true,
        IReadOnlySet<string>? selectedItemKeys = null,
        bool includeSelectionRectangle = true,
        IReadOnlySet<string>? suppressedHoverItemKeys = null,
        bool includeItemHoverFeedback = true,
        bool includeCompositedHeaderActions = false)
    {
        var baseColor = ParseOpaqueColor(geometry.Box.Appearance.Background);
        var opacity = ResolveBoxTintOpacity(geometry.Box.Appearance.Opacity, _usesAcrylicBackground);
        var boxColor = ApplyOpacity(baseColor, opacity);
        var textColor = ResolveAutoTextColor(baseColor);
        var isDarkSurface = UsesLightText(baseColor);
        var paintedBounds = RectangleF.Inflate(geometry.Bounds, -0.5f, -0.5f);
        using var path = RoundedRectangle(
            paintedBounds,
            (float)_runtime.State.Settings.Appearance.CornerRadius);
        using var fill = new SolidBrush(boxColor);
        graphics.FillPath(fill, path);

        using var titleFont = CreateFont(
            geometry.Box.Appearance.TitleFontFamily,
            (float)geometry.Box.Appearance.TitleFontSize,
            geometry.Box.Appearance.TitleFontBold ? FontStyle.Bold : FontStyle.Regular,
            GraphicsUnit.Point);
        using var titleBrush = new SolidBrush(ResolveTitleColor(geometry.Box.Appearance.TitleColor, baseColor));
        using var titleFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        if (_editingBox?.Id != geometry.Box.Id)
        {
            var titleBounds = CalculateTitleTextBounds(geometry.Header, centered: true);
            graphics.DrawString(geometry.Box.Title, titleFont, titleBrush,
                titleBounds,
                titleFormat);
        }
        if (ShouldDrawHeaderActionsOnCurrentLayer(
                _isCompositedByIconSurface,
                _headerActionOverlayUnavailable,
                includeCompositedHeaderActions) &&
            ShouldShowHeaderActions(geometry.Box.Id, _hoveredBoxId, _searchingBoxId))
        {
            var headerAccent = ResolveAccentColor(
                baseColor,
                ParseOpaqueColor(geometry.Box.Appearance.Accent));
            DrawSearchButton(
                graphics,
                geometry.Search,
                _searchingBoxId == geometry.Box.Id,
                _hoveredSearchBoxId == geometry.Box.Id,
                headerAccent,
                textColor,
                isDarkSurface);
            DrawAutoExpandButton(
                graphics,
                geometry.AutoExpand,
                geometry.Box.ExpandOnHover,
                _hoveredAutoExpandBoxId == geometry.Box.Id,
                headerAccent,
                textColor,
                isDarkSurface);
            DrawMenuButton(
                graphics,
                geometry.Menu,
                _hoveredMenuBoxId == geometry.Box.Id,
                textColor,
                isDarkSurface);
        }
        DrawBoxTabs(
            graphics,
            geometry,
            ResolveAccentColor(
                baseColor,
                ParseOpaqueColor(geometry.Box.Appearance.Accent)),
            textColor,
            isDarkSurface);

        if (geometry.IsCollapsed)
        {
            if (includeDropPreview)
            {
                DrawDropTargetFeedback(graphics, geometry, clipBounds);
            }
            return;
        }

        var state = graphics.Save();
        graphics.SetClip(geometry.Body);
        using var itemFont = geometry.Box.Appearance.ShowItemLabels
            ? CreateFont(
                geometry.Box.Appearance.LabelFontFamily,
                (float)geometry.Box.Appearance.LabelFontSize,
                FontStyle.Regular,
                GraphicsUnit.Point)
            : null;
        using var itemBrush = geometry.Box.Appearance.ShowItemLabels
            ? new SolidBrush(textColor)
            : null;
        using var itemFormat = geometry.Box.Appearance.ShowItemLabels
            ? CreateItemTextFormat(geometry.Box.ViewMode)
            : null;
        using var selectedGridItemFormat = geometry.Box.Appearance.ShowItemLabels &&
                                           geometry.Box.ViewMode == BoxViewMode.Grid
            ? CreateSelectedGridItemTextFormat()
            : null;
        var effectiveSelection = selectedItemKeys ?? _selection;
        var visibleItems = GetRenderedItemsForBox(geometry, clipBounds, includeDropPreview)
            // Raised labels are drawn last so the complete selected filename
            // remains readable when it overlaps a neighbouring item.
            .OrderBy(item => IsRaisedVisual(
                item,
                effectiveSelection,
                suppressedHoverItemKeys,
                includeItemHoverFeedback))
            .ToArray();
        foreach (var item in visibleItems)
        {
            DrawItem(
                graphics,
                item,
                itemFont,
                itemBrush,
                itemFormat,
                selectedGridItemFormat,
                geometry.Body,
                effectiveSelection,
                suppressedHoverItemKeys,
                includeItemHoverFeedback);
        }
        if (includeDropPreview)
        {
            DrawDropDragFeedback(graphics, geometry, clipBounds);
        }
        if (!_runtime.AreDesktopItemsHidden && geometry.Box.IsMappedFolder &&
            visibleItems.Length == 0)
        {
            DrawMappedFolderState(graphics, geometry, textColor);
        }
        if (includeSelectionRectangle &&
            _selectionBox?.Id == geometry.Box.Id &&
            !_selectionRectangle.IsEmpty)
        {
            var selectionColor = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            using var selectionFill = new SolidBrush(Color.FromArgb(42, selectionColor));
            using var selectionBorder = new Pen(Color.FromArgb(190, selectionColor), 1)
            {
                DashStyle = DashStyle.Dash
            };
            graphics.FillRectangle(selectionFill, _selectionRectangle);
            graphics.DrawRectangle(
                selectionBorder,
                _selectionRectangle.X,
                _selectionRectangle.Y,
                _selectionRectangle.Width,
                _selectionRectangle.Height);
        }
        graphics.Restore(state);

        DrawVerticalScrollBar(
            graphics,
            geometry,
            ResolveAccentColor(
                baseColor,
                ParseOpaqueColor(geometry.Box.Appearance.Accent)),
            textColor);

        if (includeDropPreview)
        {
            DrawDropTargetFeedback(graphics, geometry, clipBounds);
        }

        if (_runtime.State.Settings.Appearance.ShowResizeGrip)
        {
            using var grip = new Pen(Color.FromArgb(130, textColor), 1);
            graphics.DrawLine(grip, geometry.Resize.Right - 10, geometry.Resize.Bottom - 3, geometry.Resize.Right - 3, geometry.Resize.Bottom - 10);
            graphics.DrawLine(grip, geometry.Resize.Right - 6, geometry.Resize.Bottom - 3, geometry.Resize.Right - 3, geometry.Resize.Bottom - 6);
        }
    }

    private void DrawMarqueeSelectionOverlay(
        Graphics graphics,
        BoxGeometry geometry,
        RectangleF clipBounds)
    {
        if (_selectionRectangle.IsEmpty && _marqueeSelectionItems.Count == 0)
        {
            return;
        }

        var state = graphics.Save();
        graphics.SetClip(geometry.Body, CombineMode.Intersect);
        var textColor = ResolveAutoTextColor(ParseOpaqueColor(geometry.Box.Appearance.Background));
        using var itemFont = geometry.Box.Appearance.ShowItemLabels
            ? CreateFont(
                geometry.Box.Appearance.LabelFontFamily,
                (float)geometry.Box.Appearance.LabelFontSize,
                FontStyle.Regular,
                GraphicsUnit.Point)
            : null;
        using var itemBrush = geometry.Box.Appearance.ShowItemLabels
            ? new SolidBrush(textColor)
            : null;
        using var itemFormat = geometry.Box.Appearance.ShowItemLabels
            ? CreateItemTextFormat(geometry.Box.ViewMode)
            : null;
        using var selectedGridItemFormat = geometry.Box.Appearance.ShowItemLabels &&
                                           geometry.Box.ViewMode == BoxViewMode.Grid
            ? CreateSelectedGridItemTextFormat()
            : null;

        foreach (var item in _marqueeSelectionItems
                     .Where(item => item.Box.Id == geometry.Box.Id &&
                                    GetMarqueeItemOverlayBounds(item, geometry.Body).IntersectsWith(clipBounds))
                     .OrderBy(item => IsRaisedVisual(item, _selection, includeItemHoverFeedback: false)))
        {
            DrawItem(
                graphics,
                item,
                itemFont,
                itemBrush,
                itemFormat,
                selectedGridItemFormat,
                geometry.Body,
                _selection,
                includeItemHoverFeedback: false);
        }

        if (!_selectionRectangle.IsEmpty)
        {
            var selectionColor = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            using var selectionFill = new SolidBrush(Color.FromArgb(42, selectionColor));
            using var selectionBorder = new Pen(Color.FromArgb(190, selectionColor), 1)
            {
                DashStyle = DashStyle.Dash
            };
            graphics.FillRectangle(selectionFill, _selectionRectangle);
            graphics.DrawRectangle(
                selectionBorder,
                _selectionRectangle.X,
                _selectionRectangle.Y,
                _selectionRectangle.Width,
                _selectionRectangle.Height);
        }

        graphics.Restore(state);
    }

    private RectangleF? GetMarqueeSelectionOverlayBounds(BoxGeometry geometry)
    {
        RectangleF? bounds = null;
        if (!_selectionRectangle.IsEmpty)
        {
            bounds = RectangleF.Inflate(_selectionRectangle, 4, 4);
        }

        foreach (var item in _marqueeSelectionItems)
        {
            if (item.Box.Id != geometry.Box.Id)
            {
                continue;
            }

            var itemBounds = GetMarqueeItemOverlayBounds(item, geometry.Body);
            bounds = bounds is { } existing
                ? RectangleF.Union(existing, itemBounds)
                : itemBounds;
        }

        return bounds;
    }

    private static RectangleF GetMarqueeItemOverlayBounds(
        ItemGeometry item,
        RectangleF contentBounds)
    {
        var bounds = RectangleF.Inflate(item.Bounds, 4, 4);
        if (item.Box.ViewMode != BoxViewMode.Grid || !item.Box.Appearance.ShowItemLabels)
        {
            return bounds;
        }

        // A selected grid label can grow to the bottom of the box. Include
        // that possible visual extent without measuring text on every move.
        var labelBounds = new RectangleF(
            item.Bounds.X - 4,
            item.Bounds.Y - 4,
            item.Bounds.Width + 8,
            Math.Max(item.Bounds.Height + 8, contentBounds.Bottom - item.Bounds.Y + 4));
        return RectangleF.Union(bounds, labelBounds);
    }

    private IReadOnlyList<ItemGeometry> GetRenderedItemsForBox(
        BoxGeometry geometry,
        RectangleF clipBounds,
        bool includeDropPreview = true)
    {
        if (_runtime.AreDesktopItemsHidden)
        {
            return [];
        }

        // During a box marquee the geometry built for hit testing already is
        // the visible layout. Reusing it avoids recalculating the layout,
        // tab projection and scroll state for every overlay frame.
        if (!includeDropPreview && !_dragStarted)
        {
            return _items
                .Where(item => item.Box.Id == geometry.Box.Id &&
                               item.Bounds.IntersectsWith(clipBounds))
                .ToArray();
        }

        var hiddenKeySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_dragStarted && _pressedBoxId == geometry.Box.Id)
        {
            hiddenKeySet.UnionWith(
                GetCachedItemsForBox(geometry.Box.Id)
                    .Where(item => _selection.Contains(item.Key.ToString()))
                    .Select(item => item.Key.ToString()));
        }
        IReadOnlySet<string> hiddenKeys = hiddenKeySet;
        var layoutItems = GetVisibleItemsForBox(geometry);

        var appearance = _runtime.State.Settings.Appearance;
        var layout = DesktopItemLayoutEngine.CalculateVisible(
            geometry.Box.ViewMode,
            new LayoutRect(geometry.Body.X, geometry.Body.Y, geometry.Body.Width, geometry.Body.Height),
            layoutItems.Count,
            geometry.Box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconHorizontalSpacing, geometry.Box.Appearance.IconSize),
            DesktopItemLayoutEngine.ScaleIconSpacing(appearance.IconVerticalSpacing, geometry.Box.Appearance.IconSize),
            _scrollOffsets.GetValueOrDefault(GetItemViewKey(geometry)));
        return layout.Items
            .Select(entry =>
            {
                var bounds = entry.Bounds;
                return new ItemGeometry(
                    geometry.Box,
                    layoutItems[entry.Index],
                    new RectangleF(
                        (float)bounds.X,
                        (float)bounds.Y,
                        (float)bounds.Width,
                        (float)bounds.Height));
            })
            .Where(item => !hiddenKeys.Contains(item.Item.Key.ToString()))
            .Where(item => item.Bounds.IntersectsWith(clipBounds))
            .ToArray();
    }

    private static void DrawBoxTabs(
        Graphics graphics,
        BoxGeometry geometry,
        Color accent,
        Color textColor,
        bool isDarkSurface)
    {
        var tabCount = geometry.ManualTabs.Count > 0
            ? geometry.ManualTabs.Count
            : geometry.CategoryTabs.Count;
        if (tabCount == 0 || geometry.TabBar.IsEmpty)
        {
            return;
        }

        using var divider = new Pen(Color.FromArgb(isDarkSurface ? 64 : 54, textColor), 1);
        graphics.DrawLine(divider, geometry.TabBar.Left, geometry.TabBar.Bottom - 1, geometry.TabBar.Right, geometry.TabBar.Bottom - 1);
        var tabFontSize = Math.Clamp(
            (float)geometry.Box.Appearance.LabelFontSize + 0.5f,
            9.5f,
            11f);
        using var activeFont = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            tabFontSize,
            FontStyle.Bold,
            GraphicsUnit.Point);
        using var inactiveFont = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            tabFontSize,
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        for (var index = 0; index < tabCount; index++)
        {
            var (label, active) = geometry.ManualTabs.Count > 0
                ? (
                    geometry.ManualTabs[index].Label,
                    geometry.ManualTabs[index].Id == geometry.ActiveManualTabId)
                : (
                    geometry.CategoryTabs[index].Label,
                    geometry.CategoryTabs[index].Category == geometry.ActiveMappedFolderCategory);
            var bounds = GetBoxTabBounds(geometry, index, tabCount);
            using var labelBrush = new SolidBrush(active
                ? accent
                : Color.FromArgb(isDarkSurface ? 196 : 184, textColor));
            graphics.DrawString(
                label,
                active ? activeFont : inactiveFont,
                labelBrush,
                RectangleF.Inflate(bounds, -2, 0),
                format);
            if (active)
            {
                using var underline = new Pen(accent, 2);
                graphics.DrawLine(
                    underline,
                    bounds.Left + 7,
                    geometry.TabBar.Bottom - 1,
                    bounds.Right - 7,
                    geometry.TabBar.Bottom - 1);
            }
        }
    }

    private void DrawMappedFolderState(Graphics graphics, BoxGeometry geometry, Color textColor)
    {
        var snapshot = _runtime.GetMappedFolderSnapshot(geometry.Box.Id);
        var message = snapshot?.Availability switch
        {
            MappedFolderAvailability.Available => "此文件夹为空",
            MappedFolderAvailability.Missing => "文件夹不存在",
            MappedFolderAvailability.Offline => "磁盘或网络位置不可用",
            MappedFolderAvailability.AccessDenied => "没有访问此文件夹的权限",
            MappedFolderAvailability.Error => snapshot.Message ?? "无法读取此文件夹",
            _ => "正在读取文件夹"
        };
        using var font = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            Math.Clamp((float)geometry.Box.Appearance.LabelFontSize + 0.5f, 9.5f, 12f),
            FontStyle.Regular,
            GraphicsUnit.Point);
        using var brush = new SolidBrush(Color.FromArgb(210, textColor));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        graphics.DrawString(message, font, brush, geometry.Body, format);
    }

    private void DrawItem(
        Graphics graphics,
        ItemGeometry item,
        Font? labelFont,
        Brush? labelBrush,
        StringFormat? labelFormat,
        StringFormat? selectedGridItemFormat,
        RectangleF contentBounds,
        IReadOnlySet<string>? selectedItemKeys = null,
        IReadOnlySet<string>? suppressedHoverItemKeys = null,
        bool includeItemHoverFeedback = true)
    {
        var itemKey = item.Item.Key.ToString();
        var isRenaming = _renamingBoxId == item.Box.Id &&
                         string.Equals(_renamingItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
        var isSelected = (selectedItemKeys ?? _selection).Contains(itemKey);
        var isHovered =
            includeItemHoverFeedback &&
            _runtime.State.Settings.Appearance.HoverFeedback &&
            !(suppressedHoverItemKeys?.Contains(itemKey) ?? false) &&
            string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase);
        var iconSize = (float)item.Box.Appearance.IconSize;
        var iconBounds = GetItemIconBounds(item);
        var isFolderDropTarget =
            item.Item.Kind == DesktopItemKind.Folder &&
            !string.IsNullOrEmpty(_folderDropTargetName) &&
            string.Equals(_folderDropTargetName, item.Item.DisplayName, StringComparison.Ordinal);
        var showsFullLabel = DesktopIconLabelDisplayPolicy.ShowsFullLabel(
            isSelected || isFolderDropTarget,
            isHovered);
        var textBounds = item.Box.Appearance.ShowItemLabels
            ? GetItemTextBounds(graphics, item, iconBounds, labelFont!, showsFullLabel, contentBounds)
            : RectangleF.Empty;
        var visualBounds = textBounds.IsEmpty
            ? item.Bounds
            : RectangleF.Union(item.Bounds, textBounds);
        var visualKey = (item.Box.Id, itemKey);
        if (isSelected || isHovered || isFolderDropTarget)
        {
            _expandedItemHitBounds[visualKey] = RectangleF.Intersect(visualBounds, contentBounds);
        }
        else
        {
            _expandedItemHitBounds.Remove(visualKey);
        }
        var cornerRadius = DesktopItemVisualStyle.SelectionCornerRadius(iconSize);
        if (isFolderDropTarget)
        {
            // The drop target folder inside a mapped box is highlighted with
            // the selection fill plus an accent border so the destination is
            // unambiguous while an item hovers over it.
            var configuredSelection = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            using var folderFill = new SolidBrush(Color.FromArgb(DesktopItemVisualStyle.SelectedFillAlpha, configuredSelection));
            using var folderBorder = new Pen(Color.FromArgb(DesktopItemVisualStyle.HoverBorderAlpha, configuredSelection), 1.5f);
            using var folderPath = RoundedRectangle(RectangleF.Inflate(visualBounds, -2, -2), cornerRadius);
            graphics.FillPath(folderFill, folderPath);
            graphics.DrawPath(folderBorder, folderPath);
        }
        else if (isHovered)
        {
            // Hover remains visible while the item is selected and is kept
            // brighter than the settled selection treatment.
            var configuredSelection = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            var hoverColor = DesktopItemVisualStyle.Brighten(configuredSelection);
            using var hovered = new SolidBrush(Color.FromArgb(DesktopItemVisualStyle.HoverFillAlpha, hoverColor));
            using var hoverBorder = new Pen(Color.FromArgb(DesktopItemVisualStyle.HoverBorderAlpha, hoverColor), 1);
            using var hoveredPath = RoundedRectangle(RectangleF.Inflate(visualBounds, -2, -2), cornerRadius);
            graphics.FillPath(hovered, hoveredPath);
            graphics.DrawPath(hoverBorder, hoveredPath);
        }
        else if (isSelected)
        {
            var configuredSelection = ParseOpaqueColor(_runtime.State.Settings.Appearance.SelectionColor);
            using var selected = new SolidBrush(Color.FromArgb(DesktopItemVisualStyle.SelectedFillAlpha, configuredSelection));
            using var selectedPath = RoundedRectangle(RectangleF.Inflate(visualBounds, -2, -2), cornerRadius);
            graphics.FillPath(selected, selectedPath);
        }

        var bitmap = GetIconBitmap(item.Item, iconSize) ?? ShellIconProvider.GetGenericFileIcon();
        if (bitmap is not null)
        {
            var imageBounds = IconImageLayout.Contain(bitmap, iconBounds);
            if (!imageBounds.IsEmpty)
            {
                graphics.DrawImage(bitmap, imageBounds);
            }
        }
        if (!item.Box.Appearance.ShowItemLabels)
        {
            return;
        }
        if (isRenaming)
        {
            return;
        }
        graphics.DrawString(
            item.Item.DisplayName,
            labelFont!,
            labelBrush!,
            textBounds,
            showsFullLabel && item.Box.ViewMode == BoxViewMode.Grid
                ? selectedGridItemFormat!
                : labelFormat!);
    }

    private bool IsRaisedVisual(
        ItemGeometry item,
        IReadOnlySet<string>? selectedItemKeys = null,
        IReadOnlySet<string>? suppressedHoverItemKeys = null,
        bool includeItemHoverFeedback = true)
    {
        var itemKey = item.Item.Key.ToString();
        return (selectedItemKeys ?? _selection).Contains(itemKey) ||
            (includeItemHoverFeedback &&
             _runtime.State.Settings.Appearance.HoverFeedback &&
             !(suppressedHoverItemKeys?.Contains(itemKey) ?? false) &&
             string.Equals(_hoveredItemKey, itemKey, StringComparison.OrdinalIgnoreCase));
    }

    private static RectangleF GetItemIconBounds(ItemGeometry item)
    {
        var iconSize = (float)item.Box.Appearance.IconSize;
        return item.Box.ViewMode == BoxViewMode.List
            ? new RectangleF(
                item.Bounds.X + 8,
                item.Bounds.Y + (item.Bounds.Height - iconSize) / 2,
                iconSize,
                iconSize)
            : new RectangleF(
                item.Bounds.X + (item.Bounds.Width - iconSize) / 2,
                item.Bounds.Y + 5,
                iconSize,
                iconSize);
    }

    private static RectangleF GetItemTextBounds(
        Graphics graphics,
        ItemGeometry item,
        RectangleF iconBounds,
        Font labelFont,
        bool isSelected,
        RectangleF contentBounds)
    {
        if (item.Box.ViewMode == BoxViewMode.List)
        {
            return new RectangleF(
                iconBounds.Right + 10,
                item.Bounds.Y,
                Math.Max(0, item.Bounds.Right - iconBounds.Right - 18),
                item.Bounds.Height);
        }

        var textTop = iconBounds.Bottom + 3;
        var textWidth = Math.Max(0, item.Bounds.Width - 4);
        var compactHeight = Math.Max(
            0,
            Math.Min(
                item.Bounds.Bottom - textTop - 3,
                labelFont.GetHeight(graphics) * CompactGridLabelLineCount + 2));
        var availableHeight = Math.Max(0, contentBounds.Bottom - textTop - 3);
        var textHeight = isSelected
            // Keep the full measured layout even when it extends below the
            // box body. The body's graphics clip hides the overflow without
            // forcing an ellipsis or shrinking the selected label.
            ? ResolveSelectedGridLabelHeight(
                MeasureFullGridLabelHeight(graphics, item.Item.DisplayName, labelFont, textWidth),
                availableHeight)
            : compactHeight;
        return new RectangleF(
            item.Bounds.X + 2,
            textTop,
            textWidth,
            textHeight);
    }

    private static float MeasureFullGridLabelHeight(
        Graphics graphics,
        string displayName,
        Font labelFont,
        float width)
    {
        if (width <= 0)
        {
            return 0;
        }

        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None
        };
        // Measure the whole filename first; the caller then caps it at the
        // visible bottom edge of the box rather than at an arbitrary line count.
        return graphics.MeasureString(displayName, labelFont, new SizeF(width, 100_000), format).Height + 2;
    }

    internal static float ResolveSelectedGridLabelHeight(
        float measuredHeight,
        float availableHeight) =>
        Math.Max(0, measuredHeight);

    private static StringFormat CreateItemTextFormat(BoxViewMode viewMode) => new()
    {
        Alignment = viewMode == BoxViewMode.List ? StringAlignment.Near : StringAlignment.Center,
        LineAlignment = viewMode == BoxViewMode.List ? StringAlignment.Center : StringAlignment.Near,
        Trimming = StringTrimming.EllipsisCharacter,
        // An idle grid item gets exactly two complete label lines. Any
        // remaining filename text is represented by the standard ellipsis.
        FormatFlags = viewMode == BoxViewMode.List
            ? StringFormatFlags.NoWrap
            : StringFormatFlags.LineLimit
    };

    private static StringFormat CreateSelectedGridItemTextFormat() => new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Near,
        Trimming = StringTrimming.None,
        // Selection removes the fixed line limit and vertical truncation. The
        // box body clip, rather than the label rectangle, decides what remains
        // visible when the name extends below the box.
        FormatFlags = StringFormatFlags.LineLimit
    };

    private void DrawItemHoverOverlay(Graphics graphics, RectangleF overlayBounds)
    {
        var item = FindHoveredItem();
        var geometry = item is null
            ? null
            : _boxes.LastOrDefault(box => box.Box.Id == item.Box.Id);
        if (item is null || geometry is null)
        {
            return;
        }

        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.Low;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.TextContrast = 4;
        graphics.Transform = new Matrix(
            (float)_scale,
            0,
            0,
            (float)_scale,
            -(float)(overlayBounds.X * _scale),
            -(float)(overlayBounds.Y * _scale));
        graphics.SetClip(geometry.Body, CombineMode.Intersect);

        var baseColor = ParseOpaqueColor(geometry.Box.Appearance.Background);
        var textColor = ResolveAutoTextColor(baseColor);
        using var itemFont = geometry.Box.Appearance.ShowItemLabels
            ? CreateFont(
                geometry.Box.Appearance.LabelFontFamily,
                (float)geometry.Box.Appearance.LabelFontSize,
                FontStyle.Regular,
                GraphicsUnit.Point)
            : null;
        using var itemBrush = geometry.Box.Appearance.ShowItemLabels
            ? new SolidBrush(textColor)
            : null;
        using var itemFormat = geometry.Box.Appearance.ShowItemLabels
            ? CreateItemTextFormat(geometry.Box.ViewMode)
            : null;
        using var selectedGridItemFormat = geometry.Box.Appearance.ShowItemLabels &&
                                           geometry.Box.ViewMode == BoxViewMode.Grid
            ? CreateSelectedGridItemTextFormat()
            : null;
        DrawItem(
            graphics,
            item,
            itemFont,
            itemBrush,
            itemFormat,
            selectedGridItemFormat,
            geometry.Body,
            _selection,
            includeItemHoverFeedback: true);
        graphics.ResetTransform();
    }

    private void DrawHeaderActionOverlay(Graphics graphics, RectangleF overlayBounds)
    {
        var targetBoxId = _searchingBoxId ?? _hoveredBoxId;
        var geometry = targetBoxId is { } boxId
            ? _boxes.LastOrDefault(box => box.Box.Id == boxId)
            : null;
        if (geometry is null)
        {
            return;
        }

        // This overlay covers just the header controls. Keeping it on the
        // quality path avoids rough vector edges without affecting desktop
        // drag rendering.
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.TextContrast = 4;
        graphics.Transform = new Matrix(
            (float)_scale,
            0,
            0,
            (float)_scale,
            -(float)(overlayBounds.X * _scale),
            -(float)(overlayBounds.Y * _scale));
        graphics.SetClip(geometry.Header, CombineMode.Intersect);

        var baseColor = ParseOpaqueColor(geometry.Box.Appearance.Background);
        var textColor = ResolveAutoTextColor(baseColor);
        var isDarkSurface = UsesLightText(baseColor);
        var accent = ResolveAccentColor(
            baseColor,
            ParseOpaqueColor(geometry.Box.Appearance.Accent));
        DrawSearchButton(
            graphics,
            geometry.Search,
            _searchingBoxId == geometry.Box.Id,
            _hoveredSearchBoxId == geometry.Box.Id,
            accent,
            textColor,
            isDarkSurface);
        DrawAutoExpandButton(
            graphics,
            geometry.AutoExpand,
            geometry.Box.ExpandOnHover,
            _hoveredAutoExpandBoxId == geometry.Box.Id,
            accent,
            textColor,
            isDarkSurface);
        DrawMenuButton(
            graphics,
            geometry.Menu,
            _hoveredMenuBoxId == geometry.Box.Id,
            textColor,
            isDarkSurface);
        graphics.ResetTransform();
    }

    private RectangleF GetItemHoverVisualBounds(
        Graphics graphics,
        ItemGeometry item,
        RectangleF contentBounds)
    {
        var iconBounds = GetItemIconBounds(item);
        if (!item.Box.Appearance.ShowItemLabels)
        {
            return item.Bounds;
        }

        var isSelected = _selection.Contains(item.Item.Key.ToString());
        using var labelFont = CreateFont(
            item.Box.Appearance.LabelFontFamily,
            (float)item.Box.Appearance.LabelFontSize,
            FontStyle.Regular,
            GraphicsUnit.Point);
        var textBounds = GetItemTextBounds(
            graphics,
            item,
            iconBounds,
            labelFont,
            DesktopIconLabelDisplayPolicy.ShowsFullLabel(isSelected, isHovered: true),
            contentBounds);
        return textBounds.IsEmpty
            ? item.Bounds
            : RectangleF.Union(item.Bounds, textBounds);
    }

    private static void DrawMenuButton(
        Graphics graphics,
        RectangleF bounds,
        bool hovered,
        Color textColor,
        bool isDark)
    {
        if (hovered)
        {
            using var fill = new SolidBrush(Color.FromArgb(isDark ? 36 : 24, textColor));
            using var path = RoundedRectangle(RectangleF.Inflate(bounds, -2, -2), 4);
            graphics.FillPath(fill, path);
        }

        LucideRuntimeIcons.Draw(
            graphics,
            LucideRuntimeIcon.Menu,
            bounds,
            textColor,
            15f);
    }

    private static void DrawSearchButton(
        Graphics graphics,
        RectangleF bounds,
        bool active,
        bool hovered,
        Color accent,
        Color textColor,
        bool isDark)
    {
        if (active || hovered)
        {
            var fillColor = active
                ? Color.FromArgb(isDark ? 76 : 48, accent)
                : Color.FromArgb(isDark ? 36 : 24, textColor);
            using var fill = new SolidBrush(fillColor);
            using var path = RoundedRectangle(RectangleF.Inflate(bounds, -2, -2), 4);
            graphics.FillPath(fill, path);
            if (active)
            {
                using var border = new Pen(Color.FromArgb(isDark ? 150 : 120, accent), 1);
                graphics.DrawPath(border, path);
            }
        }

        LucideRuntimeIcons.Draw(
            graphics,
            LucideRuntimeIcon.Search,
            bounds,
            textColor,
            15f);
    }

    private static void DrawAutoExpandButton(
        Graphics graphics,
        RectangleF bounds,
        bool enabled,
        bool hovered,
        Color accent,
        Color textColor,
        bool isDark)
    {
        if (enabled || hovered)
        {
            var fillColor = enabled
                ? Color.FromArgb(isDark ? 76 : 48, accent)
                : Color.FromArgb(isDark ? 36 : 24, textColor);
            using var fill = new SolidBrush(fillColor);
            using var path = RoundedRectangle(RectangleF.Inflate(bounds, -2, -2), 4);
            graphics.FillPath(fill, path);
            if (enabled)
            {
                using var border = new Pen(Color.FromArgb(isDark ? 150 : 120, accent), 1);
                graphics.DrawPath(border, path);
            }
        }

        LucideRuntimeIcons.Draw(
            graphics,
            LucideRuntimeIcon.ChevronsUpDown,
            bounds,
            textColor,
            15f);
    }

    private static Font CreateFont(
        string? familyName,
        float size,
        FontStyle style,
        GraphicsUnit unit)
    {
        try
        {
            return new Font(
                string.IsNullOrWhiteSpace(familyName) ? "Segoe UI" : familyName,
                size,
                style,
                unit);
        }
        catch (ArgumentException)
        {
            return new Font("Segoe UI", size, style, unit);
        }
    }

}

