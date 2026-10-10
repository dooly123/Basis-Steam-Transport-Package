using Basis.Network.Core;
using Basis.Network.Steam;
using System.Net;
using System.Reflection;
using System.Xml.Serialization;
using Xunit;

namespace BasisServerTests;

public class SteamAddressTests
{
    private const ulong Id = 76561198000000001UL;

    [Theory]
    [InlineData("steam:76561198000000001:7")]
    [InlineData("76561198000000001")]
    [InlineData("steam://76561198000000001")]
    [InlineData("76561198000000001#key")]
    public void SteamIdsParseFromEveryAddressForm(string raw)
    {
        Assert.True(BasisSteamConnectionTargetParser.TryParseSteamId(raw, out ulong steamId));
        Assert.Equal(Id, steamId);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("steam:0")]
    [InlineData("-5")]
    [InlineData("")]
    public void AnythingElseIsNotASteamId(string raw)
    {
        Assert.False(BasisSteamConnectionTargetParser.TryParseSteamId(raw, out _));
    }

    [Fact]
    public void OnlySteamAddressesAreClaimed()
    {
        Assert.True(BasisSteamConnectionTargetParser.IsSteamAddress(" Steam:76561198000000001"));
        Assert.False(BasisSteamConnectionTargetParser.IsSteamAddress("server1.basisvr.org:4296"));
        Assert.False(BasisSteamConnectionTargetParser.IsSteamAddress("wss://example.org/basis"));
    }

    [Theory]
    [InlineData("steam:76561198000000001:7", "76561198000000001", "7", "", "steam:76561198000000001:7")]
    [InlineData("76561198000000001", "76561198000000001", "0", "", "steam:76561198000000001")]
    [InlineData("steam:76561198000000001:99999", "76561198000000001", "0", "", "steam:76561198000000001")]
    [InlineData("steam:76561198000000001:7#s3cr#et", "76561198000000001", "7", "s3cr#et", "steam:76561198000000001:7#s3cr#et")]
    public void ParseAndFormatRoundTrip(string raw, string address, string port, string password, string formatted)
    {
        BasisSteamConnectionTargetParser parser = new BasisSteamConnectionTargetParser();
        ConnectionTarget target = new ConnectionTarget(BasisSteamTransport.StackId, raw);
        parser.Parse(target);
        Assert.Equal(address, target.Get(ConnectionTarget.Keys.Address));
        Assert.Equal(port, target.Get(ConnectionTarget.Keys.Port));
        Assert.Equal(password, target.Get(ConnectionTarget.Keys.Password));
        Assert.Equal(formatted, parser.Format(target));
    }

    [Fact]
    public void PseudoAddressesRoundTripAndStayDistinct()
    {
        IPAddress address = BasisSteamAddress.For(Id);
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetworkV6, address.AddressFamily);
        Assert.True(BasisSteamAddress.TryGetSteamId(address, out ulong back));
        Assert.Equal(Id, back);
        Assert.NotEqual(address, BasisSteamAddress.For(Id + 1));
        Assert.Equal(address, IPAddress.Parse(address.ToString()));
        Assert.False(BasisSteamAddress.TryGetSteamId(IPAddress.Parse("::1"), out _));
        Assert.True(BasisSteamAddress.TryGetSteamId(BasisSteamAddress.For(ulong.MaxValue), out ulong max));
        Assert.Equal(ulong.MaxValue, max);
    }

    [Fact]
    public void BulkTrafficAndResourcesUseTheirOwnLanes()
    {
        MethodInfo lane = typeof(BasisSteamNetManager).GetMethod("LaneFor", BindingFlags.NonPublic | BindingFlags.Static)!;
        ushort Lane(byte channel, DeliveryMethod method) => (ushort)lane.Invoke(null, new object[] { channel, method })!;
        Assert.Equal(0, Lane(BasisNetworkCommons.VoiceChannel, DeliveryMethod.Sequenced));
        Assert.Equal(0, Lane(BasisNetworkCommons.PlayerAvatarHighChannel, DeliveryMethod.Unreliable));
        Assert.Equal(1, Lane(BasisNetworkCommons.AuthIdentityChannel, DeliveryMethod.ReliableOrdered));
        Assert.Equal(2, Lane(BasisNetworkCommons.SceneChannel, DeliveryMethod.ReliableOrdered));
        Assert.Equal(2, Lane(BasisNetworkCommons.CompressedAvatarBundleChannel, DeliveryMethod.ReliableOrdered));
    }
}

[Collection("BasisServer shared network statics")]
public class SteamPackageTests
{
    [Fact]
    public void ThePackageRegistersTheSteamStack()
    {
        BasisSteamTransport.Register();
        Assert.True(BasisNetworkStackRegistry.IsRegistered(BasisSteamTransport.StackId));
        Assert.Equal("Steam", BasisNetworkStackRegistry.GetDisplayName(BasisSteamTransport.StackId));
        Assert.IsType<BasisSteamConnectionTargetParser>(BasisNetworkStackRegistry.GetParser(BasisSteamTransport.StackId));
        Assert.True(BasisNetworkStackRegistry.TryMatchAddress("steam:76561198000000001", out string matched));
        Assert.Equal(BasisSteamTransport.StackId, matched);
        Assert.Equal(typeof(BasisSteamTransportConfig), BasisTransportConfigStore.RegisteredTypes[BasisSteamTransport.StackId]);
        Assert.Contains(typeof(BasisSteamServerPackage).Assembly.GetCustomAttributes<BasisNetworkPackageAttribute>(), attribute => attribute.Entry == typeof(BasisSteamServerPackage));
    }

    [Fact]
    public void TheSettingsFileIsDocumented()
    {
        BasisSteamTransport.Register();
        using var writer = new StringWriter();
        BasisConfigXmlDocs.Serialize(new XmlSerializer(typeof(BasisSteamTransportConfig)), typeof(BasisSteamTransportConfig), new BasisSteamTransportConfig(), writer);
        string xml = writer.ToString();
        Assert.Contains("Steam transport (sidecar for the 'steam' network stack)", xml);
        Assert.Contains("Game Server Login Token", xml);
        Assert.Contains("<GameServerToken />", xml);
        Assert.Contains("<AppId>0</AppId>", xml);
    }

    [Fact]
    public void SteamAppIdsResolveFromConfigBeforeBasisLabs()
    {
        Assert.Equal(1234u, BasisSteamHost.ResolveAppId(1234));
        string previous = Environment.GetEnvironmentVariable(BasisSteamHost.AppIdEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(BasisSteamHost.AppIdEnvironmentVariable, "5678");
            Assert.Equal(5678u, BasisSteamHost.ResolveAppId(0));
            Environment.SetEnvironmentVariable(BasisSteamHost.AppIdEnvironmentVariable, null);
            Assert.Equal(3157090u, BasisSteamHost.BasisLabsAppId);
            Assert.Equal(BasisSteamHost.BasisLabsAppId, BasisSteamHost.ResolveAppId(0));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BasisSteamHost.AppIdEnvironmentVariable, previous);
        }
    }
}
