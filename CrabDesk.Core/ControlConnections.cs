namespace CrabDesk.Core;

public enum ControlConnectionKind { WiFi, Bluetooth }

public enum ControlConnectionDeviceKind { Generic, Computer, Phone, Audio, Input, Keyboard, Mouse }

public sealed record ControlConnectionItem(string Id, string Name, string Detail, bool Connected,
    bool CanConnect, bool CanDisconnect, bool RequiresPassword = false, int? SignalPercent = null,
    ControlConnectionDeviceKind DeviceKind = ControlConnectionDeviceKind.Generic);

public sealed record ControlConnectionSnapshot(bool? Enabled, IReadOnlyList<ControlConnectionItem> Items,
    string Status = "", string? SettingsUri = null)
{
    public static ControlConnectionSnapshot Empty { get; } = new(null, []);
}

public sealed record ControlOperationResult(bool Succeeded, string Error = "", bool PasswordRequired = false)
{
    public static ControlOperationResult Success { get; } = new(true);
}
