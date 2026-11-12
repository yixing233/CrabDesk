using CrabDesk.Core;
using CrabDesk.WinUI.Windows;

namespace CrabDesk.WinUI;

public partial class App
{
    private readonly Dictionary<Guid, FileShelfWindow> _fileShelfWindows = [];
    private bool _fileShelfRefreshQueued, _closingFileShelves;

    private void FileShelvesChanged(object? sender, EventArgs args)
    {
        if (_closingFileShelves || _fileShelfRefreshQueued || _window is null) return;
        _fileShelfRefreshQueued = true;
        _window.DispatcherQueue.TryEnqueue(() => { _fileShelfRefreshQueued = false; ReconcileFileShelves(); });
    }

    private void ReconcileFileShelves()
    {
        if (_closingFileShelves || _runtime is null) return;
        var widgets = _runtime.State.Widgets.Where(widget => widget.Kind == DesktopWidgetKind.FileShelf && widget.FileShelfFloating.Enabled).ToArray();
        foreach (var (id, window) in _fileShelfWindows.ToArray())
            if (!widgets.Contains(window.Widget)) { _fileShelfWindows.Remove(id); window.Close(); }
        foreach (var widget in widgets)
        {
            if (_fileShelfWindows.ContainsKey(widget.Id)) continue;
            var window = new FileShelfWindow(_runtime, widget);
            _fileShelfWindows.Add(widget.Id, window);
            window.Closed += (_, _) =>
            {
                if (_fileShelfWindows.GetValueOrDefault(widget.Id) != window) return;
                _fileShelfWindows.Remove(widget.Id);
                if (!_closingFileShelves && _runtime.State.Widgets.Contains(widget) && widget.FileShelfFloating.Enabled)
                    _runtime.SetFileShelfFloating(widget, false, widget.FileShelfFloating.AutoHide);
            };
            window.Present();
        }
    }

    private void CloseFileShelves()
    {
        _closingFileShelves = true;
        if (_runtime is not null) _runtime.Changed -= FileShelvesChanged;
        foreach (var window in _fileShelfWindows.Values.ToArray()) window.Close();
        _fileShelfWindows.Clear();
    }
}
