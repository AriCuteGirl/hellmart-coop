using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Host-authoritative store shelves. Each shelf's state is its Pack: product (ProductData.saveKey)
    // and count. The host sends every shelf once a second; a client makes its shelves match
    // (clear / set product / fill / remove from the end). Snapshots, not events, so nothing is lost.
    //
    // Shelves are matched by a hash of their scene path (names + sibling indexes), because the order in
    // which racks register with ShelfManager isn't guaranteed to be the same on both machines.
    //
    // Client restocking (box ↔ shelf, Pack.TransferProductTo) happens locally at once and is sent to
    // the host as "+1/−1 of product K on shelf S"; the host applies it to its shelf, and the next
    // snapshot confirms it. A shelf the client just touched ignores snapshots for a moment.
    public static class ShelfSync
    {
        public const byte Channel = 4;
        public const byte ActionChannel = 7;
        private const float LocalGrace = 2f;
        private static readonly Dictionary<uint, float> lastLocalChange = new Dictionary<uint, float>();
        private const float Interval = 1f;
        private const int ShelvesPerPacket = 50;

        private static float nextSend;
        private static Dictionary<uint, Shelf> byId;
        private static int knownShelfCount = -1;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        private static bool TryGetShelfId(Pack pack, out uint id)
        {
            id = 0;
            var index = Index();
            if (index == null || pack == null)
                return false;
            foreach (var pair in index)
            {
                if (pair.Value != null && pair.Value.GetPack() == pack)
                {
                    id = pair.Key;
                    return true;
                }
            }
            return false;
        }

        [HarmonyPatch(typeof(Pack), nameof(Pack.TransferProductTo))]
        private static class ClientRestock
        {
            // The source's product before the move (an emptied pack forgets its product).
            private static void Prefix(Pack __instance, out string __state)
            {
                var config = __instance.GetProductConfig();
                __state = config != null ? config.saveKey : null;
            }

            private static void Postfix(Pack __instance, Pack pack, string __state)
            {
                var net = Plugin.Instance.Net;
                if (net == null || net.IsHost || __state == null)
                    return;
                if (TryGetShelfId(pack, out uint into))
                    SendChange(net, into, __state, +1);
                if (TryGetShelfId(__instance, out uint outOf))
                    SendChange(net, outOf, __state, -1);
            }
        }

        private static void SendChange(Net net, uint shelfId, string key, sbyte delta)
        {
            lastLocalChange[shelfId] = Time.unscaledTime;
            net.SendAction(ActionChannel, w =>
            {
                w.Write(shelfId);
                w.Write(key);
                w.Write(delta);
            });
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            uint id = r.ReadUInt32();
            string key = r.ReadString();
            sbyte delta = r.ReadSByte();
            var index = Index();
            if (index == null || !index.TryGetValue(id, out var shelf) || shelf == null)
            {
                Plugin.Log.LogWarning($"Shelves: client {clientId} changed an unknown shelf {id:X8}");
                return;
            }
            var pack = shelf.GetPack();
            var config = pack.GetProductConfig();
            if (delta > 0)
            {
                if (config == null)
                {
                    var product = Products.Find(key);
                    if (product == null)
                        return;
                    pack.SetConfig(product);
                }
                else if (config.saveKey != key)
                {
                    // Someone filled it with something else meanwhile; the snapshot will correct the client.
                    Plugin.Log.LogInfo($"Shelves: client {clientId} put '{key}' on a shelf holding '{config.saveKey}', ignored");
                    return;
                }
                pack.FillPack(pack.ProductCount + delta);
            }
            else if (pack.ProductCount > 0)
            {
                Object.Destroy(pack.ReduceProduct().gameObject);
            }
            Plugin.Log.LogInfo($"Shelves: client {clientId} {(delta > 0 ? "added" : "took")} '{key}' on {shelf.transform.parent?.name}, now {pack.ProductCount}");
        }

        private static List<Shelf> Shelves()
        {
            if (ShelfManager.Instance == null)
                return null;
            return Traverse.Create(ShelfManager.Instance).Field<List<Shelf>>("shelves").Value;
        }

        // Rebuilt when racks register (Rack.Start) or get destroyed.
        private static Dictionary<uint, Shelf> Index()
        {
            var shelves = Shelves();
            if (shelves == null)
                return null;
            if (byId == null || knownShelfCount != shelves.Count)
            {
                byId = new Dictionary<uint, Shelf>();
                foreach (var shelf in shelves)
                {
                    if (shelf != null)
                        byId[PathId(shelf.transform)] = shelf;
                }
                knownShelfCount = shelves.Count;
            }
            return byId;
        }

        public static uint PathId(Transform t)
        {
            // FNV-1a over "scene/name#index/name#index/...": stable across machines and runs.
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent)
                parts.Add($"{x.name}#{x.GetSiblingIndex()}");
            parts.Add(t.gameObject.scene.name);
            parts.Reverse();
            uint hash = 2166136261;
            foreach (char c in string.Join("/", parts))
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost || Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + Interval;
            RunTest();
            var index = Index();
            if (index == null || index.Count == 0)
                return;
            var all = new List<KeyValuePair<uint, Shelf>>(index);
            for (int start = 0; start < all.Count; start += ShelvesPerPacket)
            {
                int end = Mathf.Min(start + ShelvesPerPacket, all.Count);
                net.BroadcastWorld(Channel, w =>
                {
                    w.Write((byte)(end - start));
                    for (int i = start; i < end; i++)
                    {
                        var shelf = all[i].Value;
                        var pack = shelf != null ? shelf.GetPack() : null;
                        var config = pack != null ? pack.GetProductConfig() : null;
                        w.Write(all[i].Key);
                        w.Write(config != null ? config.saveKey : "");
                        w.Write((byte)(pack != null ? pack.ProductCount : 0));
                    }
                });
            }
        }

        private static void Apply(BinaryReader r)
        {
            int count = r.ReadByte();
            var index = Index();
            for (int n = 0; n < count; n++)
            {
                uint id = r.ReadUInt32();
                string key = r.ReadString();
                int target = r.ReadByte();
                if (index == null || !index.TryGetValue(id, out var shelf) || shelf == null)
                    continue;
                if (lastLocalChange.TryGetValue(id, out float when) && Time.unscaledTime - when < LocalGrace)
                    continue;
                Match(shelf.GetPack(), key, target);
            }
        }

        private static void Match(Pack pack, string key, int target)
        {
            var config = pack.GetProductConfig();
            string current = config != null ? config.saveKey : "";
            if (target == 0 || key == "")
            {
                if (pack.ProductCount > 0)
                    pack.ClearPack();
                return;
            }
            if (current != key)
            {
                var product = Products.Find(key);
                if (product == null)
                {
                    Plugin.Log.LogWarning($"Shelves: unknown product '{key}'");
                    return;
                }
                pack.ClearPack();
                pack.SetConfig(product);
                pack.FillPack(target);
                Plugin.Log.LogInfo($"Shelves: {pack.transform.parent?.name} now {key} x{target} (host)");
                return;
            }
            if (pack.ProductCount < target)
            {
                pack.FillPack(target);
            }
            else
            {
                while (pack.ProductCount > target)
                    Object.Destroy(pack.ReduceProduct().gameObject);
            }
        }

        public static void Clear()
        {
            lastLocalChange.Clear();
            byId = null;
            knownShelfCount = -1;
        }

        // "shelves N, products M" for the status line: equal on both sides when in sync.
        public static string Describe()
        {
            var shelves = Shelves();
            if (shelves == null)
                return "";
            int products = 0;
            foreach (var s in shelves)
            {
                if (s == null)
                    continue;
                products += s.GetPack().ProductCount;
            }
            return $", shelves {shelves.Count} products {products}";
        }

        // Test (HMCOOP_SHELFTEST=client, client): 30 s after the shelves exist, move one product from the
        // fullest shelf to an empty shelf with Pack.TransferProductTo (the call a box in hand uses), which
        // sends a −1 and a +1 to the host.
        private static float clientTestStart = -1f;
        private static bool clientTestDone;

        public static void ClientTest()
        {
            if (clientTestDone || System.Environment.GetEnvironmentVariable("HMCOOP_SHELFTEST") != "client")
                return;
            var shelves = Shelves();
            if (shelves == null || shelves.Count == 0)
                return;
            if (clientTestStart < 0f)
                clientTestStart = Time.unscaledTime;
            if (Time.unscaledTime - clientTestStart < 30f)
                return;
            clientTestDone = true;
            Shelf fullest = null;
            foreach (var s in shelves)
            {
                if (s != null && s.GetPack().ProductCount > 1 && (fullest == null || s.GetPack().ProductCount > fullest.GetPack().ProductCount))
                    fullest = s;
            }
            Shelf empty = null;
            if (fullest != null)
            {
                var config = fullest.GetPack().GetProductConfig();
                foreach (var s in shelves)
                {
                    // The game's own check for putting this product there.
                    if (s != null && s.GetPack().ProductCount == 0 && s.GetPack().CanAddProduct(config))
                    {
                        empty = s;
                        break;
                    }
                }
            }
            if (fullest == null || empty == null)
            {
                Plugin.Log.LogWarning("Shelf client test: no suitable shelves");
                return;
            }
            var key = fullest.GetPack().GetProductConfig().saveKey;
            fullest.GetPack().TransferProductTo(empty.GetPack());
            Plugin.Log.LogInfo($"Shelf client test: moved one '{key}' from {fullest.transform.parent?.name} ({fullest.GetPack().ProductCount} left) to {empty.transform.parent?.name} ({empty.GetPack().ProductCount})");
        }

        // Test (HMCOOP_SHELFTEST=1, host): 30 s after the shelves exist, take up to 3 products off the
        // fullest shelf and stock the first empty shelf with 5 of that product.
        private static float testStart = -1f;
        private static bool testDone;

        private static void RunTest()
        {
            if (testDone || System.Environment.GetEnvironmentVariable("HMCOOP_SHELFTEST") != "1")
                return;
            var shelves = Shelves();
            if (shelves == null || shelves.Count == 0)
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            if (Time.unscaledTime - testStart < 30f)
                return;
            testDone = true;
            Shelf fullest = null, empty = null;
            foreach (var s in shelves)
            {
                if (s == null)
                    continue;
                int n = s.GetPack().ProductCount;
                if (n > 0 && (fullest == null || n > fullest.GetPack().ProductCount))
                    fullest = s;
                else if (n == 0 && empty == null)
                    empty = s;
            }
            if (fullest == null)
            {
                Plugin.Log.LogWarning("Shelf test: every shelf is empty");
                return;
            }
            var product = fullest.GetPack().GetProductConfig();
            int take = Mathf.Min(3, fullest.GetPack().ProductCount - 1);
            for (int i = 0; i < take; i++)
                Object.Destroy(fullest.GetPack().ReduceProduct().gameObject);
            Plugin.Log.LogInfo($"Shelf test: took {take} '{product.saveKey}' from {fullest.transform.parent?.name}, {fullest.GetPack().ProductCount} left");
            if (empty != null)
            {
                empty.GetPack().SetConfig(product);
                empty.GetPack().FillPack(5);
                Plugin.Log.LogInfo($"Shelf test: stocked {empty.transform.parent?.name} with {empty.GetPack().ProductCount} '{product.saveKey}'");
            }
        }
    }
}
