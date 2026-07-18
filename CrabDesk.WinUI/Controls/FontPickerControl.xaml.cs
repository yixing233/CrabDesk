using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Controls;

public sealed partial class FontPickerControl : UserControl
{
    private readonly ObservableCollection<string> _suggestions = [];
    private bool _ignoreTextChange;

    public FontPickerControl()
    {
        InitializeComponent();
        SearchBox.ItemsSource = _suggestions;
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(string),
            typeof(FontPickerControl),
            new PropertyMetadata(string.Empty));

    public IReadOnlyList<string> FontFamilies
    {
        get => (IReadOnlyList<string>)GetValue(FontFamiliesProperty);
        set => SetValue(FontFamiliesProperty, value);
    }

    public static readonly DependencyProperty FontFamiliesProperty =
        DependencyProperty.Register(
            nameof(FontFamilies),
            typeof(IReadOnlyList<string>),
            typeof(FontPickerControl),
            new PropertyMetadata(Array.Empty<string>(), OnFontFamiliesChanged));

    public string SelectedFont
    {
        get => (string)GetValue(SelectedFontProperty);
        set => SetValue(SelectedFontProperty, value);
    }

    public static readonly DependencyProperty SelectedFontProperty =
        DependencyProperty.Register(
            nameof(SelectedFont),
            typeof(string),
            typeof(FontPickerControl),
            new PropertyMetadata(string.Empty, OnSelectedFontChanged));

    private static void OnFontFamiliesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var picker = (FontPickerControl)sender;
        picker.RefreshSuggestions(picker.SearchBox.Text);
    }

    private static void OnSelectedFontChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var picker = (FontPickerControl)sender;
        picker.SetSearchText((string?)args.NewValue ?? string.Empty);
    }

    private void OnGotFocus(object sender, RoutedEventArgs args)
    {
        RefreshSuggestions(SearchBox.Text);
        SearchBox.IsSuggestionListOpen = false;
    }

    private void OnLostFocus(object sender, RoutedEventArgs args)
    {
        SetSearchText(SelectedFont);
        SearchBox.IsSuggestionListOpen = false;
    }

    private void OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_ignoreTextChange || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.IsSuggestionListOpen = false;
            return;
        }

        RefreshSuggestions(sender.Text);
        sender.IsSuggestionListOpen = _suggestions.Count > 0;
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string font) return;

        SelectedFont = font;
        SetSearchText(font);
        sender.IsSuggestionListOpen = false;
    }

    private void RefreshSuggestions(string? query)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        var families = FontFamilies ?? Array.Empty<string>();
        var matches = string.IsNullOrEmpty(normalizedQuery)
            ? families
            : families.Where(font => font.Contains(normalizedQuery, StringComparison.CurrentCultureIgnoreCase));

        _suggestions.Clear();
        foreach (var font in matches) _suggestions.Add(font);
    }

    private void SetSearchText(string value)
    {
        if (SearchBox.Text == value) return;

        _ignoreTextChange = true;
        SearchBox.Text = value;
        _ignoreTextChange = false;
    }
}
