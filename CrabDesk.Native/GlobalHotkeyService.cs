using System.Runtime.InteropServices;
using System.Windows.Interop;
using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class GlobalHotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private readonly HwndSource _source;
    private readonly Dictionary<HotkeyAction, int> _registered = [];
    private bool _disposed;

    public GlobalHotkeyService()
    {
        var parameters = new HwndSourceParameters("CrabDesk.GlobalHotkeys")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowHook);
    }

    public event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed;

    public HotkeyRegistrationStatus Register(HotkeyAction action, HotkeyBinding binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister(action);
        if (!binding.Enabled)
        {
            return HotkeyRegistrationStatus.Disabled;
        }
        if (binding.Modifiers == HotkeyModifiers.None)
        {
            return HotkeyRegistrationStatus.Failed;
        }

        var id = 0x5100 + (int)action;
        if (RegisterHotKey(
                _source.Handle,
                id,
                (uint)binding.Modifiers | ModNoRepeat,
                (uint)binding.Key))
        {
            _registered[action] = id;
            return HotkeyRegistrationStatus.Registered;
        }

        return Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered
            ? HotkeyRegistrationStatus.Conflict
            : HotkeyRegistrationStatus.Failed;
    }

    public void Unregister(HotkeyAction action)
    {
        if (_registered.Remove(action, out var id))
        {
            UnregisterHotKey(_source.Handle, id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var action in _registered.Keys.ToArray())
        {
            Unregister(action);
        }
        _source.RemoveHook(WindowHook);
        _source.Dispose();
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }
        var id = wParam.ToInt32();
        var match = _registered.FirstOrDefault(pair => pair.Value == id);
        if (_registered.ContainsKey(match.Key) && match.Value == id)
        {
            handled = true;
            Pressed?.Invoke(this, new GlobalHotkeyPressedEventArgs(match.Key));
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
