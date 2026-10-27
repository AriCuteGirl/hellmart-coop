using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace HellmartCoop
{
    // Name tags over the other players. Your name is your Steam name when Steam (or PupBerg) is up,
    // otherwise [Player] Name in the config, otherwise the PC's user name.
    // Client → host (action channel 30): "my name is ..." once per connection (reliable).
    // Host → clients (world channel 31, 1 Hz): every player's id and name, the host's own included.
    public static class PlayerNames
    {
        public const byte Channel = 31;
        public const byte ActionChannel = 30;
        private const float Interval = 1f;

        private static ConfigEntry<string> nameEntry;
        private static readonly Dictionary<byte, string> names = new Dictionary<byte, string>();
        private static float nextSend;
        private static byte sentForId = 255;

        public static void Bind(ConfigFile config)
        {
            nameEntry = config.Bind("Player", "Name", "", "Name shown over your head when Steam isn't running (empty: your PC user name).");
        }

        public static string LocalName
        {
            get
            {
                try
                {
                    if (Steamworks.SteamClient.IsValid && !string.IsNullOrEmpty(Steamworks.SteamClient.Name))
                        return Steamworks.SteamClient.Name;
                }
                catch (System.Exception)
                {
                    // steam_api not loaded / not initialised.
                }
                if (!string.IsNullOrWhiteSpace(nameEntry?.Value))
                    return nameEntry.Value.Trim();
                return System.Environment.UserName;
            }
        }

        public static void Register(Net net)
        {
            sentForId = 255;
            names.Clear();
            net.OnAction(ActionChannel, (clientId, r) =>
            {
                string name = Clean(r.ReadString());
                names[clientId] = name;
                Plugin.Log.LogInfo($"Names: player {clientId} is '{name}'");
            });
            net.OnWorld(Channel, r =>
            {
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                    names[r.ReadByte()] = r.ReadString();
            });
            net.ClientLeft += id => names.Remove(id);
        }

        private static string Clean(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0)
                return "Player";
            return name.Length > 24 ? name.Substring(0, 24) : name;
        }

        public static string NameOf(byte id) => names.TryGetValue(id, out var n) ? n : null;

        public static void Update(Net net)
        {
            if (!net.IsHost)
            {
                // Each time the host gives us an id (a new one after a timeout too).
                if (net.LocalId != 255 && net.LocalId != sentForId)
                {
                    sentForId = net.LocalId;
                    string mine = Clean(LocalName);
                    net.SendAction(ActionChannel, w => w.Write(mine));
                    Plugin.Log.LogInfo($"Names: I am '{mine}'");
                }
                return;
            }
            if (Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + Interval;
            names[0] = Clean(LocalName);
            var list = new List<KeyValuePair<byte, string>>(names);
            net.BroadcastWorld(Channel, w =>
            {
                w.Write((byte)list.Count);
                foreach (var pair in list)
                {
                    w.Write(pair.Key);
                    w.Write(pair.Value);
                }
            });
        }
    }
}
