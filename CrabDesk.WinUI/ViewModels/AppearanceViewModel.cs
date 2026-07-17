using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;

namespace CrabDesk.WinUI.ViewModels;

public partial class AppearanceViewModel : ObservableObject
{
    private readonly ICrabDeskService _service;
    private readonly IBackdropService _backdrops;
    private string _manualTitleColor = "#FFFFFFFF";

    public AppearanceViewModel(
        ICrabDeskService service,
        IBackdropService backdrops,
        IFontCatalogService fontCatalog)
    {
        _service = service;
        _backdrops = backdrops;
        FontFamilies = fontCatalog.FontFamilies is { Count: > 0 } families
            ? families
            : ["Segoe UI"];
        _service.Changed += (_, _) => Refresh();
        Refresh();
    }

    public IReadOnlyList<BackdropKind> BackdropKinds { get; } = Enum.GetValues<BackdropKind>();
    public IReadOnlyList<BoxTitleAlignment> TitleAlignments { get; } = Enum.GetValues<BoxTitleAlignment>();
    public IReadOnlyList<BoxViewMode> ViewModes { get; } = Enum.GetValues<BoxViewMode>();
    public IReadOnlyList<BoxSortMode> SortModes { get; } = Enum.GetValues<BoxSortMode>();
    public IReadOnlyList<string> FontFamilies { get; }
    public BackdropKind Backdrop
    {
        get => _backdrops.Current;
        set
        {
            if (value == _backdrops.Current) return;
            _backdrops.Apply(App.GetService<MainWindow>(), value);
            OnPropertyChanged();
        }
    }
    private DesktopBox CurrentBox => _service.Boxes.First();

    public string Background { get => CurrentBox.Appearance.Background; set { if (IsColor(value)) _service.SetBoxBackground(null, value); } }
    public string Accent { get => CurrentBox.Appearance.Accent; set { if (IsColor(value)) _service.SetBoxAccent(null, value); } }
    public double Opacity { get => CurrentBox.Appearance.Opacity * 100; set => _service.SetBoxOpacity(null, value / 100); }
    public double CornerRadius { get => _service.State.Settings.Appearance.CornerRadius; set => _service.SetCornerRadius(value); }
    public bool ShowBorder { get => _service.State.Settings.Appearance.ShowBorder; set => _service.SetShowBoxBorder(value); }
    public bool ShowResizeGrip { get => _service.State.Settings.Appearance.ShowResizeGrip; set => _service.SetShowResizeGrip(value); }
    public bool HoverFeedback { get => _service.State.Settings.Appearance.HoverFeedback; set => _service.SetHoverFeedback(value); }
    public double HorizontalSpacing { get => _service.State.Settings.Appearance.IconHorizontalSpacing; set => _service.SetIconSpacing(value, VerticalSpacing); }
    public double VerticalSpacing { get => _service.State.Settings.Appearance.IconVerticalSpacing; set => _service.SetIconSpacing(HorizontalSpacing, value); }
    public string SelectionColor { get => _service.State.Settings.Appearance.SelectionColor; set { if (IsColor(value)) _service.SetSelectionColor(value); } }
    public double TitleBarHeight { get => CurrentBox.Appearance.TitleBarHeight; set => _service.SetBoxTitleBarHeight(null, value); }
    public BoxTitleAlignment TitleAlignment { get => CurrentBox.Appearance.TitleAlignment; set => _service.SetBoxTitleAlignment(null, value); }
    public string TitleColor { get => CurrentBox.Appearance.TitleColor; set { if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase) || IsColor(value)) _service.SetBoxTitleColor(null, value); } }
    public string TitleFontFamily { get => CurrentBox.Appearance.TitleFontFamily; set { if (!string.IsNullOrWhiteSpace(value) && value != TitleFontFamily) _service.SetBoxTitleFontFamily(null, value); } }
    public string ManualTitleColor
    {
        get => IsColor(TitleColor) ? TitleColor : _manualTitleColor;
        set
        {
            if (!IsColor(value)) return;
            _manualTitleColor = value;
            OnPropertyChanged();
            if (!UseAutomaticTitleColor) _service.SetBoxTitleColor(null, value);
        }
    }
    public bool UseAutomaticTitleColor
    {
        get => string.Equals(TitleColor, "Auto", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value == UseAutomaticTitleColor) return;
            if (value && IsColor(TitleColor)) _manualTitleColor = TitleColor;
            _service.SetBoxTitleColor(null, value ? "Auto" : _manualTitleColor);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsManualTitleColorEnabled));
            OnPropertyChanged(nameof(ManualTitleColor));
        }
    }
    public bool IsManualTitleColorEnabled => !UseAutomaticTitleColor;
    public double TitleFontSize { get => CurrentBox.Appearance.TitleFontSize; set => _service.SetBoxTitleFontSize(null, value); }
    public bool TitleFontBold { get => CurrentBox.Appearance.TitleFontBold; set => _service.SetBoxTitleFontBold(null, value); }
    public bool ShowCollapseButton { get => CurrentBox.Appearance.ShowCollapseButton; set => _service.SetShowCollapseButton(null, value); }
    public double IconSize { get => CurrentBox.Appearance.IconSize; set => _service.SetBoxIconSize(null, value); }
    public string LabelFontFamily { get => CurrentBox.Appearance.LabelFontFamily; set { if (!string.IsNullOrWhiteSpace(value) && value != LabelFontFamily) _service.SetBoxLabelFontFamily(null, value); } }
    public double LabelFontSize { get => CurrentBox.Appearance.LabelFontSize; set => _service.SetBoxLabelFontSize(null, value); }
    public bool ShowItemLabels { get => CurrentBox.Appearance.ShowItemLabels; set => _service.SetBoxShowItemLabels(null, value); }
    public BoxViewMode ViewMode { get => CurrentBox.ViewMode; set => _service.SetBoxViewMode(null, value); }
    public BoxSortMode SortMode { get => CurrentBox.SortMode; set => _service.SetBoxSortMode(null, value); }

    [RelayCommand] private void Reset() => _service.ResetAppearance();

    private void Refresh()
    {
        if (_service.Boxes.FirstOrDefault() is { } box && IsColor(box.Appearance.TitleColor))
        {
            _manualTitleColor = box.Appearance.TitleColor;
        }
        OnPropertyChanged(string.Empty);
    }

    private static bool IsColor(string value)
    {
        var hex = value.TrimStart('#');
        return (hex.Length == 6 || hex.Length == 8) && hex.All(Uri.IsHexDigit);
    }
}
