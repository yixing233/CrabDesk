using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed partial class BoxTabDragTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TabSwitchUsesOneLocalRefreshAndHonorsAnimationPreference(bool enabled)
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Runtime.State.Settings.Appearance.AnimationEnabled = enabled;
            using var form = fixture.CreateForm();
            var refreshes = new List<RectangleF>();
            form.SetIconLayerPartialRenderRequest(refreshes.Add);
            Set(form, "_isCompositedByIconSurface", true);
            SelectTab(fixture, form, 1);

            Assert.Equal(fixture.Box.ManualTabs[0].Id, ActiveTabs(form)[fixture.Box.Id]);
            Assert.Equal(enabled ? 1 : 0, TabTransitions(form).Count);
            Assert.Single(refreshes);
            Assert.Equal(new RectangleF(8, 8, 564, 344), refreshes[0]);
            Assert.Equal(enabled, ((DesktopAnimationFrameClock)Get(form, "_animationFrameClock")!).Enabled);

            SelectTab(fixture, form, 1);
            Assert.Single(refreshes); // Clicking the selected tab does not restart motion.
        });
    }

    [Fact]
    public void RapidTabClicksContinueFromTheVisibleContentAndIndicator()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            using var form = fixture.CreateForm();
            SelectTab(fixture, form, 1);
            var first = TabTransitions(form)[fixture.Box.Id]!;
            SetTransitionTime(first, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            using var visible = (Bitmap)typeof(DesktopBoxForm)
                .GetMethod("CaptureTabContent", Hidden)!.Invoke(form, [fixture.Geometry(form), true])!;
            var indicator = TransitionProperty<RectangleF>(first, "FromIndicator");

            SelectTab(fixture, form, 2);

            var replacement = TabTransitions(form)[fixture.Box.Id]!;
            Assert.NotSame(first, replacement);
            Assert.Equal(indicator, TransitionProperty<RectangleF>(replacement, "FromIndicator"));
            Assert.Equal(TransitionPixels(visible), TransitionPixels(TransitionProperty<Bitmap>(replacement, "Outgoing")));
            Assert.Equal(fixture.Box.ManualTabs[1].Id, ActiveTabs(form)[fixture.Box.Id]);
        });
    }

    [Fact]
    public void SwitchingTabsStopsThePreviousViewsScrollAndClearsItsHover()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            using var form = fixture.CreateForm();
            var keyType = typeof(DesktopBoxForm).GetNestedType("ItemViewKey", BindingFlags.NonPublic)!;
            var key = Activator.CreateInstance(keyType, fixture.Box.Id, "manual:all")!;
            Invoke(form, "StartScrollAnimation", key, 0d, 90d);
            Set(form, "_hoveredItemKey", fixture.A.Key.ToString());

            SelectTab(fixture, form, 1);

            Assert.Null(Get(form, "_scrollAnimationKey"));
            Assert.Null(Get(form, "_hoveredItemKey"));
            Assert.Single(TabTransitions(form));
            Assert.True(((DesktopAnimationFrameClock)Get(form, "_animationFrameClock")!).Enabled);
        });
    }

    [Fact]
    public void DragPreviewSwitchesImmediatelyAndPreservesSelectedFiles()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            using var form = fixture.CreateForm();
            Selection(form).Add(fixture.A.Key.ToString());
            SelectTab(fixture, form, 2, preserveSelection: true);
            Assert.Empty(TabTransitions(form));
            Assert.Contains(fixture.A.Key.ToString(), Selection(form));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TabTransitionFinishesAndReleasesItsFrameClock(bool disableAnimations)
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            using var form = fixture.CreateForm();
            SelectTab(fixture, form, 1);
            var transition = TabTransitions(form)[fixture.Box.Id]!;
            if (disableAnimations) fixture.Runtime.State.Settings.Appearance.AnimationEnabled = false;
            else SetTransitionTime(transition, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
            Invoke(form, "OnAnimationFrame", form, EventArgs.Empty);

            Assert.Empty(TabTransitions(form));
            Assert.False(((DesktopAnimationFrameClock)Get(form, "_animationFrameClock")!).Enabled);
            Assert.Equal(fixture.Box.ManualTabs[0].Id, ActiveTabs(form)[fixture.Box.Id]);
        });
    }

    [Fact]
    public void MappedFolderCategoriesUseTheSameTransition()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            using var form = fixture.CreateForm();
            fixture.Box.ManualTabs.Clear();
            fixture.Box.MappedFolder = new() { Path = Path.GetTempPath(), EnableCategoryTabs = true };
            var items = new DesktopItemRef[]
            {
                new() { Key = new("path", "a.png"), ParsingName = "a.png", DisplayName = "a.png", Kind = DesktopItemKind.File },
                new() { Key = new("path", "b.txt"), ParsingName = "b.txt", DisplayName = "b.txt", Kind = DesktopItemKind.File }
            };
            ((Dictionary<Guid, IReadOnlyList<DesktopItemRef>>)Get(form, "_boxItems")!)[fixture.Box.Id] = items;
            var geometry = fixture.Geometry(form);
            var bar = (RectangleF)geometry.GetType().GetProperty("TabBar")!.GetValue(geometry)!;
            Invoke(form, "TrySelectBoxTab", geometry, new PointF(bar.Left + bar.Width / 2, bar.Top + 4), false);
            Assert.Single(TabTransitions(form));
        });
    }

    [Fact]
    public void TabTransitionPreviewRendersClippedFramesAndAnEmptyTarget()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Box.ManualTabs.Add(new() { Title = "Empty" });
            using var form = fixture.CreateForm();
            SelectTab(fixture, form, 1);
            using var sheet = new Bitmap(1120, 680, PixelFormat.Format32bppArgb);
            using var output = Graphics.FromImage(sheet);
            output.Clear(Color.FromArgb(28, 34, 46));
            var transition = TabTransitions(form)[fixture.Box.Id]!;
            for (var index = 0; index < 3; index++)
            {
                SetTransitionTime(transition, Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * index * 0.06));
                using var frame = new Bitmap(580, 360, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(frame))
                {
                    graphics.Clear(Color.Transparent);
                    form.RenderOnIconLayer(graphics, new RectangleF(0, 0, 580, 360));
                }
                Assert.Equal(0, frame.GetPixel(0, 0).A);
                output.DrawImage(frame, new Rectangle(index % 2 * 560, index / 2 * 340, 560, 340));
            }
            SelectTab(fixture, form, 3);
            Assert.Single(TabTransitions(form));
            using var empty = new Bitmap(580, 360, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(empty))
                form.RenderOnIconLayer(graphics, new RectangleF(0, 0, 580, 360));
            output.DrawImage(empty, new Rectangle(560, 340, 560, 340));
            sheet.Save(Path.Combine(Path.GetTempPath(), "CrabDesk-Tab-Transitions.png"), ImageFormat.Png);
        });
    }

    private static void SelectTab(Fixture fixture, DesktopBoxForm form, int index, bool preserveSelection = false)
    {
        var point = fixture.TabPoint(form, index);
        Invoke(form, "TrySelectBoxTab", fixture.Geometry(form), point, preserveSelection);
    }

    private static IDictionary TabTransitions(DesktopBoxForm form) => (IDictionary)Get(form, "_tabTransitions")!;
    private static T TransitionProperty<T>(object transition, string name) => (T)transition.GetType().GetProperty(name)!.GetValue(transition)!;
    private static void SetTransitionTime(object transition, long timestamp) => transition.GetType().GetProperty("StartedTimestamp")!.SetValue(transition, timestamp);
    private static int[] TransitionPixels(Bitmap bitmap) => Enumerable.Range(0, bitmap.Height)
        .SelectMany(y => Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, y).ToArgb())).ToArray();
}
