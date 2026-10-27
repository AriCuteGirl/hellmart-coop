using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

namespace HellmartCoop
{
    // Customer dialogues for the client at the register (e.g. "give cigarettes / no smoking").
    //
    // When a client sits at the register and the customer at the counter starts a chat, the host runs
    // the chat's logic but not on its own screen: it does what ChatLogic.StartChat/EndChat do except the
    // player-state switch and DialogueUI (so the host keeps playing and the clock doesn't pause), and
    // sends the current node to that client.
    // Host → client (world channel 24, 4 Hz while a chat is on): session and node numbers, speaker name
    //   key, text key + args, options (key, args, clickable). Keys, not text: each side translates.
    // Client: shows it in its own DialogueUI box (same look, same buttons) without leaving the register.
    // Client → host (action channel 25): the option picked, or "continue" (-1) on a node without options.
    // Host: option.Select() (the game effect, e.g. the customer wants cigarettes), then the next node or
    //   the end of the chat.
    public static class DialogueSync
    {
        public const byte Channel = 24;
        public const byte ActionChannel = 25;
        private const float Interval = 0.25f;

        private static readonly MethodInfo ShowUI = AccessTools.Method(typeof(DialogueUI), "ShowUI");
        private static readonly MethodInfo ClearButtons = AccessTools.Method(typeof(DialogueUI), "ClearButtons");

        // Host: the chat being shown on a client.
        private static ChatLogic hostChat;
        private static ChatData hostData;
        private static ChatNode hostNode;
        private static ushort sessionSeq;
        private static ushort nodeSeq;
        private static float nextSend;
        private static bool hostEnded;
        private static float endedUntil;

        // Client: what is on screen.
        private static ushort shownSession;
        private static ushort shownNode;
        private static bool clientShowing;
        private static bool clickHooked;

        private static bool IsClient => WorldClock.IsClient;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        // ---------------- host ----------------

        [HarmonyPatch(typeof(ChatLogic), nameof(ChatLogic.StartChat), new System.Type[0])]
        private static class RouteToClientCashier
        {
            private static bool Prefix(ChatLogic __instance)
            {
                var net = Plugin.Instance.Net;
                if (net == null || !net.IsHost || !CheckoutSync.RemoteCashier || StoreCheckout.Instance == null)
                    return true;
                var customer = __instance.GetComponentInParent<Customer>();
                if (customer == null || customer != StoreCheckout.Instance.CurrentCustomer)
                    return true;

                // StartChat without the host's player state and DialogueUI.
                var t = Traverse.Create(__instance);
                t.Field("isActive").SetValue(true);
                t.Field<LookAtMe>("lookAtMe").Value?.StartLooking();
                t.Field<System.Action>("OnDialogueStart").Value?.Invoke();
                var data = t.Field<ChatData>("chatData").Value;
                Traverse.Create(typeof(ChatLogic)).Field<System.Action<ChatData>>("OnDialogueGlobalStart").Value?.Invoke(data);

                hostChat = __instance;
                hostData = data;
                sessionSeq++;
                hostEnded = false;
                ShowNode(data != null ? data.startNode : null);
                Plugin.Log.LogInfo($"Dialogue: '{data?.Name}' goes to the client at the register");
                return false;
            }
        }

        private static void ShowNode(ChatNode node)
        {
            if (node == null)
            {
                EndHostChat();
                return;
            }
            hostNode = node;
            nodeSeq++;
            node.ShowTrigger();
            nextSend = 0f;
        }

