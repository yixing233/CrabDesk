using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class BackupBoxRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupLayerPaintsExpandedAndCollapsedBoxesAtTheirMonitorPosition(bool collapsed)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
                var monitor = new MonitorLayout
                {
                    Id = "backup-test", DeviceName = "backup-test", DpiScale = 1.25,
                    Bounds = new(800, 0, 800, 600), WorkArea = new(800, 0, 800, 600),
                    PixelBounds = new(1000, 0, 1000, 750), PixelWorkArea = new(1000, 0, 1000, 750)
                };
                var state = new CrabDeskState();
                state.Settings.Appearance.CornerRadius = 0;
                state.Settings.Appearance.ShowBorder = false;
                state.Boxes.Add(new DesktopBox
                {
                    Title = "备份盒子", MonitorId = monitor.Id,
                    Bounds = new(100, 100, 200, 160), ExpandOnHover = collapsed
                });
                Set(runtime, "<State>k__BackingField", state);
                Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
                Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
                using var form = new DesktopBoxForm(runtime, monitor);
                form.SetAcrylicBackground(true);
                using var bitmap = DesktopLayerBitmapFactory.Create(2000, 750);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Transparent);
                var originalTransform = graphics.Transform.Elements;

                form.RenderBackupLayer(graphics, new LayoutRect(0, 0, 2000, 750));

                Assert.True(bitmap.GetPixel(1140, 135).A > 0);
                Assert.Equal(0, bitmap.GetPixel(1100, 135).A);
                Assert.Equal(collapsed ? 0 : 1, bitmap.GetPixel(1140, 250).A > 0 ? 1 : 0);
                Assert.Equal(originalTransform, graphics.Transform.Elements);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
