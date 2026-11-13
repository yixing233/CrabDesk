using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetCenterSurfaceTests
{
    [Fact]
    public void CenterLyricsMenuUsesTheCenterThemeAndKeepsSavedDesktopOptions() => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var widget = Add(fixture.Runtime, DesktopWidgetKind.Lyrics, new(800, 400, 320, 220));
        widget.LyricsTransparentBackground = true;
        widget.LyricsLightText = false;
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        using var menu = new FluentContextMenuStrip();
        Call(center, "AddLyricsContextMenu", menu, widget);
        Assert.DoesNotContain(menu.Items.Cast<Forms.ToolStripItem>(), item => item.Text is "透明背景" or "文字颜色");
        Assert.Contains(menu.Items.Cast<Forms.ToolStripItem>(), item => item.Text == "逐字覆盖动画");
        Assert.True(widget.LyricsTransparentBackground);
        Assert.False(widget.LyricsLightText);
    });

    [Theory]
    [InlineData(DesktopWidgetKind.Clock)]
    [InlineData(DesktopWidgetKind.Calendar)]
    [InlineData(DesktopWidgetKind.Note)]
    [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Translation)]
    [InlineData(DesktopWidgetKind.Weather)]
    [InlineData(DesktopWidgetKind.Performance)]
    [InlineData(DesktopWidgetKind.Network)]
    [InlineData(DesktopWidgetKind.ControlCenter)]
    [InlineData(DesktopWidgetKind.Lyrics)]
    [InlineData(DesktopWidgetKind.FileShelf)]
    public void ChangingCenterThemeRepaintsPanelAndActualCardsWithoutChangingDesktopState(DesktopWidgetKind kind) => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var runtime = fixture.Runtime;
        var widget = Add(runtime, kind, new(800, 400, 320, 220));
        widget.BackgroundMaterial = WidgetBackgroundMaterial.Acrylic;
        widget.ColorTheme = WidgetColorTheme.Signature;
        widget.LyricsTransparentBackground = true;
        var saved = System.Text.Json.JsonSerializer.Serialize(widget);
        using var center = new WidgetCenterForm(runtime, runtime.Monitors[0]);
        typeof(CrabDeskRuntime).GetField("_widgetCenter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, center);
        using var bitmap = new Bitmap(700, 800, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        foreach (var mode in new[] { ApplicationThemeMode.Dark, ApplicationThemeMode.Light, ApplicationThemeMode.Dark })
        {
            runtime.SetWidgetCenterThemeMode(mode);
            var dark = mode == ApplicationThemeMode.Dark;
            Assert.Equal(dark ? Color.FromArgb(31, 34, 40) : Color.FromArgb(248, 249, 252), center.BackColor);
            var surface = (WidgetSurfaceAppearance)Call(center, "ResolveWidgetSurface", widget)!;
            var expected = dark ? kind == DesktopWidgetKind.Calendar ? "#FF000000" : "#FF1C1C1E" : "#FFFFFFFF";
            Assert.Equal(expected, surface.Background);
            graphics.Clear(Color.Transparent);
            Call(center, "DrawWidget", graphics, widget);
            var bounds = center.WidgetViewBounds[widget.Id];
            Assert.Equal(ColorTranslator.FromHtml("#" + expected[3..]).ToArgb(),
                bitmap.GetPixel((int)bounds.X + 2, (int)(bounds.Y + bounds.Height / 2)).ToArgb());
            Assert.Equal(saved, System.Text.Json.JsonSerializer.Serialize(widget));
        }
        Assert.False(center.IsHandleCreated);
    });

    [Fact]
    public void CenterUsesSharedRealSurfaceButIsNotADesktopForm()
    {
        Assert.Equal(typeof(WidgetSurfaceController), typeof(WidgetCenterForm).BaseType);
        Assert.Equal(typeof(WidgetSurfaceController), typeof(DesktopBoxForm).BaseType);
        Assert.False(typeof(DesktopBoxForm).IsAssignableFrom(typeof(WidgetCenterForm)));
        foreach (var name in new[] { "DrawWidget", "HandleWidgetClick", "BeginWidgetNoteInput", "BeginWidgetTodoInput", "BeginWidgetTranslationInput", "UpdateControlCenterDrag", "DrawLyricsWidget" })
            Assert.Equal(typeof(WidgetSurfaceController), typeof(DesktopBoxForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        foreach (var name in new[] { "_noteEditingWidget", "_todoEditingWidget", "_widgetScroll", "_controlPressedWidgetId", "_widgetTimer" })
            Assert.NotNull(typeof(DesktopBoxForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic));
        foreach (var name in new[] { "NoteFieldRectangle", "WidgetBodyRectangle", "WidgetText", "DrawTranslationField" })
            Assert.NotNull(typeof(DesktopBoxForm).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
    }

    [Fact]
    public void CenterLayoutsAndHitTestsOriginalModelsWithoutTouchingDesktopBounds() => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var runtime = fixture.Runtime;
        var note = Add(runtime, DesktopWidgetKind.Note, new(720, 360, 320, 200));
        var desktop = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, MonitorId = runtime.Monitors[0].Id, Bounds = new(16, 80, 180, 180) };
        runtime.State.Widgets.Add(desktop);
        var original = note.Bounds;
        using var center = new WidgetCenterForm(runtime, runtime.Monitors[0]);
        var host = (IWidgetHostContext)center;
        Assert.Same(note, Assert.Single(host.Widgets));
        var view = host.GetViewBounds(note);
        Assert.NotEqual(original, view);
        Assert.Same(note, Call(center, "WidgetAt", new PointF((float)view.X + 12, (float)view.Y + 12)));
        Assert.Equal(original, note.Bounds);
        Assert.Equal(runtime.Monitors[0].Id, note.MonitorId);
        Assert.False(center.IsHandleCreated); // No Explorer attachment or top-level presentation.
    });

    [Fact]
    public void ViewScopesNestAndRestoreTheExistingStaticDesktopApi() => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var widget = Add(fixture.Runtime, DesktopWidgetKind.Note, new(900, 300, 320, 200));
        using var first = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        using var second = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        second.ClientSize = new(360, 420);
        var desktop = DesktopBoxForm.WidgetRectangle(widget);
        using (first.EnterWidgetView())
        {
            var view = DesktopBoxForm.WidgetRectangle(widget);
            Assert.Equal(((IWidgetHostContext)first).GetViewBounds(widget).X, view.X);
            using (second.EnterWidgetView())
                Assert.Equal(((IWidgetHostContext)second).GetViewBounds(widget).Width, DesktopBoxForm.WidgetRectangle(widget).Width);
            Assert.Equal(view, DesktopBoxForm.WidgetRectangle(widget));
        }
        Assert.Equal(desktop, DesktopBoxForm.WidgetRectangle(widget));
    });

    [Theory]
    [InlineData(DesktopWidgetKind.Note)]
    [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Translation)]
    [InlineData(DesktopWidgetKind.FileShelf)]
    [InlineData(DesktopWidgetKind.Performance)]
    [InlineData(DesktopWidgetKind.Network)]
    [InlineData(DesktopWidgetKind.Lyrics)]
    public void ActualCardsRenderOpaqueWithoutMutatingSavedMaterial(DesktopWidgetKind kind) => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var runtime = fixture.Runtime;
        runtime.State.Settings.Appearance.UseAcrylicBoxes = true;
        var widget = Add(runtime, kind, new(800, 400, 320, 220));
        widget.BackgroundMaterial = WidgetBackgroundMaterial.Acrylic;
        widget.LyricsTransparentBackground = true;
        widget.Text = "真实组件内容";
        widget.Translation = "Actual component content";
        widget.Tasks.Add(new WidgetTodoItem { Text = "真实待办" });
        var original = widget.Bounds;
        using var center = new WidgetCenterForm(runtime, runtime.Monitors[0]);
        using var bitmap = new Bitmap(700, 800, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        Call(center, "DrawWidget", graphics, widget);
        var bounds = ((IWidgetHostContext)center).GetViewBounds(widget);
        Assert.Equal(255, bitmap.GetPixel((int)bounds.X + 2, (int)(bounds.Y + bounds.Height / 2)).A);
        var appearance = (WidgetSurfaceAppearance)Call(center, "ResolveWidgetSurface", widget)!;
        Assert.False(appearance.Acrylic);
        Assert.Equal(1, appearance.Opacity);
        Assert.Null(appearance.GradientEnd);
        Assert.Equal(WidgetBackgroundMaterial.Acrylic, widget.BackgroundMaterial);
        Assert.True(widget.LyricsTransparentBackground);
        Assert.Equal(original, widget.Bounds);
    });

    [Fact]
    public void CenterPressDoesNotCaptureDesktopDragOrCommitPosition() => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var widget = Add(fixture.Runtime, DesktopWidgetKind.Lyrics, new(700, 300, 320, 200));
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        var original = widget.Bounds;
        var view = ((IWidgetHostContext)center).GetViewBounds(widget);
        var point = new PointF((float)view.X + 50, (float)view.Y + 12);
        Call(center, "HandleWidgetMouseDown", new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, (int)point.X, (int)point.Y, 0), point);
        Assert.False((bool)typeof(WidgetSurfaceController).GetField("_widgetCanDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(center)!);
        Call(center, "FinishWidgetPress", true);
        Assert.Equal(original, widget.Bounds);
    });

    [Fact]
    public void CenterRefreshKeepsContentScrollAndOriginalReferences() => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var note = Add(fixture.Runtime, DesktopWidgetKind.Note, new(800, 300, 320, 600));
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.ClientSize = new(420, 300);
        center.SetScrollOffset(72);
        var fields = (Dictionary<Guid, float>)typeof(WidgetSurfaceController).GetField("_widgetScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(center)!;
        fields[note.Id] = 38;
        var before = center.WidgetViewBounds[note.Id];
        for (var index = 0; index < 10; index++) center.RefreshWidgets();
        Assert.Equal(72, center.ScrollOffset);
        Assert.Equal(38, fields[note.Id]);
        Assert.Equal(before, center.WidgetViewBounds[note.Id]);
        Assert.Same(note, Assert.Single(((IWidgetHostContext)center).Widgets));
        Assert.False(((Forms.Timer)typeof(WidgetSurfaceController).GetField("_widgetTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(center)!).Enabled);
        Assert.False(center.IsHandleCreated);
    });

    [Theory]
    [InlineData("")]
    [InlineData("A short note")]
    public void WheelOverAShortNoteScrollsTheCenter(string text) => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var note = Add(fixture.Runtime, DesktopWidgetKind.Note, new(0, 0, 372, 180));
        note.Text = text;
        Add(fixture.Runtime, DesktopWidgetKind.Clock, new(0, 0, 180, 180));
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.ClientSize = new(416, 300);
        using var frame = new Bitmap(416, 300);
        using var graphics = Graphics.FromImage(frame);
        Call(center, "DrawWidget", graphics, note);
        Wheel(center, -120);
        Assert.Equal(60, center.ScrollOffset);
        Assert.Equal(0, NoteScroll(center, note));
        Wheel(center, 120);
        Assert.Equal(0, center.ScrollOffset);
    });

    [Fact]
    public void WheelOverALongNoteScrollsItsBodyAndHandsOffAtBothEnds() => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var note = Add(fixture.Runtime, DesktopWidgetKind.Note, new(0, 0, 372, 180));
        note.Text = string.Join("\n", Enumerable.Repeat("A longer note line", 40));
        Add(fixture.Runtime, DesktopWidgetKind.Clock, new(0, 0, 180, 180));
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.ClientSize = new(416, 300);
        using var frame = new Bitmap(416, 300);
        using var graphics = Graphics.FromImage(frame);
        Call(center, "DrawWidget", graphics, note);
        Wheel(center, -120);
        Assert.Equal(0, center.ScrollOffset);
        Assert.Equal(36, NoteScroll(center, note));
        for (var i = 0; i < 100 && center.ScrollOffset == 0; i++) Wheel(center, -120);
        Assert.Equal(60, center.ScrollOffset);
        for (var i = 0; i < 100 && NoteScroll(center, note) > 0; i++) Wheel(center, 120);
        Assert.Equal(60, center.ScrollOffset);
        Assert.Equal(0, NoteScroll(center, note));
        Wheel(center, 120);
        Assert.Equal(0, center.ScrollOffset);
    });

    [Fact]
    public void CenterChromeKeepsAccessibleActionsWithoutNativeButtonsOrScrollbars() => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        Assert.DoesNotContain(center.Controls.Cast<Forms.Control>(), control => control is Forms.Button or Forms.ScrollBar);
        var actions = center.AccessibilityObject;
        Assert.Equal(1, actions.GetChildCount());
        Assert.Equal("添加组件", actions.GetChild(0)!.Name);
        Assert.Equal(Forms.AccessibleRole.PushButton, actions.GetChild(0)!.Role);
        Assert.Null(actions.GetChild(1));
        actions.GetChild(0)!.DoDefaultAction();
        Assert.Equal("返回组件", actions.GetChild(0)!.Name);
    });

    [Fact]
    public void PickerDistinguishesAddMoveAndAlreadyAddedWithoutCloning()
    {
        var desktop = new DesktopWidget { Kind = DesktopWidgetKind.Note };
        var center = new DesktopWidget { Kind = DesktopWidgetKind.Todo, Placement = WidgetPlacement.Center };
        var items = WidgetCenterPicker.GetItems([desktop, center]);
        Assert.Equal(Enum.GetValues<DesktopWidgetKind>().Length, items.Count);
        Assert.Equal(WidgetCenterPickerStatus.NotAdded, items.Single(item => item.Kind == DesktopWidgetKind.Translation).Status);
        Assert.Equal(WidgetCenterPickerStatus.MoveToCenter, items.Single(item => item.Kind == desktop.Kind).Status);
        Assert.Same(desktop, items.Single(item => item.Kind == desktop.Kind).Widget);
        Assert.Equal(WidgetCenterPickerStatus.Added, items.Single(item => item.Kind == center.Kind).Status);
    }

    [Fact]
    public void PanelAnchorsToRightEdgeAndIgnoresOldFreeWindowCoordinates()
    {
        var monitor = new MonitorLayout { Id = "left", DeviceName = "left", DpiScale = 1.5,
            Bounds = new(-1280, 0, 1280, 720), WorkArea = new(-1280, 20, 1280, 680),
            PixelBounds = new(-1920, 0, 1920, 1080), PixelWorkArea = new(-1920, 30, 1920, 1020) };
        var settings = new WidgetCenterSettings { Width = 600, Height = 500 };
        var expected = new Rectangle(-642, 48, 624, 984);
        Assert.Equal(expected, WidgetCenterForm.ResolvePixelBounds(settings, monitor));
        settings.HasPosition = true; settings.X = -1000; settings.Y = 100;
        Assert.Equal(expected, WidgetCenterForm.ResolvePixelBounds(settings, monitor));
        settings.X = 400; settings.Y = 2000;
        Assert.Equal(expected, WidgetCenterForm.ResolvePixelBounds(settings, monitor));
    }

    [Fact]
    public void CompactPanelFitsTwoSmallCardsAndWideCardsWithoutScrollbarOverlap() => RunSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        var runtime = fixture.Runtime;
        var calendar = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, Placement = WidgetPlacement.Center,
            Size = DesktopWidgetSize.Medium, CenterOrder = 0, MonitorId = runtime.Monitors[0].Id };
        var clock = new DesktopWidget { Kind = DesktopWidgetKind.Clock, Placement = WidgetPlacement.Center,
            Size = DesktopWidgetSize.Medium, CenterOrder = 1, MonitorId = runtime.Monitors[0].Id };
        var note = new DesktopWidget { Kind = DesktopWidgetKind.Note, Placement = WidgetPlacement.Center,
            Size = DesktopWidgetSize.Large, CenterOrder = 2, MonitorId = runtime.Monitors[0].Id, Bounds = new(40, 50, 372, 180) };
        runtime.State.Widgets.AddRange([calendar, clock, note]);
        var original = note.Bounds;
        using var center = new WidgetCenterForm(runtime, runtime.Monitors[0]);
        center.ClientSize = new(416, 700);
        var first = center.WidgetViewBounds[calendar.Id];
        var second = center.WidgetViewBounds[clock.Id];
        var wide = center.WidgetViewBounds[note.Id];
        Assert.Equal(first.Y, second.Y);
        Assert.Equal(180, first.Width);
        Assert.Equal(180, second.Width);
        Assert.Equal(first.X + first.Width + 16, second.X);
        Assert.Equal(372, wide.Width);
        Assert.Equal(first.X, wide.X);
        Assert.True(wide.Y >= first.Y + first.Height + 16);
        var track = WidgetCenterChrome.ScrollTrack(416, 700);
        Assert.True(second.X + second.Width <= track.Left);
        Assert.True(wide.X + wide.Width <= track.Left);
        Assert.Equal(original, note.Bounds);
    });

    [Theory]
    [InlineData(1, 416)]
    [InlineData(1.25, 520)]
    [InlineData(1.5, 624)]
    public void CompactPanelUsesTheSameLogicalWidthAcrossDpi(double scale, int expectedWidth)
    {
        var monitor = new MonitorLayout { Id = "compact", DeviceName = "compact", DpiScale = scale,
            Bounds = new(0, 0, 1600, 900), WorkArea = new(0, 0, 1600, 850),
            PixelBounds = new(0, 0, 2400, 1350), PixelWorkArea = new(0, 0, 2400, 1300) };
        Assert.Equal(expectedWidth, WidgetCenterForm.ResolvePixelBounds(new(), monitor).Width);
    }

    [Fact]
    public void PanelMotionSlidesFromRightWithoutChangingOpacity()
    {
        Assert.Equal(600, WidgetCenterForm.PanelMotionOffset(600, 0, 0));
        Assert.Equal(75, WidgetCenterForm.PanelMotionOffset(600, 0, .5));
        Assert.Equal(0, WidgetCenterForm.PanelMotionOffset(600, 0, 1));
        Assert.Equal(525, WidgetCenterForm.PanelMotionOffset(0, 600, .5));
        Assert.Equal(600, WidgetCenterForm.PanelMotionOffset(0, 600, 2));
    }

    [Theory]
    [InlineData(true, false, false, 2u, 1u, true)]
    [InlineData(true, true, false, 2u, 1u, false)]
    [InlineData(true, false, true, 2u, 1u, false)]
    [InlineData(true, false, false, 1u, 1u, false)]
    [InlineData(true, false, false, 0u, 1u, false)]
    [InlineData(false, false, false, 2u, 1u, false)]
    public void OwnedEditorsMenusPickersAndSameProcessWinUIAreNotOutsideClicks(bool visible, bool busy, bool owner, uint process, uint host, bool expected) =>
        Assert.Equal(expected, WidgetCenterForm.ShouldDismissForActivation(visible, busy, owner, process, host));

    private static DesktopWidget Add(CrabDeskRuntime runtime, DesktopWidgetKind kind, LayoutRect bounds)
    {
        var widget = new DesktopWidget { Kind = kind, Placement = WidgetPlacement.Center, Size = DesktopWidgetSize.Custom, MonitorId = runtime.Monitors[0].Id, Bounds = bounds, CenterOrder = runtime.State.Widgets.Count };
        runtime.State.Widgets.Add(widget);
        return widget;
    }
    private static object? Call(object instance, string name, params object[] args) =>
        typeof(WidgetSurfaceController).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static float NoteScroll(WidgetCenterForm center, DesktopWidget note) =>
        ((Dictionary<Guid, float>)typeof(WidgetSurfaceController).GetField("_widgetScroll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(center)!).GetValueOrDefault(note.Id);
    private static void Wheel(WidgetCenterForm center, int delta) =>
        typeof(WidgetCenterForm).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(center, [new Forms.MouseEventArgs(Forms.MouseButtons.None, 0, 48, 150, delta)]);
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
