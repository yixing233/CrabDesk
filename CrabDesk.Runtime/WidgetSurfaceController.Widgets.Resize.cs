using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal abstract partial class WidgetSurfaceController
{
    protected WidgetResizeEdges _widgetResizeEdges;
    protected bool _widgetResizing;

    protected static Forms.Cursor WidgetResizeCursor(WidgetResizeEdges edges) => edges switch
    {
        WidgetResizeEdges.Left or WidgetResizeEdges.Right => Forms.Cursors.SizeWE,
        WidgetResizeEdges.Top or WidgetResizeEdges.Bottom => Forms.Cursors.SizeNS,
        WidgetResizeEdges.TopLeft or WidgetResizeEdges.BottomRight => Forms.Cursors.SizeNWSE,
        WidgetResizeEdges.TopRight or WidgetResizeEdges.BottomLeft => Forms.Cursors.SizeNESW,
        _ => Forms.Cursors.Default
    };

}
