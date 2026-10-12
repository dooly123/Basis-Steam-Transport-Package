using Basis.Network.Core;
using Steamworks;
using Steamworks.Data;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
#if UNITY_5_3_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Basis.Network.Steam
{
    internal static class BasisSteamPacket
    {
        public const byte Application = 0, ConnectRequest = 1, AssignPeer = 2, Ping = 3, Pong = 4, Reject = 5, Disconnect = 6, Query = 7, QueryResponse = 8;
        public const int ApplicationHeaderBytes = 3, PingBytes = 9, PongBytes = 17;
        public const ushort TransientLane = 0, ControlLane = 1, ResourceLane = 2;
    }
    internal sealed class BasisSteamPending
    {
        public Connection Connection;
        public ulong SteamId;
        public long Created;
        public bool Requested, Queried;
    }
    internal sealed class BasisSteamConnectionRequest : ConnectionRequest
    {
        private readonly BasisSteamNetManager owner;
        private readonly BasisSteamPending pending;
        public BasisSteamConnectionRequest(BasisSteamNetManager owner, BasisSteamPending pending, byte[] payload, int virtualPort)
        {
            this.owner = owner;
            this.pending = pending;
            Data = new NetDataReader(payload);
            RemoteEndPoint = new IPEndPoint(BasisSteamAddress.For(pending.SteamId), virtualPort);
        }
        public NetDataReader Data { get; }
        public IPEndPoint RemoteEndPoint { get; }
        public string VerifiedIdentity => pending.SteamId == 0 ? null : BasisSteamTransport.IdentityPrefix + pending.SteamId.ToString(CultureInfo.InvariantCulture);
        public NetPeer Accept()
        {
            return owner.Accept(pending);
        }
        public void Reject(NetDataWriter w)
        {
            owner.Reject(pending, w);
        }
    }
    internal sealed class BasisSteamServerSocket : SocketManager
    {
        public BasisSteamNetManager Owner;
        public override void OnConnecting(Connection connection, ConnectionInfo info)
        {
            base.OnConnecting(connection, info);
            Owner?.OnServerConnecting(connection, info);
        }
        public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            Owner?.OnServerMessage(connection, data, size);
        }
        public override void OnDisconnected(Connection connection, ConnectionInfo info)
        {
            Owner?.OnServerDisconnected(connection, info);
            base.OnDisconnected(connection, info);
        }
    }
    internal sealed class BasisSteamClientConnection : ConnectionManager
    {
        public BasisSteamNetManager Owner;
        public BasisSteamNetPeer Peer;
        public byte[] Payload;
        public override void OnConnected(ConnectionInfo info)
        {
            base.OnConnected(info);
            Owner?.OnClientConnected(this);
        }
        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            Owner?.OnClientMessage(this, data, size);
        }
        public override void OnDisconnected(ConnectionInfo info)
        {
            Owner?.OnClientDisconnected(this, info);
            base.OnDisconnected(info);
        }
    }
#if UNITY_5_3_OR_NEWER
    [AutoStaticsCleanup]
