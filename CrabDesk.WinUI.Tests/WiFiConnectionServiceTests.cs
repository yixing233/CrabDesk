using CrabDesk.Native;
using Windows.Networking.Connectivity;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WiFiConnectionServiceTests
{
    [Theory]
    [InlineData(NetworkAuthenticationType.Open80211, NetworkEncryptionType.None, true, false)]
    [InlineData(NetworkAuthenticationType.Open80211, NetworkEncryptionType.Wep, false, false)]
    [InlineData(NetworkAuthenticationType.SharedKey80211, NetworkEncryptionType.Wep, false, false)]
    [InlineData(NetworkAuthenticationType.RsnaPsk, NetworkEncryptionType.Ccmp, true, true)]
    [InlineData(NetworkAuthenticationType.WpaPsk, NetworkEncryptionType.Tkip, true, true)]
    [InlineData(NetworkAuthenticationType.Wpa3Sae, NetworkEncryptionType.Ccmp, true, true)]
    [InlineData(NetworkAuthenticationType.Wpa3, NetworkEncryptionType.Ccmp, false, false)]
    [InlineData(NetworkAuthenticationType.Rsna, NetworkEncryptionType.Ccmp, false, false)]
    [InlineData(NetworkAuthenticationType.Wpa, NetworkEncryptionType.Tkip, false, false)]
    [InlineData(NetworkAuthenticationType.Unknown, NetworkEncryptionType.Unknown, false, false)]
    [InlineData(NetworkAuthenticationType.Owe, NetworkEncryptionType.Ccmp, true, false)]
    public void PasswordSupportDistinguishesPersonalEnterpriseAndEncryptedOpenAuthentication(
        NetworkAuthenticationType authentication, NetworkEncryptionType encryption, bool canConnect, bool passwordSupported)
    {
        var result = WiFiConnectionService.Security(authentication, encryption);

        Assert.Equal(canConnect, result.CanConnect);
        Assert.Equal(passwordSupported, result.PasswordSupported);
    }

    [Fact]
    public void NetworkIdentityIncludesAdapterExactSsidAndSecurity()
    {
        var firstAdapter = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var secondAdapter = Guid.Parse("00112233-4455-6677-8899-aabbccddeef0");
        var identities = new[]
        {
            WiFiConnectionService.NetworkKey(firstAdapter, "Office", NetworkAuthenticationType.RsnaPsk, NetworkEncryptionType.Ccmp),
            WiFiConnectionService.NetworkKey(secondAdapter, "Office", NetworkAuthenticationType.RsnaPsk, NetworkEncryptionType.Ccmp),
            WiFiConnectionService.NetworkKey(firstAdapter, "office", NetworkAuthenticationType.RsnaPsk, NetworkEncryptionType.Ccmp),
            WiFiConnectionService.NetworkKey(firstAdapter, "Office ", NetworkAuthenticationType.RsnaPsk, NetworkEncryptionType.Ccmp),
            WiFiConnectionService.NetworkKey(firstAdapter, "Office", NetworkAuthenticationType.Open80211, NetworkEncryptionType.None),
            WiFiConnectionService.NetworkKey(firstAdapter, "Office", NetworkAuthenticationType.RsnaPsk, NetworkEncryptionType.Tkip)
        };

        Assert.Equal(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task CommandsRejectMissingCandidatesBeforeTouchingHardware()
    {
        using var service = new WiFiConnectionService();

        var connect = await service.ConnectAsync("removed-network", null);
        var disconnect = await service.DisconnectAsync("removed-network");

        Assert.False(connect.Succeeded);
        Assert.False(connect.PasswordRequired);
        Assert.Contains("刷新", connect.Error);
        Assert.False(disconnect.Succeeded);
        Assert.Contains("刷新", disconnect.Error);
    }
}
