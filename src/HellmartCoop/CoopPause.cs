using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // In co-op the pause menu (and tutorial pop-ups) only pause *you*: the menu opens and your own
    // controls lock, but time keeps running and the world (clock, snow, customers, monsters, doors,
    // generator) carries on, like any online game. Single player is unchanged.
    //
    // The game pauses in two ways, and both are undone here while co-op is active:
    //  - Time.timeScale = 0 → set back to 1.
    //  - PauseManager.IsPaused, read all over the code. Player-side readers (movement, look, interact,
    //    inventory, dialogue clicks) keep seeing it, so your controls stay locked. World-side readers
    //    below see "not paused" for the duration of their call.
    //  - PauseObject freezes NavMesh agents, animators and rigidbodies on pause → only on the local player.
    // The death screen (PauseState.Dead) keeps the game's own behaviour.
    public static class CoopPause
    {
        private static readonly MethodInfo SetIsPaused = AccessTools.PropertySetter(typeof(PauseManager), nameof(PauseManager.IsPaused));

        public static bool Active => Plugin.Instance != null && Plugin.Instance.Net != null;

        private static bool MenuPauseOnly => Active && PauseManager.IsPaused && PauseManager.State != PauseState.Dead;

        // Test (HMCOOP_PAUSETEST=1): opens the pause menu 33 s after the world loads, closes it at 43 s.
        private static float testStart = -1f;
        private static int testStep;

        public static void TestUpdate()
        {
            if (System.Environment.GetEnvironmentVariable("HMCOOP_PAUSETEST") != "1" || PauseManager.Instance == null || DayNightManager.Instance == null)
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            float t = Time.unscaledTime - testStart;
            if (testStep == 0 && t >= 33f)
            {
                PauseManager.Instance.InteractEsc();
                Plugin.Log.LogInfo($"Pause test: menu opened, paused={PauseManager.IsPaused}, timeScale={Time.timeScale}");
                testStep++;
            }
            else if (testStep == 1 && t >= 43f)
            {
                PauseManager.Instance.InteractEsc();
                Plugin.Log.LogInfo($"Pause test: menu closed, paused={PauseManager.IsPaused}, timeScale={Time.timeScale}");
                testStep++;
            }
        }

        [HarmonyPatch(typeof(PauseManager), "SetState")]
        private static class KeepTimeRunning
        {
            private static void Postfix()
            {
                if (MenuPauseOnly)
                    Time.timeScale = 1f;
            }
        }

        // World-side code: hide the pause while it runs.
        [HarmonyPatch]
        private static class WorldIgnoresPause
        {
            private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(TimeUtility), nameof(TimeUtility.GetDeltaTime));
                yield return AccessTools.Method(typeof(TimeUtility), nameof(TimeUtility.GetGameDeltaTime));
                yield return AccessTools.PropertyGetter(typeof(DayNightManager), nameof(DayNightManager.IsTimePaused));
                yield return AccessTools.Method(typeof(Monster), "Update");
                yield return AccessTools.Method(typeof(DoorCore), "Update");
                yield return AccessTools.Method(typeof(DoorShake), "LateUpdate");
                yield return AccessTools.Method(typeof(DoorInvasion), "Update");
                yield return AccessTools.Method(typeof(Generator), "Update");
            }

            private static void Prefix(out bool __state)
            {
                __state = MenuPauseOnly;
                if (__state)
                    SetIsPaused.Invoke(null, new object[] { false });
            }

            private static void Finalizer(bool __state)
            {
                if (__state)
                    SetIsPaused.Invoke(null, new object[] { true });
            }
        }

        [HarmonyPatch(typeof(PauseObject), "Pause")]
        private static class OnlyPauseLocalPlayer
        {
            private static bool Prefix(PauseObject __instance)
            {
                if (!MenuPauseOnly)
                    return true;
                return __instance.GetComponentInParent<PlayerManager>() != null;
            }
        }
    }
}
