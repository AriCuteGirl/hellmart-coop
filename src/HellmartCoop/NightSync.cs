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
            string mode = System.Environment.GetEnvironmentVariable("HMCOOP_MONSTERTEST");
            if (testStep >= 4 || manager == null || day == null || (mode != "1" && mode != "fridge"))
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
            else if (testStep == 1 && mode == "1" && t >= 30f)
            {
                Plugin.Log.LogInfo("Monster test: host spawns the fake-customer monster");
                manager.SpawnLikeAFake();
                testStep++;
            }
            else if (testStep == 1 && mode == "fridge" && t >= 36f)
            {
                // Right at the fridge a remote player hides in (after HMCOOP_HIDETEST on the client),
                // the way AnomalyGenerator spawns its monster.
                Fridge target = null;
                foreach (var pose in Plugin.Instance.Puppets.Poses())
                {
                    float best = float.MaxValue;
                    foreach (var f in Object.FindObjectsByType<Fridge>(FindObjectsSortMode.None))
                    {
                        float d = Vector3.Distance(f.transform.position, pose.position);
                        if (d < best)
                        {
                            best = d;
                            target = f;
                        }
                    }
                }
                if (target == null)
                {
                    Plugin.Log.LogWarning("Monster test: no remote player to spawn next to");
                    testStep = 4;
                    return;
                }
                var position = target.transform.position + Vector3.up * 0.1f;
                var monster = Object.Instantiate(manager.MonsterPrefab, position, Quaternion.identity).GetComponent<Monster>();
                monster.SetTargetPosition(position);
                monster.Activate();
                Plugin.Log.LogInfo($"Monster test: spawned a monster at the fridge {target.transform.position}");
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
