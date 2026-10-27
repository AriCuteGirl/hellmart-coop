using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Dirt, trash and cleaning, host-authoritative.
    //
    // Cleanables are CanBeCleaned (dirt decals, dirty glass: fade out) and TrashCleaned (trash piles:
    // shrink stage by stage). Two kinds:
    //  - scene-placed (both games have them): matched by scene path, synced active + progress;
    //  - made during play ("(Clone)" in the path: dirt spawner dirt, footprints, blood): the host
    //    gives them a net id and sends prefab name + pose; the client makes the same object.
    // Host → client (world channel 21, 1 Hz). Whatever the host cleaned disappears on the client.
    // Client → host (action channel 22): the client's own cleaning, batched as "clean this much".
    // The client makes no dirt of its own (DirtManager spawning, blood). FloorDirtAnomaly's dirt is the
    // anomaly's (AnomalySync spawns it with the same seed on the client), so it isn't mirrored again.
    public static class CleanSync
    {
        public const byte Channel = 21;
        public const byte ActionChannel = 22;
        private const float Interval = 1f;
        private const float ActionInterval = 0.25f;
        private const float LocalGrace = 1.5f;
        private const byte KindDecal = 0, KindTrash = 1;

        private static readonly MethodInfo ChangeAlpha = AccessTools.Method(typeof(CanBeCleaned), "ChangeAlpha");
        private static readonly MethodInfo ChangeTrash = AccessTools.Method(typeof(TrashCleaned), "ChangeTrash");

        // Host: net ids for runtime cleanables.
        private static readonly Dictionary<Component, ushort> hostIds = new Dictionary<Component, ushort>();
        private static ushort nextId = 1;
        // Client: runtime copies by net id, and the reverse.
        private static readonly Dictionary<ushort, CanBeCleaned> copies = new Dictionary<ushort, CanBeCleaned>();
        private static readonly Dictionary<Component, uint> clientKeys = new Dictionary<Component, uint>();
        // Client: cleaning not sent yet, and when each target was last cleaned locally.
        private static readonly Dictionary<uint, float> pendingClean = new Dictionary<uint, float>();
        private static readonly Dictionary<uint, float> lastLocal = new Dictionary<uint, float>();
        private static Dictionary<string, GameObject> prefabs;
        private static float nextSend;
        private static float nextAction;
        private static bool applying;

        private static bool IsClient => WorldClock.IsClient;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        private static bool IsRuntime(Transform t) => Path(t).Contains("(Clone)");

        private static string Path(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent)
                s = p.name + "/" + s;
            return s;
        }

        // The top object Instantiate made (its name ends in "(Clone)").
        private static Transform CloneRoot(Transform t)
        {
            var root = t;
            for (var p = t; p != null; p = p.parent)
            {
                if (p.name.EndsWith("(Clone)"))
                    root = p;
            }
            return root;
        }

        // Key on the wire: runtime → 0x80000000 | net id; scene → scene-path hash with the top bit clear.
        private static uint SceneKey(Transform t) => ShelfSync.PathId(t) & 0x7FFFFFFF;

        // ---------------- host ----------------

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost || Time.unscaledTime < nextSend || DirtManager.Instance == null)
                return;
            nextSend = Time.unscaledTime + Interval;

            var anomalyDirt = new HashSet<CanBeCleaned>();
            foreach (var a in Object.FindObjectsByType<ScreamerSystem.FloorDirtAnomaly>(FindObjectsSortMode.None))
            {
                var target = Traverse.Create(a).Field<CanBeCleaned>("cleanedTarget").Value;
                if (target != null)
                    anomalyDirt.Add(target);
            }

            var runtime = new List<(ushort id, string prefab, Vector3 pos, Quaternion rot, float alpha)>();
            var scene = new List<(uint key, byte kind, bool active, float progress)>();
            foreach (var c in Object.FindObjectsByType<CanBeCleaned>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!c.gameObject.scene.IsValid() || anomalyDirt.Contains(c))
                    continue;
                if (IsRuntime(c.transform))
                {
                    if (!c.gameObject.activeInHierarchy)
                        continue;
                    if (!hostIds.TryGetValue(c, out ushort id))
                        hostIds[c] = id = nextId++;
                    var root = CloneRoot(c.transform);
                    runtime.Add((id, root.name.Replace("(Clone)", "").Trim(), root.position, root.rotation, c.CurrentAlpha));
                }
                else
                {
                    scene.Add((SceneKey(c.transform), KindDecal, c.gameObject.activeSelf, c.CurrentAlpha));
                }
            }
            foreach (var t in Object.FindObjectsByType<TrashCleaned>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.gameObject.scene.IsValid() && !IsRuntime(t.transform))
                    scene.Add((SceneKey(t.transform), KindTrash, t.gameObject.activeSelf, Traverse.Create(t).Field<int>("currentTrash").Value));
            }

            // One datagram holds ~40 runtime entries; send the newest if there are more.
            if (runtime.Count > 40)
                runtime.RemoveRange(0, runtime.Count - 40);
            net.BroadcastWorld(Channel, w =>
            {
                w.Write((byte)runtime.Count);
                foreach (var r in runtime)
                {
                    w.Write(r.id);
                    w.Write(r.prefab);
                    w.Write(r.pos.x); w.Write(r.pos.y); w.Write(r.pos.z);
                    w.Write(r.rot.x); w.Write(r.rot.y); w.Write(r.rot.z); w.Write(r.rot.w);
                    w.Write(r.alpha);
                }
                w.Write((ushort)scene.Count);
                foreach (var s in scene)
                {
                    w.Write(s.key);
                    w.Write(s.kind);
                    w.Write(s.active);
                    w.Write(s.progress);
                }
            });
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                uint key = r.ReadUInt32();
                float amount = r.ReadSingle();
                var target = HostFind(key);
                if (target is CanBeCleaned c && c != null)
                    c.CleanDirt(amount);
                else if (target is TrashCleaned t && t != null)
                {
                    for (int k = 0; k < Mathf.RoundToInt(amount); k++)
                        t.CleanDirt(1f);
                }
                if (target != null)
                    Plugin.Log.LogInfo($"Cleaning: client {clientId} cleaned {target.name} by {amount:F2}");
            }
        }

        private static Component HostFind(uint key)
        {
            if ((key & 0x80000000) != 0)
            {
                ushort id = (ushort)(key & 0xFFFF);
                foreach (var pair in hostIds)
                {
                    if (pair.Value == id && pair.Key != null)
                        return pair.Key;
                }
                return null;
            }
            foreach (var c in Object.FindObjectsByType<CanBeCleaned>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c.gameObject.scene.IsValid() && !IsRuntime(c.transform) && SceneKey(c.transform) == key)
                    return c;
            }
            foreach (var t in Object.FindObjectsByType<TrashCleaned>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.gameObject.scene.IsValid() && SceneKey(t.transform) == key)
                    return t;
            }
            return null;
        }

        // ---------------- client ----------------

        private static void Apply(BinaryReader r)
        {
            if (DirtManager.Instance == null)
                return;
            applying = true;
            try
            {
                int runtimeCount = r.ReadByte();
                var seen = new HashSet<ushort>();
                for (int i = 0; i < runtimeCount; i++)
                {
                    ushort id = r.ReadUInt16();
                    string prefabName = r.ReadString();
                    var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    float alpha = r.ReadSingle();
                    seen.Add(id);
                    uint key = 0x80000000u | id;
                    if (!copies.TryGetValue(id, out var copy) || copy == null)
                    {
                        var prefab = FindPrefab(prefabName);
                        if (prefab == null)
                            continue;
                        var go = Object.Instantiate(prefab, pos, rot);
                        copy = go.GetComponentInChildren<CanBeCleaned>();
                        if (copy == null)
                        {
                            Object.Destroy(go);
                            continue;
                        }
                        copies[id] = copy;
                        clientKeys[copy] = key;
                    }
                    if (!RecentlyCleaned(key))
                        SetAlpha(copy, alpha);
                }
                var gone = new List<ushort>();
                foreach (var id in copies.Keys)
                {
                    if (!seen.Contains(id))
                        gone.Add(id);
                }
                foreach (var id in gone)
                {
                    if (copies[id] != null)
                    {
                        clientKeys.Remove(copies[id]);
                        Object.Destroy(CloneRoot(copies[id].transform).gameObject);
                    }
                    copies.Remove(id);
                }

                int sceneCount = r.ReadUInt16();
                var byKey = new Dictionary<uint, (byte kind, bool active, float progress)>();
                for (int i = 0; i < sceneCount; i++)
                    byKey[r.ReadUInt32()] = (r.ReadByte(), r.ReadBoolean(), r.ReadSingle());
                foreach (var c in Object.FindObjectsByType<CanBeCleaned>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (!c.gameObject.scene.IsValid() || IsRuntime(c.transform))
                        continue;
                    uint key = SceneKey(c.transform);
                    clientKeys[c] = key;
                    if (!byKey.TryGetValue(key, out var s) || RecentlyCleaned(key))
                        continue;
                    if (c.gameObject.activeSelf != s.active)
                        c.gameObject.SetActive(s.active);
                    SetAlpha(c, s.progress);
                }
                foreach (var t in Object.FindObjectsByType<TrashCleaned>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (!t.gameObject.scene.IsValid() || IsRuntime(t.transform))
                        continue;
                    uint key = SceneKey(t.transform);
                    clientKeys[t] = key;
                    if (!byKey.TryGetValue(key, out var s) || RecentlyCleaned(key))
                        continue;
                    if (t.gameObject.activeSelf != s.active)
                        t.gameObject.SetActive(s.active);
                    int stage = Mathf.RoundToInt(s.progress);
                    var tr = Traverse.Create(t).Field<int>("currentTrash");
                    if (s.active && stage >= 0 && tr.Value != stage)
                    {
                        tr.Value = stage;
                        ChangeTrash.Invoke(t, new object[] { stage });
                    }
                }
            }
            finally
            {
                applying = false;
            }
        }

        private static bool RecentlyCleaned(uint key) =>
            lastLocal.TryGetValue(key, out float when) && Time.unscaledTime - when < LocalGrace;

        private static void SetAlpha(CanBeCleaned c, float alpha)
        {
            var t = Traverse.Create(c).Field<float>("currentAlpha");
            if (Mathf.Abs(t.Value - alpha) < 0.005f)
                return;
            t.Value = alpha;
            ChangeAlpha.Invoke(c, null);
        }

        private static GameObject FindPrefab(string name)
        {
            if (prefabs == null || !prefabs.ContainsKey(name))
            {
                prefabs = new Dictionary<string, GameObject>();
                foreach (var c in Resources.FindObjectsOfTypeAll<CanBeCleaned>())
                {
                    var root = c.transform.root.gameObject;
                    if (!root.scene.IsValid() && !prefabs.ContainsKey(root.name))
                        prefabs[root.name] = root;
                }
            }
            prefabs.TryGetValue(name, out var prefab);
            if (prefab == null)
                Plugin.Log.LogWarning($"Cleaning: no dirt prefab '{name}' on this client");
            return prefab;
        }

        // The client's own cleaning: remember how much, send in batches.
        [HarmonyPatch(typeof(CanBeCleaned), nameof(CanBeCleaned.CleanDirt))]
        private static class ClientCleansDecal
        {
            private static void Prefix(CanBeCleaned __instance, float amount) => Queue(__instance, amount);
        }

        [HarmonyPatch(typeof(TrashCleaned), nameof(TrashCleaned.CleanDirt))]
        private static class ClientCleansTrash
        {
            private static void Prefix(TrashCleaned __instance) => Queue(__instance, 1f);
        }

        private static void Queue(Component target, float amount)
        {
            if (!IsClient || applying || !clientKeys.TryGetValue(target, out uint key))
                return;
            pendingClean.TryGetValue(key, out float sum);
            pendingClean[key] = sum + amount;
            lastLocal[key] = Time.unscaledTime;
        }

        public static void ClientUpdate(Net net)
        {
            if (net.IsHost || pendingClean.Count == 0 || Time.unscaledTime < nextAction)
                return;
            nextAction = Time.unscaledTime + ActionInterval;
            var batch = new List<KeyValuePair<uint, float>>(pendingClean);
            pendingClean.Clear();
            net.SendAction(ActionChannel, w =>
            {
                w.Write((byte)Mathf.Min(batch.Count, 60));
                for (int i = 0; i < batch.Count && i < 60; i++)
                {
                    w.Write(batch[i].Key);
                    w.Write(batch[i].Value);
                }
            });
        }

        // Clients make no dirt of their own: it comes from the host.
        [HarmonyPatch]
        private static class HostOnlyDirt
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(DirtManager), nameof(DirtManager.TrySpawnDirt));
                yield return AccessTools.Method(typeof(MonsterManager), nameof(MonsterManager.SpawnBloodyDirt));
            }

            private static bool Prefix() => !IsClient;
        }

        public static void Clear()
        {
            hostIds.Clear();
            copies.Clear();
            clientKeys.Clear();
            pendingClean.Clear();
            lastLocal.Clear();
        }

        // ---------------- tests ----------------

        // HMCOOP_CLEANTEST=host: 25 s in, spawn dirt at 4 free DirtSpawners.
        // HMCOOP_CLEANTEST=client: 35 s in, clean the first mirrored dirt fully (in steps, like a mop),
        //   and one stage off the first active trash pile.
        private static float testStart = -1f;
        private static int testStep;

        public static void Test(bool isHost)
        {
            string mode = System.Environment.GetEnvironmentVariable("HMCOOP_CLEANTEST");
            if (testStep >= 2 || DirtManager.Instance == null || mode != (isHost ? "host" : "client"))
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            float t = Time.unscaledTime - testStart;
            if (isHost && testStep == 0 && t >= 25f)
            {
                int made = 0;
                foreach (var s in Object.FindObjectsByType<DirtSpawner>(FindObjectsSortMode.None))
                {
                    if (!s.IsSpawned && made < 4)
                    {
                        s.SpawnDirt();
                        made++;
                    }
                }
                Plugin.Log.LogInfo($"Clean test: host spawned {made} dirt");
                testStep = 2;
            }
            else if (!isHost && testStep == 0 && t >= 35f)
            {
                CanBeCleaned target = null;
                foreach (var c in copies.Values)
                {
                    if (c != null)
                    {
                        target = c;
                        break;
                    }
                }
                if (target != null)
                {
                    Plugin.Log.LogInfo($"Clean test: client mops {target.name} (alpha {target.CurrentAlpha:F2})");
                    for (int i = 0; i < 12 && target != null && target.CurrentAlpha > 0.1f; i++)
                        target.CleanDirt(0.1f);
                }
                foreach (var tc in Object.FindObjectsByType<TrashCleaned>(FindObjectsSortMode.None))
                {
                    Plugin.Log.LogInfo($"Clean test: client shovels {tc.name} (stage {Traverse.Create(tc).Field<int>("currentTrash").Value})");
                    tc.CleanDirt(1f);
                    break;
                }
                testStep = 2;
            }
        }

        public static string Describe()
        {
            int decals = 0, trash = 0;
            foreach (var c in Object.FindObjectsByType<CanBeCleaned>(FindObjectsSortMode.None))
                decals += c.CurrentAlpha > 0.1f ? 1 : 0;
            foreach (var t in Object.FindObjectsByType<TrashCleaned>(FindObjectsSortMode.None))
                trash += Traverse.Create(t).Field<int>("currentTrash").Value + 1;
            return $", dirt {decals} trash {trash}";
        }
    }
}
