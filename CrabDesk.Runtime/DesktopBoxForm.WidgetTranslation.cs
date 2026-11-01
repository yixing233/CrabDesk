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
    private RectangleF _hoveredTranslationButton;
    private readonly Dictionary<(Guid Widget, bool Result), DesktopWidgetTextVisual> _translationTextVisuals = [];
    private static readonly string[] TranslationLanguages = ["简体中文", "繁體中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语"];

    internal readonly record struct TranslationLayout(RectangleF Source, RectangleF Result, RectangleF Language, RectangleF Translate, RectangleF Clear, RectangleF Copy, RectangleF Status);

    internal static TranslationLayout GetTranslationLayout(DesktopWidget widget)
    {
        var body = WidgetBodyRectangle(widget);
        var width = (body.Width - 10) / 2;
        return new(new(body.X, body.Y + 26, width, body.Height - 58),
            new(body.X + width + 10, body.Y + 26, width, body.Height - 58),
            new(body.X + width + 10, body.Y - 4, width - 36, 28),
            new(body.Right - 72, body.Bottom - 28, 72, 28),
            new(body.X + width - 28, body.Y - 4, 28, 28),
            new(body.Right - 28, body.Y - 4, 28, 28),
            new(body.X, body.Bottom - 28, body.Width - 84, 28));
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
        var textColor = TranslationTextColor(ResolveAutoTextColor(background));
        background = TranslationFieldBackground(textColor);
        input.Present(new Rectangle(location, new Size(ToPixel(source.Width), ToPixel(source.Height))), result ? widget.Translation : widget.Text,
            background, textColor, textColor,
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
            caretPoint: point is { } click ? new PointF(click.X - source.X, click.Y - source.Y) : null,
            contentPadding: new System.Windows.Thickness(7), showFocusBorder: false);
    }

    internal void DismissWidgetTranslationInput()
    {
        _translationEditingWidget = null;
        if (_translationInput is { IsDisposed: false }) _translationInput.Dismiss();
        if (_translationResultInput is { IsDisposed: false }) _translationResultInput.Dismiss();
    }

    private void RefreshWidgetTranslationInput()
    {
        foreach (var key in _translationTextVisuals.Keys.Where(key => !_runtime.State.Widgets.Any(widget => widget.Id == key.Widget)).ToArray())
        {
            _translationTextVisuals[key].Dispose(); _translationTextVisuals.Remove(key);
        }
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
        else if (layout.Clear.Contains(point) && !string.IsNullOrEmpty(widget.Text))
        {
            DismissWidgetTranslationInput();
            _translationCopiedId = null;
            _translationSourceScroll.Remove(widget.Id); _widgetScroll.Remove(widget.Id);
            _runtime.UpdateWidgetTranslationInput(widget, string.Empty);
        }
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

    private void DrawTranslationWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text)
    {
        text = TranslationTextColor(text);
        var muted = text.GetBrightness() > .5f ? Color.FromArgb(205, 205, 205) : Color.FromArgb(90, 90, 90);
        var layout = GetTranslationLayout(widget);
        WidgetText(graphics, "原文", new(body.X, body.Y - 4, layout.Source.Width - 36, 28), text, 11);
        WidgetText(graphics, $"{widget.TargetLanguage} ▾", layout.Language, text, 11);
        foreach (var field in new[] { layout.Source, layout.Result })
        {
            using var path = RoundedRectangle(field, (float)Math.Min(8, _runtime.State.Settings.Appearance.CornerRadius));
            using var fill = new SolidBrush(TranslationFieldBackground(text));
            using var border = new Pen(Color.FromArgb(50, text), 1);
            graphics.FillPath(fill, path); graphics.DrawPath(border, path);
        }
        DesktopWidgetTextVisual Visual(bool result)
        {
            if (!_translationTextVisuals.TryGetValue((widget.Id, result), out var visual))
                _translationTextVisuals[(widget.Id, result)] = visual = new();
            return visual;
        }
        _translationSourceScroll[widget.Id] = DrawTranslationField(graphics, widget.Text, "输入要翻译的文字…", layout.Source, text, muted, _translationSourceScroll.GetValueOrDefault(widget.Id), Visual(false));
        _widgetScroll[widget.Id] = DrawTranslationField(graphics, widget.Translation, widget.IsLoading ? "正在翻译…" : "译文将在这里显示", layout.Result, text, muted, _widgetScroll.GetValueOrDefault(widget.Id), Visual(true));
        var status = _translationCopiedId == widget.Id ? "译文已复制" : widget.IsLoading ? "正在翻译…" :
            !string.IsNullOrEmpty(widget.Error) ? widget.Error : "Ctrl+Enter 翻译";
        var hovered = _hoveredWidgetId == widget.Id ? _hoveredTranslationButton : RectangleF.Empty;
        if (hovered == layout.Clear) status = string.IsNullOrEmpty(widget.Text) ? "原文为空" : "清空原文";
        else if (hovered == layout.Copy) status = _translationCopiedId == widget.Id ? "译文已复制" :
            string.IsNullOrWhiteSpace(widget.Translation) ? "暂无译文可复制" : "复制译文";
        WidgetText(graphics, status, layout.Status, muted, 10.5f);
        DrawTranslationButton(graphics, layout.Translate, widget.IsLoading ? "取消" : "翻译", text,
            widget.IsLoading || !string.IsNullOrWhiteSpace(widget.Text), hovered == layout.Translate);
        DrawTranslationIcon(graphics, layout.Clear, LucideRuntimeIcon.Eraser, text, !string.IsNullOrEmpty(widget.Text), hovered == layout.Clear);
        DrawTranslationIcon(graphics, layout.Copy, _translationCopiedId == widget.Id ? LucideRuntimeIcon.Check : LucideRuntimeIcon.Copy,
            text, !string.IsNullOrWhiteSpace(widget.Translation), hovered == layout.Copy);
    }

    private static float DrawTranslationField(Graphics graphics, string value, string placeholder, RectangleF field, Color text, Color muted, float offset, DesktopWidgetTextVisual visual)
    {
        var bounds = RectangleF.Inflate(field, -10, -8);
        var empty = string.IsNullOrWhiteSpace(value);
        if (empty) value = placeholder;
        offset = visual.Draw(graphics, value, field, empty ? muted : text, offset);
        var height = (float)visual.Height;
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

    private static Color TranslationTextColor(Color foreground) => foreground.GetBrightness() > .5f ? Color.White : Color.FromArgb(31, 31, 31);

    private static Color TranslationFieldBackground(Color foreground) => foreground.GetBrightness() > .5f
        ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250);

    private static Color TranslationFieldColor(Color background, Color foreground, int alpha) => Color.FromArgb(
        (background.R * (255 - alpha) + foreground.R * alpha) / 255,
        (background.G * (255 - alpha) + foreground.G * alpha) / 255,
        (background.B * (255 - alpha) + foreground.B * alpha) / 255);

    private void DrawTranslationIcon(Graphics graphics, RectangleF bounds, LucideRuntimeIcon icon, Color text, bool enabled, bool hovered)
    {
        if (enabled && hovered)
        {
            using var path = RoundedRectangle(bounds, (float)Math.Min(6, _runtime.State.Settings.Appearance.CornerRadius));
            using var wash = new SolidBrush(Color.FromArgb(24, text));
            graphics.FillPath(wash, path);
        }
        var disabled = text.GetBrightness() > .5f ? Color.FromArgb(155, 155, 155) : Color.FromArgb(115, 115, 115);
        LucideRuntimeIcons.Draw(graphics, icon, bounds, enabled ? text : disabled, 16);
    }

    private void DrawTranslationButton(Graphics graphics, RectangleF bounds, string label, Color text, bool enabled, bool hovered)
    {
        var dark = text.GetBrightness() > .5f;
        var background = Color.FromArgb(enabled ? hovered ? 38 : 24 : 10, text);
        var foreground = enabled ? text
            : dark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(110, 110, 110);
        using var path = RoundedRectangle(bounds, (float)Math.Min(6, _runtime.State.Settings.Appearance.CornerRadius));
        using var fill = new SolidBrush(background);
        using var border = new Pen(Color.FromArgb(enabled ? 32 : 14, text), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        WidgetText(graphics, label, bounds, foreground, 12, centered: true);
    }
}
