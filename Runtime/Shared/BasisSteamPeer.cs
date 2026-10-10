using Basis.Network.Core;
using Steamworks;
using Steamworks.Data;
using System;
using System.Diagnostics;
using System.Net;
using System.Threading;

namespace Basis.Network.Steam
{
    public static class BasisSteamAddress
    {
        private static readonly byte[] prefix = { 0xfd, 0x53, 0x74, 0x65, 0x61, 0x6d, 0x00, 0x00 };
        public static IPAddress For(ulong steamId)
        {
            byte[] bytes = new byte[16];
            Buffer.BlockCopy(prefix, 0, bytes, 0, prefix.Length);
            for (int index = 0; index < 8; index++)
            {
                bytes[8 + index] = (byte)(steamId >> (56 - index * 8));
            }
            return new IPAddress(bytes);
        }
        public static bool TryGetSteamId(IPAddress address, out ulong steamId)
        {
            steamId = 0;
            if (address == null)
            {
                return false;
            }
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 16)
            {
                return false;
            }
            for (int index = 0; index < prefix.Length; index++)
            {
                if (bytes[index] != prefix[index])
                {
                    return false;
                }
            }
            for (int index = 0; index < 8; index++)
            {
                steamId = (steamId << 8) | bytes[8 + index];
            }
            return true;
        }
    }
    public sealed class BasisSteamNetPeer : NetPeer
    {
        private static readonly long statusIntervalTicks = Stopwatch.Frequency / 50;
        private readonly BasisSteamNetManager owner;
        private IPAddress address;
        private int id = -1, remoteId = -1, roundTripMs, pendingUnreliableBytes, pendingReliableBytes;
        private long lastReceive, remoteTimeDelta, statusStamp;
        internal ulong SteamId;
        internal Connection Connection;
        internal byte[] ClosePayload;
        internal readonly bool IsClientSide;
        internal bool Rejected, Assigned;
        internal int Closed;
        internal BasisSteamNetPeer(BasisSteamNetManager owner, ulong steamId, Connection connection, bool isClientSide)
        {
            this.owner = owner;
            SteamId = steamId;
            Connection = connection;
            IsClientSide = isClientSide;
            address = BasisSteamAddress.For(steamId);
            lastReceive = Stopwatch.GetTimestamp();
        }
        public object Tag { get; set; }
        public string StackId => BasisSteamTransport.StackId;
        public bool SupportsDirectConnect => false;
        public int Id => id;
        public int RemoteId => remoteId;
        public IPAddress Address => address;
        public int RoundTripTime => Volatile.Read(ref roundTripMs);
        public float TimeSinceLastPacket => (float)((Stopwatch.GetTimestamp() - Interlocked.Read(ref lastReceive)) * 1000.0 / Stopwatch.Frequency);
        public long RemoteTimeDelta => Interlocked.Read(ref remoteTimeDelta);
        public int Mtu => owner.Mtu;
        public ulong RemoteSteamId => SteamId;
        internal void SetSteamId(ulong steamId)
        {
            SteamId = steamId;
            address = BasisSteamAddress.For(steamId);
        }
        internal void AssignId(int assigned)
        {
            id = assigned;
            remoteId = assigned;
            Assigned = true;
        }
        internal void MarkReceived()
        {
            Interlocked.Exchange(ref lastReceive, Stopwatch.GetTimestamp());
        }
        internal void ApplyPong(long sentTimestamp, long remoteUtcTicks)
        {
            long elapsed = Stopwatch.GetTimestamp() - sentTimestamp;
            if (elapsed < 0 || elapsed > Stopwatch.Frequency * 120)
            {
                return;
            }
            int sample = (int)(elapsed * 1000 / Stopwatch.Frequency);
            int previous = Volatile.Read(ref roundTripMs);
            Volatile.Write(ref roundTripMs, previous <= 0 ? Math.Max(1, sample) : Math.Max(1, (previous * 3 + sample) / 4));
            if (remoteUtcTicks > 0)
            {
                long halfTrip = TimeSpan.TicksPerSecond * elapsed / Stopwatch.Frequency / 2;
                Interlocked.Exchange(ref remoteTimeDelta, remoteUtcTicks + halfTrip - DateTime.UtcNow.Ticks);
            }
        }
        public void Disconnect()
        {
            owner.Close(this, null, true);
        }
        public void Disconnect(byte[] b)
        {
            owner.Close(this, b, true);
        }
        public void DisconnectForce()
        {
            owner.Close(this, null, false);
        }
        public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            owner.SendApplication(this, data, 0, data.Length, channelNumber, deliveryMethod, -1, 0);
        }
        public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            owner.SendApplication(this, data.Data, 0, data.Length, channelNumber, deliveryMethod, -1, 0);
        }
        public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0)
        {
            owner.SendApplication(this, data, offset, length, channelNumber, DeliveryMethod.Unreliable, patchOffset, patchValue);
        }
        public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod)
        {
            RefreshStatus();
            int bytes = deliveryMethod == DeliveryMethod.Unreliable || deliveryMethod == DeliveryMethod.Sequenced ? Volatile.Read(ref pendingUnreliableBytes) : Volatile.Read(ref pendingReliableBytes);
            int mtu = owner.Mtu;
            return (bytes + mtu - 1) / mtu;
        }
        private void RefreshStatus()
        {
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref statusStamp);
            if (now - last < statusIntervalTicks || Interlocked.CompareExchange(ref statusStamp, now, last) != last)
            {
                return;
            }
            if (!BasisSteamHost.IsValid || Volatile.Read(ref Closed) != 0)
            {
                Volatile.Write(ref pendingUnreliableBytes, 0);
                Volatile.Write(ref pendingReliableBytes, 0);
                return;
            }
            ConnectionStatus status = Connection.QuickStatus();
            Volatile.Write(ref pendingUnreliableBytes, status.PendingUnreliable);
            Volatile.Write(ref pendingReliableBytes, status.PendingReliable);
        }
        public override string ToString()
        {
            return $"Steam peer {SteamId} (id {id})";
        }
    }
}
