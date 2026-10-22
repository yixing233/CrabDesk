using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Moq;
using Xunit;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

public sealed class BoxTabDragTests
{
    [Fact]
    public void HoveringAnotherTabPreservesTheSelectionAndDroppingInItsBodyMovesTheWholeGroup()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            var (runtime, box, a, b, c, _) = fixture;
            using var form = fixture.CreateForm();
            ActiveTabs(form)[box.Id] = box.ManualTabs[0].Id;
            Selection(form).UnionWith([a.Key.ToString(), b.Key.ToString()]);
            Set(form, "_pressedItem", a);
            Set(form, "_pressedBoxId", box.Id);
            var data = ItemDrag(box.Id, a, b);
            var over = Args(form, data, fixture.TabPoint(form, 2));

            Invoke(form, "OnDragOver", form, over);

            Assert.Equal(box.ManualTabs[1].Id, ActiveTabs(form)[box.Id]);
            Assert.Equal(2, Selection(form).Count);
            Assert.Same(a, Get(form, "_pressedItem"));
            Assert.Equal(box.ManualTabs[0].Id, box.ItemTabAssignments[a.Key.ToString()]);
            var itemBounds = fixture.ItemBounds(form, c.Key.ToString());
            var drop = Args(form, data, new PointF(itemBounds.X + 2, itemBounds.Y + 2));
            var changes = 0;
            runtime.Changed += (_, _) => changes++;

            Invoke(form, "OnDragDrop", form, drop);

