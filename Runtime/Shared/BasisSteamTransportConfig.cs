using Basis.Network.Core;
using System;

namespace Basis.Network.Steam
{
    [Serializable]
    public sealed class BasisSteamTransportConfig : IBasisTransportTimeouts
    {
        public const int CurrentConfigVersion = 1;
        public int ConfigVersion = 0;
        public uint AppId = 0;
        public string GameServerToken = "";
        public int ServerPollIntervalMs = 1;
        public int VirtualPort = 0;
        public int Mtu = 1200;
        public int ConnectTimeoutMs = 10000;
        public int DisconnectTimeoutMs = 10000;
        public int SendBufferBytes = 4 * 1024 * 1024;
        public int PingIntervalMs = 1000;
        public int PendingConnectionTimeoutMs = 10000;
        public int MaxPendingConnections = 64;
        public int MaxMessagesPerPoll = 1024;
        public float MaxPollMilliseconds = 2f;
        int IBasisTransportTimeouts.DisconnectTimeoutMs => DisconnectTimeoutMs;
    }
}
