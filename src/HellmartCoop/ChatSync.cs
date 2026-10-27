using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace HellmartCoop
{
    // Text chat (asked for by Ari's friend). Enter or T opens a line at the bottom-left, Enter sends,
    // Esc cancels. While typing, the game's input actions are off (no walking or looking around) and Esc
    // doesn't open the pause menu. Lines show "Name: text" in the sender's color and fade after a while.
    // Client → host (action channel 32, reliable): name, color, text. The host gives every line a number
    // and sends it to everyone (world channel 33, repeated, deduplicated), its own lines too.
    public static class ChatSync
    {
        public const byte ActionChannel = 32;
        public const byte Channel = 33;
        private const int MaxLength = 120;
        private const int MaxLines = 8;
        private const float ShowSeconds = 12f;

        private class Line
        {
            public string Name;
            public byte Color;
            public string Text;
            public float Time;
        }

        private static readonly List<Line> lines = new List<Line>();
        private static readonly HashSet<ushort> seen = new HashSet<ushort>();
        private static readonly List<(ushort seq, string name, byte color, string text, int sendsLeft)> outgoing =
            new List<(ushort, string, byte, string, int)>();
        private static ushort nextSeq = 1;

        private static GameObject root;
        private static TextMeshProUGUI log;
        private static GameObject inputBox;
        private static TMP_InputField input;
        private static bool open;
        private static int closedFrame = -10;
        private static bool dirty = true;

        public static bool IsTyping => open;

        public static void Register(Net net)
        {
            seen.Clear();
            outgoing.Clear();
            net.OnAction(ActionChannel, (clientId, r) =>
            {
                string name = r.ReadString();
                byte color = r.ReadByte();
                string text = r.ReadString();
                HostPost(name, color, text);
            });
            net.OnWorld(Channel, r =>
            {
                ushort seq = r.ReadUInt16();
                string name = r.ReadString();
                byte color = r.ReadByte();
                string text = r.ReadString();
                if (seen.Add(seq))
                    AddLine(name, color, text);
            });
        }

        private static string Clean(string text)
        {
            // Whole <tags> out (no rich-text tricks), then any stray brackets.
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "");
            text = text.Replace("<", "").Replace(">", "").Replace("\n", " ").Replace("\r", " ").Trim();
            return text.Length > MaxLength ? text.Substring(0, MaxLength) : text;
        }

        // Host: number the line, show it, send it to everyone a few times.
        private static void HostPost(string name, byte color, string text)
        {
            name = Clean(name);
            text = Clean(text);
            if (text.Length == 0)
                return;
            ushort seq = nextSeq++;
            seen.Add(seq);
            AddLine(name, color, text);
            outgoing.Add((seq, name, color, text, 4));
            Plugin.Log.LogInfo($"Chat: {name}: {text}");
        }

        public static void Send(string text)
        {
            var net = Plugin.Instance.Net;
            text = Clean(text);
            if (net == null || text.Length == 0)
                return;
            string name = PlayerNames.LocalName;
            byte color = PlayerLook.ColorIndex;
            if (net.IsHost)
                HostPost(name, color, text);
            else
                net.SendAction(ActionChannel, w =>
                {
                    w.Write(Clean(name));
                    w.Write(color);
                    w.Write(text);
                });
        }

        private static void AddLine(string name, byte color, string text)
        {
            lines.Add(new Line { Name = name, Color = color, Text = text, Time = Time.unscaledTime });
            while (lines.Count > MaxLines)
                lines.RemoveAt(0);
            dirty = true;
            if (Plugin.Instance.Net != null && !Plugin.Instance.Net.IsHost)
                Plugin.Log.LogInfo($"Chat: {name}: {text}");
        }

        public static void Update(Net net)
        {
            if (net.IsHost)
            {
                for (int i = outgoing.Count - 1; i >= 0; i--)
                {
                    var o = outgoing[i];
                    net.BroadcastWorld(Channel, w =>
                    {
                        w.Write(o.seq);
                        w.Write(o.name);
                        w.Write(o.color);
                        w.Write(o.text);
                    });
                    if (o.sendsLeft <= 1)
                        outgoing.RemoveAt(i);
                    else
                        outgoing[i] = (o.seq, o.name, o.color, o.text, o.sendsLeft - 1);
                }
            }

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (!open)
                {
                    bool canOpen = PlayerManager.Instance != null && !PauseManager.IsPaused && !DialogueUI.IsActive;
                    if (canOpen && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame || keyboard.tKey.wasPressedThisFrame))
                        Open();
                }
                else if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    Close();
                }
                else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                {
                    Send(input.text);
                    Close();
                }
            }
            Refresh();
        }

        private static void Open()
        {
            Build();
            open = true;
            inputBox.SetActive(true);
            input.text = "";
            input.ActivateInputField();
            input.Select();
            InputManager.Instance?.GetInputActions.Disable();
            dirty = true;
        }

        private static void Close()
        {
            open = false;
            closedFrame = Time.frameCount;
            if (inputBox != null)
            {
                input.DeactivateInputField();
                inputBox.SetActive(false);
            }
            InputManager.Instance?.GetInputActions.Enable();
            dirty = true;
        }

        private static void Refresh()
        {
            if (root == null)
            {
                if (lines.Count == 0)
                    return;
                Build();
            }
            // Recent lines only (all of them while typing).
            var sb = new System.Text.StringBuilder();
            bool any = false;
            foreach (var line in lines)
            {
                float age = Time.unscaledTime - line.Time;
                if (!open && age > ShowSeconds)
                    continue;
                float alpha = open ? 1f : Mathf.Clamp01((ShowSeconds - age) / 2f);
                var c = Color.Lerp(PlayerLook.ColorOf(line.Color), Color.white, 0.45f);
                string a = ((int)(alpha * 255)).ToString("X2");
                sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(c)}{a}>{line.Name}:</color> <color=#FFFFFF{a}>{line.Text}</color>\n");
                any = true;
            }
            if (dirty || any)
            {
                log.text = sb.ToString();
                dirty = false;
            }
            root.SetActive(any || open);
        }

        private static void Build()
        {
            if (root != null)
                return;
            root = new GameObject("CoopChat");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 450;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            root.AddComponent<GraphicRaycaster>();
            var font = SpectatorBanner.GameFont();

            var logGo = new GameObject("Log", typeof(RectTransform));
            logGo.transform.SetParent(root.transform, false);
            var logRect = (RectTransform)logGo.transform;
            logRect.anchorMin = logRect.anchorMax = logRect.pivot = new Vector2(0f, 0f);
            logRect.anchoredPosition = new Vector2(30f, 110f);
            logRect.sizeDelta = new Vector2(820f, 320f);
            log = logGo.AddComponent<TextMeshProUGUI>();
            if (font != null)
                log.font = font;
            log.fontSize = 32f;
            log.alignment = TextAlignmentOptions.BottomLeft;
            log.richText = true;
            log.outlineWidth = 0.18f;
            log.outlineColor = new Color32(0, 0, 0, 220);
            log.raycastTarget = false;

            inputBox = new GameObject("Input", typeof(RectTransform));
            inputBox.transform.SetParent(root.transform, false);
            var boxRect = (RectTransform)inputBox.transform;
            boxRect.anchorMin = boxRect.anchorMax = boxRect.pivot = new Vector2(0f, 0f);
            boxRect.anchoredPosition = new Vector2(24f, 50f);
            boxRect.sizeDelta = new Vector2(830f, 54f);
            var bg = inputBox.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.6f);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(inputBox.transform, false);
            var textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14f, 4f);
            textRect.offsetMax = new Vector2(-14f, -4f);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            if (font != null)
                text.font = font;
            text.fontSize = 30f;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.richText = false;

            input = inputBox.AddComponent<TMP_InputField>();
            input.textViewport = textRect;
            input.textComponent = text;
            input.characterLimit = MaxLength;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.richText = false;
            input.caretColor = Color.white;
            input.customCaretColor = true;
            inputBox.SetActive(false);
        }

        public static void Clear()
        {
            if (open)
                Close();
        }

        // Esc while typing closes the chat, not the pause menu (also on the frame the chat closed).
        [HarmonyPatch(typeof(PauseManager), "Update")]
        private static class NoPauseWhileTyping
        {
            private static bool Prefix() => !open && Time.frameCount - closedFrame > 1;
        }

        // Test (HMCOOP_CHATTEST=1): 30 s after the world loads, say hello.
        private static float testStart = -1f;
        private static bool testDone;

        public static void Test()
        {
            if (testDone || PlayerManager.Instance == null || System.Environment.GetEnvironmentVariable("HMCOOP_CHATTEST") != "1")
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            if (Time.unscaledTime - testStart < 30f)
                return;
            testDone = true;
            Send($"hello from {PlayerNames.LocalName} <b>no tags</b>");
        }
    }
}
