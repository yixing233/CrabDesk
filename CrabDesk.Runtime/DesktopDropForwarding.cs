using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>
/// A per-monitor desktop surface that owns drag-and-drop handling for the
/// desktop area, reachable by a window that receives the OLE events on its
/// behalf.
/// </summary>
internal interface IDesktopDropForwardTarget
{
    bool ContainsScreenPixel(Point screenPixel);
    void ForwardDragOver(Forms.DragEventArgs eventArgs);
    void ForwardDragLeave();
    void ForwardDragDrop(Forms.DragEventArgs eventArgs);
}

/// <summary>
/// Routes the OLE drag events a covering top-level window receives to the
/// desktop surface under the pointer.
/// </summary>
/// <remarks>
/// The acrylic host is a top-level window ordered directly above Explorer's
/// desktop. It answers <c>WM_NCHITTEST</c> with <c>HTTRANSPARENT</c>, which is
/// enough for mouse input and for drags CrabDesk starts itself: hit testing
/// defers to the next window on the same thread, i.e. the icon surface. An OLE
/// drag started by Explorer or any other process runs <c>WindowFromPoint</c>
/// on that process's thread, where the transparency answer is never consulted;
/// the drag lands on the host, and without a registered drop target the source
/// shows the no-drop cursor. The host therefore registers itself and hands the
/// events to the surface that would have received them.
/// </remarks>
internal sealed class DesktopDropForwarder
{
    private readonly Func<Point, IDesktopDropForwardTarget?> _resolve;
    private IDesktopDropForwardTarget? _current;

    internal DesktopDropForwarder(Func<Point, IDesktopDropForwardTarget?> resolve)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    /// <summary>The surface that received the last DragEnter/DragOver, if any.</summary>
    internal IDesktopDropForwardTarget? Current => _current;

    internal static bool ContainsPixel(LayoutRect bounds, Point pixel) =>
        pixel.X >= bounds.X &&
        pixel.X < bounds.X + bounds.Width &&
        pixel.Y >= bounds.Y &&
        pixel.Y < bounds.Y + bounds.Height;

    internal void DragOver(Forms.DragEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        var target = _resolve(new Point(eventArgs.X, eventArgs.Y));
        if (!ReferenceEquals(target, _current))
        {
            // Crossing from one monitor's surface to another: the previous one
            // never gets an OLE DragLeave of its own, so send it here.
            _current?.ForwardDragLeave();
            _current = target;
        }

        if (target is null)
        {
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }

        target.ForwardDragOver(eventArgs);
    }

    internal void DragLeave()
    {
        var current = _current;
        _current = null;
        current?.ForwardDragLeave();
    }

    internal void DragDrop(Forms.DragEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        var target = _resolve(new Point(eventArgs.X, eventArgs.Y)) ?? _current;
        var previous = _current;
        _current = null;
        if (previous is not null && !ReferenceEquals(previous, target))
        {
            previous.ForwardDragLeave();
        }

        if (target is null)
        {
            eventArgs.Effect = Forms.DragDropEffects.None;
            return;
        }

        target.ForwardDragDrop(eventArgs);
    }
}
