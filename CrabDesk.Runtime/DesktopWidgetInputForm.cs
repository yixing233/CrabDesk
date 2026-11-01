using System.Runtime.InteropServices;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>Shared activation lifecycle for desktop widget editors.</summary>
internal abstract class DesktopWidgetInputForm : Forms.Form
{
    protected override Forms.CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DesktopInputProxy.Register(Handle);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        DesktopInputProxy.Unregister(Handle);
        base.OnHandleDestroyed(e);
    }
    protected void HideWithoutActivation()
    {
        if (ContainsFocus) System.Windows.Input.Keyboard.ClearFocus();
        if (IsHandleCreated && Visible)
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0297); // HIDEWINDOW | NOACTIVATE | NO(Z/OWNERZ)ORDER | NOMOVE | NOSIZE
        Hide();
    }
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
