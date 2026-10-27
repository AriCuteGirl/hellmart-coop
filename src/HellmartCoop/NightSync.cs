using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // The night's "directors" run on the host only: door invasions, anomaly monsters, fake customers
    // turning into monsters. Their results reach clients through the mirrors (CustomerSync shows the
    // host's monsters) and the other sync channels.
    public static class NightSync
    {
        [HarmonyPatch]
        private static class HostOnlyDirectors
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(InvasionGenerator), "OnActivate");
                yield return AccessTools.Method(typeof(AnomalyGenerator), "OnActivate");
                yield return AccessTools.Method(typeof(MarketManager), nameof(MarketManager.StartInvasion));
                yield return AccessTools.Method(typeof(MonsterManager), nameof(MonsterManager.SpawnLikeAFake));
                yield return AccessTools.Method(typeof(MonsterManager), nameof(MonsterManager.CreateUndergroundMonster));
                yield return AccessTools.Method(typeof(DoorInvasion), nameof(DoorInvasion.SummonMonster));
            }

            private static bool Prefix(MethodBase __originalMethod)
            {
                if (!WorldClock.IsClient)
                    return true;
                Plugin.Log.LogInfo($"Night: skipped {__originalMethod.DeclaringType.Name}.{__originalMethod.Name} on the client (host runs the night)");
                return false;
            }
        }

        // Test (HMCOOP_MONSTERTEST=1, host): 25 s in, step inside the store (safe from the night rule);
        // 30 s in, spawn the fake-customer monster; 44 s, remove it; 46 s, switch to Morning (revives
        // anyone spectating).
        private static float testStart = -1f;
        private static int testStep;

        public static void HostTest()
        {
            var manager = MonsterManager.Instance;
            var day = DayNightManager.Instance;
            if (testStep >= 4 || manager == null || day == null || System.Environment.GetEnvironmentVariable("HMCOOP_MONSTERTEST") != "1")
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            float t = Time.unscaledTime - testStart;
            if (testStep == 0 && t >= 25f)
            {
                var spot = new Vector3(1.33f, 0.3f, -4.54f);
                PlayerManager.Instance.Warp(spot);
                Plugin.Log.LogInfo($"Monster test: host steps inside the store, inside={MarketManager.IsInsideMarket(spot)}");
                testStep++;
            }
            else if (testStep == 1 && t >= 30f)
            {
                Plugin.Log.LogInfo("Monster test: host spawns the fake-customer monster");
                manager.SpawnLikeAFake();
                testStep++;
            }
            else if (testStep == 2 && t >= 44f)
            {
                // Gone at once (EscapeMonster dives slowly and can still catch the host on the way).
                Plugin.Log.LogInfo($"Monster test: removing the monster (monster here: {manager.MonsterHere != null})");
                if (manager.MonsterHere != null)
                    Object.Destroy(manager.MonsterHere.gameObject);
                testStep++;
            }
            else if (testStep == 3 && t >= 46f)
            {
                Plugin.Log.LogInfo("Monster test: host -> Morning");
                AccessTools.Method(typeof(DayNightManager), "SwitchState").Invoke(day, new object[] { day.MorningState });
                testStep++;
            }
        }
    }
}
