using System.Collections;
using DayNight;
using HarmonyLib;
using Unity.Cinemachine;
using UnityEngine;

namespace HellmartCoop
{
    // Co-op death rule (Ari's choice): a player who dies spectates the others until morning, then
    // comes back next to them. The run is lost only when everyone is dead, or when the shift's quota
    // isn't met (a team loss); then everyone gets the game's own death screen.
    //
    // - Deaths from PlayerController.Die (staying outside at night, quota), from the host's own monster
    //   kill (Monster.HandleAttack) and from a kill sent by the host (MonsterHunt) all come here.
    // - While dead, the player's packets carry Dead=true: the others drop the puppet and the host's
    //   monsters stop seeing that player.
    // - Spectating uses the game's watch-camera player state with our own camera following a living
    //   player's eyes (controls, HUD and hands are off in that state).
    // - Morning (host clock) revives everyone dead.
    // - The host decides "run lost" and broadcasts it (world channel 15).
    public static class CoopDeath
    {
        public const byte Channel = 15;
        private const float DeathAnimation = 4f;
        private const float EyeHeight = 1.6f;

        public static bool LocalDead { get; private set; }
        private static bool spectating;
        private static bool runLost;
        private static CinemachineCamera spectatorCam;
        private static bool quotaCheck;
        private static DayNightPhase? lastPhase;
        private static float nextLostSend;

