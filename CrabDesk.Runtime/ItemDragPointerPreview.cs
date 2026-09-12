using System.Drawing;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>
/// Owns the pointer image for the entire item OLE drag, independently of
/// the drop target. The image and grab offset are already in physical pixels.
/// </summary>
internal sealed class ItemDragPointerPreview : IDisposable
{
    [ThreadStatic]
    private static ItemDragPointerPreview? _active;
    private readonly Forms.Control _host;
    private readonly Bitmap _image;
    private readonly Point _grabOffset;
    private readonly DesktopDragOverlay _overlay;
    private readonly Forms.Timer _timer;
    private Point? _lastCursor;
    private bool _disposed;

    internal static bool IsActive => _active is not null;

    private ItemDragPointerPreview(Forms.Control host, Bitmap image, Point grabOffset)
    {
        _host = host;
        _image = image; // Borrowed; the drag source disposes it after the preview.
        _grabOffset = grabOffset;
        _overlay = new DesktopDragOverlay(host);
        _timer = new Forms.Timer { Interval = 16 };
        _timer.Tick += OnTick;
    }

    internal static ItemDragPointerPreview? TryCreate(Forms.Control host, Bitmap image, Point grabOffset)
    {
        var preview = new ItemDragPointerPreview(host, image, grabOffset);
        if (!preview.UpdateAtCursor())
        {
            preview.Dispose();
            return null;
        }
        _active = preview;
        preview._timer.Start();
        return preview;
    }

    internal static Rectangle CalculateBounds(Point cursor, Point grabOffset, Size imageSize) =>
        new(cursor.X - grabOffset.X, cursor.Y - grabOffset.Y, imageSize.Width, imageSize.Height);

    internal static void UpdateActiveAtCursor() => _active?.UpdateAtCursor();

    private void OnTick(object? sender, EventArgs args) => UpdateAtCursor();

    private bool UpdateAtCursor()
    {
        if (_disposed)
        {
            return false;
        }
        if (_host.IsDisposed || !_host.IsHandleCreated)
        {
            Dispose();
            return false;
        }
        try
        {
            var cursor = Forms.Cursor.Position;
            if (_lastCursor == cursor)
            {
                return true;
            }
            var bounds = CalculateBounds(cursor, _grabOffset, _image.Size);
            // No DIP rescaling here: using screen pixels preserves the grab
            // point across monitor boundaries, including negative coordinates.
            if (!_overlay.Present(bounds, 1d, (graphics, aligned) =>
                graphics.DrawImageUnscaled(_image,
                    bounds.X - (int)aligned.X, bounds.Y - (int)aligned.Y), out var diagnostic, screenCoordinates: true))
            {
                DiagnosticLog.Info($"Box drag preview unavailable: {diagnostic}");
                Dispose();
                return false;
            }
            _lastCursor = cursor;
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Box drag preview failed", exception);
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(_active, this)) _active = null;
        _timer.Stop();
        _timer.Dispose();
        _overlay.Dispose();
    }
}
