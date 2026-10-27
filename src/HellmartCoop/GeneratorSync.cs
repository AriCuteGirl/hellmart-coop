using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Host-authoritative generator. The host's generator burns fuel and gets broken (monsters,
    // events, customers) as usual and broadcasts its state and fuel 4x a second.
    // Client: no local fuel use, no local breaking or state changes; it follows the host. Its own
    // refuelling (Fill) and its repair/restart mini-game success are sent to the host.
    public static class GeneratorSync
    {
        public const byte Channel = 10;
        public const byte ActionChannel = 11;
        private const float Interval = 0.25f;
        private const byte ActFill = 0, ActQteSuccess = 1;

        private static readonly MethodInfo ChangeState = AccessTools.Method(typeof(Generator), "ChangeState");
        private static readonly MethodInfo ChangePetrol = AccessTools.Method(typeof(Generator), "ChangePetrol");
        private static readonly MethodInfo HandleQteSuccess = AccessTools.Method(typeof(Generator), "HandleQTESuccess");

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
            var g = Generator.Instance;
            if (!net.IsHost || g == null || Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + Interval;
            net.BroadcastWorld(Channel, w =>
            {
                w.Write((byte)g.CurrentState);
                w.Write(g.Petrol);
            });
        }

        private static void Apply(BinaryReader r)
        {
            var state = (Generator.GeneratorState)r.ReadByte();
            float petrol = r.ReadSingle();
            var g = Generator.Instance;
            if (g == null)
                return;
            applying = true;
            try
            {
                if (Mathf.Abs(g.Petrol - petrol) > 0.001f)
                    ChangePetrol.Invoke(g, new object[] { petrol - g.Petrol });
                if (g.CurrentState != state)
                {
                    Plugin.Log.LogInfo($"Generator: {g.CurrentState} -> {state} (host)");
                    ChangeState.Invoke(g, new object[] { state });
                }
            }
            finally
            {
                applying = false;
            }
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            byte act = r.ReadByte();
            var g = Generator.Instance;
            if (g == null)
                return;
            if (act == ActFill)
            {
                float amount = r.ReadSingle();
                g.Fill(amount);
                Plugin.Log.LogInfo($"Generator: client {clientId} refuelled {amount:F1}, now {g.Petrol:F1}");
            }
            else if (act == ActQteSuccess)
            {
                var before = g.CurrentState;
                HandleQteSuccess.Invoke(g, null);
                Plugin.Log.LogInfo($"Generator: client {clientId} fixed it, {before} -> {g.CurrentState}");
            }
        }

        private static void Send(byte act, System.Action<BinaryWriter> body = null)
        {
            var net = Plugin.Instance.Net;
            if (net == null || net.IsHost)
                return;
            net.SendAction(ActionChannel, w =>
            {
                w.Write(act);
                body?.Invoke(w);
            });
        }

        // Client: fuel use and the state machine are the host's.
        [HarmonyPatch(typeof(Generator), "Update")]
        private static class NoLocalUpdate
        {
            private static bool Prefix() => !IsClient;
        }

        [HarmonyPatch(typeof(Generator), "ChangeState")]
        private static class NoLocalStateChange
        {
            private static bool Prefix() => !IsClient || applying;
        }

        [HarmonyPatch(typeof(Generator), nameof(Generator.Fill))]
        private static class ClientRefuels
        {
            private static void Postfix(float amount)
            {
                if (IsClient && !applying && amount > 0f)
                    Send(ActFill, w => w.Write(amount));
            }
        }

        [HarmonyPatch(typeof(Generator), "HandleQTESuccess")]
        private static class ClientFixes
        {
            private static void Prefix()
            {
                if (IsClient && !applying)
                    Send(ActQteSuccess);
            }
        }

        // Test (HMCOOP_GENTEST=host on the host: break it 25 s in; HMCOOP_GENTEST=client on the client:
        // 40 s in, "win" the repair mini-game and refuel 10).
        private static float testStart = -1f;
        private static int testStep;

        public static void Test(bool isHost)
        {
            string mode = System.Environment.GetEnvironmentVariable("HMCOOP_GENTEST");
            var g = Generator.Instance;
            if (g == null || testStep > 0 || mode != (isHost ? "host" : "client"))
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            float t = Time.unscaledTime - testStart;
            if (isHost && t >= 25f)
            {
                g.BreakGenerator();
                Plugin.Log.LogInfo($"Generator test: host broke it, state {g.CurrentState}, petrol {g.Petrol:F1}");
                testStep++;
            }
            else if (!isHost && t >= 40f)
            {
                Plugin.Log.LogInfo($"Generator test: client fixing, sees {g.CurrentState}, petrol {g.Petrol:F1}");
                HandleQteSuccess.Invoke(g, null);
                g.Fill(10f);
                testStep++;
            }
        }
    }
}
