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
            public uint LastSeq;
            public float LastHeard;
        }

        private static readonly Color[] Colors = { Color.red, Color.cyan, Color.yellow, Color.magenta };

        private readonly Dictionary<byte, Puppet> puppets = new Dictionary<byte, Puppet>();

        public int Count => puppets.Count;

        public void Apply(PlayerState state)
        {
            if (!state.InGame)
            {
                Remove(state.Id);
                return;
            }
            if (!puppets.TryGetValue(state.Id, out var p))
            {
                p = Create(state.Id);
                p.Root.transform.position = state.Position;
                puppets[state.Id] = p;
                Plugin.Log.LogInfo($"Puppet {state.Id} spawned at {state.Position}");
            }
            // Drop reordered packets.
            if (state.Seq <= p.LastSeq)
                return;
            p.LastSeq = state.Seq;
            p.TargetPosition = state.Position;
            p.TargetYaw = state.Yaw;
            p.TargetPitch = state.Pitch;
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
                p.Head.localRotation = Quaternion.Slerp(p.Head.localRotation, Quaternion.Euler(p.TargetPitch, 0f, 0f), t);
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
            Color color = Colors[id % Colors.Length];
            var root = new GameObject($"CoopPuppet_{id}");
            // The player's transform sits at the feet, so lift the capsule by half its height.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.6f, 0.9f, 0.6f);
            var head = new GameObject("Head").transform;
            head.SetParent(root.transform, false);
            head.localPosition = new Vector3(0f, 1.6f, 0f);
            var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.transform.SetParent(head, false);
            visor.transform.localPosition = new Vector3(0f, 0f, 0.3f);
            visor.transform.localScale = new Vector3(0.35f, 0.15f, 0.3f);
            foreach (var go in new[] { body, visor })
            {
                // Puppets must not block the local player or trigger anything.
                Object.Destroy(go.GetComponent<Collider>());
                var renderer = go.GetComponent<Renderer>();
                var material = LitMaterial();
                if (material != null)
                    renderer.sharedMaterial = material;
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", go == visor ? Color.black : color);
                block.SetColor("_Color", go == visor ? Color.black : color);
                renderer.SetPropertyBlock(block);
            }
            return new Puppet { Root = root, Head = head };
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
