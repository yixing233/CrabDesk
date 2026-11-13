using System.Drawing;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>The view boundary; it never replaces or edits the persisted widget model.</summary>
internal interface IWidgetHostContext
{
    IReadOnlyList<DesktopWidget> Widgets { get; }
    LayoutRect GetViewBounds(DesktopWidget widget);
    double DpiScale { get; }
    MonitorLayout Monitor { get; }
    Forms.Form Owner { get; }
    Point ViewToScreen(PointF point);
    bool OwnsWidget(DesktopWidget widget);
    void RequestRedraw();
}
