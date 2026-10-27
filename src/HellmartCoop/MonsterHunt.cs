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
        }

        // ---------------- host ----------------

        private struct Saved
        {
            public bool Swapped;
            public Vector3 PlayerPosition;
            public Vector3 LookPosition;
            public Quaternion LookRotation;
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
            }

            private static void Finalizer(Saved __state)
            {
                remoteTarget = null;
                if (!__state.Swapped || PlayerManager.Instance == null)
                    return;
                PlayerManager.Instance.transform.position = __state.PlayerPosition;
                PlayerManager.LookController.transform.SetPositionAndRotation(__state.LookPosition, __state.LookRotation);
            }
        }

        // The fridge belongs to the host's player; a remote player is never "hidden" in it (yet).
        [HarmonyPatch(typeof(MonsterManager), nameof(MonsterManager.IsHeroHidden), MethodType.Getter)]
        private static class RemoteNotHidden
        {
            private static bool Prefix(ref bool __result)
            {
                if (remoteTarget == null)
                    return true;
                __result = false;
                return false;
            }
        }

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
            if (!net.IsHost || outgoing.Count == 0)
                return;
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
            handledKills.Clear();
            remoteTarget = null;
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
