using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopHoverFramePerformanceTests
{
    [Fact]
    public void RapidHoverAcrossSixBoxesDoesNotPaintInsideEachMouseMessage()
    {
        OnSta(() =>
        {
            using var scene = new Scene();
            var started = Stopwatch.StartNew();
            for (var step = 0; step < 48; step++) scene.Hover(step % 6);
            var elapsed = started.Elapsed.TotalMilliseconds;
            Assert.True(scene.Presents == 0,
                $"48 hover messages synchronously painted {scene.Presents} full frames in {elapsed:F2}ms.");
            Call(scene.Form, "FlushQueuedVisualFrame");
            Assert.Equal(1, scene.Presents);
            Assert.Equal(scene.State.Boxes[5].Id, Get<Guid?>(scene.Form, "_hoveredBoxId"));
            Assert.Equal(scene.State.Boxes[5].Id, Get<Guid?>(scene.Form, "_focusedBoxId"));
        });
    }

    private sealed class Scene : IDisposable
    {
        internal readonly CrabDeskState State = new();
        internal readonly DesktopBoxForm Form;
        private readonly Forms.Form _parent = new();
        internal int Presents;
        internal Bitmap? LastFrame;
        internal Scene()
        {
            var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
            var monitor = new MonitorLayout
            {
                Id = "hover-test", DeviceName = "hover-test", DpiScale = 1,
                Bounds = new(0, 0, 800, 600), WorkArea = new(0, 0, 800, 600),
                PixelBounds = new(0, 0, 800, 600), PixelWorkArea = new(0, 0, 800, 600)
            };
            State.Settings.Appearance.AnimationEnabled = false;
            for (var i = 0; i < 6; i++)
                State.Boxes.Add(new DesktopBox
                {
                    Title = $"Box {i}", MonitorId = monitor.Id,
                    Bounds = new(20 + (i % 3) * 220, 20 + (i / 3) * 200, 180, 160)
                });
            Set(runtime, "<State>k__BackingField", State);
            Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
            Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
            Form = new DesktopBoxForm(runtime, monitor);
            Form.AttachToDesktop(_parent.Handle);
            Form.SetAcrylicBackground(true);
            Form.SetAcrylicFramePresenter((bitmap, _, _) => { Presents++; LastFrame = bitmap; });
            Assert.True(Form.RefreshWorkspace(), Form.LayerDiagnostic);
            Presents = 0;
        }
        internal void Hover(int box)
        {
            var bounds = State.Boxes[box].Bounds;
            Call(Form, "OnMouseMove", null!, new Forms.MouseEventArgs(Forms.MouseButtons.None, 0,
                (int)bounds.X + 80, (int)bounds.Y + 16, 0));
        }
        public void Dispose() { Form.Dispose(); _parent.Dispose(); }
    }

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args)
    {
        var method = args.Length == 0
            ? target.GetType().GetMethod(name, Hidden, null, Type.EmptyTypes, null)
            : target.GetType().GetMethod(name, Hidden, null, new[] { typeof(object), typeof(Forms.MouseEventArgs) }, null);
        return method!.Invoke(target, args);
    }
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
