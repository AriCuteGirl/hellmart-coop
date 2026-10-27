using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Night doors, host-authoritative: open/closed and angle, planks nailed on, chains, and the monster
    // hands that tear planks and chains off during an invasion.
    //
    // Host → client (world channel 17, 5 Hz): every door (scene-path id) with open, angle, plank count,
    //   chained; every monster hand with showing, animator layer-0 state and float/int/bool params.
    // Client: matches doors (Open/CloseDoor, SetTargetAngle, add planks / BreakPlank, chain
    //   Chained/Unchained); hands are visual only: shown/hidden and animated from the host. Their
    //   animation events (break a plank, unchain, burst in) don't run on the client; the host's
    //   snapshot carries the result.
    // Client → host (action channel 18): door opened/closed, plank nailed, chain on/off, hand hit.
    public static class DoorSync
    {
        public const byte Channel = 17;
        public const byte ActionChannel = 18;
        private const float Interval = 0.2f;
        private const float LocalGrace = 1.5f;
        private const byte ActDoor = 0, ActPlank = 1, ActChain = 2, ActHit = 3;

        private static readonly MethodInfo CreateBarricade = AccessTools.Method(typeof(DoorBarricadeManager), "CreateBarricade");
        private static float nextSend;
        private static bool applying;

        // CanBoardUp is the player's check (plank in hand, standing in front); sync only needs a slot.
        private static bool HasFreeSlot(DoorBarricadeManager bars)
        {
            var points = Traverse.Create(bars).Field<List<Transform>>("woodPoints").Value;
            return points != null && bars.BarricadeCount < points.Count;
        }
        private static readonly Dictionary<uint, float> lastLocal = new Dictionary<uint, float>();

        private static bool IsClient => WorldClock.IsClient;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        private static List<DoorCore> Doors() => MarketManager.Instance != null ? MarketManager.Instance.Doors : null;

        private static T FindById<T>(uint id) where T : Component
        {
            foreach (var c in Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c.gameObject.scene.IsValid() && ShelfSync.PathId(c.transform) == id)
                    return c;
            }
            return null;
        }

        // ---------------- host ----------------

        public static void HostUpdate(Net net)
        {
            var doors = Doors();
            if (!net.IsHost || doors == null || Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + Interval;
            var hands = Object.FindObjectsByType<MonsterHand>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            net.BroadcastWorld(Channel, w =>
            {
                w.Write((byte)doors.Count);
                foreach (var door in doors)
                {
                    var bars = door.GetComponent<DoorBarricadeManager>();
                    var chain = door.GetComponent<DoorChainHandler>();
                    w.Write(ShelfSync.PathId(door.transform));
                    w.Write(door.IsOpen);
                    w.Write(Traverse.Create(door).Field<Quaternion>("_targetLocalRot").Value.eulerAngles.y);
                    w.Write((byte)(bars != null ? bars.BarricadeCount : 0));
                    w.Write(chain != null && chain.IsChained);
                }
                w.Write((byte)hands.Length);
                foreach (var hand in hands)
                {
                    var center = Traverse.Create(hand).Field<GameObject>("handCenter").Value;
                    var animator = Traverse.Create(hand).Field<Animator>("animator").Value;
                    w.Write(ShelfSync.PathId(hand.transform));
                    w.Write(center != null && center.activeSelf);
                    bool animate = animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null;
                    w.Write(animate ? animator.GetCurrentAnimatorStateInfo(0).fullPathHash : 0);
                    w.Write(animate ? animator.GetCurrentAnimatorStateInfo(0).normalizedTime : 0f);
                    var ps = animate ? animator.parameters : new AnimatorControllerParameter[0];
                    var values = new List<float>();
                    foreach (var p in ps)
                    {
                        if (p.type == AnimatorControllerParameterType.Float) values.Add(animator.GetFloat(p.nameHash));
                        else if (p.type == AnimatorControllerParameterType.Int) values.Add(animator.GetInteger(p.nameHash));
                        else if (p.type == AnimatorControllerParameterType.Bool) values.Add(animator.GetBool(p.nameHash) ? 1f : 0f);
                    }
                    w.Write((byte)values.Count);
                    foreach (float v in values)
                        w.Write(v);
                }
            });
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            byte act = r.ReadByte();
            uint id = r.ReadUInt32();
            switch (act)
            {
                case ActDoor:
                {
                    bool open = r.ReadBoolean();
                    var src = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var door = FindById<DoorCore>(id);
                    if (door == null)
                        return;
                    if (open) door.OpenDoor(src); else door.CloseDoor();
                    Plugin.Log.LogInfo($"Doors: client {clientId} {(open ? "opened" : "closed")} {door.name}");
                    break;
                }
                case ActPlank:
                {
                    var door = FindById<DoorCore>(id);
                    var bars = door != null ? door.GetComponent<DoorBarricadeManager>() : null;
                    if (bars == null || !HasFreeSlot(bars))
                        return;
                    // BoardUp without the host's hand: the client used its own plank.
                    door.CloseDoor();
                    CreateBarricade.Invoke(bars, new object[] { Traverse.Create(bars).Field("defaultBarricade").GetValue() });
                    Traverse.Create(bars).Field<System.Action>("OnBarricade").Value?.Invoke();
                    Plugin.Log.LogInfo($"Doors: client {clientId} nailed a plank on {door.name}, now {bars.BarricadeCount}");
                    break;
                }
                case ActChain:
                {
                    bool chained = r.ReadBoolean();
                    var chain = FindById<DoorChain>(id);
                    if (chain == null)
                        return;
                    if (chained) chain.Chained(); else chain.Unchained();
                    Plugin.Log.LogInfo($"Doors: client {clientId} {(chained ? "chained" : "unchained")} {chain.name}");
                    break;
                }
                case ActHit:
                {
                    var hand = FindById<MonsterHand>(id);
                    if (hand == null || !hand.IsActive)
                        return;
                    hand.Damaged();
                    Plugin.Log.LogInfo($"Doors: client {clientId} hit the monster hand {hand.name}");
                    break;
                }
            }
        }

        // ---------------- client ----------------

        private static void Apply(BinaryReader r)
        {
            applying = true;
            try
            {
                int doorCount = r.ReadByte();
                for (int i = 0; i < doorCount; i++)
                {
                    uint id = r.ReadUInt32();
                    bool open = r.ReadBoolean();
                    float angle = r.ReadSingle();
                    int planks = r.ReadByte();
                    bool chained = r.ReadBoolean();
                    if (lastLocal.TryGetValue(id, out float when) && Time.unscaledTime - when < LocalGrace)
                        continue;
                    var door = FindById<DoorCore>(id);
                    if (door == null)
                        continue;
                    MatchDoor(door, open, angle, planks, chained);
                }
                int handCount = r.ReadByte();
                for (int i = 0; i < handCount; i++)
                {
                    uint id = r.ReadUInt32();
                    bool showing = r.ReadBoolean();
                    int stateHash = r.ReadInt32();
                    float normalized = r.ReadSingle();
                    int n = r.ReadByte();
                    var values = new float[n];
                    for (int k = 0; k < n; k++)
                        values[k] = r.ReadSingle();
                    var hand = FindById<MonsterHand>(id);
                    if (hand != null)
                        MatchHand(hand, showing, stateHash, normalized, values);
                }
            }
            finally
            {
                applying = false;
            }
        }

        private static void MatchDoor(DoorCore door, bool open, float angle, int planks, bool chained)
        {
            if (door.IsOpen != open)
            {
                if (open) door.OpenDoor(door.transform.position - door.transform.forward); else door.CloseDoor();
            }
            door.SetTargetAngle(angle);
            var bars = door.GetComponent<DoorBarricadeManager>();
            if (bars != null)
            {
                var prefab = Traverse.Create(bars).Field("defaultBarricade").GetValue();
                while (bars.BarricadeCount < planks && HasFreeSlot(bars))
                    CreateBarricade.Invoke(bars, new object[] { prefab });
                while (bars.BarricadeCount > planks)
                    bars.BreakPlank();
            }
            var handler = door.GetComponent<DoorChainHandler>();
            if (handler != null && handler.Chain != null && handler.IsChained != chained)
            {
                if (chained) handler.Chain.Chained(); else handler.Chain.Unchained();
            }
        }

        private static void MatchHand(MonsterHand hand, bool showing, int stateHash, float normalized, float[] values)
        {
            var t = Traverse.Create(hand);
            var center = t.Field<GameObject>("handCenter").Value;
            if (center != null && center.activeSelf != showing)
                center.SetActive(showing);
            var animator = t.Field<Animator>("animator").Value;
            if (!showing || animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null)
                return;
            int i = 0;
            foreach (var p in animator.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Trigger)
                    continue;
                if (i >= values.Length)
                    break;
                if (p.type == AnimatorControllerParameterType.Float) animator.SetFloat(p.nameHash, values[i]);
                else if (p.type == AnimatorControllerParameterType.Int) animator.SetInteger(p.nameHash, Mathf.RoundToInt(values[i]));
                else animator.SetBool(p.nameHash, values[i] > 0.5f);
                i++;
            }
            var current = animator.GetCurrentAnimatorStateInfo(0);
            if (stateHash != 0 && (current.fullPathHash != stateHash || Mathf.Abs(current.normalizedTime - normalized) > 0.5f) && animator.HasState(0, stateHash))
                animator.Play(stateHash, 0, normalized);
        }

        private static void Send(byte act, uint id, System.Action<BinaryWriter> body = null)
        {
            var net = Plugin.Instance.Net;
            if (applying || net == null || net.IsHost)
                return;
            lastLocal[id] = Time.unscaledTime;
            net.SendAction(ActionChannel, w =>
            {
                w.Write(act);
                w.Write(id);
                body?.Invoke(w);
            });
        }

        [HarmonyPatch(typeof(DoorCore), nameof(DoorCore.OpenDoor), typeof(Vector3))]
        private static class ClientOpensDoor
        {
            private static void Prefix(DoorCore __instance, out bool __state) => __state = __instance.IsOpen;
            private static void Postfix(DoorCore __instance, Vector3 sourcePosition, bool __state)
            {
                if (IsClient && !__state && __instance.IsOpen)
                    Send(ActDoor, ShelfSync.PathId(__instance.transform), w =>
                    {
                        w.Write(true);
                        w.Write(sourcePosition.x);
                        w.Write(sourcePosition.y);
                        w.Write(sourcePosition.z);
                    });
            }
        }

        [HarmonyPatch(typeof(DoorCore), nameof(DoorCore.CloseDoor))]
        private static class ClientClosesDoor
        {
            private static void Prefix(DoorCore __instance, out bool __state) => __state = __instance.IsOpen;
            private static void Postfix(DoorCore __instance, bool __state)
            {
                if (IsClient && __state && !__instance.IsOpen)
                    Send(ActDoor, ShelfSync.PathId(__instance.transform), w =>
                    {
                        w.Write(false);
                        w.Write(0f);
                        w.Write(0f);
                        w.Write(0f);
                    });
            }
        }

        [HarmonyPatch(typeof(DoorBarricadeManager), nameof(DoorBarricadeManager.BoardUp))]
        private static class ClientNailsPlank
        {
            private static void Postfix(DoorBarricadeManager __instance)
            {
                if (IsClient)
                    Send(ActPlank, ShelfSync.PathId(__instance.transform));
            }
        }

        [HarmonyPatch(typeof(DoorChain), nameof(DoorChain.Chained))]
        private static class ClientChains
        {
            private static void Postfix(DoorChain __instance)
            {
                if (IsClient)
                    Send(ActChain, ShelfSync.PathId(__instance.transform), w => w.Write(true));
            }
        }

        [HarmonyPatch(typeof(DoorChain), nameof(DoorChain.Unchained))]
        private static class ClientUnchains
        {
            private static void Postfix(DoorChain __instance)
            {
                if (IsClient)
                    Send(ActChain, ShelfSync.PathId(__instance.transform), w => w.Write(false));
            }
        }

        // A hit on a hand on the client goes to the host's hand (it pays and makes the hand retreat).
        [HarmonyPatch(typeof(MonsterHand), nameof(MonsterHand.Damaged))]
        private static class ClientHitsHand
        {
            private static bool Prefix(MonsterHand __instance)
            {
                if (!IsClient || applying)
                    return true;
                Send(ActHit, ShelfSync.PathId(__instance.transform));
                return false;
            }
        }

        // The hand's animation events change doors; on a client only the host's results count.
        [HarmonyPatch]
        private static class NoClientHandEvents
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(MonsterHand), nameof(MonsterHand.ThrowWood));
                yield return AccessTools.Method(typeof(MonsterHand), nameof(MonsterHand.Unchain));
                yield return AccessTools.Method(typeof(MonsterHand), nameof(MonsterHand.SuccessInvasion));
                yield return AccessTools.Method(typeof(MonsterHand), nameof(MonsterHand.StopInvasion));
            }

            private static bool Prefix() => !IsClient;
        }

        public static void Clear()
        {
            lastLocal.Clear();
        }

        // ---------------- tests ----------------

        // HMCOOP_DOORTEST=host: 25 s in, nail 2 planks on every door, switch to Night, start an invasion
        // (MarketManager.StartInvasion with InvasionEvents' hand data).
        // HMCOOP_DOORTEST=client: from 40 s in, hit the first monster hand that shows.
        // Both step inside the store at 24 s.
        private static float testStart = -1f;
        private static int testStep;
        private static bool warpedInside;
        private static bool clientPlankSent;

        public static void Test(bool isHost)
        {
            string mode = System.Environment.GetEnvironmentVariable("HMCOOP_DOORTEST");
            var doors = Doors();
            if (doors == null || testStep >= 2 || mode != (isHost ? "host" : "client"))
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            float t = Time.unscaledTime - testStart;
            // Night kills anyone outside the store: both step in first.
            if (!warpedInside && t >= 24f && PlayerManager.Instance != null)
            {
                warpedInside = true;
                PlayerManager.Instance.Warp(isHost ? new Vector3(1.33f, 0.3f, -4.54f) : new Vector3(-1.5f, 0.3f, -4.54f));
                Plugin.Log.LogInfo($"Door test: inside the store = {MarketManager.IsPlayerInsideMarket()}");
            }
            if (isHost && testStep == 0 && t >= 25f)
            {
                foreach (var door in doors)
                {
                    var bars = door.GetComponent<DoorBarricadeManager>();
                    if (bars == null)
                        continue;
                    door.CloseDoor();
                    var prefab = Traverse.Create(bars).Field("defaultBarricade").GetValue();
                    for (int i = 0; i < 2 && HasFreeSlot(bars); i++)
                        CreateBarricade.Invoke(bars, new object[] { prefab });
                }
                var day = DayNightManager.Instance;
                AccessTools.Method(typeof(DayNightManager), "SwitchState").Invoke(day, new object[] { day.NightState });
                var events = Object.FindObjectsByType<InvasionEvents>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var e in events)
                {
                    // Not every InvasionEvents in the scene has hand data set.
                    var data = Traverse.Create(e).Field<ScriptableObjects.MonsterHandData>("handData").Value;
                    if (data != null)
                    {
                        MarketManager.StartInvasion(data);
                        break;
                    }
                }
                Plugin.Log.LogInfo($"Door test: host barricaded {doors.Count} doors, Night, invasion started ({events.Length} invasion event sources)");
                testStep = 2;
            }
            else if (!isHost && !clientPlankSent && t >= 30f)
            {
                // What ClientNailsPlank sends after a real BoardUp (the bot has no plank in hand).
                clientPlankSent = true;
                var door = doors[doors.Count - 1];
                Plugin.Log.LogInfo($"Door test: client nails a plank on {door.name} (has {door.GetComponent<DoorBarricadeManager>()?.BarricadeCount})");
                Send(ActPlank, ShelfSync.PathId(door.transform));
            }
            else if (!isHost && testStep == 0 && t >= 40f)
            {
                foreach (var hand in Object.FindObjectsByType<MonsterHand>(FindObjectsSortMode.None))
                {
                    var center = Traverse.Create(hand).Field<GameObject>("handCenter").Value;
                    if (center != null && center.activeSelf)
                    {
                        Plugin.Log.LogInfo($"Door test: client hits the hand {hand.name}");
                        hand.Damaged();
                        testStep = 2;
                        return;
                    }
                }
                if (t > 70f)
                {
                    Plugin.Log.LogWarning("Door test: no monster hand showing on the client");
                    testStep = 2;
                }
            }
        }

        public static string Describe()
        {
            var doors = Doors();
            if (doors == null)
                return "";
            int planks = 0, chained = 0, open = 0;
            foreach (var d in doors)
            {
                var b = d.GetComponent<DoorBarricadeManager>();
                var c = d.GetComponent<DoorChainHandler>();
                planks += b != null ? b.BarricadeCount : 0;
                chained += c != null && c.IsChained ? 1 : 0;
                open += d.IsOpen ? 1 : 0;
            }
            int hands = 0;
            foreach (var h in Object.FindObjectsByType<MonsterHand>(FindObjectsSortMode.None))
            {
                var center = Traverse.Create(h).Field<GameObject>("handCenter").Value;
                hands += center != null && center.activeSelf ? 1 : 0;
            }
            return $", doors open {open}/{doors.Count} planks {planks} chained {chained} hands {hands}";
        }
    }
}
