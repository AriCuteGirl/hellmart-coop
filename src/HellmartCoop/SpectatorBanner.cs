using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HellmartCoop
{
    // "SPECTATING / until morning" across the top of the screen while a dead player watches.
    // Its own overlay canvas (the game's HUD is switched off in the watch-camera state), in the font the
    // game's own UI uses.
    public static class SpectatorBanner
    {
        private static GameObject root;

        public static void Show(bool show)
        {
            if (show && root == null)
                Build();
            if (root != null)
                root.SetActive(show);
        }

        private static void Build()
        {
            root = new GameObject("CoopSpectatorBanner");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var font = GameFont();
            AddText("Title", "SPECTATING", font, 64f, new Color(0.85f, 0.1f, 0.1f), -60f);
            AddText("Subtitle", "until morning", font, 32f, new Color(0.9f, 0.9f, 0.9f), -125f);
        }

        private static void AddText(string name, string text, TMP_FontAsset font, float size, Color color, float y)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(root.transform, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(1200f, 90f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (font != null)
                tmp.font = font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.raycastTarget = false;
            // Readable over a bright scene.
            tmp.outlineWidth = 0.2f;
            tmp.outlineColor = new Color32(0, 0, 0, 220);
        }

        // "ROOM K7Q2" in the top-left corner while hosting a relay room (null hides it).
        private static GameObject roomRoot;
        private static TextMeshProUGUI roomText;

        public static void SetRoom(string code)
        {
            if (code == null)
            {
                if (roomRoot != null)
                    roomRoot.SetActive(false);
                return;
            }
            if (roomRoot == null)
            {
                roomRoot = new GameObject("CoopRoomLabel");
                Object.DontDestroyOnLoad(roomRoot);
                var canvas = roomRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 400;
                var scaler = roomRoot.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                var go = new GameObject("Text", typeof(RectTransform));
                go.transform.SetParent(roomRoot.transform, false);
                var rect = (RectTransform)go.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(28f, -22f);
                rect.sizeDelta = new Vector2(600f, 60f);
                roomText = go.AddComponent<TextMeshProUGUI>();
                var font = GameFont();
                if (font != null)
                    roomText.font = font;
                roomText.fontSize = 30f;
                roomText.color = new Color(1f, 1f, 1f, 0.85f);
                roomText.outlineWidth = 0.2f;
                roomText.outlineColor = new Color32(0, 0, 0, 200);
                roomText.raycastTarget = false;
            }
            roomText.text = $"ROOM {code}";
            if (!roomRoot.activeSelf)
                roomRoot.SetActive(true);
        }

        // The game's subtitle font (the serif used for the player's lines); otherwise the most used font
        // among its UI texts, not counting TMP's built-in fallback.
        internal static TMP_FontAsset GameFont()
        {
            var subtitles = MonologueUI.Instance != null ? MonologueUI.Instance.GetComponentInChildren<TMP_Text>(true) : null;
            if (subtitles != null && subtitles.font != null)
            {
                Plugin.Log.LogInfo($"Spectator banner font: {subtitles.font.name} (subtitles)");
                return subtitles.font;
            }
            var counts = new System.Collections.Generic.Dictionary<TMP_FontAsset, int>();
            foreach (var t in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (t.font == null || t.font.name.StartsWith("LiberationSans"))
                    continue;
                counts.TryGetValue(t.font, out int n);
                counts[t.font] = n + 1;
            }
            TMP_FontAsset best = null;
            int bestCount = 0;
            foreach (var pair in counts)
            {
                if (pair.Value > bestCount)
                {
                    best = pair.Key;
                    bestCount = pair.Value;
                }
            }
            Plugin.Log.LogInfo($"Spectator banner font: {(best != null ? best.name : "default")} ({counts.Count} game fonts)");
            return best;
        }
    }
}
