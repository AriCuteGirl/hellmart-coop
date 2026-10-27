using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace HellmartCoop
{
    // One player's pose as sent over the wire.
    public struct PlayerState
    {
        public byte Id;
        public uint Seq;
        public Vector3 Position;
        public float Yaw;
        public float Pitch;
        public bool InGame;
        // Spectating until morning (co-op death).
        public bool Dead;
        // Hiding in a fridge.
        public bool InFridge;
        // PlayerLook: capsule color and accessory.
        public byte LookColor;
        public byte LookAccessory;
    }

    // Plain UDP, star topology: clients talk only to the host, the host forwards each
    // client's state to the other clients. Good enough for a LAN/localhost test; Steam P2P later.
    public class Net : IDisposable
    {
        private const uint Magic = 0x31434D48; // "HMC1"
        private const byte MsgState = 1;
        // magic 4 + type 1 + id 1 + seq 4 + position 12 + yaw 4 + pitch 4 + in-game 1 + dead 1 + fridge 1 + look 2
        private const int StateSize = 35;

        public readonly bool IsHost;
        public byte LocalId { get; private set; }

        private readonly UdpClient udp;
        private readonly IPEndPoint hostEndPoint;
        // Clients that stay silent this long are dropped (crash, Alt+F4, rejoin from a new socket).
        private const float ClientTimeout = 5f;

        private class Client
        {
            public byte Id;
            public float LastHeard;
            public readonly HashSet<uint> HandledActions = new HashSet<uint>();
        }

        private readonly Dictionary<IPEndPoint, Client> clients = new Dictionary<IPEndPoint, Client>();
        private byte nextClientId = 1;
        private readonly ConcurrentQueue<(IPEndPoint from, byte[] data)> inbox = new ConcurrentQueue<(IPEndPoint, byte[])>();
        private readonly Thread receiver; // UDP only
        private volatile bool running = true;
        private uint seq;

        // Relay (hmrelay.py on a server): host and friends connect out to it and it forwards between
        // them by room code, so nobody forwards ports. Like Steam, each relay peer gets a stand-in
        // endpoint (10.200.x.y, port 2) and Send wraps packets for the relay.
        private static readonly byte[] RelayMagic = { (byte)'H', (byte)'M', (byte)'R', (byte)'1' };
        private const byte RCreate = 1, RCreated = 2, RJoin = 3, RJoined = 4, RError = 5, RData = 6, RPing = 7, RLeft = 8;
        private IPEndPoint relay;
        private readonly Dictionary<IPEndPoint, ushort> relayPeers = new Dictionary<IPEndPoint, ushort>();
        private readonly Dictionary<ushort, IPEndPoint> relayById = new Dictionary<ushort, IPEndPoint>();
        private float nextRelayPing;

        public string RoomCode { get; private set; }
        public string RelayError { get; private set; }
        public bool RelayJoined { get; private set; }
        public bool UsesRelay => relay != null;

        // Called from the receive thread and the main thread.
        private IPEndPoint RelayEndPoint(ushort peer)
        {
            lock (relayPeers)
            {
                if (!relayById.TryGetValue(peer, out var ep))
                {
                    ep = new IPEndPoint(new IPAddress(new byte[] { 10, 200, (byte)(peer >> 8), (byte)peer }), 2);
                    relayById[peer] = ep;
                    relayPeers[ep] = peer;
                }
                return ep;
            }
        }

        private static byte[] RelayPacket(byte type, byte[] body)
        {
            var packet = new byte[5 + body.Length];
            Buffer.BlockCopy(RelayMagic, 0, packet, 0, 4);
            packet[4] = type;
            Buffer.BlockCopy(body, 0, packet, 5, body.Length);
            return packet;
        }

        private static byte[] RelayStr(params string[] parts)
        {
            using (var ms = new MemoryStream())
            {
                foreach (var part in parts)
                {
                    var b = System.Text.Encoding.UTF8.GetBytes(part ?? "");
                    int n = Math.Min(b.Length, 64);
                    ms.WriteByte((byte)n);
                    ms.Write(b, 0, n);
                }
                return ms.ToArray();
            }
        }

        private static IPEndPoint ParseRelay(string address)
        {
            // "host:port" or "host" (default port 27360); host may be a name.
            string host = address.Trim();
            int port = 27360;
            int colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host.Substring(colon + 1), out int p))
            {
                port = p;
                host = host.Substring(0, colon);
            }
            if (!IPAddress.TryParse(host, out var ip))
                ip = Dns.GetHostAddresses(host)[0];
            return new IPEndPoint(ip, port);
        }

        // Host: also open a room on the relay (the code arrives as RoomCode).
        public void OpenRelayRoom(string relayAddress, string name)
        {
            relay = ParseRelay(relayAddress);
            createPacket = RelayPacket(RCreate, RelayStr(name));
            udp.Send(createPacket, createPacket.Length, relay);
        }

        private byte[] createPacket;

        // Client through a relay room.
        public Net(string relayAddress, string code, string name)
        {
            IsHost = false;
            LocalId = 255;
            relay = ParseRelay(relayAddress);
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            hostEndPoint = new IPEndPoint(new IPAddress(new byte[] { 10, 200, 255, 255 }), 2);
            receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "HellmartCoop.Net" };
            receiver.Start();
            var join = RelayPacket(RJoin, RelayStr(code.Trim().ToUpperInvariant(), name));
            udp.Send(join, join.Length, relay);
            joinPacket = join;
        }

        private byte[] joinPacket;

        // Relay control messages; data packets become ordinary inbox items from stand-in endpoints.
        private bool HandleRelay(IPEndPoint from, byte[] data)
        {
            if (relay == null || !from.Equals(relay) || data.Length < 5 || data[0] != 'H' || data[1] != 'M' || data[2] != 'R' || data[3] != '1')
                return false;
            switch (data[4])
            {
                case RCreated:
                    RoomCode = System.Text.Encoding.UTF8.GetString(data, 6, data[5]);
                    break;
                case RJoined:
                    RelayJoined = true;
                    break;
                case RError:
                    RelayError = System.Text.Encoding.UTF8.GetString(data, 6, data[5]);
                    break;
                case RData:
                    if (IsHost)
                    {
                        ushort peer = BitConverter.ToUInt16(data, 5);
                        var payload = new byte[data.Length - 7];
                        Buffer.BlockCopy(data, 7, payload, 0, payload.Length);
                        inbox.Enqueue((RelayEndPoint(peer), payload));
                    }
                    else
                    {
                        var payload = new byte[data.Length - 5];
                        Buffer.BlockCopy(data, 5, payload, 0, payload.Length);
                        inbox.Enqueue((hostEndPoint, payload));
                    }
                    break;
            }
            return true;
        }

        private void RelayKeepAlive()
        {
            if (relay == null || Time.unscaledTime < nextRelayPing)
                return;
            nextRelayPing = Time.unscaledTime + 3f;
            // Until the relay answers, keep asking (UDP); after that, a ping keeps the mapping open.
            byte[] packet = !IsHost && !RelayJoined && joinPacket != null ? joinPacket
                : IsHost && RoomCode == null && createPacket != null ? createPacket
                : RelayPacket(RPing, new byte[0]);
            try
            {
                udp.Send(packet, packet.Length, relay);
            }
            catch (SocketException)
            {
            }
        }

        // Steam P2P (also PupBerg): peers are SteamIDs. Everything above works on IPEndPoints, so each
        // SteamID gets a stand-in endpoint (port 1, never a real UDP peer) and Send routes those to Steam.
        private const int SteamChannel = 0;
        private bool steam;
        private readonly Dictionary<IPEndPoint, Steamworks.SteamId> steamPeers = new Dictionary<IPEndPoint, Steamworks.SteamId>();

        public bool UsesSteam => steam;

        private IPEndPoint SteamEndPoint(Steamworks.SteamId id)
        {
            ulong v = id.Value;
            var ep = new IPEndPoint(new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }), 1);
            steamPeers[ep] = id;
            return ep;
        }

        // UDP host (plus Steam when it's up) or UDP client.
        public Net(bool isHost, string hostAddress, int port)
        {
            IsHost = isHost;
            if (isHost)
            {
                LocalId = 0;
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                steam = SteamUp();
            }
            else
            {
                // The host assigns the real id from the client's endpoint; until then use 255.
                LocalId = 255;
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                hostEndPoint = new IPEndPoint(IPAddress.Parse(hostAddress), port);
            }
            receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "HellmartCoop.Net" };
            receiver.Start();
            if (steam)
                AcceptSteamSessions();
        }

        // Steam client: everything to and from the host's SteamID.
        public Net(Steamworks.SteamId host)
        {
            IsHost = false;
            LocalId = 255;
            steam = true;
            hostEndPoint = SteamEndPoint(host);
            AcceptSteamSessions();
        }

        private static bool SteamUp()
        {
            try
            {
                return Steamworks.SteamClient.IsValid;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void AcceptSteamSessions()
        {
            Steamworks.SteamNetworking.AllowP2PPacketRelay(true);
            Steamworks.SteamNetworking.OnP2PSessionRequest = id => Steamworks.SteamNetworking.AcceptP2PSessionWithUser(id);
        }

        private void PollSteam()
        {
            // A host made before the game brought Steam up: switch Steam on once it is.
            if (!steam && IsHost && SteamUp())
            {
                steam = true;
                AcceptSteamSessions();
                Plugin.Log.LogInfo("Steam is up: hosting on Steam as well");
            }
            if (!steam)
                return;
            while (Steamworks.SteamNetworking.IsP2PPacketAvailable(SteamChannel))
            {
                var packet = Steamworks.SteamNetworking.ReadP2PPacket(SteamChannel);
                if (packet.HasValue)
                    inbox.Enqueue((SteamEndPoint(packet.Value.SteamId), packet.Value.Data));
            }
        }

        private void ReceiveLoop()
        {
            while (running)
            {
                try
                {
                    IPEndPoint from = null;
                    byte[] data = udp.Receive(ref from);
                    if (!HandleRelay(from, data))
                        inbox.Enqueue((from, data));
                }
                catch (SocketException)
                {
                    // ICMP port unreachable before the host is up; keep listening.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        // ---- the host's save over plain datagrams (for the relay, which has no stream) ----
        // Client → host: "HMS?" + u16 count + u16 chunk indexes it still needs (count 0 = all).
        // Host → client: "HMSC" + u16 index + u16 total + up to 1000 bytes of SaveShare's wire format.
        private const int SaveChunk = 1000;
        private byte[] saveCache;
        private float saveCacheUntil;
        public event Action<int, int, byte[]> SaveChunkReceived;

        public void RequestSaveChunks(IList<int> missing)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("HMS?"));
                int n = Math.Min(missing.Count, 200);
                w.Write((ushort)n);
                for (int i = 0; i < n; i++)
                    w.Write((ushort)missing[i]);
                var packet = ms.ToArray();
                Send(packet, hostEndPoint);
            }
        }

        private bool TryHandleSaveChunks(byte[] data, IPEndPoint from)
        {
            if (data.Length < 6 || data[0] != 'H' || data[1] != 'M' || data[2] != 'S')
                return false;
            if (data[3] == '?' && IsHost)
            {
                if (saveCache == null || Time.unscaledTime > saveCacheUntil)
                {
                    saveCache = SaveShare.HostSaveBytes();
                    saveCacheUntil = Time.unscaledTime + 30f;
                }
                int total = (saveCache.Length + SaveChunk - 1) / SaveChunk;
                int n = BitConverter.ToUInt16(data, 4);
                var wanted = new List<int>();
                if (n == 0)
                    for (int i = 0; i < total; i++) wanted.Add(i);
                else
                    for (int i = 0; i < n && 6 + i * 2 + 1 < data.Length; i++) wanted.Add(BitConverter.ToUInt16(data, 6 + i * 2));
                foreach (int index in wanted)
                {
                    if (index >= total)
                        continue;
                    int offset = index * SaveChunk;
                    int len = Math.Min(SaveChunk, saveCache.Length - offset);
                    var packet = new byte[8 + len];
                    packet[0] = (byte)'H'; packet[1] = (byte)'M'; packet[2] = (byte)'S'; packet[3] = (byte)'C';
                    BitConverter.GetBytes((ushort)index).CopyTo(packet, 4);
                    BitConverter.GetBytes((ushort)total).CopyTo(packet, 6);
                    Buffer.BlockCopy(saveCache, offset, packet, 8, len);
                    Send(packet, from);
                }
                return true;
            }
            if (data[3] == 'C' && !IsHost && data.Length >= 8)
            {
                int index = BitConverter.ToUInt16(data, 4);
                int total = BitConverter.ToUInt16(data, 6);
                var chunk = new byte[data.Length - 8];
                Buffer.BlockCopy(data, 8, chunk, 0, chunk.Length);
                SaveChunkReceived?.Invoke(index, total, chunk);
                return true;
            }
            return false;
        }

        public void SendState(PlayerState state)
        {
            state.Id = LocalId;
            state.Seq = ++seq;
            byte[] packet = Encode(state);
            if (IsHost)
            {
                foreach (var client in clients.Keys)
                    Send(packet, client);
            }
            else
            {
                Send(packet, hostEndPoint);
            }
        }

        // Drains received packets on the main thread; calls onState for every remote player state.
        public void Poll(Action<PlayerState> onState)
        {
            PollSteam();
            RelayKeepAlive();
            while (inbox.TryDequeue(out var item))
            {
                if (IsHost && IsBye(item.data))
                {
                    if (clients.TryGetValue(item.from, out var leaving))
                        DropClient(item.from, leaving, "left");
                    continue;
                }
                if (TryHandleSaveChunks(item.data, item.from) || TryDispatchWorld(item.data) || TryHandleAction(item.data, item.from))
                    continue;
                if (!TryDecode(item.data, out var state))
                    continue;
                if (IsHost)
                {
                    if (!clients.TryGetValue(item.from, out var client))
                    {
                        client = new Client { Id = nextClientId++ };
                        clients[item.from] = client;
                        Plugin.Log.LogInfo($"Client {client.Id} joined from {item.from}");
                    }
                    client.LastHeard = Time.unscaledTime;
                    state.Id = client.Id;
                    byte[] forward = Encode(state);
                    foreach (var other in clients.Keys)
                    {
                        if (!other.Equals(item.from))
                            Send(forward, other);
                    }
                    // Tell the client which id the host gave it.
                    Send(EncodeWelcome(client.Id), item.from);
                }
                else if (state.Id == LocalId)
                {
                    continue;
                }
                onState(state);
            }
            if (IsHost)
                DropSilentClients(onState);
            else
                ResendPendingActions();
        }

        private void DropSilentClients(Action<PlayerState> onState)
        {
            List<IPEndPoint> silent = null;
            foreach (var pair in clients)
            {
                if (Time.unscaledTime - pair.Value.LastHeard > ClientTimeout)
                    (silent ?? (silent = new List<IPEndPoint>())).Add(pair.Key);
            }
            if (silent == null)
                return;
            foreach (var endPoint in silent)
                DropClient(endPoint, clients[endPoint], "timed out");
        }

        // Removes the client and tells everyone else (host included, via ClientLeft) to drop its puppet.
        private void DropClient(IPEndPoint endPoint, Client client, string reason)
        {
            clients.Remove(endPoint);
            Plugin.Log.LogInfo($"Client {client.Id} {reason}");
            var gone = Encode(new PlayerState { Id = client.Id, Seq = uint.MaxValue, InGame = false });
            foreach (var other in clients.Keys)
                Send(gone, other);
            ClientLeft?.Invoke(client.Id);
        }

        public event Action<byte> ClientLeft;

        public int ClientCount => clients.Count;

        private void Send(byte[] packet, IPEndPoint to)
        {
            if (to == null)
                return;
            if (relay != null)
            {
                byte[] wrapped = null;
                if (IsHost)
                {
                    ushort peer;
                    bool known;
                    lock (relayPeers)
                        known = relayPeers.TryGetValue(to, out peer);
                    if (known)
                    {
                        wrapped = new byte[7 + packet.Length];
                        Buffer.BlockCopy(RelayMagic, 0, wrapped, 0, 4);
                        wrapped[4] = RData;
                        BitConverter.GetBytes(peer).CopyTo(wrapped, 5);
                        Buffer.BlockCopy(packet, 0, wrapped, 7, packet.Length);
                    }
                }
                else if (to.Equals(hostEndPoint))
                {
                    wrapped = RelayPacket(RData, packet);
                }
                if (wrapped != null)
                {
                    try
                    {
                        udp.Send(wrapped, wrapped.Length, relay);
                    }
                    catch (SocketException)
                    {
                    }
                    return;
                }
            }
            if (steamPeers.TryGetValue(to, out var steamId))
            {
                Steamworks.SteamNetworking.SendP2PPacket(steamId, packet, packet.Length, SteamChannel, Steamworks.P2PSend.Unreliable);
                return;
            }
            if (udp == null)
                return;
            try
            {
                udp.Send(packet, packet.Length, to);
            }
            catch (SocketException)
            {
            }
        }

        private static byte[] Encode(PlayerState s)
        {
            using (var ms = new MemoryStream(32))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(MsgState);
                w.Write(s.Id);
                w.Write(s.Seq);
                w.Write(s.Position.x);
                w.Write(s.Position.y);
                w.Write(s.Position.z);
                w.Write(s.Yaw);
                w.Write(s.Pitch);
                w.Write(s.InGame);
                w.Write(s.Dead);
                w.Write(s.InFridge);
                w.Write(s.LookColor);
                w.Write(s.LookAccessory);
                return ms.ToArray();
            }
        }

        private const byte MsgWelcome = 2;
        private const byte MsgBye = 3;
        // Host → clients world state: magic, type, channel, then the channel's own body.
        private const byte MsgWorld = 4;
        // Client → host actions (one-off events): magic, type, seq u32, channel, body. The client
        // resends until the host acks the seq; the host ignores seqs it has already handled.
        private const byte MsgAction = 5;
        private const byte MsgAck = 6;
        private const float ResendInterval = 0.3f;

        private class PendingAction
        {
            public byte[] Packet;
            public float NextSend;
        }

        private readonly Dictionary<uint, PendingAction> pendingActions = new Dictionary<uint, PendingAction>();
        private readonly Dictionary<byte, Action<byte, BinaryReader>> actionHandlers = new Dictionary<byte, Action<byte, BinaryReader>>();
        private uint nextActionSeq = 1;

        // Host side: handler(clientId, body) on the main thread, once per action.
        public void OnAction(byte channel, Action<byte, BinaryReader> handler)
        {
            actionHandlers[channel] = handler;
        }

        // Client side: delivered to the host at least once and handled exactly once.
        public void SendAction(byte channel, Action<BinaryWriter> write)
        {
            if (IsHost)
                return;
            uint actionSeq = nextActionSeq++;
            byte[] packet;
            using (var ms = new MemoryStream(64))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(MsgAction);
                w.Write(actionSeq);
                w.Write(channel);
                write(w);
                packet = ms.ToArray();
            }
            pendingActions[actionSeq] = new PendingAction { Packet = packet, NextSend = Time.unscaledTime + ResendInterval };
            Send(packet, hostEndPoint);
        }

        private void ResendPendingActions()
        {
            foreach (var pending in pendingActions.Values)
            {
                if (Time.unscaledTime < pending.NextSend)
                    continue;
                pending.NextSend = Time.unscaledTime + ResendInterval;
                Send(pending.Packet, hostEndPoint);
            }
        }

        public int PendingActionCount => pendingActions.Count;

        private bool TryHandleAction(byte[] data, IPEndPoint from)
        {
            if (data.Length < 9 || BitConverter.ToUInt32(data, 0) != Magic)
                return false;
            if (data[4] == MsgAck)
            {
                if (!IsHost)
                    pendingActions.Remove(BitConverter.ToUInt32(data, 5));
                return true;
            }
            if (data[4] != MsgAction)
                return false;
            if (!IsHost || data.Length < 10 || !clients.TryGetValue(from, out var client))
                return true; // unknown sender: no ack, it will resend once registered
            uint actionSeq = BitConverter.ToUInt32(data, 5);
            var ack = new byte[9];
            BitConverter.GetBytes(Magic).CopyTo(ack, 0);
            ack[4] = MsgAck;
            BitConverter.GetBytes(actionSeq).CopyTo(ack, 5);
            Send(ack, from);
            if (!client.HandledActions.Add(actionSeq))
                return true; // a resend of something already done
            if (actionHandlers.TryGetValue(data[9], out var handler))
            {
                using (var r = new BinaryReader(new MemoryStream(data, 10, data.Length - 10)))
                {
                    try
                    {
                        handler(client.Id, r);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Action channel {data[9]} from client {client.Id}: {e.Message}");
                    }
                }
            }
            return true;
        }

        private readonly Dictionary<byte, Action<BinaryReader>> worldHandlers = new Dictionary<byte, Action<BinaryReader>>();

        // Client side: called on the main thread for every world message on that channel.
        public void OnWorld(byte channel, Action<BinaryReader> handler)
        {
            worldHandlers[channel] = handler;
        }

        // Host side: sends one world message to every client. Keep bodies under ~1200 bytes
        // (one UDP datagram without fragmentation); split bigger state into several messages.
        public void BroadcastWorld(byte channel, Action<BinaryWriter> write)
        {
            if (!IsHost || clients.Count == 0)
                return;
            byte[] packet;
            using (var ms = new MemoryStream(256))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(MsgWorld);
                w.Write(channel);
                write(w);
                packet = ms.ToArray();
            }
            foreach (var client in clients.Keys)
                Send(packet, client);
        }

        private bool TryDispatchWorld(byte[] data)
        {
            if (data.Length < 6 || BitConverter.ToUInt32(data, 0) != Magic || data[4] != MsgWorld)
                return false;
            if (!IsHost && worldHandlers.TryGetValue(data[5], out var handler))
            {
                using (var r = new BinaryReader(new MemoryStream(data, 6, data.Length - 6)))
                {
                    try
                    {
                        handler(r);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"World channel {data[5]}: {e.Message}");
                    }
                }
            }
            return true;
        }

        private static bool IsBye(byte[] data)
        {
            return data.Length == 5 && BitConverter.ToUInt32(data, 0) == Magic && data[4] == MsgBye;
        }

        private static byte[] EncodeBye()
        {
            var packet = new byte[5];
            BitConverter.GetBytes(Magic).CopyTo(packet, 0);
            packet[4] = MsgBye;
            return packet;
        }

        private static byte[] EncodeWelcome(byte id)
        {
            using (var ms = new MemoryStream(8))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(MsgWelcome);
                w.Write(id);
                return ms.ToArray();
            }
        }

        private bool TryDecode(byte[] data, out PlayerState s)
        {
            s = default;
            if (data.Length < 6)
                return false;
            using (var r = new BinaryReader(new MemoryStream(data)))
            {
                if (r.ReadUInt32() != Magic)
                    return false;
                byte type = r.ReadByte();
                if (type == MsgWelcome)
                {
                    byte id = r.ReadByte();
                    if (!IsHost && LocalId != id)
                    {
                        LocalId = id;
                        Plugin.Log.LogInfo($"Host assigned id {id}");
                    }
                    return false;
                }
                if (type != MsgState || data.Length < StateSize)
                    return false;
                s.Id = r.ReadByte();
                s.Seq = r.ReadUInt32();
                s.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                s.Yaw = r.ReadSingle();
                s.Pitch = r.ReadSingle();
                s.InGame = r.ReadBoolean();
                s.Dead = r.ReadBoolean();
                s.InFridge = r.ReadBoolean();
                s.LookColor = r.ReadByte();
                s.LookAccessory = r.ReadByte();
                return true;
            }
        }

        public void Dispose()
        {
            // Let the host drop us now instead of after the timeout.
            if (!IsHost)
                Send(EncodeBye(), hostEndPoint);
            running = false;
            udp?.Close();
            if (steam)
            {
                foreach (var id in steamPeers.Values)
                    Steamworks.SteamNetworking.CloseP2PSessionWithUser(id);
            }
        }
    }
}
