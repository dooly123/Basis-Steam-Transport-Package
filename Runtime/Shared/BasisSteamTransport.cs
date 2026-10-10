using Basis.Network.Core;

namespace Basis.Network.Steam
{
    public static class BasisSteamTransport
    {
        public const string StackId = "steam";
        public const string DisplayName = "Steam";
        public static void Register()
        {
            BasisNetworkStackRegistry.Register(StackId, DisplayName, (listener, configuration) => new BasisSteamNetManager(listener, configuration));
            BasisNetworkStackRegistry.RegisterParser(StackId, new BasisSteamConnectionTargetParser());
            BasisNetworkStackRegistry.RegisterProbe(StackId, BasisSteamProbe.ProbeAsync);
            BasisNetworkStackRegistry.RegisterAddressMatcher(StackId, BasisSteamConnectionTargetParser.IsSteamAddress);
            BasisTransportConfigStore.RegisterType(StackId, typeof(BasisSteamTransportConfig));
            BasisConfigXmlDocs.Register(typeof(BasisSteamTransportConfig),
                " Steam transport (sidecar for the 'steam' network stack). Only used when NetworkStackId in config.xml includes 'steam'. Players connect through Steam's relay network to the server's Steam ID, so the server needs no open ports. On a dedicated server Basis logs in to Steam as a game server; see the Basis Steam Transport package README for the Steam files it needs. ",
                new BasisConfigXmlDocs.FieldDoc("ConfigVersion", " Config schema version, managed automatically; new settings are added to this file on load - don't edit by hand. "),
                new BasisConfigXmlDocs.FieldDoc("AppId", " Steam app id the server logs in as. Players must run the same app id. 0 = the SteamAppId environment variable, then steam_appid.txt next to the server, then 3157090 (Basis Labs, the Basis app on Steam). uint. ", " ===== Dedicated server ===== "),
                new BasisConfigXmlDocs.FieldDoc("GameServerToken", " Game Server Login Token for this app id, from https://steamcommunity.com/dev/managegameservers. Empty = log in anonymously, which works but gives the server a new Steam ID on every start, so the address players use (steam:<SteamID64>) changes; with a token it stays the same. string. "),
                new BasisConfigXmlDocs.FieldDoc("ServerPollIntervalMs", " How long the server's Steam thread sleeps between passes (Steam callbacks, receiving, pings). Lower answers faster and costs more idle CPU. int (ms). "),
                new BasisConfigXmlDocs.FieldDoc("VirtualPort", " Steam relay virtual port, a port number inside Steam's network. Players address the server as steam:<SteamID64>, adding :<VirtualPort> when it is not 0. int. ", " ===== Relay ===== "),
                new BasisConfigXmlDocs.FieldDoc("Mtu", " Payload size the server assumes for a Steam peer when it packs avatar updates. int (bytes). "),
                new BasisConfigXmlDocs.FieldDoc("ConnectTimeoutMs", " How long a connection may take to be established through Steam. int (ms). ", " ===== Timing ===== "),
                new BasisConfigXmlDocs.FieldDoc("DisconnectTimeoutMs", " Time with nothing received from a peer before Steam drops the connection. int (ms). "),
                new BasisConfigXmlDocs.FieldDoc("SendBufferBytes", " Steam's send buffer per connection. int (bytes). "),
                new BasisConfigXmlDocs.FieldDoc("PingIntervalMs", " Interval between pings used to measure round trip time. int (ms). "),
                new BasisConfigXmlDocs.FieldDoc("PendingConnectionTimeoutMs", " How long a connected peer has to send its join request before it is dropped. int (ms). "),
                new BasisConfigXmlDocs.FieldDoc("MaxPendingConnections", " Connections allowed to wait for their join request at once; more are refused until some finish. int. ", " ===== Limits ===== "),
                new BasisConfigXmlDocs.FieldDoc("MaxMessagesPerPoll", " Most messages read from Steam in one pass. int. "),
                new BasisConfigXmlDocs.FieldDoc("MaxPollMilliseconds", " Time budget for reading messages in one pass. float (ms). "));
        }
        public static BasisSteamTransportConfig Config => BasisTransportConfigStore.Get<BasisSteamTransportConfig>(StackId);
    }
}
