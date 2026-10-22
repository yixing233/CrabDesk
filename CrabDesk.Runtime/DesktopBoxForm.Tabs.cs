using System.Drawing;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    internal const string ManualTabDragFormat = "CrabDesk.ManualTabDrag";
    internal sealed record ManualTabDrag(Guid BoxId, Guid TabId);
    private sealed record ManualTabDropPreview(Guid BoxId, int InsertionIndex);
    private ManualTabDrag? _pressedManualTab;
    private bool _manualTabDragActive;
    private ManualTabDropPreview? _manualTabDropPreview;

    private bool HandleManualTabMouseMove(PointF point, Forms.MouseButtons buttons)
    {
        if (_pressedManualTab is not { } tab) return false;
        if ((buttons & Forms.MouseButtons.Left) == 0)
        {
            _pressedManualTab = null;
            return false;
        }
        if (Math.Abs(point.X - _pressPoint.X) < 4 && Math.Abs(point.Y - _pressPoint.Y) < 4)
            return true;
        _pressedManualTab = null;
        var data = new Forms.DataObject();
        data.SetData(ManualTabDragFormat, false, tab);
        _manualTabDragActive = true;
        try
        {
            // A private OLE drag supplies Escape cancellation and cross-window
            // routing without exposing the label as a file to other programs.
            DoDragDrop(data, Forms.DragDropEffects.Move);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Manual tab drag failed", exception);
        }
        finally
        {
            _manualTabDragActive = false;
            ClearManualTabDropPreview();
            _dragCancelled = false;
            _dragDropCommitted = false;
            Capture = false;
        }
        return true;
    }

    private static bool TryGetManualTabDrag(Forms.DragEventArgs args, out ManualTabDrag drag)
    {
        drag = args.Data?.GetDataPresent(ManualTabDragFormat) == true
            ? args.Data.GetData(ManualTabDragFormat) as ManualTabDrag ?? null! : null!;
        return drag is not null;
    }

    private void UpdateManualTabDrop(Forms.DragEventArgs args, ManualTabDrag drag, bool commit)
    {
        EnsureGeometry();
        var point = ToDip(PointToClient(new Point(args.X, args.Y)));
        var box = _boxes.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if ((args.AllowedEffect & Forms.DragDropEffects.Move) == 0 ||
            box is null || box.Box.Id != drag.BoxId || box.Box.IsMappedFolder ||
            !box.Box.ManualTabs.Any(tab => tab.Id == drag.TabId) ||
            box.ManualTabs.Count < 2 || !box.TabBar.Contains(point))
        {
            args.Effect = Forms.DragDropEffects.None;
            ClearManualTabDropPreview();
            return;
        }

        var width = box.TabBar.Width / box.ManualTabs.Count;
        var position = (point.X - box.TabBar.X) / width;
        // The built-in All view always remains first. Insertion slots refer
        // only to the persisted list of user-created child tabs.
        var insertionIndex = Math.Clamp((int)Math.Floor(position + 0.5f) - 1,
            0, box.Box.ManualTabs.Count);
        args.Effect = Forms.DragDropEffects.Move;
        if (commit)
        {
            ClearManualTabDropPreview();
            _runtime.ReorderManualTab(drag.BoxId, drag.TabId, insertionIndex);
            _geometryDirty = true;
            InvalidateDip(box.TabBar);
            return;
        }
        var preview = new ManualTabDropPreview(box.Box.Id, insertionIndex);
        if (_manualTabDropPreview == preview) return;
        _manualTabDropPreview = preview;
        RequestManualTabRender(box.TabBar);
    }

    private void ClearManualTabDropPreview()
    {
        if (_manualTabDropPreview is not { } previous) return;
        _manualTabDropPreview = null;
        var box = _boxes.FirstOrDefault(candidate => candidate.Box.Id == previous.BoxId);
        if (box is not null) RequestManualTabRender(box.TabBar);
    }

    private void RequestManualTabRender(RectangleF bounds)
    {
        if (_isCompositedByIconSurface && _iconLayerPartialRenderRequest is not null)
            _iconLayerPartialRenderRequest(RectangleF.Inflate(bounds, 2, 2));
        else InvalidateDip(bounds);
    }

    private void DrawManualTabInsertion(Graphics graphics, BoxGeometry box, Color accent)
    {
        if (_manualTabDropPreview is not { } preview || preview.BoxId != box.Box.Id ||
            box.ManualTabs.Count == 0) return;
        var x = box.TabBar.Left + box.TabBar.Width / box.ManualTabs.Count * (preview.InsertionIndex + 1);
        x = Math.Clamp(x, box.TabBar.Left + 2, box.TabBar.Right - 2);
        using var pen = new Pen(accent, 2);
        graphics.DrawLine(pen, x, box.TabBar.Top + 3, x, box.TabBar.Bottom - 3);
    }
}
