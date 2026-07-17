using System.Runtime.InteropServices;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Native;

public sealed class GlobalHotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private readonly HotkeyWindow _window = new();
    private readonly Dictionary<HotkeyAction, int> _registered = [];
    private bool _disposed;

    public GlobalHotkeyService()
    {
        _window.HotkeyPressed += OnHotkeyPressed;
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
                _window.Handle,
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
            UnregisterHotKey(_window.Handle, id);
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
        _window.HotkeyPressed -= OnHotkeyPressed;
        _window.Dispose();
    }

    private void OnHotkeyPressed(int id)
    {
        var match = _registered.FirstOrDefault(pair => pair.Value == id);
        if (_registered.ContainsKey(match.Key) && match.Value == id)
        {
            Pressed?.Invoke(this, new GlobalHotkeyPressedEventArgs(match.Key));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private sealed class HotkeyWindow : Forms.NativeWindow, IDisposable
    {
        internal HotkeyWindow()
        {
            CreateHandle(new Forms.CreateParams
            {
                Caption = "CrabDesk.GlobalHotkeys",
                Parent = new IntPtr(-3)
            });
        }

        internal event Action<int>? HotkeyPressed;

        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == WmHotkey)
            {
                HotkeyPressed?.Invoke(message.WParam.ToInt32());
            }
            base.WndProc(ref message);
        }

        public void Dispose() => DestroyHandle();
    }
}
