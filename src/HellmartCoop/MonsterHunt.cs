using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Host monsters hunt every player, not only the host.
    //
    // The monster AI only knows PlayerManager.Instance (position) and PlayerManager.LookController (eyes).
    // Before each Monster.Update on the host we pick the nearest player; if that is a remote player,
    // the host's player and look transforms stand in at the remote pose for the duration of the call
    // and are put back right after. So seeing, hearing, chasing and the kill distance all work on the
    // remote player with the game's own code.
    //
    // A kill that starts while a remote player is the target doesn't play on the host: the host sends
    // it to that player (world channel 14, repeated a few times, deduplicated by kill number), skips
    // its own death screen for that attack and puts the monster back to searching a few seconds later.
    // The caught client plays the same kill variant on its copy of the monster, then spectates
    // (CoopDeath).
    public static class MonsterHunt
    {
        public const byte Channel = 14;
        public const byte GetOutChannel = 16;
        private const float RemoteKillDuration = 5f;

        // Host: set while Monster.Update runs against a remote player.
        private static byte? remoteTarget;
        private static readonly Dictionary<Monster, float> remoteKills = new Dictionary<Monster, float>();
        private static ushort killSeq;
        private static readonly List<(byte client, ushort monster, string variant, ushort seq, int sendsLeft)> outgoing =
            new List<(byte, ushort, string, ushort, int)>();

        // Client.
        private static readonly HashSet<ushort> handledKills = new HashSet<ushort>();

        private static bool CoopHost => Plugin.Instance.Net != null && Plugin.Instance.Net.IsHost;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, ApplyKill);
            net.OnWorld(GetOutChannel, ApplyGetOut);
        }

        // ---------------- host ----------------

        private struct Saved
        {
            public bool Swapped;
            public Vector3 PlayerPosition;
            public Vector3 LookPosition;
            public Quaternion LookRotation;
            public bool FridgeSwapped;
            public Fridge HostFridge;
        }

        [HarmonyPatch(typeof(Monster), "Update")]
        private static class HuntNearestPlayer
        {
            private static void Prefix(Monster __instance, out Saved __state)
            {
                __state = default;
                remoteTarget = null;
                var player = PlayerManager.Instance;
                if (!CoopHost || player == null)
                    return;
                var look = PlayerManager.LookController.transform;
                Vector3 monsterPos = __instance.transform.position;
                // A dead (spectating) host is no target.
                float best = CoopDeath.LocalDead ? float.MaxValue : Vector3.Distance(monsterPos, player.transform.position);
                (byte id, Vector3 position, float yaw, float pitch)? target = null;
                foreach (var pose in Plugin.Instance.Puppets.Poses())
                {
                    float d = Vector3.Distance(monsterPos, pose.position);
                    if (d < best)
                    {
                        best = d;
                        target = pose;
                    }
                }
                // Mid-kill on a remote player: stay on that player.
                if (remoteKills.ContainsKey(__instance) && target == null)
                    return;
                if (target == null)
                    return;

                __state = new Saved
                {
                    Swapped = true,
                    PlayerPosition = player.transform.position,
                    LookPosition = look.position,
                    LookRotation = look.rotation,
                };
                var t = target.Value;
                Vector3 eyeOffset = look.position - player.transform.position;
                player.transform.position = t.position;
                look.SetPositionAndRotation(t.position + eyeOffset, Quaternion.Euler(t.pitch, t.yaw, 0f));
                remoteTarget = t.id;

                // A remote player hiding in a fridge: the game's code reaches for CurrentFridge when it
                // pulls the hidden player out, so stand the host's copy of that fridge in for it.
                if (Plugin.Instance.Puppets.IsInFridge(t.id))
                {
                    __state.FridgeSwapped = true;
                    __state.HostFridge = MonsterManager.Instance.CurrentFridge;
                    MonsterManager.Instance.CurrentFridge = NearestFridge(t.position);
                }
            }

            private static void Finalizer(Saved __state)
            {
                remoteTarget = null;
                if (__state.FridgeSwapped && MonsterManager.Instance != null)
                    MonsterManager.Instance.CurrentFridge = __state.HostFridge;
                if (!__state.Swapped || PlayerManager.Instance == null)
                    return;
                PlayerManager.Instance.transform.position = __state.PlayerPosition;
                PlayerManager.LookController.transform.SetPositionAndRotation(__state.LookPosition, __state.LookRotation);
            }
        }

        private static Fridge NearestFridge(Vector3 position)
        {
            Fridge best = null;
            float bestDistance = float.MaxValue;
            foreach (var f in UnityEngine.Object.FindObjectsByType<Fridge>(FindObjectsSortMode.None))
            {
                float d = Vector3.Distance(f.transform.position, position);
                if (d < bestDistance)
                {
                    best = f;
                    bestDistance = d;
                }
            }
            return best;
        }

        // Hiding is per player: against a remote target, "is the hero hidden" means that player.
        [HarmonyPatch(typeof(MonsterManager), nameof(MonsterManager.IsHeroHidden), MethodType.Getter)]
        private static class RemoteHidden
        {
            private static bool Prefix(ref bool __result)
            {
                if (remoteTarget == null)
                    return true;
                __result = Plugin.Instance.Puppets.IsInFridge(remoteTarget.Value);
                return false;
            }
        }

        // The monster pulls a hidden remote player out: that happens on their machine, not to the host.
        [HarmonyPatch(typeof(Fridge), nameof(Fridge.GetOut))]
        private static class RemoteGetOut
        {
            private static bool Prefix()
            {
                if (remoteTarget == null)
                    return true;
                getOuts.Add((remoteTarget.Value, ++killSeq, 5));
                Plugin.Log.LogInfo($"Monster: pulling client {remoteTarget.Value} out of the fridge");
                return false;
            }
        }

        private static readonly List<(byte client, ushort seq, int sendsLeft)> getOuts = new List<(byte, ushort, int)>();

        [HarmonyPatch(typeof(MonsterKills), nameof(MonsterKills.Play))]
        private static class RedirectRemoteKill
        {
            private static bool Prefix(MonsterKills __instance, KillAnimationVariant killAnimation)
            {
                // Nobody alive in reach: a spectating host must not get a second kill shot.
                if (remoteTarget == null)
                    return !(Plugin.Instance.Net != null && CoopDeath.LocalDead);
                var monster = __instance.GetComponentInParent<Monster>();
                if (monster == null || !CustomerSync.TryGetId(monster, out ushort monsterId))
                    return true;
                string variant = VariantName(__instance, killAnimation);
                remoteKills[monster] = Time.unscaledTime;
                outgoing.Add((remoteTarget.Value, monsterId, variant, ++killSeq, 5));
                Plugin.Log.LogInfo($"Monster: caught client {remoteTarget.Value} ({variant})");
                return false;
            }
        }

        public static bool IsRemoteKill(Monster monster) => remoteKills.ContainsKey(monster);

        // Called instead of HandleAttack while the monster's attack is on a remote player.
        public static void TickRemoteKill(Monster monster)
        {
            if (remoteKills.TryGetValue(monster, out float started) && Time.unscaledTime - started > RemoteKillDuration)
            {
                remoteKills.Remove(monster);
                BackToSearch(monster);
            }
        }

        public static void BackToSearch(Monster monster)
        {
            var t = Traverse.Create(monster);
            var stateField = t.Field("currentState");
            stateField.SetValue(Enum.Parse(stateField.GetValueType(), "Search"));
            t.Field("attackTimer").SetValue(0f);
            t.Field("isPlayerKilled").SetValue(false);
            Plugin.Log.LogInfo("Monster: back to searching");
        }

        private static string VariantName(MonsterKills kills, KillAnimationVariant v)
        {
            if (v == kills.EatPlayer) return nameof(MonsterKills.EatPlayer);
            if (v == kills.KickBackPlayer) return nameof(MonsterKills.KickBackPlayer);
            if (v == kills.ZobPlayer) return nameof(MonsterKills.ZobPlayer);
            return nameof(MonsterKills.SpikeNosePlayer);
        }

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost)
                return;
            for (int i = getOuts.Count - 1; i >= 0; i--)
            {
                var g = getOuts[i];
                net.BroadcastWorld(GetOutChannel, w =>
                {
                    w.Write(g.client);
                    w.Write(g.seq);
                });
                if (g.sendsLeft <= 1)
                    getOuts.RemoveAt(i);
                else
                    getOuts[i] = (g.client, g.seq, g.sendsLeft - 1);
            }
            for (int i = outgoing.Count - 1; i >= 0; i--)
            {
                var k = outgoing[i];
                net.BroadcastWorld(Channel, w =>
                {
                    w.Write(k.client);
                    w.Write(k.monster);
                    w.Write(k.variant);
                    w.Write(k.seq);
                });
                if (k.sendsLeft <= 1)
                    outgoing.RemoveAt(i);
                else
                    outgoing[i] = (k.client, k.monster, k.variant, k.seq, k.sendsLeft - 1);
            }
        }

        // ---------------- client ----------------

        private static void ApplyKill(BinaryReader r)
        {
            byte client = r.ReadByte();
            ushort monsterId = r.ReadUInt16();
            string variant = r.ReadString();
            ushort seq = r.ReadUInt16();
            var net = Plugin.Instance.Net;
            if (net == null || client != net.LocalId || !handledKills.Add(seq))
                return;
            Plugin.Log.LogInfo($"Monster: caught by monster {monsterId} ({variant})");
            Plugin.Instance.StartCoroutine(PlayKill(monsterId, variant));
        }

        private static void ApplyGetOut(BinaryReader r)
        {
            byte client = r.ReadByte();
            ushort seq = r.ReadUInt16();
            var net = Plugin.Instance.Net;
            if (net == null || client != net.LocalId || !handledKills.Add(seq))
                return;
            var fridge = MonsterManager.Instance != null ? MonsterManager.Instance.CurrentFridge : null;
            Plugin.Log.LogInfo($"Monster: pulled out of the fridge (hiding: {fridge != null})");
            if (fridge != null)
                fridge.GetOut();
        }

        private static IEnumerator PlayKill(ushort monsterId, string variant)
        {
            CoopDeath.LocalDied("monster");
            var root = CustomerSync.MirrorRoot(monsterId);
            var kills = root != null ? root.GetComponentInChildren<MonsterKills>(true) : null;
            if (kills == null)
                yield break;
            CustomerSync.Freeze(monsterId, 15f);
            // The copy's cameras were switched off with its scripts; the kill shot needs them.
            var cams = root.GetComponentsInChildren<Unity.Cinemachine.CinemachineCamera>(true);
            foreach (var cam in cams)
                cam.enabled = true;
            var v = Traverse.Create(kills).Property<KillAnimationVariant>(variant).Value;
            try
            {
                kills.Play(v);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Monster: kill animation failed: {e.Message}");
            }
            // Just before spectating starts: put the kill set-up back.
            yield return new WaitForSecondsRealtime(3.9f);
            if (v != null)
                v.gameObject.SetActive(false);
            // The kill warped our copy to the game's fixed kill spot; let it follow the host's monster
            // again (a spectator camera standing there would otherwise be inside its body).
            CustomerSync.Freeze(monsterId, 0f);
            foreach (var cam in cams)
            {
                if (cam != null)
                    cam.enabled = false;
            }
        }

        public static void Clear()
        {
            remoteKills.Clear();
            outgoing.Clear();
            getOuts.Clear();
            handledKills.Clear();
            remoteTarget = null;
        }

        // Test (HMCOOP_HIDETEST=1, client): 30 s after the world loads, hide in the fridge nearest to
        // where the test monster appears, the way a click on it does.
        private static float hideStart = -1f;
        private static bool hideDone;

        public static void ClientHide()
        {
            if (hideDone || PlayerManager.Instance == null || Environment.GetEnvironmentVariable("HMCOOP_HIDETEST") != "1")
                return;
            if (hideStart < 0f)
                hideStart = Time.unscaledTime;
            if (Time.unscaledTime - hideStart < 30f)
                return;
            hideDone = true;
            var fridge = NearestFridge(new Vector3(1.22f, 0.09f, 3.27f));
            if (fridge == null)
            {
                Plugin.Log.LogWarning("Hide test: no fridge");
                return;
            }
            // Stand on the store side of the fridge (outside the store the night rule kills you).
            var spot = fridge.transform.position + fridge.transform.forward * 1f;
            foreach (var dir in new[] { fridge.transform.forward, -fridge.transform.forward, fridge.transform.right, -fridge.transform.right })
            {
                var candidate = fridge.transform.position + dir * 1f;
                if (MarketManager.IsInsideMarket(candidate))
                {
                    spot = candidate;
                    break;
                }
            }
            PlayerManager.Instance.Warp(spot);
            fridge.GetComponent<LookAtObject>().Interact();
            Plugin.Log.LogInfo($"Hide test: hiding in the fridge at {fridge.transform.position}, {Vector3.Distance(fridge.transform.position, new Vector3(1.22f, 0.09f, 3.27f)):F1} m from the monster spawn, standing inside the store={MarketManager.IsInsideMarket(spot)}, hidden={MonsterManager.Instance.IsHeroHidden}");
        }

        // Test (HMCOOP_BAIT=1, client): 36 s after the world loads, stand right next to the host's
        // monster (if one is mirrored), well inside its kill distance.
        private static float baitStart = -1f;
        private static bool baitDone;

        public static void ClientBait()
        {
            if (baitDone || PlayerManager.Instance == null || Environment.GetEnvironmentVariable("HMCOOP_BAIT") != "1")
                return;
            if (baitStart < 0f)
                baitStart = Time.unscaledTime;
            if (Time.unscaledTime - baitStart < 36f)
                return;
            foreach (var m in UnityEngine.Object.FindObjectsByType<MonsterKills>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var root = m.transform.root;
                if (!root.name.StartsWith("CoopCustomer_"))
                    continue;
                baitDone = true;
                var spot = root.position + root.forward * 1.5f;
                Plugin.Log.LogInfo($"Bait test: standing next to the monster at {root.position}");
                PlayerManager.Instance.Warp(spot);
                return;
            }
        }
    }
}
