using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CrabDesk.Native;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class SystemThemeStateTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(7, null)]
    public void ReadsTheWindowsSystemColorMode(int light, bool? expected)
    {
        using var settings = new ThemeSettings();
        settings.Key.SetValue(SystemThemeState.SystemValue, light, RegistryValueKind.DWord);
        settings.Key.SetValue(SystemThemeState.AppsValue, light == 0 ? 1 : 0, RegistryValueKind.DWord);
        Assert.Equal(expected, SystemThemeState.Read(settings.Key));
    }

    [Fact]
    public void MissingPreferencesUseTheWindowsLightDefault()
    {
        using var settings = new ThemeSettings();
        Assert.False(SystemThemeState.Read(settings.Key));
        Assert.False(SystemThemeState.Read(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyingAColorModeUpdatesWindowsAndAppsBeforeNotifying(bool dark)
    {
        using var settings = new ThemeSettings();
        settings.Key.SetValue(SystemThemeState.SystemValue, 0, RegistryValueKind.DWord);
        settings.Key.SetValue(SystemThemeState.AppsValue, 1, RegistryValueKind.DWord);
        settings.Key.SetValue("ColorPrevalence", 1, RegistryValueKind.DWord);
        var notifications = 0;
        Assert.True(SystemThemeState.Write(settings.Key, dark, () =>
        {
            notifications++;
            Assert.Equal(dark ? 0 : 1, settings.Key.GetValue(SystemThemeState.SystemValue));
            Assert.Equal(dark ? 0 : 1, settings.Key.GetValue(SystemThemeState.AppsValue));
        }));
        Assert.Equal(1, notifications);
        Assert.Equal(dark, SystemThemeState.Read(settings.Key));
        Assert.Equal(RegistryValueKind.DWord, settings.Key.GetValueKind(SystemThemeState.SystemValue));
        Assert.Equal(RegistryValueKind.DWord, settings.Key.GetValueKind(SystemThemeState.AppsValue));
        Assert.Equal(1, settings.Key.GetValue("ColorPrevalence"));
    }

    [Fact]
    public void ReadOnlyPreferencesReportFailureWithoutNotifying()
    {
        using var settings = new ThemeSettings();
        settings.Key.SetValue(SystemThemeState.SystemValue, 1, RegistryValueKind.DWord);
        using var readOnly = Registry.CurrentUser.OpenSubKey(settings.Path)!;
        var notified = false;
        Assert.False(SystemThemeState.Write(readOnly, true, () => notified = true));
        Assert.False(notified);
        Assert.False(SystemThemeState.Read(settings.Key));
    }

    [Fact]
    public void ThemeNotificationPreservesTheSettingNameInAUnicodeWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new ThemeNotificationWindow();
                var setting = Marshal.StringToHGlobalUni("ImmersiveColorSet");
                try
                {
                    Assert.NotEqual(IntPtr.Zero, NativeMethods.SendMessageTimeout(
                        window.Handle, 0x001A, IntPtr.Zero, setting,
                        NativeMethods.SmtoAbortIfHung, 1000, out _));
                    Assert.Equal("ImmersiveColorSet", window.SettingName);
                }
                finally { Marshal.FreeHGlobal(setting); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ThemeNotificationWindow : Forms.NativeWindow, IDisposable
    {
        internal string? SettingName { get; private set; }

        internal ThemeNotificationWindow() =>
            CreateHandle(new Forms.CreateParams { Parent = new IntPtr(-3) });

        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == 0x001A)
            {
                SettingName = Marshal.PtrToStringUni(message.LParam);
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose() => DestroyHandle();
    }

    private sealed class ThemeSettings : IDisposable
    {
        internal string Path { get; } = @"Software\CrabDesk.Tests\SystemTheme\" + Guid.NewGuid().ToString("N");
        internal RegistryKey Key { get; }
        internal ThemeSettings() => Key = Registry.CurrentUser.CreateSubKey(Path);
        public void Dispose()
        {
            Key.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
        }
    }
}
