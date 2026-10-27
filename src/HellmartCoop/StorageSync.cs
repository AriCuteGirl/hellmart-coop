using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DayNight.Simulator.Storage;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Back-room storage: the box slots on the storage racks, boxes taken out and put back, and
    // ordering refills on the computer. Host-authoritative.
    //
    // Host → client (world channel 26, 1 Hz): every slot (scene-path id): has a box, its product, how
    //   many products are in it. The client makes its slots match; its own refills (noon refresh) are off.
    // Client → host (action channel 27):
    //   - took the box out of a slot (it stays in the client's hands, local to that client; restocking
    //     from it goes through ShelfSync) → the host's slot is emptied;
    //   - put a box into a slot → the host puts a box with that product and count there;
    //   - ordered a refill for a rack (StorageManager.FillByRack) → the host does it (the payment already
    //     goes to the shared wallet through CashSync).
    public static class StorageSync
    {
        public const byte Channel = 26;
        public const byte ActionChannel = 27;
        private const float Interval = 1f;
        private const float LocalGrace = 2f;
        private const byte ActTake = 0, ActPut = 1, ActOrder = 2;

        private static readonly MethodInfo SetPackage = AccessTools.Method(typeof(PackageShelf), "SetPackage");
        private static readonly MethodInfo SetPackageProperty = AccessTools.PropertySetter(typeof(PackageShelf), nameof(PackageShelf.Package));
        private static readonly Dictionary<uint, float> lastLocal = new Dictionary<uint, float>();
        private static float nextSend;
        private static bool applying;

        private static bool IsClient => WorldClock.IsClient;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        private static PackageShelf[] Slots() =>
            Object.FindObjectsByType<PackageShelf>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        private static PackageShelf FindSlot(uint id)
        {
            foreach (var s in Slots())
            {
                if (s.gameObject.scene.IsValid() && ShelfSync.PathId(s.transform) == id)
                    return s;
            }
            return null;
        }

        private static string Key(ProductData p) =>
            p == null ? "" : !string.IsNullOrEmpty(p.saveKey) ? p.saveKey : "name:" + p.name;

        // A box with that product and count in that slot, the way PackageShelf.SpawnPackage makes one.
        private static void SpawnBox(PackageShelf slot, string key, int count)
        {
            var product = Products.Find(key);
            var t = Traverse.Create(slot);
            var prefab = t.Field<GameObject>("packagePrefab").Value;
            var point = t.Field<Transform>("packagePoint").Value;
            if (product == null || prefab == null || point == null)
                return;
            var go = Object.Instantiate(prefab, point);
            var pack = go.GetComponent<Pack>();
            pack.SetConfig(product);
            pack.FillPack(count);
            SetPackage.Invoke(slot, new object[] { go });
        }

        private static void EmptySlot(PackageShelf slot)
        {
            if (slot.Package == null)
                return;
            Object.Destroy(slot.Package.gameObject);
            SetPackageProperty.Invoke(slot, new object[] { null });
        }

        // ---------------- host ----------------

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost || StorageManager.Instance == null || Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + Interval;
            var list = new List<PackageShelf>();
            foreach (var s in Slots())
            {
                if (s.gameObject.scene.IsValid())
                    list.Add(s);
            }
            // 40 slots per datagram (~20 bytes each).
            for (int start = 0; start < list.Count; start += 40)
            {
                int end = Mathf.Min(start + 40, list.Count);
                net.BroadcastWorld(Channel, w =>
                {
                    w.Write((byte)(end - start));
                    for (int i = start; i < end; i++)
                    {
                        var slot = list[i];
                        var pack = slot.Package != null ? slot.Package.GetComponent<Pack>() : null;
                        w.Write(ShelfSync.PathId(slot.transform));
                        w.Write(pack != null);
                        w.Write(pack != null ? Key(pack.GetProductConfig()) : "");
                        w.Write((byte)(pack != null ? pack.ProductCount : 0));
                    }
                });
            }
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            byte act = r.ReadByte();
            uint id = r.ReadUInt32();
            switch (act)
            {
                case ActTake:
                {
                    var slot = FindSlot(id);
                    if (slot == null)
                        return;
                    EmptySlot(slot);
                    Traverse.Create(slot).Field<StorageRack>("rack").Value?.UpdateData();
                    Plugin.Log.LogInfo($"Storage: client {clientId} took the box from {slot.name}");
                    break;
                }
                case ActPut:
                {
                    string key = r.ReadString();
                    int count = r.ReadByte();
                    var slot = FindSlot(id);
                    if (slot == null || !slot.IsFree)
                        return;
                    SpawnBox(slot, key, count);
                    Traverse.Create(slot).Field<StorageRack>("rack").Value?.UpdateData();
                    Plugin.Log.LogInfo($"Storage: client {clientId} put a box of '{key}' x{count} into {slot.name}");
                    break;
                }
                case ActOrder:
                {
                    foreach (var rack in Object.FindObjectsByType<StorageRack>(FindObjectsSortMode.None))
                    {
                        if (ShelfSync.PathId(rack.transform) == id)
                        {
                            StorageManager.Instance.FillByRack(rack.RackData);
                            Plugin.Log.LogInfo($"Storage: client {clientId} ordered a refill for {rack.name}");
                            return;
                        }
                    }
                    break;
                }
            }
        }

        // ---------------- client ----------------

        private static void Apply(BinaryReader r)
        {
            int count = r.ReadByte();
            var states = new Dictionary<uint, (bool has, string key, int n)>();
            for (int i = 0; i < count; i++)
                states[r.ReadUInt32()] = (r.ReadBoolean(), r.ReadString(), r.ReadByte());
            if (StorageManager.Instance == null)
                return;
            applying = true;
            try
            {
                foreach (var slot in Slots())
                {
                    if (!slot.gameObject.scene.IsValid())
                        continue;
                    uint id = ShelfSync.PathId(slot.transform);
                    if (!states.TryGetValue(id, out var s) || (lastLocal.TryGetValue(id, out float when) && Time.unscaledTime - when < LocalGrace))
                        continue;
                    if (!s.has)
                    {
                        EmptySlot(slot);
                        continue;
                    }
                    var pack = slot.Package != null ? slot.Package.GetComponent<Pack>() : null;
                    if (pack == null)
                    {
                        SpawnBox(slot, s.key, s.n);
                        continue;
                    }
                    if (Key(pack.GetProductConfig()) != s.key)
                    {
                        EmptySlot(slot);
                        SpawnBox(slot, s.key, s.n);
                        continue;
                    }
                    if (pack.ProductCount < s.n)
                        pack.FillPack(s.n);
                    while (pack.ProductCount > s.n)
                        Object.Destroy(pack.ReduceProduct().gameObject);
                }
            }
            finally
            {
                applying = false;
            }
        }

        private static void Send(byte act, uint id, System.Action<BinaryWriter> body = null)
        {
            var net = Plugin.Instance.Net;
            if (applying || net == null || net.IsHost)
                return;
            lastLocal[id] = Time.unscaledTime;
            net.SendAction(ActionChannel, w =>
            {
                w.Write(act);
                w.Write(id);
                body?.Invoke(w);
            });
        }

        [HarmonyPatch(typeof(PackageShelf), "PackageGrabbed")]
        private static class ClientTakesBox
        {
            private static void Postfix(PackageShelf __instance)
            {
                if (IsClient)
                    Send(ActTake, ShelfSync.PathId(__instance.transform));
            }
        }

        [HarmonyPatch(typeof(PackageShelf), nameof(PackageShelf.PutPackage))]
        private static class ClientPutsBox
        {
            private static void Postfix(PackageShelf __instance, ProductPackage productPackage)
            {
                if (!IsClient || productPackage == null)
                    return;
                var pack = productPackage.GetComponent<Pack>();
                string key = pack != null ? Key(pack.GetProductConfig()) : "";
                int n = pack != null ? pack.ProductCount : 0;
                Send(ActPut, ShelfSync.PathId(__instance.transform), w =>
                {
                    w.Write(key);
                    w.Write((byte)n);
                });
            }
        }

        [HarmonyPatch(typeof(StorageManager), nameof(StorageManager.FillByRack))]
        private static class ClientOrders
        {
            private static bool Prefix(ScriptableObjects.StorageRackData rackData)
            {
                if (!IsClient)
                    return true;
                foreach (var rack in Object.FindObjectsByType<StorageRack>(FindObjectsSortMode.None))
                {
                    if (rack.RackData == rackData)
                    {
                        Send(ActOrder, ShelfSync.PathId(rack.transform));
                        Plugin.Log.LogInfo($"Storage: ordered a refill for {rack.name}, asking the host");
                        break;
                    }
                }
                return false;
            }
        }

        // Client: boxes appear only from the host (the noon refresh would make our own).
        [HarmonyPatch]
        private static class NoClientRefill
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(PackageShelf), nameof(PackageShelf.RefreshPackage));
                yield return AccessTools.Method(typeof(PackageShelf), nameof(PackageShelf.SpawnPackage));
            }

            private static bool Prefix() => !IsClient;
        }

        public static void Clear()
        {
            lastLocal.Clear();
        }

        // Test (HMCOOP_STORAGETEST=client): 35 s in, take the box out of the first full slot and put it into
        // the first free slot; 40 s, order a refill for the rack of the slot it came from.
        private static float testStart = -1f;
        private static int testStep;
        private static PackageShelf takenFrom;

        public static void ClientTest()
        {
            if (testStep >= 2 || StorageManager.Instance == null || System.Environment.GetEnvironmentVariable("HMCOOP_STORAGETEST") != "client")
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            float t = Time.unscaledTime - testStart;
            if (testStep == 0 && t >= 35f)
            {
                testStep = 1;
                PackageShelf full = null, free = null;
                foreach (var s in Slots())
                {
                    if (!s.gameObject.scene.IsValid())
                        continue;
                    if (full == null && !s.IsFree) full = s;
                    else if (free == null && s.IsFree) free = s;
                }
                if (full == null || free == null)
                {
                    Plugin.Log.LogWarning($"Storage test: need a full and a free slot (full {full != null}, free {free != null})");
                    testStep = 2;
                    return;
                }
                takenFrom = full;
                var box = full.Package;
                var pack = box.GetComponent<Pack>();
                Plugin.Log.LogInfo($"Storage test: client takes the box ({Key(pack.GetProductConfig())} x{pack.ProductCount}) from {full.name} and puts it into {free.name}");
                // What a grab does to the slot (GrabObject.OnGrabbed → PackageGrabbed), then PutToStorage.
                AccessTools.Method(typeof(PackageShelf), "PackageGrabbed").Invoke(full, null);
                free.PutPackage(box);
            }
            else if (testStep == 1 && t >= 40f)
            {
                testStep = 2;
                var rack = Traverse.Create(takenFrom).Field<StorageRack>("rack").Value;
                if (rack != null)
                {
                    Plugin.Log.LogInfo($"Storage test: client orders a refill for {rack.name}");
                    StorageManager.Instance.FillByRack(rack.RackData);
                }
            }
        }

        public static string Describe()
        {
            int boxes = 0, products = 0;
            foreach (var s in Slots())
            {
                if (s.Package == null)
                    continue;
                boxes++;
                products += s.Package.GetComponent<Pack>()?.ProductCount ?? 0;
            }
            return $", storage boxes {boxes} items {products}";
        }
    }
}
