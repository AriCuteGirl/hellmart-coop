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
    }

    // Plain UDP, star topology: clients talk only to the host, the host forwards each
    // client's state to the other clients. Good enough for a LAN/localhost test; Steam P2P later.
    public class Net : IDisposable
    {
        private const uint Magic = 0x31434D48; // "HMC1"
        private const byte MsgState = 1;
        // magic 4 + type 1 + id 1 + seq 4 + position 12 + yaw 4 + pitch 4 + in-game 1 + dead 1 + fridge 1
        private const int StateSize = 33;

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
        private readonly Thread receiver;
        private volatile bool running = true;
        private uint seq;

        public Net(bool isHost, string hostAddress, int port)
        {
            IsHost = isHost;
            if (isHost)
            {
                LocalId = 0;
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
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
        }

        private void ReceiveLoop()
        {
            while (running)
            {
                try
                {
                    IPEndPoint from = null;
                    byte[] data = udp.Receive(ref from);
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
            while (inbox.TryDequeue(out var item))
            {
                if (IsHost && IsBye(item.data))
                {
                    if (clients.TryGetValue(item.from, out var leaving))
                        DropClient(item.from, leaving, "left");
                    continue;
                }
                if (TryDispatchWorld(item.data) || TryHandleAction(item.data, item.from))
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
                return true;
            }
        }

        public void Dispose()
        {
            // Let the host drop us now instead of after the timeout.
            if (!IsHost)
                Send(EncodeBye(), hostEndPoint);
            running = false;
            udp.Close();
        }
    }
}