#endif
    public sealed partial class BasisSteamNetManager : NetManager, IBasisSharedPeerIds, IBasisTransportHealth
    {
        private const int ListenerTimeoutMs = 10000;
        private const int ReceiveBatch = 64;
        private static readonly int[] lanePriorities = { 10, 10, 10 };
        private static readonly ushort[] laneWeights = { 6, 3, 1 };
        private static readonly List<BasisSteamNetManager> active = new List<BasisSteamNetManager>();
#if UNITY_5_3_OR_NEWER
        [NoAutoStaticsCleanup]
#endif
        private static readonly object activeLock = new object();
        private static BasisSteamNetManager[] activeSnapshot = Array.Empty<BasisSteamNetManager>();
        private readonly EventBasedNetListener listener;
        private readonly BasisSteamTransportConfig config;
        private readonly NetStatistics statistics = new NetStatistics();
        private readonly Dictionary<uint, BasisSteamPending> pending = new Dictionary<uint, BasisSteamPending>();
        private readonly Dictionary<uint, BasisSteamNetPeer> peers = new Dictionary<uint, BasisSteamNetPeer>();
        private readonly List<BasisSteamPending> expired = new List<BasisSteamPending>();
        private readonly byte[] pingBuffer = new byte[BasisSteamPacket.PingBytes];
        private BasisPeerIdAllocator peerIds = new BasisPeerIdAllocator();
        private bool ownsPeerIds = true;
        private BasisSteamServerSocket server;
        private BasisSteamClientConnection client;
        private BasisSteamNetPeer clientPeer;
        private Func<int, bool, int> serverReceive, clientReceive;
        private volatile bool running, listening, wantsListener;
        private volatile int connectedCount;
        private int virtualPort;
        private long nextPing, nextSweep;
        public readonly int Mtu;
        public BasisSteamNetManager(EventBasedNetListener listener, Configuration configuration) : this(listener, BasisSteamTransport.Config)
        {
        }
        public BasisSteamNetManager(EventBasedNetListener listener, BasisSteamTransportConfig config)
        {
            this.listener = listener ?? throw new ArgumentNullException(nameof(listener));
            this.config = config ?? new BasisSteamTransportConfig();
            Mtu = this.config.Mtu > 0 ? this.config.Mtu : 1200;
            BasisSteamHost.Prepare();
        }
        public string StackId => BasisSteamTransport.StackId;
        public bool IsRunning => running && (!wantsListener || listening);
        public string ListenDescription => listening ? $"Steam relay, virtual port {virtualPort}" : null;
        public NetStatistics Statistics => statistics;
        public void WriteHealth(IBasisHealthWriter writer)
        {
            writer.Text("mode", BasisSteamHost.Mode);
            writer.Text("steamId", BasisSteamHost.LocalSteamId.ToString(CultureInfo.InvariantCulture));
            writer.Flag("loggedOn", BasisSteamHost.IsLoggedOn);
            writer.Flag("listening", listening);
            writer.Number("virtualPort", virtualPort);
            writer.Number("pending", pending.Count);
        }
        public int ConnectedPeersCount => connectedCount;
        public void UsePeerIdAllocator(BasisPeerIdAllocator allocator)
        {
            peerIds = allocator ?? new BasisPeerIdAllocator();
            ownsPeerIds = allocator == null;
        }
        public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            if (running)
            {
                return;
            }
            running = true;
            nextPing = Stopwatch.GetTimestamp();
            nextSweep = nextPing;
            Register(this);
            if (SetPort <= 0)
            {
                return;
            }
            wantsListener = true;
            virtualPort = config.VirtualPort;
            if (BasisSteamHost.IsSteamThread)
            {
                OpenListener();
                return;
            }
            ManualResetEventSlim opened = new ManualResetEventSlim(false);
            BasisSteamHost.Actions.Enqueue(() =>
            {
                try
                {
                    OpenListener();
                }
                finally
                {
                    opened.Set();
                }
            });
            if (!opened.Wait(ListenerTimeoutMs))
            {
                BNL.LogError("The Steam relay listener was not opened in time; the Steam thread did not get to it.");
            }
        }
        public void StartManual(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            Start(IPv4Address, IPv6Address, SetPort);
        }
        public void PollEvents()
        {
        }
        public void ManualUpdate(float elapsedMilliseconds)
        {
        }
        private void OpenListener()
        {
            if (!running || server != null)
            {
                return;
            }
            if (!BasisSteamHost.EnsureStarted())
            {
                BNL.LogError($"The Steam relay listener could not start: {BasisSteamHost.LastError}");
                return;
            }
            BasisSteamServerSocket socket = SteamNetworkingSockets.CreateRelaySocket<BasisSteamServerSocket>(virtualPort);
            if (socket == null || (uint)socket.Socket == 0)
            {
                BNL.LogError($"Steam refused to open a relay listener on virtual port {virtualPort}.");
                return;
            }
            socket.Owner = this;
            server = socket;
            serverReceive = socket.Receive;
            listening = true;
            BNL.Log(BasisSteamHost.IsLoggedOn
                ? $"Listening for Steam relay connections on virtual port {virtualPort} as {BasisSteamHost.LocalSteamId}"
                : $"Listening for Steam relay connections on virtual port {virtualPort}; the Steam ID follows once Steam login completes");
        }
        public NetPeer Connect(string sIP, int port, NetDataWriter Writer)
        {
            ulong hostSteamId = 0;
            bool local = IsLocalAddress(sIP);
            if (local)
            {
                port = config.VirtualPort;
            }
            else if (!BasisSteamConnectionTargetParser.TryParseSteamId(sIP, out hostSteamId))
            {
                BNL.LogError($"'{sIP}' is not a SteamID64. The Steam transport connects to the host's Steam account.");
                return null;
            }
            if (!running)
            {
                Start(IPAddress.Any, IPAddress.IPv6Any, 0);
            }
            byte[] payload = new byte[Writer.Length + 1];
            payload[0] = BasisSteamPacket.ConnectRequest;
            Buffer.BlockCopy(Writer.Data, 0, payload, 1, Writer.Length);
            BasisSteamNetPeer peer = new BasisSteamNetPeer(this, hostSteamId, default, true);
            clientPeer = peer;
            BasisSteamHost.Run(() => OpenConnection(peer, port, payload));
            return peer;
        }
        private void OpenConnection(BasisSteamNetPeer peer, int port, byte[] payload)
        {
            if (!running || clientPeer != peer)
            {
                return;
            }
            if (!BasisSteamHost.EnsureStarted())
            {
                BNL.LogError($"Steam is not available: {BasisSteamHost.LastError}");
                FailLater(peer);
                return;
            }
            if (peer.SteamId == 0)
            {
                peer.SetSteamId(BasisSteamHost.LocalSteamId);
            }
            if (peer.SteamId == BasisSteamHost.LocalSteamId)
            {
                BNL.LogWarning("Connecting to this machine's own Steam relay listener, so the host's own traffic goes through Steam. Host with the litenetlib,steam stack list to keep it local.");
            }
            BasisSteamClientConnection connection = SteamNetworkingSockets.ConnectRelay<BasisSteamClientConnection>(peer.SteamId, port);
            if (connection == null || connection.Connection.Id == 0)
            {
                BNL.LogError($"Steam could not start a relay connection to {peer.SteamId}.");
                FailLater(peer);
                return;
            }
            connection.Owner = this;
            connection.Peer = peer;
            connection.Payload = payload;
            peer.Connection = connection.Connection;
            client = connection;
            clientReceive = connection.Receive;
            BNL.Log($"Connecting to {peer.SteamId} over the Steam relay (virtual port {port})");
        }
        private static bool IsLocalAddress(string address)
        {
            string value = (address ?? string.Empty).Trim();
            return string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase) || value == "127.0.0.1" || value == "::1" || value == "[::1]";
        }
        private void FailLater(BasisSteamNetPeer peer)
        {
            BasisSteamHost.Actions.Enqueue(() => FinishClient(peer, DisconnectReason.ConnectionFailed, null));
        }
        public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint)
        {
            if (writer == null || writer.Length <= 0 || !BasisSteamAddress.TryGetSteamId(remoteEndPoint?.Address, out ulong steamId))
            {
                return false;
            }
            byte[] packet = new byte[writer.Length + 1];
            packet[0] = BasisSteamPacket.QueryResponse;
            Buffer.BlockCopy(writer.Data, 0, packet, 1, writer.Length);
            BasisSteamHost.Run(() => AnswerQuery(steamId, packet));
            return true;
        }
        private void AnswerQuery(ulong steamId, byte[] packet)
        {
            BasisSteamPending found = null;
            foreach (BasisSteamPending entry in pending.Values)
            {
                if (entry.Queried && entry.SteamId == steamId)
                {
                    found = entry;
                    break;
                }
            }
            if (found == null)
            {
                return;
            }
            pending.Remove(found.Connection.Id);
            SendRaw(found.Connection, packet, packet.Length, SendType.Reliable, BasisSteamPacket.ControlLane);
            found.Connection.Close(true, 0, "Query answered");
        }
        public void Stop()
        {
            if (!running)
            {
                return;
            }
            running = false;
            listening = false;
            Unregister(this);
            BasisSteamHost.Run(CloseEverything);
        }
        private void CloseEverything()
        {
            bool steam = BasisSteamHost.IsValid;
            foreach (BasisSteamNetPeer peer in peers.Values)
            {
                Interlocked.Exchange(ref peer.Closed, 1);
                if (steam)
                {
                    peer.Connection.Close(false, 0, "Server stopped");
                }
                if (!ownsPeerIds)
                {
                    peerIds.Release(peer.Id);
                }
            }
            foreach (BasisSteamPending entry in pending.Values)
            {
                if (steam)
                {
                    entry.Connection.Close(false, 0, "Server stopped");
                }
            }
            if (steam)
            {
                server?.Close();
                client?.Close(false, 0, "Disconnected");
            }
            BasisSteamNetPeer orphan = clientPeer;
            peers.Clear();
            pending.Clear();
            server = null;
            client = null;
            clientPeer = null;
            serverReceive = null;
            clientReceive = null;
            connectedCount = 0;
            wantsListener = false;
            if (ownsPeerIds)
            {
                peerIds.Reset();
            }
            if (orphan != null && Interlocked.Exchange(ref orphan.Closed, 1) == 0)
            {
                listener.RaisePeerDisconnected(orphan, Info(DisconnectReason.DisconnectPeerCalled, null));
            }
        }
        internal void OnServerConnecting(Connection connection, ConnectionInfo info)
        {
            if (!running || pending.Count >= config.MaxPendingConnections)
            {
                connection.Close(false, 0, "Busy");
                return;
            }
            Result lanes = connection.ConfigureConnectionLanes(lanePriorities, laneWeights);
            if (lanes != Result.OK)
            {
                BNL.LogWarning($"Steam lanes could not be configured for {info.Identity}: {lanes}");
            }
            pending[connection.Id] = new BasisSteamPending
            {
                Connection = connection,
                SteamId = info.Identity.SteamId.Value,
                Created = Stopwatch.GetTimestamp(),
            };
        }
        internal void OnServerMessage(Connection connection, IntPtr data, int size)
        {
            if (size <= 0)
            {
                return;
            }
            byte[] buffer = Copy(data, size);
            bool keep = false;
            try
            {
                Interlocked.Increment(ref statistics.PacketsReceived);
                Interlocked.Add(ref statistics.BytesReceived, size);
                if (peers.TryGetValue(connection.Id, out BasisSteamNetPeer peer))
                {
                    keep = Dispatch(peer, buffer, size, false);
                    return;
                }
                if (pending.TryGetValue(connection.Id, out BasisSteamPending entry))
                {
                    if (buffer[0] == BasisSteamPacket.Query)
                    {
                        if (!entry.Requested && !entry.Queried)
                        {
                            entry.Queried = true;
                            keep = true;
                            listener.RaiseNetworkReceiveUnconnected(new IPEndPoint(BasisSteamAddress.For(entry.SteamId), virtualPort), new NetPacketReader(buffer, 1, size, () => ArrayPool<byte>.Shared.Return(buffer)));
                        }
                        return;
                    }
                    HandleConnectRequest(entry, buffer, size);
                    return;
                }
                connection.Close(false, 0, "Unknown connection");
            }
            catch (Exception ex)
            {
                BNL.LogError($"Steam server message failed: {ex}");
            }
            finally
            {
                if (!keep)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        private void HandleConnectRequest(BasisSteamPending entry, byte[] buffer, int size)
        {
            if (entry.Requested || buffer[0] != BasisSteamPacket.ConnectRequest || size < 2)
            {
                pending.Remove(entry.Connection.Id);
                entry.Connection.Close(false, 0, "Invalid connect request");
                return;
            }
            entry.Requested = true;
            byte[] payload = new byte[size - 1];
            Buffer.BlockCopy(buffer, 1, payload, 0, payload.Length);
            listener.RaiseConnectionRequest(new BasisSteamConnectionRequest(this, entry, payload, virtualPort));
        }
        internal NetPeer Accept(BasisSteamPending entry)
        {
            if (!pending.Remove(entry.Connection.Id))
            {
                return null;
            }
            BasisSteamNetPeer peer = new BasisSteamNetPeer(this, entry.SteamId, entry.Connection, false);
            peer.AssignId(peerIds.Allocate());
            peers[entry.Connection.Id] = peer;
            connectedCount = peers.Count;
            byte[] packet = new byte[3];
            packet[0] = BasisSteamPacket.AssignPeer;
            packet[1] = (byte)peer.Id;
            packet[2] = (byte)(peer.Id >> 8);
            SendRaw(entry.Connection, packet, packet.Length, SendType.Reliable, BasisSteamPacket.ControlLane);
            listener.RaisePeerConnected(peer);
            return peer;
        }
        internal void Reject(BasisSteamPending entry, NetDataWriter writer)
        {
            if (!pending.Remove(entry.Connection.Id))
            {
                return;
            }
            int length = writer != null ? writer.Length : 0;
            byte[] packet = new byte[length + 1];
            packet[0] = BasisSteamPacket.Reject;
            if (length > 0)
            {
                Buffer.BlockCopy(writer.Data, 0, packet, 1, length);
            }
            SendRaw(entry.Connection, packet, packet.Length, SendType.Reliable, BasisSteamPacket.ControlLane);
            entry.Connection.Close(true, 0, "Rejected");
        }
        internal void OnServerDisconnected(Connection connection, ConnectionInfo info)
        {
            if (pending.Remove(connection.Id))
            {
                return;
            }
            if (peers.TryGetValue(connection.Id, out BasisSteamNetPeer peer) && RemoveServerPeer(connection.Id, peer))
            {
                Interlocked.Exchange(ref peer.Closed, 1);
                listener.RaisePeerDisconnected(peer, Info(MapReason(info, true), null));
            }
        }
        private bool RemoveServerPeer(uint connectionId, BasisSteamNetPeer peer)
        {
            if (!peers.Remove(connectionId))
            {
                return false;
            }
            connectedCount = peers.Count;
            peerIds.Release(peer.Id);
            return true;
        }
        internal void OnClientConnected(BasisSteamClientConnection connection)
        {
            Result lanes = connection.Connection.ConfigureConnectionLanes(lanePriorities, laneWeights);
            if (lanes != Result.OK)
            {
                BNL.LogWarning($"Steam lanes could not be configured: {lanes}");
            }
            if (connection.Payload != null)
            {
                SendRaw(connection.Connection, connection.Payload, connection.Payload.Length, SendType.Reliable, BasisSteamPacket.ControlLane);
                connection.Payload = null;
            }
        }
        internal void OnClientMessage(BasisSteamClientConnection connection, IntPtr data, int size)
        {
            if (size <= 0 || connection.Peer == null)
            {
                return;
            }
            byte[] buffer = Copy(data, size);
            bool keep = false;
            try
            {
                Interlocked.Increment(ref statistics.PacketsReceived);
                Interlocked.Add(ref statistics.BytesReceived, size);
                keep = Dispatch(connection.Peer, buffer, size, true);
            }
            catch (Exception ex)
            {
                BNL.LogError($"Steam client message failed: {ex}");
            }
            finally
            {
                if (!keep)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        internal void OnClientDisconnected(BasisSteamClientConnection connection, ConnectionInfo info)
        {
            BasisSteamNetPeer peer = connection.Peer;
            if (client == connection)
            {
                client = null;
                clientReceive = null;
            }
            if (peer == null)
            {
                return;
            }
            DisconnectReason reason = peer.Rejected ? DisconnectReason.ConnectionRejected : MapReason(info, peer.Assigned);
            FinishClient(peer, reason, peer.ClosePayload);
        }
        private void FinishClient(BasisSteamNetPeer peer, DisconnectReason reason, byte[] payload)
        {
            if (clientPeer == peer)
            {
                clientPeer = null;
            }
            if (Interlocked.Exchange(ref peer.Closed, 1) != 0)
            {
                return;
            }
            listener.RaisePeerDisconnected(peer, Info(reason, payload));
        }
        private static DisconnectInfo Info(DisconnectReason reason, byte[] payload)
        {
            byte[] data = payload ?? Array.Empty<byte>();
            return new DisconnectInfo
            {
                Reason = reason,
                AdditionalData = new NetPacketReader(data, 0, data.Length, null),
            };
        }
        private bool Dispatch(BasisSteamNetPeer peer, byte[] buffer, int size, bool fromServer)
        {
            peer.MarkReceived();
            switch (buffer[0])
            {
                case BasisSteamPacket.Application:
                    if (size < BasisSteamPacket.ApplicationHeaderBytes)
                    {
                        return false;
                    }
                    byte channel = buffer[2];
                    DeliveryMethod method = (DeliveryMethod)buffer[1];
                    NetPacketReader reader = new NetPacketReader(buffer, BasisSteamPacket.ApplicationHeaderBytes, size, () => ArrayPool<byte>.Shared.Return(buffer));
                    try
                    {
                        listener.RaiseNetworkReceive(peer, reader, channel, method);
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"Steam message on channel {channel} failed: {ex}");
                    }
                    return true;
                case BasisSteamPacket.Ping:
                    if (size >= BasisSteamPacket.PingBytes)
                    {
                        byte[] pong = new byte[BasisSteamPacket.PongBytes];
                        pong[0] = BasisSteamPacket.Pong;
                        Buffer.BlockCopy(buffer, 1, pong, 1, 8);
                        WriteInt64(pong, 9, DateTime.UtcNow.Ticks);
                        SendRaw(peer.Connection, pong, pong.Length, SendType.Unreliable | SendType.NoNagle, BasisSteamPacket.ControlLane);
                    }
                    return false;
                case BasisSteamPacket.Pong:
                    if (size >= BasisSteamPacket.PongBytes)
                    {
                        peer.ApplyPong(ReadInt64(buffer, 1), ReadInt64(buffer, 9));
                    }
                    return false;
                case BasisSteamPacket.Disconnect:
                    peer.ClosePayload = CopyBody(buffer, size);
                    return false;
                case BasisSteamPacket.AssignPeer:
                    if (fromServer && size >= 3 && !peer.Assigned)
                    {
                        peer.AssignId(buffer[1] | (buffer[2] << 8));
                        listener.RaisePeerConnected(peer);
                    }
                    return false;
                case BasisSteamPacket.Reject:
                    if (fromServer)
                    {
                        peer.Rejected = true;
                        peer.ClosePayload = CopyBody(buffer, size);
                    }
                    return false;
                default:
                    return false;
            }
        }
        internal void SendApplication(BasisSteamNetPeer peer, byte[] data, int offset, int length, byte channel, DeliveryMethod method, int patchOffset, byte patchValue)
        {
            if (data == null || length < 0 || Volatile.Read(ref peer.Closed) != 0 || !running)
            {
                return;
            }
            int total = length + BasisSteamPacket.ApplicationHeaderBytes;
            byte[] packet = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                packet[0] = BasisSteamPacket.Application;
                packet[1] = (byte)method;
                packet[2] = channel;
                Buffer.BlockCopy(data, offset, packet, BasisSteamPacket.ApplicationHeaderBytes, length);
                if (patchOffset >= 0 && patchOffset < length)
                {
                    packet[BasisSteamPacket.ApplicationHeaderBytes + patchOffset] = patchValue;
                }
                SendRaw(peer.Connection, packet, total, SendTypeFor(method), LaneFor(channel, method));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
        private bool SendRaw(Connection connection, byte[] packet, int length, SendType sendType, ushort lane)
        {
            if (!BasisSteamHost.IsValid || connection.Id == 0)
            {
                return false;
            }
            Result result = connection.SendMessage(packet, 0, length, sendType, lane);
            if (result != Result.OK)
            {
                return false;
            }
            Interlocked.Increment(ref statistics.PacketsSent);
            Interlocked.Add(ref statistics.BytesSent, length);
            return true;
        }
        internal void Close(BasisSteamNetPeer peer, byte[] payload, bool linger)
        {
            if (Interlocked.Exchange(ref peer.Closed, 1) != 0)
            {
                return;
            }
            BasisSteamHost.Run(() => CloseOnMainThread(peer, payload, linger));
        }
        private void CloseOnMainThread(BasisSteamNetPeer peer, byte[] payload, bool linger)
        {
            if (peer.IsClientSide)
            {
                if (peer == clientPeer)
                {
                    clientPeer = null;
                    if (client != null && BasisSteamHost.IsValid)
                    {
                        client.Close(linger, 0, "Disconnected");
                    }
                    client = null;
                    clientReceive = null;
                }
                listener.RaisePeerDisconnected(peer, Info(DisconnectReason.DisconnectPeerCalled, null));
                return;
            }
            if (BasisSteamHost.IsValid && peer.Connection.Id != 0)
            {
                if (payload != null && payload.Length > 0)
                {
                    byte[] packet = new byte[payload.Length + 1];
                    packet[0] = BasisSteamPacket.Disconnect;
                    Buffer.BlockCopy(payload, 0, packet, 1, payload.Length);
                    SendRaw(peer.Connection, packet, packet.Length, SendType.Reliable, BasisSteamPacket.ControlLane);
                }
                peer.Connection.Close(linger, 0, "Disconnected");
            }
            if (peers.TryGetValue(peer.Connection.Id, out BasisSteamNetPeer known) && known == peer && RemoveServerPeer(peer.Connection.Id, peer))
            {
                listener.RaisePeerDisconnected(peer, Info(DisconnectReason.DisconnectPeerCalled, null));
            }
        }
        private void Poll()
        {
            if (!running)
            {
                return;
            }
            long now = Stopwatch.GetTimestamp();
            long budgetEnd = now + (long)(config.MaxPollMilliseconds * Stopwatch.Frequency / 1000f);
            int budget = config.MaxMessagesPerPoll;
            if (serverReceive != null)
            {
                budget -= Drain(serverReceive, budget, budgetEnd);
            }
            if (clientReceive != null)
            {
                Drain(clientReceive, budget, budgetEnd);
            }
            if (now >= nextPing)
            {
                nextPing = now + Math.Max(100, config.PingIntervalMs) * Stopwatch.Frequency / 1000;
                SendPings();
            }
            if (pending.Count > 0 && now >= nextSweep)
            {
                nextSweep = now + Stopwatch.Frequency;
                SweepPending(now);
            }
        }
        private int Drain(Func<int, bool, int> receive, int budget, long budgetEnd)
        {
            int total = 0;
            while (total < budget)
            {
                int batch = Math.Min(ReceiveBatch, budget - total);
                int received = receive(batch, false);
                total += received;
                if (received < batch || Stopwatch.GetTimestamp() >= budgetEnd)
                {
                    break;
                }
            }
            return total;
        }
        private void SendPings()
        {
            pingBuffer[0] = BasisSteamPacket.Ping;
            WriteInt64(pingBuffer, 1, Stopwatch.GetTimestamp());
            foreach (BasisSteamNetPeer peer in peers.Values)
            {
                SendRaw(peer.Connection, pingBuffer, pingBuffer.Length, SendType.Unreliable | SendType.NoNagle, BasisSteamPacket.ControlLane);
            }
            if (clientPeer != null && clientPeer.Assigned && client != null)
            {
                SendRaw(clientPeer.Connection, pingBuffer, pingBuffer.Length, SendType.Unreliable | SendType.NoNagle, BasisSteamPacket.ControlLane);
            }
        }
        private void SweepPending(long now)
        {
            long timeout = Math.Max(1000, config.PendingConnectionTimeoutMs) * Stopwatch.Frequency / 1000;
            expired.Clear();
            foreach (BasisSteamPending entry in pending.Values)
            {
                if (now - entry.Created > timeout)
                {
                    expired.Add(entry);
                }
            }
            for (int index = 0; index < expired.Count; index++)
            {
                BasisSteamPending entry = expired[index];
                pending.Remove(entry.Connection.Id);
                entry.Connection.Close(false, 0, "Connect request timed out");
            }
            expired.Clear();
        }
        private static byte[] Copy(IntPtr data, int size)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
            Marshal.Copy(data, buffer, 0, size);
            return buffer;
        }
        private static byte[] CopyBody(byte[] buffer, int size)
        {
            if (size <= 1)
            {
                return null;
            }
            byte[] body = new byte[size - 1];
            Buffer.BlockCopy(buffer, 1, body, 0, body.Length);
            return body;
        }
        private static void WriteInt64(byte[] buffer, int offset, long value)
        {
            for (int index = 0; index < 8; index++)
            {
                buffer[offset + index] = (byte)(value >> (index * 8));
            }
        }
        private static long ReadInt64(byte[] buffer, int offset)
        {
            long value = 0;
            for (int index = 7; index >= 0; index--)
            {
                value = (value << 8) | buffer[offset + index];
            }
            return value;
        }
        private static SendType SendTypeFor(DeliveryMethod method)
        {
            return method == DeliveryMethod.Unreliable || method == DeliveryMethod.Sequenced ? SendType.Unreliable | SendType.NoNagle : SendType.Reliable;
        }
        private static ushort LaneFor(byte channel, DeliveryMethod method)
        {
            if (method == DeliveryMethod.Unreliable || method == DeliveryMethod.Sequenced)
            {
                return BasisSteamPacket.TransientLane;
            }
            switch (channel)
            {
                case BasisNetworkCommons.AvatarChangeMessageChannel:
                case BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel:
                case BasisNetworkCommons.SceneChannel:
                case BasisNetworkCommons.LoadResourceChannel:
                case BasisNetworkCommons.UnloadResourceChannel:
                case BasisNetworkCommons.ModifyResourceChannel:
                case BasisNetworkCommons.ContentShareChannel:
                case BasisNetworkCommons.CompressedAvatarBundleChannel:
                case BasisNetworkCommons.ServerLibraryChannel:
                    return BasisSteamPacket.ResourceLane;
                default:
                    return BasisSteamPacket.ControlLane;
            }
        }
        private static DisconnectReason MapReason(ConnectionInfo info, bool everConnected)
        {
            switch (info.EndReason)
            {
                case NetConnectionEnd.Remote_Timeout:
                case NetConnectionEnd.Misc_Timeout:
                    return everConnected ? DisconnectReason.Timeout : DisconnectReason.ConnectionFailed;
                case NetConnectionEnd.Misc_SteamConnectivity:
                case NetConnectionEnd.Misc_NoRelaySessionsToClient:
                case NetConnectionEnd.Misc_P2P_Rendezvous:
                case NetConnectionEnd.Misc_P2P_NAT_Firewall:
                case NetConnectionEnd.Misc_PeerSentNoConnection:
                case NetConnectionEnd.Local_OfflineMode:
                case NetConnectionEnd.Local_ManyRelayConnectivity:
                case NetConnectionEnd.Local_HostedServerPrimaryRelay:
                case NetConnectionEnd.Local_NetworkConfig:
                    return everConnected ? DisconnectReason.NetworkUnreachable : DisconnectReason.HostUnreachable;
            }
            if (info.State == ConnectionState.ClosedByPeer && everConnected)
            {
                return DisconnectReason.RemoteConnectionClose;
            }
            return DisconnectReason.ConnectionFailed;
        }
        internal static void PollAll()
        {
            BasisSteamNetManager[] managers = activeSnapshot;
            for (int index = 0; index < managers.Length; index++)
            {
                managers[index].Poll();
            }
        }
        internal static void StopAll()
        {
            BasisSteamNetManager[] managers = activeSnapshot;
            for (int index = 0; index < managers.Length; index++)
            {
                managers[index].Stop();
            }
        }
        private static void Register(BasisSteamNetManager manager)
        {
            lock (activeLock)
            {
                if (!active.Contains(manager))
                {
                    active.Add(manager);
                    activeSnapshot = active.ToArray();
                }
            }
        }
        private static void Unregister(BasisSteamNetManager manager)
        {
            lock (activeLock)
            {
                if (active.Remove(manager))
                {
                    activeSnapshot = active.ToArray();
                }
            }
        }
    }
}
