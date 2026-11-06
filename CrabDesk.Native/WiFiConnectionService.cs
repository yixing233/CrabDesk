using System.Text;
using CrabDesk.Core;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace CrabDesk.Native;

/// <summary>
/// Keeps the adapter and scan result together so a command cannot accidentally
/// target another adapter or a similarly named network. Call explicit refreshes
/// and radio commands from the UI thread, where Windows can request consent.
/// </summary>
public sealed class WiFiConnectionService : IControlConnectionService
{
    private const string WiFiSettings = "ms-settings:network-wifi";
    private const string LocationSettings = "ms-settings:privacy-location";
    private const string AccessDenied = "Windows 未允许访问 Wi‑Fi。请在位置设置中允许位置服务和应用访问，然后刷新。";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Candidate> _candidates = new(StringComparer.Ordinal);
    private IReadOnlyList<WiFiAdapter> _adapters = [];
    private string _status = "点击刷新以查找附近的 Wi‑Fi。";
    private string _settingsUri = WiFiSettings;
    private int _disposed;

    public ControlConnectionKind Kind => ControlConnectionKind.WiFi;

    public async Task<ControlConnectionSnapshot> ReadAsync(bool scan, CancellationToken token = default)
    {
        // Preserve the caller's UI context until RequestAccessAsync has started.
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (scan)
            {
                _candidates.Clear();
                _adapters = [];
                var access = await WiFiAdapter.RequestAccessAsync().AsTask(token).ConfigureAwait(false);
                if (access != WiFiAccessStatus.Allowed)
                {
                    _status = AccessDenied;
                    _settingsUri = LocationSettings;
                    return new(await ReadEnabledAsync(token).ConfigureAwait(false), [], _status, _settingsUri);
                }

                _settingsUri = WiFiSettings;
                _adapters = await WiFiAdapter.FindAllAdaptersAsync().AsTask(token).ConfigureAwait(false);
                var profiles = ReadKnownProfiles();
                var failed = 0;
                foreach (var adapter in _adapters)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        // This is the only active scan. Polls and commands reuse the report.
                        await adapter.ScanAsync().AsTask(token).ConfigureAwait(false);
                        foreach (var network in adapter.NetworkReport.AvailableNetworks)
                        {
                            if (string.IsNullOrEmpty(network.Ssid)) continue;
                            var key = NetworkKey(adapter.NetworkAdapter.NetworkAdapterId, network.Ssid,
                                network.SecuritySettings.NetworkAuthenticationType,
                                network.SecuritySettings.NetworkEncryptionType);
                            var candidate = new Candidate(adapter, network, profiles.Contains(key));
                            if (!_candidates.TryGetValue(key, out var existing) ||
                                network.NetworkRssiInDecibelMilliwatts > existing.Network.NetworkRssiInDecibelMilliwatts)
                                _candidates[key] = candidate;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) when (IsAccessDenied(exception))
                    {
                        _status = AccessDenied;
                        _settingsUri = LocationSettings;
                        failed++;
                    }
                    catch (Exception) { failed++; }
                }

                if (_settingsUri != LocationSettings)
                    _status = _adapters.Count == 0 ? "未检测到 Wi‑Fi 适配器。" :
                        failed > 0 ? "部分 Wi‑Fi 适配器扫描失败；请检查无线开关或在系统设置中重试。" :
                        _candidates.Count == 0 ? "未发现附近的 Wi‑Fi。" : "";
            }

