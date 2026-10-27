using System.Collections.Generic;
using UnityEngine;

namespace HellmartCoop
{
    // Stand-in bodies for remote players: a capsule with a box "head" that shows where they look.
    // Never driven by local input, only by received states.
    public class Puppets
    {
        private class Puppet
        {
            public GameObject Root;
            public Transform Head;
            public Vector3 TargetPosition;
            public float TargetYaw;
            public float TargetPitch;
            public bool InFridge;
            public Renderer Body;
            public GameObject Accessory;
            public GameObject Eyes;
            public int LookColor = -1;
            public int LookAccessory = -1;
            public uint LastSeq;
            public float LastHeard;
        }

        private readonly Dictionary<byte, Puppet> puppets = new Dictionary<byte, Puppet>();

        public int Count => puppets.Count;

        // Remote players who are spectating (dead until morning), by id.
        public readonly HashSet<byte> Dead = new HashSet<byte>();

        public void Apply(PlayerState state)
        {
            if (state.Dead)
                Dead.Add(state.Id);
            else
                Dead.Remove(state.Id);
            if (!state.InGame || state.Dead)
            {
                Remove(state.Id);
                return;
            }
            if (!puppets.TryGetValue(state.Id, out var p))
            {
                p = Create(state.Id);
                p.Root.transform.position = state.Position;
                puppets[state.Id] = p;
                if (hidden == state.Id)
                    SetVisible(p, false);
                Plugin.Log.LogInfo($"Puppet {state.Id} spawned at {state.Position}");
            }
            // Drop reordered packets.
            if (state.Seq <= p.LastSeq)
                return;
            p.LastSeq = state.Seq;
            p.TargetPosition = state.Position;
            p.TargetYaw = state.Yaw;
            p.TargetPitch = state.Pitch;
            if (p.LookColor != state.LookColor || p.LookAccessory != state.LookAccessory)
                ApplyLook(p, state.LookColor, state.LookAccessory, hidden != state.Id && !p.InFridge);
            if (p.InFridge != state.InFridge)
            {
                p.InFridge = state.InFridge;
                SetVisible(p, !p.InFridge && hidden != state.Id);
                Plugin.Log.LogInfo($"Player {state.Id} {(p.InFridge ? "hid in a fridge" : "left the fridge")}");
            }
            p.LastHeard = Time.unscaledTime;
        }

        public void Update()
        {
            float t = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime);
            var stale = new List<byte>();
            foreach (var pair in puppets)
            {
                var p = pair.Value;
                if (p.Root == null || Time.unscaledTime - p.LastHeard > 5f)
                {
                    stale.Add(pair.Key);
                    continue;
                }
                var tr = p.Root.transform;
                tr.position = Vector3.Lerp(tr.position, p.TargetPosition, t);
                tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0f, p.TargetYaw, 0f), t);
                // Nod only a little: a capsule head tipped far back looks broken.
                float pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, p.TargetPitch), -25f, 25f);
                p.Head.localRotation = Quaternion.Slerp(p.Head.localRotation, Quaternion.Euler(pitch, 0f, 0f), t);
            }
            foreach (byte id in stale)
                Remove(id);
        }

        public bool TryGetPosition(byte id, out Vector3 position)
        {
            position = default;
            if (!puppets.TryGetValue(id, out var p) || p.Root == null)
                return false;
            position = p.Root.transform.position;
            return true;
        }

        public IEnumerable<byte> Ids => puppets.Keys;

        // Hide one puppet (the player being spectated: the camera sits inside its head), or none.
        private byte? hidden;

        public void HideOnly(byte? id)
        {
            if (hidden == id)
                return;
            hidden = id;
            foreach (var pair in puppets)
                SetVisible(pair.Value, pair.Key != id && !pair.Value.InFridge);
        }

        private static void SetVisible(Puppet p, bool visible)
        {
            if (p.Root == null)
                return;
            foreach (var r in p.Root.GetComponentsInChildren<Renderer>(true))
                r.enabled = visible;
        }

        // Latest received pose of every remote player (not the smoothed one), for the host's monsters.
        public IEnumerable<(byte id, Vector3 position, float yaw, float pitch)> Poses()
        {
            foreach (var pair in puppets)
            {
                if (pair.Value.Root != null)
                    yield return (pair.Key, pair.Value.TargetPosition, pair.Value.TargetYaw, pair.Value.TargetPitch);
            }
        }

        public bool IsInFridge(byte id) => puppets.TryGetValue(id, out var p) && p.InFridge;

        public void Clear()
        {
            foreach (var p in puppets.Values)
            {
                if (p.Root != null)
                    Object.Destroy(p.Root);
            }
            puppets.Clear();
        }

        private void Remove(byte id)
        {
            if (!puppets.TryGetValue(id, out var p))
                return;
            if (p.Root != null)
                Object.Destroy(p.Root);
            puppets.Remove(id);
            Plugin.Log.LogInfo($"Puppet {id} removed");
        }

        private static Puppet Create(byte id)
        {
            var root = new GameObject($"CoopPuppet_{id}");
            // The player's transform sits at the feet, so lift the capsule by half its height.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.6f, 0.9f, 0.6f);
            // Puppets must not block the local player or trigger anything.
            Object.Destroy(body.GetComponent<Collider>());
            var head = new GameObject("Head").transform;
            head.SetParent(root.transform, false);
            head.localPosition = new Vector3(0f, 1.6f, 0f);
            return new Puppet { Root = root, Head = head, Body = body.GetComponent<Renderer>(), Eyes = PlayerLook.BuildEyes(head, LitMaterial()) };
        }

        // The player's color and accessory (PlayerLook), rebuilt when they change.
        private static void ApplyLook(Puppet p, byte color, byte accessory, bool visible)
        {
            var material = LitMaterial();
            PlayerLook.Paint(p.Body, PlayerLook.ColorOf(color), material);
            if (p.LookAccessory != accessory)
            {
                if (p.Accessory != null)
                    Object.Destroy(p.Accessory);
                p.Accessory = PlayerLook.BuildAccessory(p.Head, accessory, material);
                if (p.Eyes != null)
                    p.Eyes.SetActive(PlayerLook.Accessories[Mathf.Clamp(accessory, 0, PlayerLook.Accessories.Length - 1)] != "VR box");
            }
            p.LookColor = color;
            p.LookAccessory = accessory;
            SetVisible(p, visible);
        }

        private static Material litMaterial;

        // The primitives' default material uses a shader this HDRP build doesn't include (renders
        // magenta), so borrow a plain copy of a material the game already uses with HDRP/Lit.
        private static Material LitMaterial()
        {
            if (litMaterial != null)
                return litMaterial;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m.shader != null && m.shader.name == "HDRP/Lit")
                {
                    litMaterial = new Material(m.shader) { name = "CoopPuppet" };
                    Plugin.Log.LogInfo($"Puppet material: HDRP/Lit (from '{m.name}')");
                    return litMaterial;
                }
            }
            Plugin.Log.LogWarning("Puppet material: no HDRP/Lit material found");
            return null;
        }
    }
}
