using Basis.Network.Core;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
#if UNITY_5_3_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Basis.Network.Steam
{
    internal sealed class BasisSteamProbeConnection : ConnectionManager
    {
        public BasisSteamProbeSession Session;
        public override void OnConnected(ConnectionInfo info)
        {
            base.OnConnected(info);
            Session?.OnConnected(Connection);
        }
        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            Session?.OnMessage(data, size);
        }
        public override void OnDisconnected(ConnectionInfo info)
        {
            Session?.OnDisconnected();
            base.OnDisconnected(info);
        }
    }
    internal sealed class BasisSteamProbeSession
    {
        private readonly TaskCompletionSource<ServerProbeResult> result = new TaskCompletionSource<ServerProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch clock = new Stopwatch();
        private readonly ulong steamId;
        private readonly int virtualPort, timeoutMs;
        private readonly CancellationToken cancel;
        private readonly ushort nonce;
        private BasisSteamProbeConnection connection;
        public BasisSteamProbeSession(ulong steamId, int virtualPort, int timeoutMs, CancellationToken cancel)
        {
            this.steamId = steamId;
            this.virtualPort = virtualPort;
            this.timeoutMs = timeoutMs;
            this.cancel = cancel;
            unchecked
            {
                nonce = (ushort)Guid.NewGuid().GetHashCode();
            }
        }
        public Task<ServerProbeResult> Task => result.Task;
        public bool Start()
        {
            if (!BasisSteamHost.EnsureStarted())
            {
                Complete(new ServerProbeResult { Error = BasisSteamHost.LastError });
                return false;
            }
            clock.Start();
            connection = SteamNetworkingSockets.ConnectRelay<BasisSteamProbeConnection>(steamId, virtualPort);
            if (connection == null || connection.Connection.Id == 0)
            {
                connection = null;
                Complete(new ServerProbeResult { Error = "Steam could not start a relay connection" });
                return false;
            }
            connection.Session = this;
            return true;
        }
        public void OnConnected(Connection link)
        {
            NetDataWriter writer = new NetDataWriter(true, BasisNetworkCommons.ServerInfoMinRequestBytes + 1);
            writer.Put(BasisSteamPacket.Query);
            writer.Put(BasisNetworkCommons.ServerInfoQueryMagic);
            writer.Put(BasisNetworkCommons.ServerInfoProtocolVersion);
            writer.Put(nonce);
            int padding = BasisNetworkCommons.ServerInfoMinRequestBytes + 1 - writer.Length;
            if (padding > 0)
            {
                writer.Put(new byte[padding]);
            }
            if (link.SendMessage(writer.Data, 0, writer.Length, SendType.Reliable, 0) != Result.OK)
            {
                Complete(new ServerProbeResult { Error = "Steam could not send the query" });
            }
        }
        public void OnMessage(IntPtr data, int size)
        {
            if (size < 9 || result.Task.IsCompleted)
            {
                return;
            }
            byte[] bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, size);
            if (bytes[0] != BasisSteamPacket.QueryResponse)
            {
                return;
            }
            try
            {
                NetDataReader reader = new NetDataReader(bytes, 1, size);
                if (reader.GetUInt() != BasisNetworkCommons.ServerInfoResponseMagic)
                {
                    return;
                }
                ushort protocol = reader.GetUShort();
                if (reader.GetUShort() != nonce)
                {
                    return;
                }
                ushort online = reader.GetUShort();
                ushort max = reader.GetUShort();
                string name = reader.GetString();
                string motd = reader.GetString();
                Complete(new ServerProbeResult
                {
                    Reachable = true,
                    Online = online,
                    Max = max,
                    ProtocolVersion = protocol,
                    Name = name,
                    Motd = motd,
                    RoundTripMs = (int)clock.ElapsedMilliseconds,
                });
            }
            catch (Exception ex)
            {
                Complete(new ServerProbeResult { Error = "Malformed server info: " + ex.Message });
            }
        }
        public void OnDisconnected()
        {
            Complete(new ServerProbeResult { Error = "Connection closed" });
        }
        public bool Poll()
        {
            if (!result.Task.IsCompleted && connection != null)
            {
                connection.Receive(8, false);
                if (clock.ElapsedMilliseconds > timeoutMs || cancel.IsCancellationRequested)
                {
                    Complete(new ServerProbeResult { TimedOut = true, Error = "Timed out" });
                }
            }
            if (!result.Task.IsCompleted)
            {
                return true;
            }
            Close();
            return false;
        }
        public void Abort()
        {
            Complete(new ServerProbeResult { Error = "Steam shut down" });
            Close();
        }
        private void Complete(ServerProbeResult value)
        {
            result.TrySetResult(value);
        }
        private void Close()
        {
            if (connection == null)
            {
                return;
            }
            connection.Session = null;
            if (BasisSteamHost.IsValid)
            {
                connection.Close(false, 0, "Probe finished");
            }
            connection = null;
        }
    }
#if UNITY_5_3_OR_NEWER
    [AutoStaticsCleanup]
#endif
    public static partial class BasisSteamProbe
    {
        private static readonly List<BasisSteamProbeSession> sessions = new List<BasisSteamProbeSession>();
        public static Task<ServerProbeResult> ProbeAsync(ConnectionTarget target, int timeoutMs, CancellationToken ct)
        {
            if (target == null)
            {
                return System.Threading.Tasks.Task.FromResult(new ServerProbeResult { Error = "Target is null" });
            }
            if (!BasisSteamConnectionTargetParser.TryParseSteamId(target.Get(ConnectionTarget.Keys.Address, target.Raw), out ulong steamId))
            {
                return System.Threading.Tasks.Task.FromResult(new ServerProbeResult { Error = "Not a SteamID64" });
            }
            int.TryParse(target.Get(ConnectionTarget.Keys.Port, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port);
            BasisSteamProbeSession session = new BasisSteamProbeSession(steamId, port, Math.Max(250, timeoutMs), ct);
            BasisSteamHost.Run(() =>
            {
                if (session.Start())
                {
                    sessions.Add(session);
                }
            });
            return session.Task;
        }
        internal static void PollAll()
        {
            for (int index = sessions.Count - 1; index >= 0; index--)
            {
                if (!sessions[index].Poll())
                {
                    sessions.RemoveAt(index);
                }
            }
        }
        internal static void AbortAll()
        {
            for (int index = 0; index < sessions.Count; index++)
            {
                sessions[index].Abort();
            }
            sessions.Clear();
        }
    }
}