            var enabled = await ReadEnabledAsync(token).ConfigureAwait(false);
            var items = new List<ControlConnectionItem>();
            foreach (var adapter in _adapters)
            {
                ConnectionProfile? connectedProfile;
                try
                {
                    connectedProfile = await adapter.NetworkAdapter.GetConnectedProfileAsync()
                        .AsTask(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { connectedProfile = null; }

                foreach (var (id, candidate) in _candidates)
                {
                    if (!ReferenceEquals(candidate.Adapter, adapter)) continue;
                    var network = candidate.Network;
                    var connected = MatchesProfile(connectedProfile, adapter.NetworkAdapter.NetworkAdapterId, network);
                    var security = Security(network.SecuritySettings.NetworkAuthenticationType,
                        network.SecuritySettings.NetworkEncryptionType);
                    var supported = security.CanConnect && !network.IsWiFiDirect &&
                        network.NetworkKind == WiFiNetworkKind.Infrastructure;
                    var detail = connected ? "已连接" : candidate.Saved ? "已保存" : security.Label;
                    if (connected || candidate.Saved) detail += " · " + security.Label;
                    if (!supported) detail += " · 请使用系统设置连接";
                    var signal = Math.Clamp((int)network.SignalBars * 20, 0, 100);
                    items.Add(new(id, network.Ssid, detail, connected,
                        !connected && enabled != false && supported,
                        connected, !connected && supported && security.PasswordSupported && !candidate.Saved, signal));
                }
            }

            return new(enabled, items.OrderByDescending(item => item.Connected)
                .ThenByDescending(item => item.SignalPercent).ThenBy(item => item.Name, StringComparer.CurrentCulture).ToArray(),
                enabled == false ? "Wi‑Fi 已关闭。" : _status, _settingsUri);
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (Exception exception)
        {
            _candidates.Clear();
            _adapters = [];
            _status = IsAccessDenied(exception) ? AccessDenied : "无法读取 Wi‑Fi，请在系统设置中检查适配器后刷新。";
            _settingsUri = IsAccessDenied(exception) ? LocationSettings : WiFiSettings;
            return new(null, [], _status, _settingsUri);
        }
        finally { _gate.Release(); }
    }

    public async Task<ControlOperationResult> SetEnabledAsync(bool enabled, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var access = await Radio.RequestAccessAsync().AsTask(token).ConfigureAwait(false);
            if (access != RadioAccessStatus.Allowed)
                return new(false, "Windows 不允许切换 Wi‑Fi，请使用系统网络设置或硬件无线开关。");
            var radios = (await Radio.GetRadiosAsync().AsTask(token).ConfigureAwait(false))
                .Where(radio => radio.Kind == RadioKind.WiFi).ToArray();
            if (radios.Length == 0) return new(false, "未检测到 Wi‑Fi 无线电。");
            var target = enabled ? RadioState.On : RadioState.Off;
            foreach (var radio in radios)
            {
                token.ThrowIfCancellationRequested();
                if (radio.State == target) continue;
                var result = await radio.SetStateAsync(target).AsTask(token).ConfigureAwait(false);
                if (result != RadioAccessStatus.Allowed)
                    return new(false, "无法切换所有 Wi‑Fi 适配器，请检查系统设置或硬件无线开关。");
            }
            return ControlOperationResult.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (Exception) { return new(false, "无法切换 Wi‑Fi，请使用系统网络设置。"); }
        finally { _gate.Release(); }
    }

    public async Task<ControlOperationResult> ConnectAsync(string id, string? password, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        PasswordCredential? credential = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_candidates.TryGetValue(id, out var candidate) || !IsInCurrentReport(candidate))
                return new(false, "网络列表已变化，请刷新后重新选择网络。");
            var network = candidate.Network;
            var security = Security(network.SecuritySettings.NetworkAuthenticationType,
                network.SecuritySettings.NetworkEncryptionType);
            if (!security.CanConnect || network.IsWiFiDirect || network.NetworkKind != WiFiNetworkKind.Infrastructure)
                return new(false, "此网络需要企业认证或其他连接方式，请使用系统 Wi‑Fi 设置。");
            if (security.PasswordSupported && !candidate.Saved && string.IsNullOrEmpty(password))
                return new(false, "请输入 Wi‑Fi 密码。", PasswordRequired: true);

            // Windows owns saved profiles. Credentials remain local to this call;
            // they are never logged, stored in app preferences, or put in a vault.
            WiFiConnectionResult result;
            if (security.PasswordSupported && !string.IsNullOrEmpty(password))
            {
                credential = new PasswordCredential { Password = password };
                result = await candidate.Adapter.ConnectAsync(network, WiFiReconnectionKind.Manual, credential)
                    .AsTask(token).ConfigureAwait(false);
            }
            else
                result = await candidate.Adapter.ConnectAsync(network, WiFiReconnectionKind.Manual)
                    .AsTask(token).ConfigureAwait(false);

            return result.ConnectionStatus switch
            {
                WiFiConnectionStatus.Success => ControlOperationResult.Success,
                WiFiConnectionStatus.InvalidCredential => new(false, "密码或已保存的凭据无效，请重新输入密码。", security.PasswordSupported),
                WiFiConnectionStatus.NetworkNotAvailable => new(false, "网络已不可用，请刷新后重试。"),
                WiFiConnectionStatus.Timeout => new(false, "连接超时，请检查信号后重试。"),
                WiFiConnectionStatus.AccessRevoked => AccessFailure(),
                WiFiConnectionStatus.UnsupportedAuthenticationProtocol => new(false, "不支持此网络的认证方式，请使用系统 Wi‑Fi 设置。"),
                _ => new(false, "Wi‑Fi 连接失败，请重试或使用系统 Wi‑Fi 设置。")
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (Exception exception)
        {
            return IsAccessDenied(exception) ? AccessFailure() : new(false, "Wi‑Fi 连接失败，请刷新或使用系统 Wi‑Fi 设置。");
        }
        finally
        {
            credential = null;
            password = null;
            _gate.Release();
        }
    }

    public async Task<ControlOperationResult> DisconnectAsync(string id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_candidates.TryGetValue(id, out var candidate))
                return new(false, "网络列表已变化，请刷新后重新选择网络。");
            var adapterId = candidate.Adapter.NetworkAdapter.NetworkAdapterId;
            var profile = await candidate.Adapter.NetworkAdapter.GetConnectedProfileAsync()
                .AsTask(token).ConfigureAwait(false);
            if (!MatchesProfile(profile, adapterId, candidate.Network))
                return new(false, "此网络当前未连接；连接状态已变化，请刷新。");
            token.ThrowIfCancellationRequested();
            candidate.Adapter.Disconnect();
            return ControlOperationResult.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (Exception exception)
        {
            return IsAccessDenied(exception) ? AccessFailure() : new(false, "无法断开 Wi‑Fi，请使用系统 Wi‑Fi 设置。");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads SSID from connection profiles without requesting a Wi-Fi scan or location consent.</summary>
    public static string ReadConnectedName()
    {
        try
        {
            var profile = NetworkInformation.GetConnectionProfiles()
                .Where(item => item.IsWlanConnectionProfile && item.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
                .OrderByDescending(item => item.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess)
                .FirstOrDefault();
            return profile?.WlanConnectionProfileDetails?.GetConnectedSsid() ?? "";
        }
        catch (Exception) { return ""; }
    }

    private static async Task<bool?> ReadEnabledAsync(CancellationToken token)
    {
        try
        {
            var radios = (await Radio.GetRadiosAsync().AsTask(token).ConfigureAwait(false))
                .Where(radio => radio.Kind == RadioKind.WiFi).ToArray();
            if (radios.Any(radio => radio.State == RadioState.On)) return true;
            return radios.Length == 0 || radios.Any(radio => radio.State == RadioState.Unknown) ? null : false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    private static HashSet<string> ReadKnownProfiles()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                if (!profile.IsWlanConnectionProfile || profile.NetworkAdapter is null) continue;
                try
                {
                    var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                    if (!string.IsNullOrEmpty(ssid))
                        result.Add(NetworkKey(profile.NetworkAdapter.NetworkAdapterId, ssid,
                            profile.NetworkSecuritySettings.NetworkAuthenticationType,
                            profile.NetworkSecuritySettings.NetworkEncryptionType));
                }
                catch (Exception) { /* A removed or inaccessible profile is not a usable credential. */ }
            }
        }
        catch (Exception) { /* Unknown profiles can still be connected with an explicit password. */ }
        return result;
    }

    private static bool MatchesProfile(ConnectionProfile? profile, Guid adapterId, WiFiAvailableNetwork network) =>
        profile is { IsWlanConnectionProfile: true } && profile.NetworkAdapter?.NetworkAdapterId == adapterId &&
        string.Equals(profile.WlanConnectionProfileDetails?.GetConnectedSsid(), network.Ssid, StringComparison.Ordinal) &&
        profile.NetworkSecuritySettings.NetworkAuthenticationType == network.SecuritySettings.NetworkAuthenticationType &&
        profile.NetworkSecuritySettings.NetworkEncryptionType == network.SecuritySettings.NetworkEncryptionType;

    private static bool IsInCurrentReport(Candidate candidate) => candidate.Adapter.NetworkReport.AvailableNetworks.Any(network =>
        string.Equals(network.Ssid, candidate.Network.Ssid, StringComparison.Ordinal) &&
        string.Equals(network.Bssid, candidate.Network.Bssid, StringComparison.OrdinalIgnoreCase) &&
        network.SecuritySettings.NetworkAuthenticationType == candidate.Network.SecuritySettings.NetworkAuthenticationType &&
        network.SecuritySettings.NetworkEncryptionType == candidate.Network.SecuritySettings.NetworkEncryptionType);

    internal static string NetworkKey(Guid adapterId, string ssid, NetworkAuthenticationType authentication, NetworkEncryptionType encryption) =>
        $"{adapterId:N}:{(int)authentication}:{(int)encryption}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(ssid))}";

    internal static (bool CanConnect, bool PasswordSupported, string Label) Security(
        NetworkAuthenticationType authentication, NetworkEncryptionType encryption) => authentication switch
    {
        NetworkAuthenticationType.None or NetworkAuthenticationType.Open80211 when encryption == NetworkEncryptionType.None => (true, false, "开放网络"),
        NetworkAuthenticationType.Owe => (true, false, "增强开放网络"),
        NetworkAuthenticationType.WpaPsk => (true, true, "WPA 个人网络"),
        NetworkAuthenticationType.RsnaPsk => (true, true, "WPA2 个人网络"),
        NetworkAuthenticationType.Wpa3Sae => (true, true, "WPA3 个人网络"),
        NetworkAuthenticationType.Wpa or NetworkAuthenticationType.Rsna or NetworkAuthenticationType.Wpa3 => (false, false, "企业网络"),
        _ => (false, false, "受保护或不支持的网络")
    };

    private static bool IsAccessDenied(Exception exception) => exception is UnauthorizedAccessException ||
        exception.HResult == unchecked((int)0x80070005);

    private ControlOperationResult AccessFailure()
    {
        _status = AccessDenied;
        _settingsUri = LocationSettings;
        return new(false, AccessDenied);
    }

    public void Dispose()
    {
        // No event subscriptions or native handles are owned. Leave the semaphore
        // alive so an already cancelled asynchronous operation can release it.
        Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed record Candidate(WiFiAdapter Adapter, WiFiAvailableNetwork Network, bool Saved);
}
