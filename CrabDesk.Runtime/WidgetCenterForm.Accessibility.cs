using System.Drawing;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class WidgetCenterForm
{
    protected override Forms.AccessibleObject CreateAccessibilityInstance() => new CenterAccessibleObject(this);

    private sealed class CenterAccessibleObject(WidgetCenterForm center) : Forms.Control.ControlAccessibleObject(center)
    {
        public override int GetChildCount() => 1;
        public override Forms.AccessibleObject? GetChild(int index) => index == 0 ? new HeaderAccessibleObject(center, this) : null;
    }

    private sealed class HeaderAccessibleObject(WidgetCenterForm center, Forms.AccessibleObject parent) : Forms.AccessibleObject
    {
        public override string? Name { get => center._pickerVisible ? "返回组件" : "添加组件"; set { } }
        public override Forms.AccessibleRole Role => Forms.AccessibleRole.PushButton;
        public override Forms.AccessibleStates State => Forms.AccessibleStates.Focusable |
            (center._headerFocus == 0 ? Forms.AccessibleStates.Focused : Forms.AccessibleStates.None);
        public override string? DefaultAction => "按下";
        public override Forms.AccessibleObject? Parent => parent;
        public override Rectangle Bounds
        {
            get
            {
                var width = (float)(center.ClientSize.Width / center._scale);
                var bounds = WidgetCenterChrome.AddBounds(width);
                return new(center.PointToScreen(new(center.ToPixel(bounds.X), center.ToPixel(bounds.Y))),
                    new Size(center.ToPixel(bounds.Width), center.ToPixel(bounds.Height)));
            }
        }
        public override void DoDefaultAction() => center.TogglePicker();
    }
}