        private static bool Active => Plugin.Instance != null && Plugin.Instance.Net != null;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, r =>
            {
                if (!runLost)
                    ShowRunLost(r.ReadString());
            });
        }

        // ---------------- dying ----------------

        public static void LocalDied(string reason)
        {
            if (LocalDead || runLost)
                return;
            LocalDead = true;
            Plugin.Log.LogInfo($"Death: died ({reason}), spectating until morning");
            Plugin.Instance.StartCoroutine(StartSpectating());
        }

        private static IEnumerator StartSpectating()
        {
            yield return new WaitForSecondsRealtime(DeathAnimation);
            if (!LocalDead || runLost || PlayerController.Instance == null)
                yield break;
            if (spectatorCam == null)
            {
                var go = new GameObject("CoopSpectatorCam");
                Object.DontDestroyOnLoad(go);
                spectatorCam = go.AddComponent<CinemachineCamera>();
            }
            spectatorCam.Priority = 50;
            PlayerController.Instance.SwitchToWatchCamera(spectatorCam);
            spectating = true;
            SpectatorBanner.Show(true);
            Plugin.Instance.StartCoroutine(LogSpectatorView());
        }

        private static IEnumerator LogSpectatorView()
        {
            yield return new WaitForSecondsRealtime(1f);
            var main = Camera.main;
            var brain = main != null ? main.GetComponent<CinemachineBrain>() : null;
            Plugin.Log.LogInfo($"Death: spectator cam at {spectatorCam?.transform.position}, main camera at {main?.transform.position}, live {(brain != null ? brain.ActiveVirtualCamera?.Name : "no brain")}");
        }

        // PlayerController.Die: the night rule (outside the store), the quota at the end of the shift.
        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.Die))]
        private static class CoopDie
        {
            private static bool Prefix()
            {
                if (!Active || runLost)
                    return true;
                if (quotaCheck)
                {
                    // Quota not met: everyone loses.
                    HostRunLost("quota not met");
                    return true;
                }
                LocalDied("Die()");
                return false;
            }
        }

        [HarmonyPatch(typeof(ShiftStateAsset), "OnTransition")]
        private static class MarkQuotaCheck
        {
            private static void Prefix() => quotaCheck = true;
            private static void Finalizer() => quotaCheck = false;
        }

        // Monster attacks in co-op. A remote player's kill is MonsterHunt's; the host's own kill plays
        // its animation as usual, then the host spectates instead of getting the death screen and the
        // monster goes back to searching.
        [HarmonyPatch(typeof(Monster), "HandleAttack")]
        private static class MonsterAttack
        {
            private static bool Prefix(Monster __instance)
            {
                if (!Active || runLost)
                    return true;
                if (MonsterHunt.IsRemoteKill(__instance))
                {
                    MonsterHunt.TickRemoteKill(__instance);
                    return false;
                }
                // The original HandleAttack minus the death screen.
                var t = Traverse.Create(__instance);
                var agent = t.Field<UnityEngine.AI.NavMeshAgent>("agent").Value;
                if (agent != null)
                    agent.speed = 0f;
                float timer = t.Field<float>("attackTimer").Value + Time.deltaTime;
                t.Field("attackTimer").SetValue(timer);
                if (timer > 4f)
                {
                    LocalDied("monster");
                    MonsterHunt.BackToSearch(__instance);
                }
                return false;
            }
        }

        // ---------------- spectating ----------------

        public static void Update()
        {
            if (!Active)
                return;
            if (spectating && spectatorCam != null)
            {
                foreach (var pose in Plugin.Instance.Puppets.Poses())
                {
                    // Our camera sits inside that puppet's head: hide it.
                    Plugin.Instance.Puppets.HideOnly(pose.id);
                    var target = pose.position + Vector3.up * EyeHeight;
                    spectatorCam.transform.SetPositionAndRotation(
                        Vector3.Lerp(spectatorCam.transform.position, target, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime)),
                        Quaternion.Slerp(spectatorCam.transform.rotation, Quaternion.Euler(pose.pitch, pose.yaw, 0f), 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime)));
                    break;
                }
            }

            // The start of the next morning brings everyone back (not just "it is morning": a death in
            // the morning waits for the next one).
            var day = DayNightManager.Instance;
            if (day != null && Traverse.Create(day).Field("currentState").GetValue() != null)
            {
                var phase = day.CurrentPhase;
                if (LocalDead && !runLost && phase == DayNightPhase.Morning && lastPhase != DayNightPhase.Morning)
                    Revive();
                lastPhase = phase;
            }

            if (Plugin.Instance.Net.IsHost)
                HostCheckEveryoneDead();
        }

        private static void Revive()
        {
            LocalDead = false;
            spectating = false;
            SpectatorBanner.Show(false);
            Plugin.Instance?.Puppets.HideOnly(null);
            if (spectatorCam != null)
                spectatorCam.Priority = 0;
            if (PlayerController.Instance != null)
                PlayerController.Instance.SwitchState(PlayerController.Instance.stateMovement);
            // Come back next to a living player (inside the store).
            foreach (var pose in Plugin.Instance.Puppets.Poses())
            {
                PlayerManager.Instance?.Warp(pose.position + Vector3.right * 0.8f);
                break;
            }
            Plugin.Log.LogInfo("Death: morning, back in the game");
        }

        // ---------------- run lost ----------------

        private static void HostCheckEveryoneDead()
        {
            var puppets = Plugin.Instance.Puppets;
            if (LocalDead && puppets.Count == 0 && puppets.Dead.Count > 0)
                HostRunLost("everyone died");
            if (runLost && Time.unscaledTime >= nextLostSend)
            {
                // Repeat: world messages are unreliable.
                nextLostSend = Time.unscaledTime + 0.5f;
                Plugin.Instance.Net.BroadcastWorld(Channel, w => w.Write(lostReason));
            }
        }

        private static string lostReason = "";

        private static void HostRunLost(string reason)
        {
            if (runLost || !Plugin.Instance.Net.IsHost)
                return;
            lostReason = reason;
            ShowRunLost(reason);
        }

        private static void ShowRunLost(string reason)
        {
            runLost = true;
            spectating = false;
            SpectatorBanner.Show(false);
            Plugin.Instance?.Puppets.HideOnly(null);
            if (spectatorCam != null)
                spectatorCam.Priority = 0;
            Plugin.Log.LogInfo($"Death: run lost ({reason})");
            if (PlayerController.Instance != null && PlayerController.Instance.DeleteSaveOnDeath)
                StatsScreen.Instance.ShowScreen();
            else if (DeadScreen.Instance != null)
                DeadScreen.Instance.ShowScreen();
        }

        // Test (HMCOOP_DIETEST=1): die with the game's own Die() 36 s after the world loads.
        private static float dieTestStart = -1f;
        private static bool dieTestDone;

        public static void DieTest()
        {
            if (dieTestDone || PlayerController.Instance == null || System.Environment.GetEnvironmentVariable("HMCOOP_DIETEST") != "1")
                return;
            if (dieTestStart < 0f)
                dieTestStart = Time.unscaledTime;
            if (Time.unscaledTime - dieTestStart < 36f)
                return;
            dieTestDone = true;
            Plugin.Log.LogInfo("Die test: calling PlayerController.Die()");
            PlayerController.Instance.Die();
        }

        public static void Clear()
        {
            LocalDead = false;
            spectating = false;
            SpectatorBanner.Show(false);
            Plugin.Instance?.Puppets.HideOnly(null);
            runLost = false;
            quotaCheck = false;
            lastPhase = null;
        }
    }
}
