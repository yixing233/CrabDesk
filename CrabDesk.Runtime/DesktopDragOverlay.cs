using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>
/// A small, click-through layered child used for the mutable part of a desktop
/// drag. Keeping it separate from the monitor-sized desktop layer prevents a
/// pointer move from uploading an entire monitor bitmap.
/// </summary>
internal sealed class DesktopDragOverlay : Forms.Form
{
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WsClipSiblings = 0x04000000;
    private const int WsExLayered = 0x00080000;
    // Keep the child bitmap and its native layered surface stable while a
    // selection rectangle grows by a pixel at a time. The content is still
    // positioned at its exact DIP coordinates inside this padded surface.
    private const int BoundsQuantizationPixels = 64;
    private static readonly IntPtr HtTransparent = new(-1);
    private static readonly IntPtr MaNoActivate = new(3);
    private Bitmap? _bitmap;

    internal DesktopDragOverlay()
    {
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        TopLevel = false;
        DoubleBuffered = true;
        SetStyle(
            Forms.ControlStyles.AllPaintingInWmPaint |
            Forms.ControlStyles.UserPaint |
            Forms.ControlStyles.OptimizedDoubleBuffer,
            true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style &= ~WsClipSiblings;
            parameters.ExStyle |= WsExLayered;
            return parameters;
        }
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == WmMouseActivate)
        {
            message.Result = MaNoActivate;
            return;
        }
        if (message.Msg == WmNcHitTest)
        {
            message.Result = HtTransparent;
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnPaintBackground(Forms.PaintEventArgs eventArgs)
    {
    }

    protected override void OnPaint(Forms.PaintEventArgs eventArgs)
    {
    }

    internal bool Present(
        RectangleF requestedBounds,
        double scale,
        Action<Graphics, RectangleF> draw,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (IsDisposed || Parent is null || requestedBounds.Width <= 0 || requestedBounds.Height <= 0)
        {
            diagnostic = "The drag overlay has no valid parent or bounds.";
            return false;
        }

        var effectiveScale = Math.Max(scale, 0.01d);
        var requestedLeft = (int)Math.Floor(requestedBounds.Left * effectiveScale);
        var requestedTop = (int)Math.Floor(requestedBounds.Top * effectiveScale);
        var requestedRight = (int)Math.Ceiling(requestedBounds.Right * effectiveScale);
        var requestedBottom = (int)Math.Ceiling(requestedBounds.Bottom * effectiveScale);
        var left = QuantizeDown(requestedLeft);
        var top = QuantizeDown(requestedTop);
        var right = QuantizeUp(requestedRight);
        var bottom = QuantizeUp(requestedBottom);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var alignedBounds = new RectangleF(
            (float)(left / effectiveScale),
            (float)(top / effectiveScale),
            (float)(width / effectiveScale),
            (float)(height / effectiveScale));

        EnsureBitmap(width, height);
        if (Left != left || Top != top || Width != width || Height != height)
        {
            SetBounds(left, top, width, height);
        }

        using (var graphics = Graphics.FromImage(_bitmap!))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            draw(graphics, alignedBounds);
            graphics.ResetTransform();
        }

        var presented = LayeredWindowPresenter.TryPresent(
            Handle,
            _bitmap!,
            PointToScreen(Point.Empty),
            out diagnostic);
        if (presented && !Visible)
        {
            Show();
            BringToFront();
        }
        return presented;
    }

    internal void HideOverlay()
    {
        if (!IsDisposed && Visible)
        {
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            if (IsHandleCreated)
            {
                LayeredWindowPresenter.Release(Handle);
            }
        }
        base.Dispose(disposing);
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _bitmap.Width == width && _bitmap.Height == height)
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
    }

    private static int QuantizeDown(int value) =>
        (int)(Math.Floor(value / (double)BoundsQuantizationPixels) * BoundsQuantizationPixels);

    private static int QuantizeUp(int value) =>
        (int)(Math.Ceiling(value / (double)BoundsQuantizationPixels) * BoundsQuantizationPixels);
}
