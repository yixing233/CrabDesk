using System.Globalization;
using CrabDesk.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace CrabDesk.Native;

/// <summary>Reads paired Bluetooth endpoints without opening profile connections.</summary>
public sealed class BluetoothConnectionService : IControlConnectionService
{
    private const string SettingsUri = "ms-settings:bluetooth";
    private const string ConnectedProperty = "System.Devices.Aep.IsConnected";
    private const string ContainerProperty = "System.Devices.Aep.ContainerId";
    private const string AddressProperty = "System.Devices.Aep.DeviceAddress";
    private const string ProtocolProperty = "System.Devices.Aep.ProtocolId";
    private const string MajorClassProperty = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string MinorClassProperty = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string AppearanceProperty = "System.Devices.Aep.Bluetooth.Le.Appearance.Category";
    private static readonly Guid LowEnergyProtocol = new("bb7bb05e-5972-42b5-94fc-76eaa7084d49");
    private static readonly string[] EndpointProperties =
        [ConnectedProperty, ContainerProperty, AddressProperty, ProtocolProperty, MajorClassProperty, MinorClassProperty, AppearanceProperty];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public ControlConnectionKind Kind => ControlConnectionKind.Bluetooth;

    public async Task<ControlConnectionSnapshot> ReadAsync(bool scan, CancellationToken token = default)
    {
        using var operation = BeginOperation(token);
        token = operation.Token;
        token.ThrowIfCancellationRequested();

        bool? enabled;
        string status;
        try
        {
            // Radio enumeration/state reading does not require control permission.
            var radios = await Radio.GetRadiosAsync().AsTask(token).ConfigureAwait(false);
            var bluetooth = radios.Where(radio => radio.Kind == RadioKind.Bluetooth).ToArray();
            enabled = bluetooth.Any(radio => radio.State == RadioState.On) ? true :
                bluetooth.Length == 0 || bluetooth.Any(radio => radio.State == RadioState.Unknown) ? null : false;
            status = bluetooth.Length == 0 ? "未检测到蓝牙适配器" : enabled == true ? "" :
                enabled is null ? "暂时无法确定蓝牙开关状态" :
                bluetooth.All(radio => radio.State == RadioState.Disabled) ? "蓝牙已被硬件或系统禁用" : "蓝牙已关闭";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            enabled = null;
            status = ReadError(exception, "无法读取蓝牙开关状态");
        }

        try
        {
            // Refresh paired devices only. Discovery/pairing is handled by Windows Settings.
            var endpoints = await QueryEndpointsAsync(token).ConfigureAwait(false);
            var items = BuildItems(endpoints, enabled);
            if (string.IsNullOrEmpty(status) && items.Count == 0) status = "暂无已配对设备，可在系统设置中添加设备";
            return new(enabled, items, status, SettingsUri);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            var error = ReadError(exception, "无法读取已配对的蓝牙设备，请打开系统设置查看");
            return new(enabled, [], string.IsNullOrEmpty(status) ? error : $"{status}；{error}", SettingsUri);
        }
    }

    public async Task<ControlOperationResult> SetEnabledAsync(bool enabled, CancellationToken token = default)
    {
        using var operation = BeginOperation(token);
        token = operation.Token;
        token.ThrowIfCancellationRequested();
        try
        {
            // Invoked for an explicit user action. Start the consent-capable call on
            // the caller's context before moving the remaining work off that context.
            var access = await Radio.RequestAccessAsync().AsTask(token).ConfigureAwait(false);
            if (access != RadioAccessStatus.Allowed) return AccessError(access);
            var radios = (await Radio.GetRadiosAsync().AsTask(token).ConfigureAwait(false))
                .Where(radio => radio.Kind == RadioKind.Bluetooth).ToArray();
            if (radios.Length == 0) return new(false, "未检测到蓝牙适配器");
            if (enabled && radios.Any(radio => radio.State == RadioState.Disabled))
                return new(false, "蓝牙已被硬件或系统禁用，请检查飞行模式、硬件开关或系统设置");

            var target = enabled ? RadioState.On : RadioState.Off;
            foreach (var radio in radios)
            {
                token.ThrowIfCancellationRequested();
                if (radio.State == target || (!enabled && radio.State == RadioState.Disabled)) continue;
                access = await radio.SetStateAsync(target).AsTask(token).ConfigureAwait(false);
                if (access != RadioAccessStatus.Allowed) return AccessError(access);
            }
            return ControlOperationResult.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new(false, ReadError(exception, "无法切换蓝牙，请在系统设置中操作"));
        }
    }

