using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal readonly record struct WidgetCoordinates(double Latitude, double Longitude);
internal readonly record struct WidgetLocationAnchor(Rectangle Bounds, Color Background, Color Foreground);

internal interface IWidgetLocationService
{
    Task<WidgetCoordinates> GetPositionAsync(WidgetLocationAnchor? anchor, CancellationToken cancellationToken);
}

internal sealed class WidgetLocationService : IWidgetLocationService
{
    public async Task<WidgetCoordinates> GetPositionAsync(WidgetLocationAnchor? anchor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GeolocationAccessStatus access;
        // The desktop surface deliberately never activates. Give Windows a
        // small foreground window in the card for its permission request.
        using (var accessWindow = IsAppForeground() ? null : new LocationAccessWindow(anchor))
        {
            if (accessWindow is not null)
            {
                accessWindow.Show();
                accessWindow.Activate();
                await Task.Yield();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAppForeground()) throw new InvalidOperationException("请再次点击卡片上的定位按钮。");
            access = await Geolocator.RequestAccessAsync().AsTask(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (access != GeolocationAccessStatus.Allowed) throw new UnauthorizedAccessException("系统定位未获授权。");
        var locator = new Geolocator { DesiredAccuracyInMeters = 1000 };
        var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20)).AsTask(cancellationToken);
        var coordinate = position.Coordinate.Point.Position;
        return new(coordinate.Latitude, coordinate.Longitude);
    }

    private static bool IsAppForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
        return processId == Environment.ProcessId;
    }

    private sealed class LocationAccessWindow : Forms.Form
    {
        internal LocationAccessWindow(WidgetLocationAnchor? anchor)
        {
            Text = "CrabDesk 使用当前位置";
            AccessibleName = Text;
            FormBorderStyle = Forms.FormBorderStyle.None;
            ShowInTaskbar = false;
            AutoScaleMode = Forms.AutoScaleMode.None;
            StartPosition = anchor is null ? Forms.FormStartPosition.CenterScreen : Forms.FormStartPosition.Manual;
            BackColor = anchor?.Background ?? SystemColors.Window;
            ForeColor = anchor?.Foreground ?? SystemColors.WindowText;
            ClientSize = new Size(220, 80);
            Controls.Add(new Forms.Label { Text = "正在请求位置访问…", Dock = Forms.DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter, Padding = new Forms.Padding(8) });
            if (anchor is { } placement) { CreateControl(); Bounds = placement.Bounds; }
        }

        protected override Forms.CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; }
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
}
