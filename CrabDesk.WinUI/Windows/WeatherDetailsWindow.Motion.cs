using Microsoft.UI.Xaml;

namespace CrabDesk.WinUI.Windows;

public sealed partial class WeatherDetailsWindow
{
    private DetailPopupEntrance _entrance = null!;

    private void InitializeEntrance()
    {
        _entrance = new DetailPopupEntrance(this, _root, () => _runtime.State.Settings.Appearance.AnimationEnabled);
        _root.Loaded += PrepareEntrance;
        _entrance.Prepare();
        Closed += (_, _) =>
        {
            _root.Loaded -= PrepareEntrance;
            _entrance.Dispose();
        };
    }

    private void PrepareEntrance(object sender, RoutedEventArgs args)
    {
        _root.Loaded -= PrepareEntrance;
        _root.UpdateLayout();
        if (_locationsOnly) ResizeLocations();
        UpdateWindowShape();
    }
}
