using System.Numerics;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Windows.System;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace CrabDesk.Runtime;

/// <summary>
/// Best-effort live backdrop layer for the existing desktop HWND. Composition
/// is hosted on its own DispatcherQueue because the WinForms desktop thread
/// does not own a WinUI dispatcher by default.
/// </summary>
internal sealed class CompositionBoxBackdropHost : IDisposable
{
    private static readonly Guid CompositorDesktopInteropId =
        new("29E691FA-4567-4DCA-B319-D0F207EB6807");

    private readonly DispatcherQueueController _dispatcherController;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Compositor _compositor;
    private readonly DesktopWindowTarget _target;
    private readonly ContainerVisual _root;
    private readonly CompositionBrush _backdropBrush;
    private readonly Dictionary<Guid, VisualEntry> _entries = [];
    private int _disposed;

    private CompositionBoxBackdropHost(
        DispatcherQueueController dispatcherController,
        Compositor compositor,
        DesktopWindowTarget target,
        ContainerVisual root)
    {
        _dispatcherController = dispatcherController;
        _dispatcherQueue = dispatcherController.DispatcherQueue;
        _compositor = compositor;
        _target = target;
        _root = root;
        _backdropBrush = compositor.CreateHostBackdropBrush();
        _target.Root = root;
    }

