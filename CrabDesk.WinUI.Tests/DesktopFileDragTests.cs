using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopFileDragTests
{
    [Theory]
    [InlineData("20D04FE0-3AEA-1069-A2D8-08002B30309D", false)]
    [InlineData("20D04FE0-3AEA-1069-A2D8-08002B30309D", true)]
    [InlineData("645FF040-5081-101B-9F08-00AA002F954E", false)]
    [InlineData("645FF040-5081-101B-9F08-00AA002F954E", true)]
    public void SystemIconAndMixedSelectionsCanEnterTheDesktopForRepositioning(string shellId, bool mixed)
    {
        RunSta(() =>
        {
            var systemItem = new DesktopItemRef
            {
                Key = new("shell", shellId), DisplayName = "System icon",
                ParsingName = $"::{{{shellId}}}", Kind = DesktopItemKind.Shell
            };
            var items = mixed
                ? new[] { systemItem, FileItem(Path.Combine(Path.GetTempPath(), "drag-file.txt")) }
                : new[] { systemItem };
            using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
            var runtime = CreateRuntime(new CrabDeskState(), items, items, Mock.Of<IFileOperationService>(), timer);
            var monitor = new MonitorLayout
            {
                Id = "system-drag-test", DeviceName = "system-drag-test", DpiScale = 1,
                Bounds = new(0, 0, 800, 600), WorkArea = new(0, 0, 800, 600),
                PixelBounds = new(0, 0, 800, 600), PixelWorkArea = new(0, 0, 800, 600)
            };
            using var surface = new DesktopIconSurface(runtime, monitor, IntPtr.Zero);
            surface.Location = new Point(-30000, -30000);
            var keys = items.Select(item => item.Key.ToString()).ToArray();
            var data = new Forms.DataObject();
            data.SetData(DesktopIconSurface.DesktopIconDragSessionFormat, false,
                new DesktopIconSurfaceDragSession(surface, keys));
            data.SetData(DesktopBoxForm.ItemKeysFormat, keys);
            var point = surface.PointToScreen(new Point(200, 200));
            var args = new Forms.DragEventArgs(data, 0, point.X, point.Y,
                Forms.DragDropEffects.Move, Forms.DragDropEffects.None);

            surface.ForwardDragOver(args);

            Assert.Equal(Forms.DragDropEffects.Move, args.Effect);
            Assert.False(data.GetDataPresent(Forms.DataFormats.FileDrop));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BoxFilesDroppedOnDesktopFileTargetsPerformFileOperations(bool recycle, bool control)
    {
        RunSta(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "CrabDesk.DragTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var first = FileItem(Path.Combine(directory, "中文 file.txt"));
                var second = FileItem(Path.Combine(directory, "second.txt"));
                var target = recycle
                    ? new DesktopItemRef
                    {
                        Key = new("shell", "recycle"), DisplayName = "Recycle Bin",
                        ParsingName = "::{645FF040-5081-101B-9F08-00AA002F954E}",
                        Kind = DesktopItemKind.Shell
                    }
                    : FileItem(directory, DesktopItemKind.Folder);
                var state = new CrabDeskState();
                var box = new DesktopBox();
                state.Boxes.Add(box);
                state.Assignments[first.Key.ToString()] = box.Id;
                state.Assignments[second.Key.ToString()] = box.Id;
                var operations = new Mock<IFileOperationService>(MockBehavior.Strict);
                var move = recycle || !control;
                operations.Setup(service => service.DeleteAsync(
                        It.IsAny<IEnumerable<DesktopItemRef>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new FileDeleteBatchResult([
                        new(first.FileSystemPath!, null), new(second.FileSystemPath!, null)]));
                operations.Setup(service => service.ImportAsync(
                        It.IsAny<IEnumerable<string>>(), directory, move, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new FileImportBatchResult([
                        new(first.FileSystemPath!, Path.Combine(directory, "imported-first.txt"), null),
                        new(second.FileSystemPath!, Path.Combine(directory, "imported-second.txt"), null)]));
                using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
                var runtime = CreateRuntime(state, [first, second, target], [target], operations.Object, timer);
                runtime.SetVirtualBoxDesktopDropEnabled(true);
                var monitor = new MonitorLayout
                {
                    Id = "drag-test", DeviceName = "drag-test", DpiScale = 1,
                    Bounds = new(0, 0, 800, 600), WorkArea = new(0, 0, 800, 600),
                    PixelBounds = new(0, 0, 800, 600), PixelWorkArea = new(0, 0, 800, 600)
                };
                using var surface = new DesktopIconSurface(runtime, monitor, IntPtr.Zero);
                surface.Location = new Point(-30000, -30000);
                AddTargetGeometry(surface, target, new RectangleF(20, 20, 80, 80));
                var session = new DesktopBoxForm.InternalDragSession();
                var data = new Forms.DataObject();
                data.SetData(DesktopBoxForm.ItemKeysFormat, new[] { first.Key.ToString(), second.Key.ToString() });
                data.SetData(DesktopBoxForm.SourceBoxFormat, box.Id.ToString("D"));
                data.SetData(DesktopBoxForm.DragSessionFormat, false, session);
                var screenPoint = surface.PointToScreen(new Point(50, 50));
                var args = new Forms.DragEventArgs(data, control ? 8 : 0, screenPoint.X, screenPoint.Y,
                    Forms.DragDropEffects.Copy | Forms.DragDropEffects.Move, Forms.DragDropEffects.None);

                surface.ForwardDragOver(args);
                Assert.Equal(move ? Forms.DragDropEffects.Move : Forms.DragDropEffects.Copy, args.Effect);
                Assert.Null(Get(surface, "_boxDropTargetCell"));
                surface.ForwardDragDrop(args);

                Assert.True(session.HandledByDesktop);
                Assert.Equal(move ? Forms.DragDropEffects.Move : Forms.DragDropEffects.Copy, args.Effect);
                Assert.Equal(box.Id, state.Assignments[first.Key.ToString()]);
                Assert.Equal(box.Id, state.Assignments[second.Key.ToString()]);
                Assert.Null(Get(surface, "_desktopFolderDropTargetKey"));
                if (recycle)
                {
                    operations.Verify(service => service.DeleteAsync(
                        It.Is<IEnumerable<DesktopItemRef>>(items => items.Count() == 2 &&
                            items.Contains(first) && items.Contains(second)), It.IsAny<CancellationToken>()), Times.Once);
                    operations.Verify(service => service.ImportAsync(It.IsAny<IEnumerable<string>>(),
                        It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
                }
                else
                {
                    operations.Verify(service => service.ImportAsync(
                        It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(new[] { first.FileSystemPath!, second.FileSystemPath! })),
                        directory, move, It.IsAny<CancellationToken>()), Times.Once);
                    operations.Verify(service => service.DeleteAsync(
                        It.IsAny<IEnumerable<DesktopItemRef>>(), It.IsAny<CancellationToken>()), Times.Never);
                }
            }
            finally { Directory.Delete(directory); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalDragRefreshesOnlyWhenTheSourceActuallyDisappeared(bool moved)
    {
        RunSta(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "CrabDesk.DragTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "source.txt");
            var destination = Path.Combine(directory, "destination.txt");
            File.WriteAllText(path, "drag verification");
            try
            {
                var source = FileItem(path);
                using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
                var runtime = CreateRuntime(new CrabDeskState(), [source], [], Mock.Of<IFileOperationService>(), timer);
                var notifications = 0;
                runtime.Changed += (_, _) => notifications++;
                if (moved) File.Move(path, destination);

                runtime.ReconcileExternalDesktopMoveAsync([source]).GetAwaiter().GetResult();

                Assert.Equal(moved, notifications > 0);
                Assert.Equal(moved ? 0 : 1, runtime.Items.Count);
            }
            finally
            {
                File.Delete(path);
                File.Delete(destination);
                Directory.Delete(directory);
            }
        });
    }

    [Fact]
    public void RejectedDropOnAnotherWindowIsNotADesktopDrop()
    {
        RunSta(() =>
        {
            using var desktop = new Forms.Form();
            using var icons = new Forms.Control();
            using var replacement = new Forms.Control();
            using var application = new Forms.Form();
            desktop.Controls.Add(icons);
            desktop.Controls.Add(replacement);
            _ = desktop.Handle;

            Assert.True(DesktopWindowTools.IsDesktopSurfaceWindow(icons.Handle, replacement.Handle));
            Assert.True(DesktopWindowTools.IsDesktopSurfaceWindow(icons.Handle, desktop.Handle));
            Assert.False(DesktopWindowTools.IsDesktopSurfaceWindow(icons.Handle, application.Handle));
            Assert.False(DesktopWindowTools.IsDesktopSurfaceWindow(icons.Handle, IntPtr.Zero));
            Assert.False(DesktopWindowTools.IsDesktopSurfaceWindow(IntPtr.Zero, application.Handle));
        });
    }

    private static CrabDeskRuntime CreateRuntime(CrabDeskState state,
        IReadOnlyList<DesktopItemRef> before, IReadOnlyList<DesktopItemRef> after,
        IFileOperationService operations, RuntimeTimer timer)
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        var provider = new Mock<IDesktopItemProvider>();
        provider.Setup(service => service.EnumerateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(after);
        Set(runtime, "<State>k__BackingField", state);
        Set(runtime, "<Items>k__BackingField", before);
        Set(runtime, "_allDesktopItems", Array.Empty<DesktopItemRef>());
        Set(runtime, "_itemProvider", provider.Object);
        Set(runtime, "_fileOperations", operations);
        Set(runtime, "_mappedFolderProvider", Mock.Of<IMappedFolderProvider>());
        Set(runtime, "_mappedFolderSnapshots", new Dictionary<Guid, MappedFolderSnapshot>());
        Set(runtime, "_mappedRefreshLock", new SemaphoreSlim(1, 1));
        Set(runtime, "_targetedDesktopRefreshPaths", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Set(runtime, "_saveTimer", timer);
        return runtime;
    }

    private static void AddTargetGeometry(DesktopIconSurface surface, DesktopItemRef target, RectangleF bounds)
    {
        var cellType = typeof(DesktopIconSurface).GetNestedType("GridCell", BindingFlags.NonPublic)!;
        var geometryType = typeof(DesktopIconSurface).GetNestedType("DesktopIconGeometry", BindingFlags.NonPublic)!;
        var geometry = Activator.CreateInstance(geometryType, target, bounds, Activator.CreateInstance(cellType, 0, 0))!;
        geometryType.GetProperty("HitBounds", Hidden)!.SetValue(geometry, bounds);
        ((IList)Get(surface, "_items")!).Add(geometry);
    }

    private static DesktopItemRef FileItem(string path, DesktopItemKind kind = DesktopItemKind.File) => new()
    {
        Key = new("path", path), DisplayName = Path.GetFileName(path), ParsingName = path,
        FileSystemPath = path, Kind = kind
    };

    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Get(object instance, string name) => instance.GetType().GetField(name, Hidden)!.GetValue(instance);
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Hidden)!.SetValue(instance, value);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
