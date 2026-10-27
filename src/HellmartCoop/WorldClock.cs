using DayNight;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Host-authoritative day/night clock. The host's DayNightManager runs as usual and its clock is
    // broadcast 4x a second. On a client the state machine never ticks and may not change phase on
    // its own; it only follows the host (day, phase, evening / to-night flags, GameTime, DayTime).
    // State, not events: a lost packet is fixed by the next one.
    public static class WorldClock
    {
        public const byte Channel = 1;
        private const float SendInterval = 0.25f;

        private static float nextSend;
        // True while we drive the client's state machine ourselves (lets our own calls through).
        private static bool applying;

        public static bool IsClient => Plugin.Instance.Net != null && !Plugin.Instance.Net.IsHost;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
        }

        // Test script (HMCOOP_CLOCKTEST=1, host only): walks the host's clock through the phases with
        // the game's own calls, so a client can be checked without anyone playing.
        private static float testStart = -1f;
        private static int testStep;

        private static void RunClockTest(DayNightManager manager)
        {
            // "1": full walk through the phases; "shift": stop once the shift has started.
            string mode = System.Environment.GetEnvironmentVariable("HMCOOP_CLOCKTEST");
            if (mode != "1" && mode != "shift")
                return;
            if (mode == "shift" && testStep >= 2)
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            float t = Time.unscaledTime - testStart;
            float[] at = { 20f, 30f, 45f, 60f };
            if (testStep >= at.Length || t < at[testStep])
                return;
            switch (testStep)
            {
                case 0:
                    Plugin.Log.LogInfo("Clock test: host -> Noon");
                    SwitchState.Invoke(manager, new object[] { manager.NoonState });
                    break;
                case 1:
                    Plugin.Log.LogInfo("Clock test: host -> Shift");
                    SwitchState.Invoke(manager, new object[] { manager.shiftState });
                    break;
                case 2:
                    Plugin.Log.LogInfo("Clock test: host -> evening");
                    Traverse.Create(manager.shiftState).Method("StartEvening").GetValue();
                    break;
                case 3:
                    Plugin.Log.LogInfo("Clock test: host -> next day");
                    manager.NextDay();
                    break;
            }
            testStep++;
        }

        public static void HostUpdate(Net net)
        {
            var manager = DayNightManager.Instance;
            if (net.IsHost && manager != null && CurrentState(manager) != null)
                RunClockTest(manager);
            if (!net.IsHost || manager == null || CurrentState(manager) == null || Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + SendInterval;
            var shift = manager.shiftState;
            net.BroadcastWorld(Channel, w =>
            {
                w.Write(manager.CurrentDay);
                w.Write((byte)manager.CurrentPhase);
                w.Write(shift.IsEvening);
                w.Write(shift.ToNight);
                w.Write(manager.GameTime);
                w.Write(manager.DayTime);
            });
        }

        private static void Apply(System.IO.BinaryReader r)
        {
            int day = r.ReadInt32();
            var phase = (DayNightPhase)r.ReadByte();
            bool evening = r.ReadBoolean();
            bool toNight = r.ReadBoolean();
            float gameTime = r.ReadSingle();
            float dayTime = r.ReadSingle();

            var manager = DayNightManager.Instance;
            // Still loading: Init sets the first state after a short delay.
            if (manager == null || CurrentState(manager) == null)
                return;

            applying = true;
            try
            {
                while (manager.CurrentDay < day)
                {
                    Plugin.Log.LogInfo($"Clock: host is on day {day}, advancing from {manager.CurrentDay}");
                    manager.NextDay();
                }
                if (manager.CurrentPhase != phase)
                {
                    Plugin.Log.LogInfo($"Clock: phase {manager.CurrentPhase} -> {phase} (host)");
                    SwitchState.Invoke(manager, new object[] { manager.GetStateAssetForPhase(phase) });
                }
                if (phase == DayNightPhase.Shift)
                {
                    var shift = manager.shiftState;
                    if (evening && !shift.IsEvening)
                    {
                        Plugin.Log.LogInfo("Clock: evening (host)");
                        Traverse.Create(shift).Method("StartEvening").GetValue();
                    }
                    if (toNight && !shift.ToNight)
                    {
                        Plugin.Log.LogInfo("Clock: switching to night (host)");
                        shift.SwitchToNight();
                    }
                }
                manager.GameTime = gameTime;
                manager.DayTime = dayTime;
            }
            finally
            {
                applying = false;
            }
        }

        // internal in the game assembly.
        private static readonly System.Reflection.MethodInfo SwitchState =
            AccessTools.Method(typeof(DayNightManager), "SwitchState");

        private static StateAsset CurrentState(DayNightManager manager)
        {
            return Traverse.Create(manager).Field<StateAsset>("currentState").Value;
        }

        // Client: no local ticking. Tick advances time and runs transitions (open store → shift,
        // quota not met → die); all of that is the host's call.
        [HarmonyPatch(typeof(DayNightManager), "Update")]
        private static class SkipTickOnClient
        {
            private static bool Prefix() => !IsClient;
        }

        // Client: phase changes from local game code (quests, triggers) are dropped; the host's arrive
        // through Apply.
        [HarmonyPatch(typeof(DayNightManager), "SwitchState")]
        private static class BlockSwitchOnClient
        {
            private static bool Prefix(StateAsset next)
            {
                if (!IsClient || applying)
                    return true;
                Plugin.Log.LogInfo($"Clock: ignored local switch to {next?.GetPhase()} (host decides)");
                return false;
            }
        }

        [HarmonyPatch(typeof(DayNightManager), nameof(DayNightManager.NextDay))]
        private static class BlockNextDayOnClient
        {
            private static bool Prefix()
            {
                if (!IsClient || applying)
                    return true;
                Plugin.Log.LogInfo("Clock: ignored local NextDay (host decides)");
                return false;
            }
        }
    }
}
