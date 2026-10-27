using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace HellmartCoop
{
    // How other players see you: the capsule's color and one accessory, picked in the Co-op menu
    // (Look), saved in the config and sent in every player packet. Accessories are built from Unity
    // primitives on the puppet's head (the head turns with the player's look), all with the HDRP/Lit
    // material Puppets borrows.
    public static class PlayerLook
    {
        public static readonly (string name, Color color)[] Colors =
        {
            ("Red", new Color(0.85f, 0.12f, 0.12f)),
            ("Orange", new Color(0.95f, 0.5f, 0.1f)),
            ("Yellow", new Color(0.95f, 0.85f, 0.15f)),
            ("Green", new Color(0.2f, 0.7f, 0.25f)),
            ("Cyan", new Color(0.15f, 0.75f, 0.85f)),
            ("Blue", new Color(0.15f, 0.3f, 0.85f)),
            ("Purple", new Color(0.55f, 0.2f, 0.8f)),
            ("Pink", new Color(0.95f, 0.45f, 0.7f)),
            ("White", new Color(0.92f, 0.92f, 0.92f)),
            ("Black", new Color(0.12f, 0.12f, 0.12f)),
        };

        public static readonly string[] Accessories =
        {
            "None", "Glasses", "Sunglasses", "Cap", "Top hat", "Headphones", "Crown", "Party hat", "VR box",
        };

        private static ConfigEntry<int> colorEntry;
        private static ConfigEntry<int> accessoryEntry;

        public static void Bind(ConfigFile config)
        {
            colorEntry = config.Bind("Look", "Color", 0, "Your capsule color (index into the Look menu list).");
            accessoryEntry = config.Bind("Look", "Accessory", 1, "Your accessory (index into the Look menu list).");
        }

        public static byte ColorIndex => (byte)Mathf.Clamp(colorEntry?.Value ?? 0, 0, Colors.Length - 1);
        public static byte AccessoryIndex => (byte)Mathf.Clamp(accessoryEntry?.Value ?? 0, 0, Accessories.Length - 1);

        public static void NextColor() => colorEntry.Value = (ColorIndex + 1) % Colors.Length;
        public static void NextAccessory() => accessoryEntry.Value = (AccessoryIndex + 1) % Accessories.Length;

        public static string ColorName => Colors[ColorIndex].name;
        public static string AccessoryName => Accessories[AccessoryIndex];

        public static Color ColorOf(byte index) => Colors[Mathf.Clamp(index, 0, Colors.Length - 1)].color;

        // Builds the accessory under the head (head at eye height, +Z = where the player looks; the
        // capsule's radius there is 0.3). Returns the parts' root, or null for None.
        public static GameObject BuildAccessory(Transform head, byte index, Material material)
        {
            if (index == 0 || index >= Accessories.Length)
                return null;
            var root = new GameObject("Accessory");
            root.transform.SetParent(head, false);
            var black = new Color(0.05f, 0.05f, 0.05f);
            var gold = new Color(1f, 0.8f, 0.2f);
            switch (Accessories[index])
            {
                case "Glasses":
                    Lens(root, -0.11f, new Color(0.7f, 0.85f, 1f, 1f), 0.11f, material);
                    Lens(root, 0.11f, new Color(0.7f, 0.85f, 1f, 1f), 0.11f, material);
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0.02f, 0.31f), new Vector3(0.1f, 0.02f, 0.02f), black, material);
                    Part(root, PrimitiveType.Cube, new Vector3(-0.27f, 0.02f, 0.15f), new Vector3(0.02f, 0.02f, 0.3f), black, material);
                    Part(root, PrimitiveType.Cube, new Vector3(0.27f, 0.02f, 0.15f), new Vector3(0.02f, 0.02f, 0.3f), black, material);
                    break;
                case "Sunglasses":
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0f, 0.3f), new Vector3(0.5f, 0.12f, 0.04f), black, material);
                    Part(root, PrimitiveType.Cube, new Vector3(-0.27f, 0.02f, 0.15f), new Vector3(0.02f, 0.03f, 0.3f), black, material);
                    Part(root, PrimitiveType.Cube, new Vector3(0.27f, 0.02f, 0.15f), new Vector3(0.02f, 0.03f, 0.3f), black, material);
                    break;
                case "Cap":
                    var capColor = new Color(0.2f, 0.25f, 0.6f);
                    Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.11f, 0f), new Vector3(0.62f, 0.3f, 0.62f), capColor, material);
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0.1f, 0.36f), new Vector3(0.42f, 0.03f, 0.3f), capColor, material);
                    break;
                case "Top hat":
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.15f, 0f), new Vector3(0.75f, 0.015f, 0.75f), black, material);
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.35f, 0f), new Vector3(0.45f, 0.2f, 0.45f), black, material);
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.2f, 0f), new Vector3(0.46f, 0.03f, 0.46f), new Color(0.6f, 0.05f, 0.05f), material);
                    break;
                case "Headphones":
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0.22f, 0f), new Vector3(0.62f, 0.04f, 0.08f), black, material);
                    Part(root, PrimitiveType.Cube, new Vector3(-0.28f, 0.15f, 0f), new Vector3(0.04f, 0.26f, 0.08f), black, material);
                    Part(root, PrimitiveType.Cube, new Vector3(0.28f, 0.15f, 0f), new Vector3(0.04f, 0.26f, 0.08f), black, material);
                    Part(root, PrimitiveType.Cylinder, new Vector3(-0.31f, 0f, 0f), new Vector3(0.18f, 0.04f, 0.18f), new Color(0.9f, 0.1f, 0.3f), material, Quaternion.Euler(0f, 0f, 90f));
                    Part(root, PrimitiveType.Cylinder, new Vector3(0.31f, 0f, 0f), new Vector3(0.18f, 0.04f, 0.18f), new Color(0.9f, 0.1f, 0.3f), material, Quaternion.Euler(0f, 0f, 90f));
                    break;
                case "Crown":
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.17f, 0f), new Vector3(0.48f, 0.05f, 0.48f), gold, material);
                    for (int i = 0; i < 6; i++)
                    {
                        float a = i * Mathf.PI * 2f / 6f;
                        var spike = Part(root, PrimitiveType.Cube, new Vector3(Mathf.Sin(a) * 0.21f, 0.27f, Mathf.Cos(a) * 0.21f), new Vector3(0.07f, 0.14f, 0.07f), gold, material);
                        spike.transform.localRotation = Quaternion.Euler(0f, a * Mathf.Rad2Deg + 45f, 0f);
                    }
                    Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.18f, 0.24f), new Vector3(0.07f, 0.07f, 0.07f), new Color(0.9f, 0.05f, 0.1f), material);
                    break;
                case "Party hat":
                    // A cone from stacked cylinders, striped.
                    for (int i = 0; i < 6; i++)
                    {
                        float r = 0.36f * (1f - i / 6f);
                        var stripe = i % 2 == 0 ? new Color(0.95f, 0.3f, 0.6f) : new Color(0.3f, 0.8f, 0.95f);
                        Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.17f + i * 0.06f, 0f), new Vector3(r, 0.03f, r), stripe, material);
                    }
                    Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.55f, 0f), new Vector3(0.08f, 0.08f, 0.08f), new Color(1f, 0.9f, 0.2f), material);
                    break;
                case "VR box":
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0f, 0.3f), new Vector3(0.35f, 0.15f, 0.3f), black, material);
                    break;
            }
            return root;
        }

        private static void Lens(GameObject root, float x, Color tint, float size, Material material)
        {
            // A ring of frame with a pale lens.
            Part(root, PrimitiveType.Cylinder, new Vector3(x, 0.02f, 0.31f), new Vector3(size + 0.03f, 0.01f, size + 0.03f), new Color(0.05f, 0.05f, 0.05f), material, Quaternion.Euler(90f, 0f, 0f));
            Part(root, PrimitiveType.Cylinder, new Vector3(x, 0.02f, 0.32f), new Vector3(size, 0.01f, size), tint, material, Quaternion.Euler(90f, 0f, 0f));
        }

        private static GameObject Part(GameObject root, PrimitiveType type, Vector3 position, Vector3 scale, Color color, Material material, Quaternion? rotation = null)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            if (rotation.HasValue)
                go.transform.localRotation = rotation.Value;
            Paint(go.GetComponent<Renderer>(), color, material);
            return go;
        }

        // Emission strength (HDRP physical units) for puppet parts.
        private const float glow = 2.5f;

        // Cartoon eyes on the head (hidden under the VR box): white balls with dark pupils.
        public static GameObject BuildEyes(Transform head, Material material)
        {
            var root = new GameObject("Eyes");
            root.transform.SetParent(head, false);
            foreach (float x in new[] { -0.1f, 0.1f })
            {
                Part(root, PrimitiveType.Sphere, new Vector3(x, 0.03f, 0.25f), new Vector3(0.13f, 0.15f, 0.1f), new Color(0.95f, 0.95f, 0.95f), material);
                Part(root, PrimitiveType.Sphere, new Vector3(x, 0.02f, 0.3f), new Vector3(0.06f, 0.07f, 0.03f), new Color(0.03f, 0.03f, 0.03f), material);
            }
            return root;
        }

        public static void Paint(Renderer renderer, Color color, Material material)
        {
            if (material != null)
                renderer.sharedMaterial = material;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            // A soft glow in its own color: the store is dim and foggy, plain colors read as near black.
            block.SetColor("_EmissiveColor", color.linear * glow);
            renderer.SetPropertyBlock(block);
        }
    }
}