    internal static CompositionBoxBackdropHost? TryCreate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        try
        {
            var controller = DispatcherQueueController.CreateOnDedicatedThread();
            var completion = new TaskCompletionSource<CompositionBoxBackdropHost?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!controller.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var compositor = new Compositor();
                        var target = CreateDesktopWindowTarget(compositor, hwnd);
                        var root = compositor.CreateContainerVisual();
                        completion.SetResult(new CompositionBoxBackdropHost(
                            controller,
                            compositor,
                            target,
                            root));
                    }
                    catch (Exception exception)
                    {
                        DiagnosticLog.Error("Composition backdrop host creation failed", exception);
                        completion.SetResult(null);
                    }
                }))
            {
                controller.ShutdownQueueAsync().AsTask().GetAwaiter().GetResult();
                return null;
            }

            if (!completion.Task.Wait(TimeSpan.FromSeconds(3)))
            {
                controller.ShutdownQueueAsync().AsTask().GetAwaiter().GetResult();
                return null;
            }
            return completion.Task.Result;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Composition backdrop dispatcher creation failed", exception);
            return null;
        }
    }

    internal bool IsAvailable => Volatile.Read(ref _disposed) == 0;

    internal void Update(
        IEnumerable<(Guid Id, LayoutRect Bounds, BoxMaterialKind Material, string Background, double Opacity)> boxes,
        double scale,
        double cornerRadius,
        bool isDark)
    {
        if (!IsAvailable) return;

        var snapshot = boxes.ToArray();
        _dispatcherQueue.TryEnqueue(() => UpdateCore(snapshot, scale, cornerRadius, isDark));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _target.Root = null;
                    foreach (var entry in _entries.Values) entry.Dispose();
                    _entries.Clear();
                    _root.Children.RemoveAll();
                    _backdropBrush.Dispose();
                    _root.Dispose();
                    _target.Dispose();
                    _compositor.Dispose();
                }
                finally
                {
                    completion.SetResult(true);
                }
            }))
        {
            completion.Task.Wait(TimeSpan.FromSeconds(2));
        }
        _dispatcherController.ShutdownQueueAsync().AsTask().GetAwaiter().GetResult();
    }

    private void UpdateCore(
        IReadOnlyList<(Guid Id, LayoutRect Bounds, BoxMaterialKind Material, string Background, double Opacity)> boxes,
        double scale,
        double cornerRadius,
        bool isDark)
    {
        if (!IsAvailable) return;

        var active = new HashSet<Guid>();
        foreach (var box in boxes)
        {
            if (box.Material != BoxMaterialKind.AcrylicPreview) continue;

            active.Add(box.Id);
            if (!_entries.TryGetValue(box.Id, out var entry))
            {
                entry = CreateEntry();
                _entries[box.Id] = entry;
                _root.Children.InsertAtTop(entry.Container);
            }

            var bounds = box.Bounds;
            var size = new Vector2((float)(bounds.Width * scale), (float)(bounds.Height * scale));
            entry.Container.Offset = new Vector3(
                (float)(bounds.X * scale),
                (float)(bounds.Y * scale),
                0);
            entry.Container.Size = size;
            entry.Backdrop.Size = size;
            entry.TintVisual.Size = size;
            entry.Geometry.Size = size;
            entry.Geometry.CornerRadius = new Vector2(
                (float)Math.Clamp(cornerRadius * scale, 0, Math.Min(size.X, size.Y) / 2));
            entry.Tint.Color = ToCompositionColor(box.Background, box.Opacity, isDark);
        }

        foreach (var stale in _entries.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            var entry = _entries[stale];
            _root.Children.Remove(entry.Container);
            entry.Dispose();
            _entries.Remove(stale);
        }
    }

    private VisualEntry CreateEntry()
    {
        var container = _compositor.CreateContainerVisual();
        var backdrop = _compositor.CreateSpriteVisual();
        backdrop.Brush = _backdropBrush;
        var tint = _compositor.CreateSpriteVisual();
        var tintBrush = _compositor.CreateColorBrush(Color.FromArgb(1, 255, 255, 255));
        tint.Brush = tintBrush;
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2(1, 1);
        var clip = _compositor.CreateGeometricClip(geometry);
        container.Clip = clip;
        container.Children.InsertAtBottom(backdrop);
        container.Children.InsertAtTop(tint);
        return new VisualEntry(container, backdrop, tint, tintBrush, geometry, clip);
    }

    private static Color ToCompositionColor(string hex, double opacity, bool isDark)
    {
        var parsed = ParseColor(hex);
        var tint = isDark ? 0.08 : 0.18;
        var alpha = (byte)Math.Clamp(70 + opacity * 90, 0, 255);
        return Color.FromArgb(
            alpha,
            (byte)Math.Clamp(parsed.R + (255 - parsed.R) * tint, 0, 255),
            (byte)Math.Clamp(parsed.G + (255 - parsed.G) * tint, 0, 255),
            (byte)Math.Clamp(parsed.B + (255 - parsed.B) * tint, 0, 255));
    }

    private static System.Drawing.Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        try
        {
            var offset = hex.Length == 8 ? 2 : 0;
            return System.Drawing.Color.FromArgb(
                255,
                Convert.ToByte(hex.Substring(offset, 2), 16),
                Convert.ToByte(hex.Substring(offset + 2, 2), 16),
                Convert.ToByte(hex.Substring(offset + 4, 2), 16));
        }
        catch
        {
            return System.Drawing.Color.FromArgb(42, 45, 50);
        }
    }

    private static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, IntPtr hwnd)
    {
        var unknown = Marshal.GetIUnknownForObject(compositor);
        try
        {
            var iid = CompositorDesktopInteropId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out var interopPointer));
            try
            {
                var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(interopPointer);
                Marshal.ThrowExceptionForHR(interop.CreateDesktopWindowTarget(hwnd, false, out var targetPointer));
                try
                {
                    return DesktopWindowTarget.FromAbi(targetPointer);
                }
                finally
                {
                    Marshal.Release(targetPointer);
                }
            }
            finally
            {
                Marshal.Release(interopPointer);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig]
        int CreateDesktopWindowTarget(
            IntPtr hwndTarget,
            [MarshalAs(UnmanagedType.Bool)] bool isTopmost,
            out IntPtr result);
    }

    private sealed class VisualEntry(
        ContainerVisual container,
        SpriteVisual backdrop,
        SpriteVisual tintVisual,
        CompositionColorBrush tint,
        CompositionRoundedRectangleGeometry geometry,
        CompositionGeometricClip clip) : IDisposable
    {
        internal ContainerVisual Container { get; } = container;
        internal SpriteVisual Backdrop { get; } = backdrop;
        internal SpriteVisual TintVisual { get; } = tintVisual;
        internal CompositionColorBrush Tint { get; } = tint;
        internal CompositionRoundedRectangleGeometry Geometry { get; } = geometry;

        public void Dispose()
        {
            Container.Children.RemoveAll();
            Backdrop.Dispose();
            TintVisual.Dispose();
            Tint.Dispose();
            clip.Dispose();
            Geometry.Dispose();
            Container.Dispose();
        }
    }
}
