using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private DesktopWidgetTextInput? _translationInput;
    private DesktopWidgetTextInput? _translationResultInput;
    private DesktopWidget? _translationEditingWidget;
    private LayoutRect _translationEditingBounds;
    private bool _translationEditingResult;
    private readonly Dictionary<Guid, float> _translationSourceScroll = [];
    private Guid? _translationCopiedId;
    private static readonly string[] TranslationLanguages = ["简体中文", "繁體中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语"];

    internal readonly record struct TranslationLayout(RectangleF Source, RectangleF Result, RectangleF Language, RectangleF Translate, RectangleF Copy, RectangleF Status);

    internal static TranslationLayout GetTranslationLayout(DesktopWidget widget)
    {
        var body = WidgetBodyRectangle(widget);
        var width = (body.Width - 10) / 2;
        return new(new(body.X, body.Y + 22, width, body.Height - 54),
            new(body.X + width + 10, body.Y + 22, width, body.Height - 54),
            new(body.X + width + 10, body.Y - 2, width, 22),
            new(body.Right - 128, body.Bottom - 26, 72, 26),
            new(body.Right - 48, body.Bottom - 26, 48, 26),
            new(body.X, body.Bottom - 26, width, 26));
    }

    private void BeginWidgetTranslationInput(DesktopWidget widget, PointF? point = null, bool result = false)
    {
        if (result && string.IsNullOrWhiteSpace(widget.Translation)) return;
        DismissWidgetInputs();
        _runtime.CommitActiveDesktopInlineRename();
        var input = result ? _translationResultInput ??= new DesktopWidgetTextInput("翻译译文")
            : _translationInput ??= new DesktopWidgetTextInput("翻译原文", WidgetTranslationService.MaximumInputLength);
        _translationEditingWidget = widget;
        _translationEditingBounds = widget.Bounds;
        _translationEditingResult = result;
        _translationCopiedId = null;
        var source = result ? GetTranslationLayout(widget).Result : GetTranslationLayout(widget).Source;
        var location = PointToScreen(new Point(ToPixel(source.X), ToPixel(source.Y)));
        var appearance = _runtime.State.Boxes.FirstOrDefault()?.Appearance ?? _runtime.State.Settings.DefaultBoxAppearance;
        var background = ParseOpaqueColor(appearance.Background);
        var textColor = ResolveAutoTextColor(background);
        background = TranslationFieldColor(background, textColor, result ? 6 : 10);
        input.Present(new Rectangle(location, new Size(ToPixel(source.Width), ToPixel(source.Height))), result ? widget.Translation : widget.Text,
            background, textColor, ParseOpaqueColor(appearance.Accent),
            text => { if (!result) _runtime.UpdateWidgetTranslationInput(widget, text); },
            result ? null : () => _ = _runtime.TranslateWidgetAsync(widget), dismissed: offset =>
            {
                _translationEditingWidget = null;
                if (_resourcesDisposed || IsDisposed || !_runtime.State.Widgets.Contains(widget)) return;
                if (result) _widgetScroll[widget.Id] = (float)offset;
                else _translationSourceScroll[widget.Id] = (float)offset;
                RequestVisualLayerRender();
            }, verticalOffset: result ? _widgetScroll.GetValueOrDefault(widget.Id) : _translationSourceScroll.GetValueOrDefault(widget.Id),
            cornerRadius: Math.Min(8, _runtime.State.Settings.Appearance.CornerRadius), readOnly: result,
            caretPoint: point is { } click ? new PointF(click.X - source.X, click.Y - source.Y) : null);
    }

    internal void DismissWidgetTranslationInput()
    {
        _translationEditingWidget = null;
        if (_translationInput is { IsDisposed: false }) _translationInput.Dismiss();
        if (_translationResultInput is { IsDisposed: false }) _translationResultInput.Dismiss();
    }

    private void RefreshWidgetTranslationInput()
    {
        if (_translationEditingWidget is not { } widget) return;
        if (!_runtime.State.Widgets.Contains(widget) || widget.MonitorId != _monitor.Id || widget.Bounds != _translationEditingBounds || _runtime.IsPaused)
            DismissWidgetTranslationInput();
        else if (_translationEditingResult && _translationResultInput is { Visible: true } result && DesktopWidgets.NormalizeText(result.InputBox.Text) != widget.Translation)
            result.InputBox.Text = widget.Translation;
    }

    private void HandleTranslationWidgetClick(DesktopWidget widget, PointF point)
    {
        var layout = GetTranslationLayout(widget);
        if (layout.Source.Contains(point)) BeginWidgetTranslationInput(widget, point);
        else if (layout.Result.Contains(point)) BeginWidgetTranslationInput(widget, point, result: true);
        else if (layout.Language.Contains(point)) ShowTranslationLanguageMenu(widget, layout.Language);
        else if (layout.Translate.Contains(point) && (widget.IsLoading || !string.IsNullOrWhiteSpace(widget.Text)))
        {
            _translationCopiedId = null;
            if (widget.IsLoading) { _runtime.CancelWidgetRequest(widget.Id); _runtime.WidgetChanged(widget); }
            else _ = _runtime.TranslateWidgetAsync(widget);
        }
        else if (layout.Copy.Contains(point) && !string.IsNullOrWhiteSpace(widget.Translation))
        {
            try { Forms.Clipboard.SetText(widget.Translation); _translationCopiedId = widget.Id; RequestVisualLayerRender(); }
            catch (System.Runtime.InteropServices.ExternalException) { widget.Error = "剪贴板暂时不可用，请重试"; _runtime.WidgetChanged(widget); }
        }
    }

    private FluentContextMenuStrip ShowTranslationLanguageMenu(DesktopWidget widget, RectangleF anchor)
    {
        var menu = new FluentContextMenuStrip { PreferredDpiScale = (float)_scale };
        menu.Opening += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        menu.Closed += (_, _) => { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)menu.Dispose); else menu.Dispose(); };
        foreach (var language in TranslationLanguages)
        {
            var item = new FluentToolStripMenuItem(language) { Checked = widget.TargetLanguage == language };
            item.Click += (_, _) => { _translationCopiedId = null; _runtime.SetWidgetTranslationLanguage(widget, language); };
            menu.Items.Add(item);
        }
        menu.Show(this, new Point(ToPixel(anchor.X), ToPixel(anchor.Bottom)));
        return menu;
    }

    private void DrawTranslationWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text, Color muted, Color accent)
    {
        var layout = GetTranslationLayout(widget);
        WidgetText(graphics, "原文", new(body.X, body.Y - 2, layout.Source.Width, 22), muted, 11);
        WidgetText(graphics, $"{widget.TargetLanguage} ▾", layout.Language, text, 11);
        foreach (var field in new[] { layout.Source, layout.Result })
        {
            using var path = RoundedRectangle(field, (float)Math.Min(8, _runtime.State.Settings.Appearance.CornerRadius));
            using var fill = new SolidBrush(Color.FromArgb(field == layout.Source ? 10 : 6, text));
            using var border = new Pen(Color.FromArgb(28, text), 1);
            graphics.FillPath(fill, path); graphics.DrawPath(border, path);
        }
        _translationSourceScroll[widget.Id] = DrawTranslationField(graphics, widget.Text, "输入要翻译的文字…", layout.Source, text, muted, _translationSourceScroll.GetValueOrDefault(widget.Id));
        _widgetScroll[widget.Id] = DrawTranslationField(graphics, widget.Translation, widget.IsLoading ? "正在翻译…" : "译文将在这里显示", layout.Result, text, muted, _widgetScroll.GetValueOrDefault(widget.Id));
        var status = _translationCopiedId == widget.Id ? "译文已复制" : widget.IsLoading ? "正在翻译…" :
            !string.IsNullOrEmpty(widget.Error) ? widget.Error : "Ctrl+Enter 翻译";
        WidgetText(graphics, status, layout.Status, muted, 10.5f);
        DrawTranslationButton(graphics, layout.Translate, widget.IsLoading ? "取消" : "翻译", accent,
            widget.IsLoading || !string.IsNullOrWhiteSpace(widget.Text));
        DrawTranslationButton(graphics, layout.Copy, "复制", text, !string.IsNullOrWhiteSpace(widget.Translation));
    }

    private static float DrawTranslationField(Graphics graphics, string value, string placeholder, RectangleF field, Color text, Color muted, float offset)
    {
        var bounds = RectangleF.Inflate(field, -10, -8);
        var saved = graphics.Save();
        graphics.SetClip(bounds, CombineMode.Intersect);
        var empty = string.IsNullOrWhiteSpace(value);
        if (empty) value = placeholder;
        using var font = new Font(BoxAppearance.DefaultFontFamily, 13, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic);
        var height = graphics.MeasureString(value, font, new SizeF(Math.Max(1, bounds.Width), float.MaxValue), format).Height;
        offset = Math.Clamp(offset, 0, Math.Max(0, height - bounds.Height));
        using var ink = new SolidBrush(empty ? Color.FromArgb(150, text) : text);
        graphics.DrawString(value, font, ink, new RectangleF(bounds.X, bounds.Y - offset, bounds.Width, Math.Max(bounds.Height, height + 4)), format);
        graphics.Restore(saved);
        if (height > bounds.Height)
        {
            var thumbHeight = Math.Max(12, bounds.Height * bounds.Height / height);
            var thumbY = bounds.Y + offset / Math.Max(1, height - bounds.Height) * (bounds.Height - thumbHeight);
            using var thumb = new SolidBrush(Color.FromArgb(90, text));
            using var thumbPath = RoundedRectangle(new(field.Right - 5, thumbY, 2, thumbHeight), 1);
            graphics.FillPath(thumb, thumbPath);
        }
        return offset;
    }

    private static Color TranslationFieldColor(Color background, Color foreground, int alpha) => Color.FromArgb(
        (background.R * (255 - alpha) + foreground.R * alpha) / 255,
        (background.G * (255 - alpha) + foreground.G * alpha) / 255,
        (background.B * (255 - alpha) + foreground.B * alpha) / 255);

    private void DrawTranslationButton(Graphics graphics, RectangleF bounds, string label, Color color, bool enabled)
    {
        using var path = RoundedRectangle(bounds, (float)Math.Min(6, _runtime.State.Settings.Appearance.CornerRadius));
        using var fill = new SolidBrush(Color.FromArgb(enabled ? 32 : 10, color));
        graphics.FillPath(fill, path);
        WidgetText(graphics, label, bounds, enabled ? color : Color.FromArgb(120, color), 12, centered: true);
    }
}
