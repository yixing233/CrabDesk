using System.Runtime.InteropServices;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>Shared activation lifecycle for desktop widget editors.</summary>
internal abstract partial class DesktopWidgetInputForm : Forms.Form
{
    protected PopupLoadingPresenter? Loading { get; private set; }

    protected PopupLoadingPresenter CreatePopupLoading(System.Windows.FrameworkElement content, bool dark,
        bool animationsEnabled, PopupSkeletonLayout layout) => Loading = new(content, dark, animationsEnabled, layout);

    protected override Forms.CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DesktopInputProxy.Register(Handle);
        CreatePopupContent();
        if (_entranceSurface is not null && !_animateEntrance) SetEntranceCloak(true);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopPopupEntrance();
        ReleasePopupContent();
        DesktopInputProxy.Unregister(Handle);
        base.OnHandleDestroyed(e);
    }
    protected void HideWithoutActivation()
    {
        StopPopupEntrance();
        if (ContainsFocus) System.Windows.Input.Keyboard.ClearFocus();
        if (IsHandleCreated && Visible)
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0297); // HIDEWINDOW | NOACTIVATE | NO(Z/OWNERZ)ORDER | NOMOVE | NOSIZE
        Hide();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopPopupEntrance();
            Loading?.Dispose();
        }
        base.Dispose(disposing);
    }
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