            Assert.Equal(box.ManualTabs[1].Id, box.ItemTabAssignments[a.Key.ToString()]);
            Assert.Equal(box.ManualTabs[1].Id, box.ItemTabAssignments[b.Key.ToString()]);
            Assert.Equal(new[] { a, b, c, fixture.D }.Select(item => item.Key.ToString()), box.ItemOrder);
            Assert.Equal(1, changes);
            Assert.All(new[] { a, b }, item => Assert.Equal(box.Id, runtime.State.Assignments[item.Key.ToString()]));
            fixture.Operations.VerifyNoOtherCalls();
        });
    }

    [Fact]
    public void LeavingATabPreviewWithoutDroppingDoesNotMoveFiles()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            using var form = fixture.CreateForm();
            ActiveTabs(form)[fixture.Box.Id] = fixture.Box.ManualTabs[0].Id;
            var data = ItemDrag(fixture.Box.Id, fixture.A);
            var before = fixture.Box.ItemTabAssignments.ToArray();

            Invoke(form, "OnDragOver", form, Args(form, data, fixture.TabPoint(form, 2)));
            Invoke(form, "OnDragLeave", form, EventArgs.Empty);

            Assert.Equal(before, fixture.Box.ItemTabAssignments.ToArray());
            fixture.Operations.VerifyNoOtherCalls();
        });
    }

    [Fact]
    public void AGroupCanBeDroppedIntoAnEmptyChildTabBody()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Runtime.MoveItemsToManualTab(fixture.Box.Id,
                [fixture.C.Key.ToString(), fixture.D.Key.ToString()], fixture.Box.ManualTabs[0].Id);
            using var form = fixture.CreateForm();
            var data = ItemDrag(fixture.Box.Id, fixture.A, fixture.B);
            Invoke(form, "OnDragOver", form, Args(form, data, fixture.TabPoint(form, 2)));
            var geometry = fixture.Geometry(form);
            var body = (RectangleF)geometry.GetType().GetProperty("Body")!.GetValue(geometry)!;

            Invoke(form, "OnDragDrop", form, Args(form, data, new PointF(body.X + 10, body.Y + 10)));

            Assert.Equal(fixture.Box.ManualTabs[1].Id, fixture.Box.ItemTabAssignments[fixture.A.Key.ToString()]);
            Assert.Equal(fixture.Box.ManualTabs[1].Id, fixture.Box.ItemTabAssignments[fixture.B.Key.ToString()]);
        });
    }

    [Fact]
    public async Task ReorderedChildTabsAndTheirFileAssignmentsSurviveSavingAndReloading()
    {
        using var fixture = new Fixture();
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.TabPersistenceTests", Guid.NewGuid().ToString("N"));
        var store = new JsonLayoutStore(root);
        try
        {
            var firstTab = fixture.Box.ManualTabs[0].Id;
            Assert.True(fixture.Runtime.ReorderManualTab(fixture.Box.Id, firstTab, 2));
            await store.SaveAsync(fixture.Runtime.State);
            var loaded = await store.LoadAsync();
            var box = Assert.Single(loaded.Boxes);
            Assert.Equal("BA", string.Concat(box.ManualTabs.Select(tab => tab.Title)));
            Assert.Equal(firstTab, box.ItemTabAssignments[fixture.A.Key.ToString()]);
            Assert.Equal(fixture.Box.Id, loaded.Assignments[fixture.A.Key.ToString()]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DroppingOnAllClearsOnlyTheDraggedItemsChildTabAssignment()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            using var form = fixture.CreateForm();
            ActiveTabs(form)[fixture.Box.Id] = fixture.Box.ManualTabs[0].Id;
            var data = ItemDrag(fixture.Box.Id, fixture.A);
            var args = Args(form, data, fixture.TabPoint(form, 0));
            Invoke(form, "OnDragOver", form, args);
            Invoke(form, "OnDragDrop", form, args);

            Assert.False(fixture.Box.ItemTabAssignments.ContainsKey(fixture.A.Key.ToString()));
            Assert.Equal(fixture.Box.ManualTabs[0].Id, fixture.Box.ItemTabAssignments[fixture.B.Key.ToString()]);
            Assert.Equal(fixture.Box.Id, fixture.Runtime.State.Assignments[fixture.A.Key.ToString()]);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncomingDesktopOrBoxItemsUseTheActiveChildTabWhenDroppedInItsBody(bool fromDesktop)
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            var source = new DesktopBox { MonitorId = fixture.Monitor.Id };
            fixture.Runtime.State.Boxes.Add(source);
            fixture.Runtime.State.Assignments.Remove(fixture.A.Key.ToString());
            fixture.Box.ItemOrder.Remove(fixture.A.Key.ToString());
            fixture.Box.ItemTabAssignments.Remove(fixture.A.Key.ToString());
            if (!fromDesktop)
            {
                fixture.Runtime.State.Assignments[fixture.A.Key.ToString()] = source.Id;
                source.ItemTabAssignments[fixture.A.Key.ToString()] = Guid.NewGuid();
            }
            using var form = fixture.CreateForm();
            using var desktop = new DesktopIconSurface(fixture.Runtime, fixture.Monitor, IntPtr.Zero);
            ActiveTabs(form)[fixture.Box.Id] = fixture.Box.ManualTabs[1].Id;
            var data = fromDesktop ? new Forms.DataObject() : ItemDrag(source.Id, fixture.A);
            if (fromDesktop)
                data.SetData(DesktopIconSurface.DesktopIconDragSessionFormat, false,
                    new DesktopIconSurfaceDragSession(desktop, [fixture.A.Key.ToString()]));
            var bounds = fixture.ItemBounds(form, fixture.C.Key.ToString());

            Invoke(form, "OnDragDrop", form, Args(form, data, new PointF(bounds.X + 2, bounds.Y + 2)));

            Assert.Equal(fixture.Box.Id, fixture.Runtime.State.Assignments[fixture.A.Key.ToString()]);
            Assert.Equal(fixture.Box.ManualTabs[1].Id, fixture.Box.ItemTabAssignments[fixture.A.Key.ToString()]);
            Assert.False(source.ItemTabAssignments.ContainsKey(fixture.A.Key.ToString()));
            fixture.Operations.VerifyNoOtherCalls();
        });
    }

    [Theory]
    [InlineData(0, 3, "BCA", true)]
    [InlineData(2, 0, "CAB", true)]
    [InlineData(1, 1, "ABC", false)]
    [InlineData(1, 2, "ABC", false)]
    [InlineData(0, -1, "ABC", false)]
    [InlineData(0, 4, "ABC", false)]
    public void ReorderingChildTabsPreservesTheirIdentityAndItemAssignments(
        int sourceIndex, int slot, string expected, bool changed)
    {
        using var fixture = new Fixture();
        fixture.Box.ManualTabs.Add(new DesktopBoxTab { Title = "C" });
        var assignments = fixture.Box.ItemTabAssignments.ToArray();
        var notifications = 0;
        fixture.Runtime.Changed += (_, _) => notifications++;

        Assert.Equal(changed, fixture.Runtime.ReorderManualTab(fixture.Box.Id,
            fixture.Box.ManualTabs[sourceIndex].Id, slot));

        Assert.Equal(expected, string.Concat(fixture.Box.ManualTabs.Select(tab => tab.Title)));
        Assert.Equal(assignments, fixture.Box.ItemTabAssignments.ToArray());
        Assert.Equal(changed ? 1 : 0, notifications);
    }

    [Fact]
    public void TabDragShowsAnInsertionMarkerAndOnlyReordersOnDrop()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Box.ManualTabs.Add(new DesktopBoxTab { Title = "C" });
            using var form = fixture.CreateForm();
            var active = fixture.Box.ManualTabs[0].Id;
            ActiveTabs(form)[fixture.Box.Id] = active;
            var data = new Forms.DataObject();
            data.SetData(DesktopBoxForm.ManualTabDragFormat, false,
                new DesktopBoxForm.ManualTabDrag(fixture.Box.Id, active));
            var geometry = fixture.Geometry(form);
            var bar = (RectangleF)geometry.GetType().GetProperty("TabBar")!.GetValue(geometry)!;
            var args = Args(form, data, new PointF(bar.Right - 2, bar.Top + bar.Height / 2));

            Invoke(form, "OnDragOver", form, args);

            Assert.Equal(Forms.DragDropEffects.Move, args.Effect);
            Assert.Equal("ABC", string.Concat(fixture.Box.ManualTabs.Select(tab => tab.Title)));
            Assert.NotNull(Get(form, "_manualTabDropPreview"));
            using var bitmap = new Bitmap(800, 600);
            using var graphics = Graphics.FromImage(bitmap);
            Invoke(form, "DrawBoxTabs", graphics, geometry, Color.Magenta, Color.White, false);
            Assert.Equal(Color.Magenta.ToArgb(), bitmap.GetPixel((int)bar.Right - 2, (int)(bar.Top + bar.Height / 2)).ToArgb());
            Invoke(form, "OnDragLeave", form, EventArgs.Empty);
            Assert.Null(Get(form, "_manualTabDropPreview"));
            Assert.Equal("ABC", string.Concat(fixture.Box.ManualTabs.Select(tab => tab.Title)));

            Invoke(form, "OnDragOver", form, args);
            Invoke(form, "OnDragDrop", form, args);

            Assert.Equal("BCA", string.Concat(fixture.Box.ManualTabs.Select(tab => tab.Title)));
            Assert.Equal(active, ActiveTabs(form)[fixture.Box.Id]);
            Assert.False(data.GetDataPresent(Forms.DataFormats.FileDrop));
        });
    }

    [Fact]
    public void DraggingAChildTabOutsideItsOwnTabStripIsRejected()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            using var form = fixture.CreateForm();
            var data = new Forms.DataObject();
            data.SetData(DesktopBoxForm.ManualTabDragFormat, false,
                new DesktopBoxForm.ManualTabDrag(fixture.Box.Id, fixture.Box.ManualTabs[0].Id));
            var args = Args(form, data, new PointF(700, 500));
            Invoke(form, "OnDragOver", form, args);
            Invoke(form, "OnDragDrop", form, args);
            Assert.Equal(Forms.DragDropEffects.None, args.Effect);
            Assert.Equal("AB", string.Concat(fixture.Box.ManualTabs.Select(tab => tab.Title)));
        });
    }

    private sealed class Fixture : IDisposable
    {
        public CrabDeskRuntime Runtime { get; } = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        public DesktopBox Box { get; } = new()
        {
            Title = "Tabs", MonitorId = "tab-test", Bounds = new(10, 10, 560, 340),
            SortMode = BoxSortMode.Manual, ManualTabs = [new() { Title = "A" }, new() { Title = "B" }]
        };
        public MonitorLayout Monitor { get; } = new()
        {
            Id = "tab-test", DeviceName = "tab-test", DpiScale = 1,
            Bounds = new(0, 0, 800, 600), WorkArea = new(0, 0, 800, 600),
            PixelBounds = new(0, 0, 800, 600), PixelWorkArea = new(0, 0, 800, 600)
        };
        public DesktopItemRef A { get; } = Item("a");
        public DesktopItemRef B { get; } = Item("b");
        public DesktopItemRef C { get; } = Item("c");
        public DesktopItemRef D { get; } = Item("d");
        public Mock<IFileOperationService> Operations { get; } = new(MockBehavior.Strict);
        private readonly RuntimeTimer _timer = new(TimeSpan.FromHours(1), false, _ => { }, () => { });

        public Fixture()
        {
            var state = new CrabDeskState();
            state.Boxes.Add(Box);
            foreach (var item in new[] { A, B, C, D })
            {
                state.Assignments[item.Key.ToString()] = Box.Id;
                Box.ItemTabAssignments[item.Key.ToString()] = Box.ManualTabs[item == A || item == B ? 0 : 1].Id;
            }
            Box.ItemOrder.AddRange(new[] { C, A, B, D }.Select(item => item.Key.ToString()));
            Set(Runtime, "<State>k__BackingField", state);
            Set(Runtime, "<Items>k__BackingField", new[] { A, B, C, D });
            Set(Runtime, "<Monitors>k__BackingField", new[] { Monitor });
            Set(Runtime, "_fileOperations", Operations.Object);
            Set(Runtime, "_iconProvider", new CrabDesk.Native.ShellIconProvider());
            Set(Runtime, "_saveTimer", _timer);
        }

        public void Deconstruct(out CrabDeskRuntime runtime, out DesktopBox box,
            out DesktopItemRef a, out DesktopItemRef b, out DesktopItemRef c, out DesktopItemRef d)
        { runtime = Runtime; box = Box; a = A; b = B; c = C; d = D; }

        public DesktopBoxForm CreateForm()
        {
            var form = new DesktopBoxForm(Runtime, Monitor) { Location = new Point(-30000, -30000) };
            Invoke(form, "RebuildBoxItemCache");
            return form;
        }

        public object Geometry(DesktopBoxForm form)
        {
            Invoke(form, "RebuildGeometry");
            return ((IList)Get(form, "_boxes")!)[0]!;
        }

        public PointF TabPoint(DesktopBoxForm form, int index)
        {
            var geometry = Geometry(form);
            var bar = (RectangleF)geometry.GetType().GetProperty("TabBar")!.GetValue(geometry)!;
            return new PointF(bar.X + bar.Width / (Box.ManualTabs.Count + 1) * (index + 0.5f), bar.Y + bar.Height / 2);
        }

        public RectangleF ItemBounds(DesktopBoxForm form, string key)
        {
            Geometry(form);
            var items = ((IEnumerable)Get(form, "_items")!).Cast<object>();
            var item = items.Single(item => ((DesktopItemRef)item.GetType().GetProperty("Item")!.GetValue(item)!).Key.ToString() == key);
            return (RectangleF)item.GetType().GetProperty("Bounds")!.GetValue(item)!;
        }

        public void Dispose() => _timer.Dispose();
        private static DesktopItemRef Item(string name) => new()
        {
            Key = new("path", name), DisplayName = name, ParsingName = name, Kind = DesktopItemKind.File
        };
    }

    private static Forms.DataObject ItemDrag(Guid boxId, params DesktopItemRef[] items)
    {
        var data = new Forms.DataObject();
        data.SetData(DesktopBoxForm.ItemKeysFormat, items.Select(item => item.Key.ToString()).ToArray());
        data.SetData(DesktopBoxForm.SourceBoxFormat, boxId.ToString("D"));
        data.SetData(DesktopBoxForm.DragSessionFormat, false, new DesktopBoxForm.InternalDragSession());
        return data;
    }

    private static Forms.DragEventArgs Args(Forms.Form form, Forms.DataObject data, PointF point)
    {
        var screen = form.PointToScreen(Point.Round(point));
        return new(data, 0, screen.X, screen.Y, Forms.DragDropEffects.Move | Forms.DragDropEffects.Copy, Forms.DragDropEffects.None);
    }
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(object target, string name) => target.GetType().GetField(name, Hidden)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden | BindingFlags.DeclaredOnly)!.Invoke(target, args);
    private static Dictionary<Guid, Guid?> ActiveTabs(object form) => (Dictionary<Guid, Guid?>)Get(form, "_activeManualTabIds")!;
    private static HashSet<string> Selection(object form) => (HashSet<string>)Get(form, "_selection")!;

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
