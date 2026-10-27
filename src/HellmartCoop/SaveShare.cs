using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HellmartCoop
{
    // Hands the host's active save slot to joining clients over TCP (same port number as the UDP
    // traffic). A slot is a folder with meta.json and save.json, both small JSON files.
    //
    // Wire format, host → client: "HMS1", then meta.json and save.json, each as int32 length + bytes.
    public static class SaveShare
    {
        private const string Magic = "HMS1";
        public const string CoopSlotId = "coop-host";
        private const int MaxFileSize = 16 * 1024 * 1024;

        public class Host : IDisposable
        {
            private readonly TcpListener listener;
            private readonly ConcurrentQueue<TcpClient> pending = new ConcurrentQueue<TcpClient>();
            private volatile bool running = true;

            public Host(int port)
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                new Thread(AcceptLoop) { IsBackground = true, Name = "HellmartCoop.SaveShare" }.Start();
            }

            private void AcceptLoop()
            {
                while (running)
                {
                    try
                    {
                        pending.Enqueue(listener.AcceptTcpClient());
                    }
                    catch (SocketException)
                    {
                        if (!running)
                            return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }

            // Main thread: the save slot API reads Unity's persistentDataPath and settings.
            public void Serve()
            {
                while (pending.TryDequeue(out var client))
                {
                    // Read before the using block disposes the socket.
                    var remote = client.Client.RemoteEndPoint;
                    try
                    {
                        string slotId = SaveSlots.GetActiveSlotId();
                        var info = SaveSlots.ReadSlotInfo(slotId);
                        byte[] meta = ReadOrEmpty(Path.Combine(info.DirectoryPath, "meta.json"));
                        byte[] save = ReadOrEmpty(info.SavePath);
                        using (client)
                        using (var w = new BinaryWriter(client.GetStream()))
                        {
                            w.Write(Encoding.ASCII.GetBytes(Magic));
                            w.Write(meta.Length);
                            w.Write(meta);
                            w.Write(save.Length);
                            w.Write(save);
                        }
                        Plugin.Log.LogInfo($"Sent save slot '{slotId}' (day {info.Day}, {info.Phase}, {save.Length} bytes) to {remote}");
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Could not send save: {e.Message}");
                    }
                }
            }

            private static byte[] ReadOrEmpty(string path)
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            }

            public void Dispose()
            {
                running = false;
                listener.Stop();
            }
        }

        public class Received
        {
            public byte[] Meta;
            public byte[] Save;
        }

        // Background thread; the result is applied on the main thread with Install.
        public static Task<Received> Fetch(string hostAddress, int port, int timeoutMs = 5000)
        {
            return Task.Run(() =>
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(hostAddress, port);
                    if (!connect.Wait(timeoutMs))
                        throw new TimeoutException($"no answer from {hostAddress}:{port}");
                    client.ReceiveTimeout = timeoutMs;
                    using (var r = new BinaryReader(client.GetStream()))
                    {
                        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != Magic)
                            throw new InvalidDataException("not a Hellmart Coop host");
                        return new Received { Meta = ReadBlock(r), Save = ReadBlock(r) };
                    }
                }
            });
        }

        private static byte[] ReadBlock(BinaryReader r)
        {
            int length = r.ReadInt32();
            if (length < 0 || length > MaxFileSize)
                throw new InvalidDataException($"bad block length {length}");
            byte[] data = r.ReadBytes(length);
            if (data.Length != length)
                throw new EndOfStreamException("save transfer cut off");
            return data;
        }

        // Main thread: writes the host's save into its own slot (never the player's own slots)
        // and makes it the active one, so Menu.PlayGame loads it.
        public static void Install(Received received)
        {
            SaveSlots.Create(CoopSlotId, "Co-op (host)");
            var info = SaveSlots.ReadSlotInfo(CoopSlotId);
            if (received.Meta.Length > 0)
            {
                // Keep the host's day/phase for the slot list, but our own id and name.
                var meta = Newtonsoft.Json.Linq.JObject.Parse(Encoding.UTF8.GetString(received.Meta));
                meta["id"] = CoopSlotId;
                meta["name"] = "Co-op (host)";
                File.WriteAllText(Path.Combine(info.DirectoryPath, "meta.json"), meta.ToString());
            }
            File.WriteAllBytes(info.SavePath, received.Save);
            SaveGame.Instance.SwitchSlot(CoopSlotId);
            Plugin.Log.LogInfo($"Installed host save into slot '{CoopSlotId}' ({received.Save.Length} bytes)");
        }
    }
}
