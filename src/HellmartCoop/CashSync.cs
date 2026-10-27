using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // One store wallet and one quota, both the host's.
    // Host → client (4 Hz): cash, quota goal, quota earned. The client shows them (cash through the
    // game's own OnCashReceived/OnCashSpent events, quota through OnChange, so the HUD updates).
    // Client → host: whatever the client pays for (PlayerController.Pay) comes out of the host's cash.
    // The client's own quota changes are dropped; the host's register does the quota bookkeeping.
    public static class CashSync
    {
        public const byte Channel = 12;
        public const byte ActionChannel = 13;
        private const float Interval = 0.25f;

        private static readonly MethodInfo SetCash = AccessTools.PropertySetter(typeof(PlayerController), nameof(PlayerController.Cash));
        private static readonly MethodInfo SetQuotaGoal = AccessTools.PropertySetter(typeof(QuotaManager), nameof(QuotaManager.QuotaGoal));
        private static readonly MethodInfo SetQuotaCurrent = AccessTools.PropertySetter(typeof(QuotaManager), nameof(QuotaManager.QuotaCurrent));

        private static float nextSend;
        private static bool applying;

        private static bool IsClient => WorldClock.IsClient;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        public static void HostUpdate(Net net)
        {
            var player = PlayerController.Instance;
            var quota = QuotaManager.Instance;
            if (!net.IsHost || player == null || quota == null || Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + Interval;
            net.BroadcastWorld(Channel, w =>
            {
                w.Write(player.Cash);
                w.Write(quota.QuotaGoal);
                w.Write(quota.QuotaCurrent);
            });
        }

        private static void Apply(BinaryReader r)
        {
            float cash = r.ReadSingle();
            float goal = r.ReadSingle();
            float current = r.ReadSingle();
            var player = PlayerController.Instance;
            if (player != null && Mathf.Abs(player.Cash - cash) > 0.001f)
            {
                float delta = cash - player.Cash;
                SetCash.Invoke(player, new object[] { cash });
                // The HUD listens to these events.
                string evt = delta > 0f ? "OnCashReceived" : "OnCashSpent";
                Traverse.Create(player).Field<Action<float>>(evt).Value?.Invoke(Mathf.Abs(delta));
            }
            var quota = QuotaManager.Instance;
            if (quota != null && (Mathf.Abs(quota.QuotaGoal - goal) > 0.001f || Mathf.Abs(quota.QuotaCurrent - current) > 0.001f))
            {
                SetQuotaGoal.Invoke(quota, new object[] { goal });
                SetQuotaCurrent.Invoke(quota, new object[] { current });
                Traverse.Create(quota).Field<Action>("OnChange").Value?.Invoke();
            }
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            float amount = r.ReadSingle();
            var player = PlayerController.Instance;
            if (player == null)
                return;
            player.Pay(amount);
            Plugin.Log.LogInfo($"Cash: client {clientId} spent {amount:F2}, store cash now {player.Cash:F2}");
        }

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.Pay))]
        private static class ClientPays
        {
            private static void Postfix(float amount)
            {
                var net = Plugin.Instance.Net;
                if (!IsClient || amount <= 0f || net == null)
                    return;
                net.SendAction(ActionChannel, w => w.Write(amount));
            }
        }

        [HarmonyPatch(typeof(QuotaManager), nameof(QuotaManager.EarnQuota))]
        private static class NoClientQuotaEarn
        {
            private static bool Prefix() => !IsClient;
        }

        [HarmonyPatch(typeof(QuotaManager), nameof(QuotaManager.QuotaUp))]
        private static class NoClientQuotaUp
        {
            private static bool Prefix() => !IsClient;
        }

        // Test (HMCOOP_CASHTEST=1, client): 30 s in, pay 1.00 for something.
        private static float testStart = -1f;
        private static bool testDone;

        public static void ClientTest()
        {
            var player = PlayerController.Instance;
            if (testDone || player == null || Environment.GetEnvironmentVariable("HMCOOP_CASHTEST") != "1")
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            if (Time.unscaledTime - testStart < 30f)
                return;
            testDone = true;
            Plugin.Log.LogInfo($"Cash test: client pays 1.00 (sees {player.Cash:F2})");
            player.Pay(1f);
        }
    }
}
