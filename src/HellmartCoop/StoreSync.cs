using System.IO;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // The store's open sign and main door, host-authoritative but usable by everyone.
    // Client: opening/closing happens locally at once and is sent to the host as an action.
    // Host: applies it with the game's own Open/Close and broadcasts both states 4x a second; the
    // client follows those (ignoring them for a moment right after its own action, so the sign
    // doesn't flip back while the action is on its way).
    public static class StoreSync
    {
        public const byte Channel = 5;
        public const byte ActionChannel = 6;
        private const float Interval = 0.25f;
        private const float LocalGrace = 1.5f;

        private const byte Sign = 0;
        private const byte Door = 1;

        private static float nextSend;
        private static bool applying;
        private static readonly float[] lastLocalAction = { -10f, -10f };

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost || Signage.Instance == null || MainDoor.Instance == null || Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + Interval;
            net.BroadcastWorld(Channel, w =>
            {
                w.Write(Signage.IsOpen());
                w.Write(MainDoor.IsOpen());
            });
        }

        private static void Apply(BinaryReader r)
        {
            bool signOpen = r.ReadBoolean();
            bool doorOpen = r.ReadBoolean();
            if (Signage.Instance == null || MainDoor.Instance == null)
                return;
            applying = true;
            try
            {
                if (Time.unscaledTime - lastLocalAction[Sign] > LocalGrace && Signage.IsOpen() != signOpen)
                    Set(Sign, signOpen);
                if (Time.unscaledTime - lastLocalAction[Door] > LocalGrace && MainDoor.IsOpen() != doorOpen)
                    Set(Door, doorOpen);
            }
            finally
            {
                applying = false;
            }
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            byte what = r.ReadByte();
            bool open = r.ReadBoolean();
            if (Signage.Instance == null || MainDoor.Instance == null)
                return;
            Plugin.Log.LogInfo($"Store: client {clientId} {(open ? "opened" : "closed")} the {(what == Sign ? "sign" : "door")}");
            Set(what, open);
        }

        private static void Set(byte what, bool open)
        {
            if (what == Sign)
            {
                if (open)
                    Signage.Instance.Open();
                else
                    Signage.Instance.Close();
            }
            else
            {
                if (open)
                    MainDoor.Instance.Open();
                else
                    MainDoor.Instance.Close();
            }
        }

        private static void ClientChanged(byte what, bool open)
        {
            var net = Plugin.Instance.Net;
            if (applying || net == null || net.IsHost)
                return;
            lastLocalAction[what] = Time.unscaledTime;
            Plugin.Log.LogInfo($"Store: {(open ? "opened" : "closed")} the {(what == Sign ? "sign" : "door")}, telling the host");
            net.SendAction(ActionChannel, w =>
            {
                w.Write(what);
                w.Write(open);
            });
        }

        // Postfixes run only when the state really changed (Open/Close return early otherwise).
        [HarmonyPatch(typeof(Signage), nameof(Signage.Open))]
        private static class SignOpen
        {
            private static void Prefix(out bool __state) => __state = Signage.Instance != null && Signage.IsOpen();
            private static void Postfix(bool __state)
            {
                if (!__state && Signage.IsOpen())
                    ClientChanged(Sign, true);
            }
        }

        [HarmonyPatch(typeof(Signage), nameof(Signage.Close))]
        private static class SignClose
        {
            private static void Prefix(out bool __state) => __state = Signage.Instance != null && Signage.IsOpen();
            private static void Postfix(bool __state)
            {
                if (__state && !Signage.IsOpen())
                    ClientChanged(Sign, false);
            }
        }

        [HarmonyPatch(typeof(MainDoor), nameof(MainDoor.Open))]
        private static class DoorOpen
        {
            private static void Prefix(out bool __state) => __state = MainDoor.Instance != null && MainDoor.IsOpen();
            private static void Postfix(bool __state)
            {
                if (!__state && MainDoor.IsOpen())
                    ClientChanged(Door, true);
            }
        }

        [HarmonyPatch(typeof(MainDoor), nameof(MainDoor.Close))]
        private static class DoorClose
        {
            private static void Prefix(out bool __state) => __state = MainDoor.Instance != null && MainDoor.IsOpen();
            private static void Postfix(bool __state)
            {
                if (__state && !MainDoor.IsOpen())
                    ClientChanged(Door, false);
            }
        }

        // Test (HMCOOP_STORETEST=1, client): open the sign 20 s and the door 25 s after the world loads.
        private static float testStart = -1f;
        private static int testStep;

        public static void ClientTest()
        {
            if (testStep >= 2 || System.Environment.GetEnvironmentVariable("HMCOOP_STORETEST") != "1" || Signage.Instance == null || MainDoor.Instance == null)
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            float t = Time.unscaledTime - testStart;
            if (testStep == 0 && t >= 20f)
            {
                Plugin.Log.LogInfo($"Store test: opening the sign (was {Signage.IsOpen()})");
                Signage.Instance.Open();
                testStep++;
            }
            else if (testStep == 1 && t >= 25f)
            {
                Plugin.Log.LogInfo($"Store test: opening the door (was {MainDoor.IsOpen()})");
                MainDoor.Instance.Open();
                testStep++;
            }
        }
    }
}
