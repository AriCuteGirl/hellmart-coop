using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Night anomalies (dirt on the floor, wrong products on a shelf, phantoms, fire alarm, broken
    // camera...), host-authoritative.
    //
    // Several pick a random target in OnSpawn, so every spawn on the host runs with a seeded Random
    // and the seed travels with it; a client spawning the same anomaly with the same seed makes the
    // same choices (the scene lists are the same on both machines).
    // Host → client (world channel 19, 2 Hz): active anomalies (scene-path id + seed). The client
    //   spawns what it lacks and clears what the host cleared (Despawn, so its anomaly quest counts).
    // Client → host (action channel 20): the client cleared one; the host clears it (pays, counts).
    // ScanProductAnomaly only puts a product on the register tape, which CheckoutSync already mirrors,
    // so clients don't spawn it themselves.
    public static class AnomalySync
    {
        public const byte Channel = 19;
        public const byte ActionChannel = 20;
        private const float Interval = 0.5f;

        private static readonly Dictionary<AnomalyBehavior, int> hostSeeds = new Dictionary<AnomalyBehavior, int>();
        private static readonly Dictionary<uint, int> clientSeeds = new Dictionary<uint, int>();
        private static float nextSend;
        private static bool applying;
        private static bool hostSpawning;
        private static readonly System.Random seedSource = new System.Random();

        private static bool IsClient => WorldClock.IsClient;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        private static AnomalyBehavior[] All() =>
            Object.FindObjectsByType<AnomalyBehavior>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // ---------------- host ----------------

        [HarmonyPatch(typeof(AnomalyBehavior), nameof(AnomalyBehavior.Spawn))]
        private static class SeededSpawn
        {
            private static void Prefix(AnomalyBehavior __instance, out Random.State __state)
            {
                __state = Random.state;
                if (Plugin.Instance.Net == null)
                    return;
                if (IsClient)
                {
                    // Our own Apply sets the seed; anything else spawning locally is not ours to run.
                    return;
                }
                int seed = seedSource.Next();
                hostSeeds[__instance] = seed;
                Random.InitState(seed);
                Plugin.Log.LogInfo($"Anomalies: {__instance.name} spawned (seed {seed})");
            }

            private static void Postfix(AnomalyBehavior __instance, Random.State __state)
            {
                if (Plugin.Instance.Net != null && !IsClient)
                {
                    Random.state = __state;
                    Plugin.Log.LogInfo($"Anomalies: {__instance.name} target {Detail(__instance)}");
                }
            }
        }

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost || Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + Interval;
            var active = new List<KeyValuePair<AnomalyBehavior, int>>();
            foreach (var pair in hostSeeds)
            {
                if (pair.Key != null && pair.Key.isActive)
                    active.Add(pair);
            }
            net.BroadcastWorld(Channel, w =>
            {
                w.Write((byte)active.Count);
                foreach (var pair in active)
                {
                    w.Write(ShelfSync.PathId(pair.Key.transform));
                    w.Write(pair.Value);
                }
            });
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            uint id = r.ReadUInt32();
            foreach (var a in All())
            {
                if (a.gameObject.scene.IsValid() && ShelfSync.PathId(a.transform) == id && a.isActive)
                {
                    a.Despawn();
                    Plugin.Log.LogInfo($"Anomalies: client {clientId} cleared {a.name}");
                    return;
                }
            }
        }

        // ---------------- client ----------------

        private static void Apply(BinaryReader r)
        {
            int count = r.ReadByte();
            var active = new Dictionary<uint, int>();
            for (int i = 0; i < count; i++)
                active[r.ReadUInt32()] = r.ReadInt32();
            if (DayNightManager.Instance == null)
                return;
            applying = true;
            try
            {
                foreach (var a in All())
                {
                    if (!a.gameObject.scene.IsValid() || a is ScreamerSystem.ScanProductAnomaly)
                        continue;
                    uint id = ShelfSync.PathId(a.transform);
                    bool hostActive = active.TryGetValue(id, out int seed);
                    if (hostActive && !a.isActive)
                    {
                        var state = Random.state;
                        Random.InitState(seed);
                        try
                        {
                            a.Spawn();
                        }
                        finally
                        {
                            Random.state = state;
                        }
                        clientSeeds[id] = seed;
                        Plugin.Log.LogInfo($"Anomalies: {a.name} spawned (host, seed {seed}) target {Detail(a)}");
                    }
                    else if (!hostActive && a.isActive)
                    {
                        a.Despawn();
                        Plugin.Log.LogInfo($"Anomalies: {a.name} cleared (host)");
                    }
                }
            }
            finally
            {
                applying = false;
            }
        }

        // The client cleared an anomaly itself: tell the host.
        [HarmonyPatch(typeof(AnomalyBehavior), nameof(AnomalyBehavior.Despawn))]
        private static class ClientClears
        {
            private static void Prefix(AnomalyBehavior __instance)
            {
                var net = Plugin.Instance.Net;
                if (!IsClient || applying || net == null || !__instance.isActive)
                    return;
                uint id = ShelfSync.PathId(__instance.transform);
                Plugin.Log.LogInfo($"Anomalies: cleared {__instance.name}, telling the host");
                net.SendAction(ActionChannel, w => w.Write(id));
            }
        }

        // What an anomaly picked, for comparing host and client in the logs.
        private static string Detail(AnomalyBehavior a)
        {
            foreach (string field in new[] { "currentAnomaly", "phantom" })
            {
                // Plain reflection: AccessTools.Field warns for every type without the field.
                var f = a.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f == null)
                    continue;
                var value = f.GetValue(a);
                if (value is Object o)
                    return o != null ? o.name : "none";
                return value?.ToString() ?? "none";
            }
            return "-";
        }

        public static void Clear()
        {
            hostSeeds.Clear();
            clientSeeds.Clear();
        }

        // Clients: anomalies come from the host. The anomaly quest still runs locally (it counts clears).
        [HarmonyPatch]
        private static class HostOnly
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(AnomalySpawner), nameof(AnomalySpawner.Spawn));
                yield return AccessTools.Method(typeof(AnomalyGenerator), nameof(AnomalyGenerator.OnUpdate));
            }

            private static bool Prefix() => !IsClient;
        }

        // ---------------- tests ----------------

        // HMCOOP_ANOMALYTEST=host: both inside at 24 s (shared with the door test's warp spots), 26 s
        // Night, 28/30/32 s three AnomalySpawner.Spawn calls.
        // HMCOOP_ANOMALYTEST=client: 45 s, clear the first active anomaly.
        private static float testStart = -1f;
        private static int testStep;

        public static void Test(bool isHost)
        {
            string mode = System.Environment.GetEnvironmentVariable("HMCOOP_ANOMALYTEST");
            if (testStep >= 9 || DayNightManager.Instance == null || PlayerManager.Instance == null || mode != (isHost ? "host" : "client"))
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            float t = Time.unscaledTime - testStart;
            if (testStep == 0 && t >= 24f)
            {
                PlayerManager.Instance.Warp(isHost ? new Vector3(1.33f, 0.3f, -4.54f) : new Vector3(-1.5f, 0.3f, -4.54f));
                testStep = isHost ? 1 : 5;
            }
            else if (isHost && testStep == 1 && t >= 26f)
            {
                var day = DayNightManager.Instance;
                AccessTools.Method(typeof(DayNightManager), "SwitchState").Invoke(day, new object[] { day.NightState });
                testStep = 2;
            }
            else if (isHost && testStep >= 2 && testStep <= 4 && t >= 28f + 2f * (testStep - 2))
            {
                var spawners = Object.FindObjectsByType<AnomalySpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (spawners.Length > 0)
                    spawners[0].Spawn();
                testStep++;
                if (testStep == 5)
                    testStep = 9;
            }
            else if (!isHost && testStep == 5 && t >= 45f)
            {
                foreach (var a in All())
                {
                    if (a.isActive && !(a is ScreamerSystem.ScanProductAnomaly))
                    {
                        Plugin.Log.LogInfo($"Anomaly test: client clears {a.name}");
                        a.Despawn();
                        break;
                    }
                }
                testStep = 9;
            }
        }

        public static string Describe()
        {
            int n = 0;
            foreach (var a in All())
                n += a.isActive ? 1 : 0;
            return $", anomalies {n}";
        }
    }
}
