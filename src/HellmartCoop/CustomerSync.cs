using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace HellmartCoop
{
    // Host-authoritative customers and monsters (kind 0 / 1), both shown on clients as script-less
    // copies of the same prefab.
    //
    // Host: every customer made by CustomerManager.SpawnCustomer and every Monster that starts gets a
    // net id. Two channels:
    //  - Appearance (on spawn, then every 2 s): prefab name + one bit per transform in the customer's
    //    hierarchy saying whether it is active. The game picks looks by enabling child models
    //    (CustomerCustomerPool, CustomerLogic), so copying those bits copies the look without
    //    replaying its random rolls.
    //  - Motion (10 Hz): position, yaw, animator layer-0 state and its float/int/bool parameters.
    //
    // Client: the game's own customer sources don't run (spawner, day events, night generator, spawn
    // events). Each host customer becomes a copy of the same prefab with its scripts, NavMeshAgent and
    // triggers switched off: a puppet that only moves and animates. A copy the host stops mentioning
    // for 1.5 s is removed (despawned, or the host left).
    public static class CustomerSync
    {
        public const byte AppearanceChannel = 2;
        public const byte MotionChannel = 3;
        private const float MotionInterval = 0.1f;
        // Also carries the customer's cash appearing in their hand, so keep it short.
        private const float AppearanceInterval = 0.5f;
        private const float MirrorTimeout = 1.5f;
        private const int MaxPerMotionPacket = 8;

        // Host side.
        private const byte KindCustomer = 0, KindMonster = 1;
        private static readonly Dictionary<Component, ushort> ids = new Dictionary<Component, ushort>();
        private static readonly Dictionary<Component, string> prefabNames = new Dictionary<Component, string>();
        private static readonly Dictionary<Component, byte> kinds = new Dictionary<Component, byte>();
        private static ushort nextId = 1;
        private static float nextMotion;
        private static float nextAppearance;

        // Client side.
        private class Mirror
        {
            public GameObject Root;
            public Animator Animator;
            public bool[] ActiveMask;
            public Vector3 TargetPosition;
            public float TargetYaw;
            public float LastHeard;
            public int StateHash;
            // Kill cinematic running locally: ignore the host's motion until then.
            public float FrozenUntil;
        }

        // Host: the net id of a tracked customer or monster.
        public static bool TryGetId(Component c, out ushort id) => ids.TryGetValue(c, out id);

        // Client: the copy for a net id.
        public static GameObject MirrorRoot(ushort id) => mirrors.TryGetValue(id, out var m) ? m.Root : null;

        public static void Freeze(ushort id, float seconds)
        {
            if (mirrors.TryGetValue(id, out var m))
            {
                m.FrozenUntil = Time.unscaledTime + seconds;
                m.LastHeard = Time.unscaledTime + seconds; // don't time out meanwhile
            }
        }

        private static readonly Dictionary<ushort, Mirror> mirrors = new Dictionary<ushort, Mirror>();
        private static Dictionary<string, GameObject> prefabsByName;

        private static bool IsClient => WorldClock.IsClient;
        private static bool IsHost => Plugin.Instance.Net != null && Plugin.Instance.Net.IsHost;

        public static void Register(Net net)
        {
            net.OnWorld(AppearanceChannel, ApplyAppearance);
            net.OnWorld(MotionChannel, ApplyMotion);
        }

        // ---------------- host ----------------

        [HarmonyPatch(typeof(CustomerManager), nameof(CustomerManager.SpawnCustomer), typeof(GameObject), typeof(Transform))]
        private static class TrackSpawn
        {
            private static void Postfix(GameObject customerPrefab, Customer __result)
            {
                if (__result == null)
                    return;
                if (IsClient)
                {
                    // A source we don't block yet (screamer / unique event): stays local to this player.
                    Plugin.Log.LogWarning($"Customers: local-only spawn of '{customerPrefab.name}' on the client");
                    return;
                }
                ids[__result] = nextId++;
                prefabNames[__result] = customerPrefab.name;
                kinds[__result] = KindCustomer;
                nextAppearance = 0f; // send its look right away
            }
        }

        // Monsters come from door invasions, anomalies and fake customers, all via Instantiate; Start
        // catches every one.
        [HarmonyPatch(typeof(Monster), "Start")]
        private static class TrackMonster
        {
            private static void Postfix(Monster __instance)
            {
                if (!IsHost || ids.ContainsKey(__instance))
                    return;
                ids[__instance] = nextId++;
                prefabNames[__instance] = __instance.gameObject.name.Replace("(Clone)", "").Trim();
                kinds[__instance] = KindMonster;
                nextAppearance = 0f;
                Plugin.Log.LogInfo($"Monsters: '{prefabNames[__instance]}' appeared at {__instance.transform.position}");
            }
        }

        // Test (HMCOOP_CUSTOMERTEST=1, host): spawn a generic customer 35 s and 45 s after the world
        // loads (after HMCOOP_CLOCKTEST=shift has opened the shift), the way the dev cheat panel does.
        private static float testStart = -1f;
        private static int testSpawned;

        private static void RunTest()
        {
            if (System.Environment.GetEnvironmentVariable("HMCOOP_CUSTOMERTEST") != "1" || CustomerManager.Instance == null)
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            if (testSpawned >= 2 || Time.unscaledTime - testStart < 35f + 10f * testSpawned)
                return;
            testSpawned++;
            foreach (var spawner in Resources.FindObjectsOfTypeAll<CustomerSpawner>())
            {
                if (!spawner.gameObject.scene.IsValid())
                    continue;
                var prefab = Traverse.Create(spawner).Field<GameObject>("genericCustomerPrefab").Value;
                if (prefab == null)
                    continue;
                var c = CustomerManager.SpawnCustomer(prefab);
                Plugin.Log.LogInfo($"Customer test: spawned '{prefab.name}' at {c.transform.position}");
                return;
            }
            Plugin.Log.LogWarning("Customer test: no CustomerSpawner in the scene");
        }

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost)
                return;
            RunTest();
            // Drop destroyed customers (Despawn destroys the GameObject).
            List<Component> dead = null;
            foreach (var c in ids.Keys)
            {
                if (c == null)
                    (dead ?? (dead = new List<Component>())).Add(c);
            }
            if (dead != null)
            {
                foreach (var c in dead)
                {
                    ids.Remove(c);
                    prefabNames.Remove(c);
                    kinds.Remove(c);
                }
            }
            if (ids.Count == 0)
                return;

            if (Time.unscaledTime >= nextAppearance)
            {
                nextAppearance = Time.unscaledTime + AppearanceInterval;
                foreach (var pair in ids)
                    SendAppearance(net, pair.Key, pair.Value);
            }
            if (Time.unscaledTime >= nextMotion)
            {
                nextMotion = Time.unscaledTime + MotionInterval;
                var batch = new List<KeyValuePair<Component, ushort>>(ids);
                for (int start = 0; start < batch.Count; start += MaxPerMotionPacket)
                {
                    int end = Mathf.Min(start + MaxPerMotionPacket, batch.Count);
                    net.BroadcastWorld(MotionChannel, w =>
                    {
                        w.Write((byte)(end - start));
                        for (int i = start; i < end; i++)
                            WriteMotion(w, batch[i].Key, batch[i].Value);
                    });
                }
            }
        }

        // The customer's own hierarchy, without shop products it is carrying (those get parented to
        // it at runtime and would shift every index).
        private static List<Transform> BodyTransforms(GameObject root)
        {
            var result = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.GetComponentInParent<Product>(true) == null)
                    result.Add(t);
            }
            return result;
        }

        private static void SendAppearance(Net net, Component customer, ushort id)
        {
            var transforms = BodyTransforms(customer.gameObject);
            var bits = new byte[(transforms.Count + 7) / 8];
            for (int i = 0; i < transforms.Count; i++)
            {
                if (transforms[i].gameObject.activeSelf)
                    bits[i / 8] |= (byte)(1 << (i % 8));
            }
            net.BroadcastWorld(AppearanceChannel, w =>
            {
                w.Write(id);
                w.Write(kinds[customer]);
                w.Write(prefabNames[customer]);
                w.Write((ushort)transforms.Count);
                w.Write(bits);
            });
        }

        private static void WriteMotion(BinaryWriter w, Component customer, ushort id)
        {
            var t = customer.transform;
            w.Write(id);
            w.Write(t.position.x);
            w.Write(t.position.y);
            w.Write(t.position.z);
            w.Write(t.eulerAngles.y);
            var animator = ActiveAnimator(customer.gameObject);
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                w.Write(0);
                w.Write((byte)0);
                return;
            }
            w.Write(animator.GetCurrentAnimatorStateInfo(0).fullPathHash);
            var parameters = animator.parameters;
            byte count = 0;
            foreach (var p in parameters)
            {
                if (p.type != AnimatorControllerParameterType.Trigger)
                    count++;
            }
            w.Write(count);
            foreach (var p in parameters)
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        w.Write(animator.GetFloat(p.nameHash));
                        break;
                    case AnimatorControllerParameterType.Int:
                        w.Write((float)animator.GetInteger(p.nameHash));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        w.Write(animator.GetBool(p.nameHash) ? 1f : 0f);
                        break;
                }
            }
        }

        private static Animator ActiveAnimator(GameObject root)
        {
            foreach (var a in root.GetComponentsInChildren<Animator>(false))
            {
                if (a.isActiveAndEnabled)
                    return a;
            }
            return null;
        }

        // ---------------- client ----------------

        private static void ApplyAppearance(BinaryReader r)
        {
            ushort id = r.ReadUInt16();
            byte kind = r.ReadByte();
            string prefabName = r.ReadString();
            int count = r.ReadUInt16();
            byte[] bits = r.ReadBytes((count + 7) / 8);
            if (PlayerManager.Instance == null)
                return;

            if (!mirrors.TryGetValue(id, out var mirror) || mirror.Root == null)
            {
                var prefab = FindPrefab(kind, prefabName);
                if (prefab == null)
                {
                    Plugin.Log.LogWarning($"Customers: no prefab '{prefabName}' on this client");
                    return;
                }
                mirror = CreateMirror(prefab, id);
                mirrors[id] = mirror;
                Plugin.Log.LogInfo($"Customers: mirror {id} '{prefabName}' created");
            }
            mirror.LastHeard = Time.unscaledTime;

            var transforms = BodyTransforms(mirror.Root);
            if (transforms.Count != count)
            {
                Plugin.Log.LogWarning($"Customers: mirror {id} has {transforms.Count} transforms, host has {count}");
                return;
            }
            for (int i = 0; i < count; i++)
            {
                bool active = (bits[i / 8] & (1 << (i % 8))) != 0;
                if (transforms[i].gameObject.activeSelf != active)
                    transforms[i].gameObject.SetActive(active);
            }
            mirror.Animator = ActiveAnimator(mirror.Root);
            if (mirror.Animator != null)
                mirror.Animator.applyRootMotion = false;
        }

        private static void ApplyMotion(BinaryReader r)
        {
            int count = r.ReadByte();
            for (int n = 0; n < count; n++)
            {
                ushort id = r.ReadUInt16();
                var position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float yaw = r.ReadSingle();
                int stateHash = r.ReadInt32();
                int paramCount = r.ReadByte();
                var values = new float[paramCount];
                for (int i = 0; i < paramCount; i++)
                    values[i] = r.ReadSingle();

                // Motion before appearance: wait for the look.
                if (!mirrors.TryGetValue(id, out var mirror) || mirror.Root == null)
                    continue;
                if (Time.unscaledTime < mirror.FrozenUntil)
                    continue;
                if (mirror.LastHeard == 0f || (mirror.Root.transform.position - position).sqrMagnitude > 25f)
                    mirror.Root.transform.position = position;
                mirror.TargetPosition = position;
                mirror.TargetYaw = yaw;
                mirror.LastHeard = Time.unscaledTime;
                ApplyAnimator(mirror, stateHash, values);
            }
        }

        private static void ApplyAnimator(Mirror mirror, int stateHash, float[] values)
        {
            var animator = mirror.Animator;
            if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null)
                return;
            int i = 0;
            foreach (var p in animator.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Trigger)
                    continue;
                if (i >= values.Length)
                    break;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        animator.SetFloat(p.nameHash, values[i]);
                        break;
                    case AnimatorControllerParameterType.Int:
                        animator.SetInteger(p.nameHash, Mathf.RoundToInt(values[i]));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        animator.SetBool(p.nameHash, values[i] > 0.5f);
                        break;
                }
                i++;
            }
            // Parameters drive most transitions; snap when the state still differs (trigger-only moves).
            if (stateHash != 0 && stateHash != mirror.StateHash)
            {
                mirror.StateHash = stateHash;
                if (animator.GetCurrentAnimatorStateInfo(0).fullPathHash != stateHash && animator.HasState(0, stateHash))
                    animator.CrossFadeInFixedTime(stateHash, 0.15f, 0);
            }
        }

        public static void ClientUpdate()
        {
            if (mirrors.Count == 0)
                return;
            float t = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            List<ushort> stale = null;
            foreach (var pair in mirrors)
            {
                var m = pair.Value;
                if (m.Root == null || Time.unscaledTime - m.LastHeard > MirrorTimeout || PlayerManager.Instance == null)
                {
                    (stale ?? (stale = new List<ushort>())).Add(pair.Key);
                    continue;
                }
                if (Time.unscaledTime < m.FrozenUntil)
                    continue;
                var tr = m.Root.transform;
                tr.position = Vector3.Lerp(tr.position, m.TargetPosition, t);
                tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0f, m.TargetYaw, 0f), t);
            }
            if (stale == null)
                return;
            foreach (ushort id in stale)
            {
                if (mirrors[id].Root != null)
                    Object.Destroy(mirrors[id].Root);
                mirrors.Remove(id);
                Plugin.Log.LogInfo($"Customers: mirror {id} removed");
            }
        }

        public static int MirrorCount => mirrors.Count;

        // "id@position" for every customer this side knows, for the 5 s status line.
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var pair in ids)
            {
                if (pair.Key != null)
                    sb.Append($" {pair.Value}@{pair.Key.transform.position}");
            }
            foreach (var pair in mirrors)
            {
                if (pair.Value.Root != null)
                    sb.Append($" {pair.Key}@{pair.Value.Root.transform.position}");
            }
            return sb.ToString();
        }
        public static int HostCount => ids.Count;

        private static Mirror CreateMirror(GameObject prefab, ushort id)
        {
            // Instantiate under an inactive parent so no Awake/OnEnable of the game's scripts runs;
            // switch the behaviour off, then move it out into the world.
            var holder = new GameObject("CoopMirrorHolder");
            holder.SetActive(false);
            var go = Object.Instantiate(prefab, holder.transform);
            go.name = $"CoopCustomer_{id}_{prefab.name}";
            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
                behaviour.enabled = false;
            foreach (var agent in go.GetComponentsInChildren<NavMeshAgent>(true))
                agent.enabled = false;
            foreach (var collider in go.GetComponentsInChildren<Collider>(true))
            {
                // Triggers start interactions and checkout logic; solid colliders keep the body in the way.
                if (collider.isTrigger)
                    collider.enabled = false;
            }
            foreach (var body in go.GetComponentsInChildren<Rigidbody>(true))
                body.isKinematic = true;
            foreach (var audio in go.GetComponentsInChildren<AudioSource>(true))
                audio.enabled = false;
            // The cash the customer holds out stays clickable; CheckoutSync turns the click into a
            // host action.
            foreach (var cash in go.GetComponentsInChildren<CustomerCash>(true))
            {
                foreach (var behaviour in cash.GetComponentsInChildren<MonoBehaviour>(true))
                    behaviour.enabled = true;
                foreach (var collider in cash.GetComponentsInChildren<Collider>(true))
                    collider.enabled = true;
            }
            go.transform.SetParent(null, false);
            Object.Destroy(holder);
            return new Mirror { Root = go };
        }

        private static GameObject FindPrefab(byte kind, string name)
        {
            string key = $"{kind}:{name}";
            if (prefabsByName == null || !prefabsByName.ContainsKey(key))
            {
                // Prefab assets referenced by the loaded scenes (not scene instances).
                prefabsByName = new Dictionary<string, GameObject>();
                var roots = new List<GameObject>();
                foreach (var c in Resources.FindObjectsOfTypeAll<Customer>())
                    roots.Add(c.gameObject);
                foreach (var m in Resources.FindObjectsOfTypeAll<Monster>())
                    roots.Add(m.gameObject);
                foreach (var go in roots)
                {
                    byte k = go.GetComponent<Monster>() != null ? KindMonster : KindCustomer;
                    string id = $"{k}:{go.name}";
                    if (!go.scene.IsValid() && go.transform.parent == null && !prefabsByName.ContainsKey(id))
                        prefabsByName[id] = go;
                }
            }
            prefabsByName.TryGetValue(key, out var prefab);
            return prefab;
        }

        public static void Clear()
        {
            foreach (var m in mirrors.Values)
            {
                if (m.Root != null)
                    Object.Destroy(m.Root);
            }
            mirrors.Clear();
            ids.Clear();
            prefabNames.Clear();
            kinds.Clear();
        }

        // Client: the game's own customer sources stay off; customers come from the host.
        [HarmonyPatch]
        private static class BlockClientSources
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(CustomerSpawner), "OnActivate");
                yield return AccessTools.Method(typeof(NightCustomerGenerator), "OnActivate");
                yield return AccessTools.Method(typeof(SpawnCustomerEvent), "OnActivate");
                yield return AccessTools.Method(typeof(DayCustomerEvents), nameof(DayCustomerEvents.Spawn));
            }

            private static bool Prefix(System.Reflection.MethodBase __originalMethod)
            {
                if (!IsClient)
                    return true;
                Plugin.Log.LogInfo($"Customers: skipped {__originalMethod.DeclaringType.Name}.{__originalMethod.Name} on the client (host spawns)");
                return false;
            }
        }
    }
}