        private static void EndHostChat()
        {
            if (hostChat == null)
                return;
            // EndChat without DialogueUI and the player's state.
            var t = Traverse.Create(hostChat);
            t.Field("isActive").SetValue(false);
            t.Field<LookAtMe>("lookAtMe").Value?.StopLooking();
            t.Field<System.Action>("OnDialogueEnd").Value?.Invoke();
            Traverse.Create(typeof(ChatLogic)).Field<System.Action<ChatData>>("OnDialogueGlobalEnd").Value?.Invoke(hostData);
            Plugin.Log.LogInfo($"Dialogue: '{hostData?.Name}' ended");
            hostChat = null;
            hostNode = null;
            hostEnded = true;
            endedUntil = Time.unscaledTime + 2f;
            nextSend = 0f;
        }

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost || Time.unscaledTime < nextSend)
                return;
            if (hostNode == null && !(hostEnded && Time.unscaledTime < endedUntil))
                return;
            nextSend = Time.unscaledTime + Interval;
            var node = hostNode;
            net.BroadcastWorld(Channel, w =>
            {
                w.Write(sessionSeq);
                w.Write(nodeSeq);
                w.Write(node == null); // ended
                if (node == null)
                    return;
                w.Write(hostData != null ? hostData.Name ?? "" : "");
                w.Write(node.text ?? "");
                WriteArgs(w, Traverse.Create(node).Field<object[]>("args").Value);
                w.Write((byte)node.options.Count);
                foreach (var option in node.options)
                {
                    w.Write(option.text ?? "");
                    WriteArgs(w, Traverse.Create(option).Field<object[]>("args").Value);
                    w.Write(option.Interactable);
                }
            });
        }

        private static void WriteArgs(BinaryWriter w, object[] args)
        {
            w.Write((byte)(args?.Length ?? 0));
            if (args == null)
                return;
            foreach (var a in args)
                w.Write(a?.ToString() ?? "");
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            ushort session = r.ReadUInt16();
            ushort node = r.ReadUInt16();
            int choice = r.ReadSByte();
            if (hostNode == null || session != sessionSeq || node != nodeSeq)
                return; // an old click
            if (choice < 0)
            {
                if (hostNode.options.Count == 0)
                    ShowNode(hostNode.nextNode);
                return;
            }
            if (choice >= hostNode.options.Count)
                return;
            var option = hostNode.options[choice];
            Plugin.Log.LogInfo($"Dialogue: client {clientId} chose '{option.text}'");
            var next = option.nextNode;
            // Same order as DialogueUI.SelectOption: next node first, then the option's effect.
            ShowNode(next);
            option.Select();
        }

        // ---------------- client ----------------

        private static void Apply(BinaryReader r)
        {
            ushort session = r.ReadUInt16();
            ushort node = r.ReadUInt16();
            bool ended = r.ReadBoolean();
            var ui = DialogueUI.Instance;
            if (ui == null)
                return;
            if (ended)
            {
                if (clientShowing && session == shownSession)
                    HideClient(ui);
                return;
            }
            string name = r.ReadString();
            string textKey = r.ReadString();
            var textArgs = ReadArgs(r);
            int count = r.ReadByte();
            var options = new List<(string key, object[] args, bool interactable)>();
            for (int i = 0; i < count; i++)
                options.Add((r.ReadString(), ReadArgs(r), r.ReadBoolean()));
            if (clientShowing && session == shownSession && node == shownNode)
                return;
            shownSession = session;
            shownNode = node;
            ShowClient(ui, name, Localize(textKey, textArgs), options);
        }

        private static object[] ReadArgs(BinaryReader r)
        {
            int n = r.ReadByte();
            var args = new object[n];
            for (int i = 0; i < n; i++)
                args[i] = r.ReadString();
            return args;
        }

        private static string Localize(string key, object[] args)
        {
            string text = LocalizationSettings.StringDatabase.GetLocalizedString("LocTable", key, null, FallbackBehavior.UseProjectSettings);
            return args.Length > 0 ? string.Format(text, args) : text;
        }

        private static void ShowClient(DialogueUI ui, string nameKey, string text, List<(string key, object[] args, bool interactable)> options)
        {
            var t = Traverse.Create(ui);
            ShowUI.Invoke(ui, new object[] { true });
            ClearButtons.Invoke(ui, null);
            t.Field<LocText>("nameLoc").Value?.SetKey(nameKey);
            t.Field<TMPro.TMP_Text>("textTitle").Value.text = text;
            t.Field<GameObject>("nextText").Value?.SetActive(options.Count == 0);
            var buttons = t.Field<List<Button>>("buttons").Value;
            for (int i = 0; i < options.Count && i < buttons.Count; i++)
            {
                int choice = i;
                var button = buttons[i];
                button.gameObject.SetActive(true);
                button.interactable = options[i].interactable;
                button.GetComponentInChildren<TMPro.TMP_Text>().text =
                    LocalizationSettings.StringDatabase.GetLocalizedString("LocTable", options[i].key, options[i].args);
                button.onClick.AddListener(() => SendChoice(choice));
            }
            if (!clickHooked)
            {
                InputManager.Instance.OnLeftClick += ClickContinue;
                clickHooked = true;
            }
            noOptions = options.Count == 0;
            clientShowing = true;
            CursorUtility.Show(true);
            Plugin.Log.LogInfo($"Dialogue: showing '{text}' with {options.Count} options");
        }

        private static bool noOptions;

        private static void ClickContinue()
        {
            if (clientShowing && noOptions && !PauseManager.IsPaused)
                SendChoice(-1);
        }

        private static void SendChoice(int choice)
        {
            var net = Plugin.Instance.Net;
            if (net == null || net.IsHost)
                return;
            ushort session = shownSession, node = shownNode;
            net.SendAction(ActionChannel, w =>
            {
                w.Write(session);
                w.Write(node);
                w.Write((sbyte)choice);
            });
            Plugin.Log.LogInfo($"Dialogue: answered {(choice < 0 ? "continue" : $"option {choice}")}");
        }

        private static void HideClient(DialogueUI ui)
        {
            ClearButtons.Invoke(ui, null);
            ShowUI.Invoke(ui, new object[] { false });
            if (clickHooked && InputManager.Instance != null)
            {
                InputManager.Instance.OnLeftClick -= ClickContinue;
                clickHooked = false;
            }
            clientShowing = false;
            Plugin.Log.LogInfo("Dialogue: closed");
        }

        public static void Clear()
        {
            hostChat = null;
            hostNode = null;
            clientShowing = false;
            if (clickHooked && InputManager.Instance != null)
                InputManager.Instance.OnLeftClick -= ClickContinue;
            clickHooked = false;
        }

        // Test (HMCOOP_DIALOGUETEST=client): answer every dialogue that shows up with its first option
        // (for the cigarette question that's "give cigarettes"), or continue.
        public static void ClientTest()
        {
            if (!clientShowing || System.Environment.GetEnvironmentVariable("HMCOOP_DIALOGUETEST") != "client" || Time.unscaledTime < nextTestClick)
                return;
            nextTestClick = Time.unscaledTime + 1.5f;
            SendChoice(noOptions ? -1 : 0);
        }

        private static float nextTestClick;
    }
}