    public Task<ControlOperationResult> ConnectAsync(string id, string? password, CancellationToken token = default) =>
        UnsupportedOperation(token);

    public Task<ControlOperationResult> DisconnectAsync(string id, CancellationToken token = default) =>
        UnsupportedOperation(token);

    private Task<ControlOperationResult> UnsupportedOperation(CancellationToken token)
    {
        using var operation = BeginOperation(token);
        operation.Token.ThrowIfCancellationRequested();
        return Task.FromResult(new ControlOperationResult(false, "此设备的连接由 Windows 管理，请打开蓝牙系统设置操作"));
    }

    /// <summary>Returns a connected device name/count, or empty when unavailable.</summary>
    public static async Task<string> ReadConnectedLabelAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var endpoints = await QueryEndpointsAsync(token, connectedOnly: true).ConfigureAwait(false);
            var connected = BuildItems(endpoints, null).Where(item => item.Connected).ToArray();
            return connected.Length switch
            {
                0 => "",
                1 => connected[0].Name,
                _ => $"{connected.Length} 台设备已连接"
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return ""; }
    }

    private static async Task<IReadOnlyList<BluetoothEndpoint>> QueryEndpointsAsync(CancellationToken token, bool connectedOnly = false)
    {
        var selector = $"({BluetoothDevice.GetDeviceSelectorFromPairingState(true)}) OR " +
            $"({BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)})";
        if (connectedOnly) selector = $"({selector}) AND {ConnectedProperty}:=System.StructuredQueryType.Boolean#True";
        var devices = await DeviceInformation.FindAllAsync(selector, EndpointProperties,
            DeviceInformationKind.AssociationEndpoint).AsTask(token).ConfigureAwait(false);
        var endpoints = new List<BluetoothEndpoint>(devices.Count);
        foreach (var device in devices)
        {
            token.ThrowIfCancellationRequested();
            var properties = device.Properties;
            endpoints.Add(new(device.Id, device.Name,
                PropertyString(properties, ContainerProperty), PropertyString(properties, AddressProperty),
                properties.TryGetValue(ConnectedProperty, out var connected) && connected is bool state ? state : null,
                PropertyNumber(properties, MajorClassProperty), PropertyNumber(properties, AppearanceProperty),
                Guid.TryParse(PropertyString(properties, ProtocolProperty), out var protocol) && protocol == LowEnergyProtocol,
                PropertyNumber(properties, MinorClassProperty)));
        }
        return endpoints;
    }

    internal sealed record BluetoothEndpoint(string Id, string Name, string? ContainerId, string? Address,
        bool? Connected, int? MajorClass = null, int? AppearanceCategory = null, bool LowEnergy = false, int? MinorClass = null);

    internal static IReadOnlyList<ControlConnectionItem> BuildItems(IEnumerable<BluetoothEndpoint> endpoints, bool? radioEnabled)
    {
        // One physical device can expose Classic and LE endpoints, sometimes with
        // distinct container IDs. Merge only matching IDs, containers or addresses;
        // names are not unique and must never identify a physical device.
        var groups = new List<(HashSet<string> Keys, List<BluetoothEndpoint> Devices)>();
        foreach (var endpoint in endpoints)
        {
            var keys = EndpointKeys(endpoint).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = groups.Where(group => group.Keys.Overlaps(keys)).ToArray();
            var members = new List<BluetoothEndpoint> { endpoint };
            foreach (var match in matches)
            {
                keys.UnionWith(match.Keys);
                members.AddRange(match.Devices);
                groups.Remove(match);
            }
            groups.Add((keys, members));
        }

        return groups.Select(group =>
        {
            var devices = group.Devices.OrderBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            var connected = radioEnabled != false && devices.Any(device => device.Connected == true);
            var unknown = radioEnabled != false && !connected && devices.Any(device => device.Connected is null);
            var name = devices.Select(device => device.Name?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "蓝牙设备";
            var type = devices.Select(DeviceType).FirstOrDefault(value => value is not null) ??
                (devices.All(device => device.LowEnergy) ? "低功耗蓝牙设备" : "蓝牙设备");
            var state = connected ? "已连接" : unknown ? "已配对 · 连接状态未知" : "已配对 · 未连接";
            var kinds = devices.Select(DeviceKind).ToArray();
            var kind = kinds.FirstOrDefault(value => value is not (ControlConnectionDeviceKind.Generic or ControlConnectionDeviceKind.Input));
            if (kind == ControlConnectionDeviceKind.Generic && kinds.Contains(ControlConnectionDeviceKind.Input))
                kind = ControlConnectionDeviceKind.Input;
            return new ControlConnectionItem(devices[0].Id, name, $"{type} · {state}", connected, false, false, DeviceKind: kind);
        }).OrderByDescending(item => item.Connected).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EndpointKeys(BluetoothEndpoint endpoint)
    {
        yield return "id:" + endpoint.Id;
        if (Guid.TryParse(endpoint.ContainerId, out var container) && container != Guid.Empty)
            yield return "container:" + container.ToString("D");
        var address = new string((endpoint.Address ?? "").Where(char.IsAsciiHexDigit).ToArray());
        if (address.Length == 12 && address.Any(character => character != '0')) yield return "address:" + address;
    }

    private static ControlConnectionDeviceKind DeviceKind(BluetoothEndpoint endpoint)
    {
        if (endpoint.MajorClass == (int)BluetoothMajorClass.Peripheral)
        {
            var minor = endpoint.MinorClass.GetValueOrDefault();
            // bthdef.h: COD_PERIPHERAL_MINOR_KEYBOARD_MASK / POINTER_MASK.
            if ((minor & 0x10) != 0) return ControlConnectionDeviceKind.Keyboard;
            if ((minor & 0x20) != 0) return ControlConnectionDeviceKind.Mouse;
            return ControlConnectionDeviceKind.Input;
        }
        if (endpoint.MajorClass == (int)BluetoothMajorClass.Computer || endpoint.AppearanceCategory == BluetoothLEAppearanceCategories.Computer)
            return ControlConnectionDeviceKind.Computer;
        if (endpoint.MajorClass == (int)BluetoothMajorClass.Phone || endpoint.AppearanceCategory == BluetoothLEAppearanceCategories.Phone)
            return ControlConnectionDeviceKind.Phone;
        if (endpoint.MajorClass == (int)BluetoothMajorClass.AudioVideo) return ControlConnectionDeviceKind.Audio;
        if (endpoint.AppearanceCategory == BluetoothLEAppearanceCategories.HumanInterfaceDevice) return ControlConnectionDeviceKind.Input;
        return ControlConnectionDeviceKind.Generic;
    }

    private static string? DeviceType(BluetoothEndpoint endpoint)
    {
        var classic = endpoint.MajorClass switch
        {
            (int)BluetoothMajorClass.Computer => "电脑",
            (int)BluetoothMajorClass.Phone => "手机",
            (int)BluetoothMajorClass.NetworkAccessPoint => "网络设备",
            (int)BluetoothMajorClass.AudioVideo => "音频 / 视频设备",
            (int)BluetoothMajorClass.Peripheral => "输入设备",
            (int)BluetoothMajorClass.Imaging => "图像设备",
            (int)BluetoothMajorClass.Wearable => "可穿戴设备",
            (int)BluetoothMajorClass.Toy => "玩具",
            (int)BluetoothMajorClass.Health => "健康设备",
            _ => null
        };
        if (classic is not null) return classic;
        var category = endpoint.AppearanceCategory;
        if (category is null) return null;
        if (category == BluetoothLEAppearanceCategories.Computer) return "电脑";
        if (category == BluetoothLEAppearanceCategories.Phone) return "手机";
        if (category == BluetoothLEAppearanceCategories.HumanInterfaceDevice) return "输入设备";
        if (category == BluetoothLEAppearanceCategories.Watch) return "手表";
        if (category == BluetoothLEAppearanceCategories.RemoteControl) return "遥控器";
        if (category == BluetoothLEAppearanceCategories.HeartRate || category == BluetoothLEAppearanceCategories.BloodPressure ||
            category == BluetoothLEAppearanceCategories.Thermometer) return "健康设备";
        return null;
    }

    private static string? PropertyString(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private static int? PropertyNumber(IReadOnlyDictionary<string, object> properties, string key) =>
        int.TryParse(PropertyString(properties, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static ControlOperationResult AccessError(RadioAccessStatus status) => new(false, status switch
    {
        RadioAccessStatus.DeniedByUser => "蓝牙控制权限被拒绝，请在系统设置中切换蓝牙",
        RadioAccessStatus.DeniedBySystem => "系统策略不允许控制蓝牙，请在系统设置中检查",
        _ => "无法获得蓝牙控制权限，请在系统设置中操作"
    });

    private static string ReadError(Exception exception, string fallback) =>
        exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070005)
            ? "蓝牙访问被拒绝，请在系统设置中检查权限" : fallback;

    private CancellationTokenSource BeginOperation(CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}
