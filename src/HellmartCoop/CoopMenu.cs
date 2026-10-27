using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HellmartCoop
{
    // Adds a "Co-op" entry to the main menu, styled by cloning the game's own Story Mode button.
    // Co-op opens a sub-menu in the same column: Host game / Host IP: [field] / Join / Color / Look / Back.
    // Host starts networking and opens the game's save slot picker. Join downloads the host's
    // save into a separate slot and starts the game on it.
    [HarmonyPatch(typeof(Menu), "Start")]
    internal static class CoopMenu
    {
        private static Menu menu;
        private static Transform layout;
        private static TMP_Text coopLabel;
        private static TMP_Text joinLabel;
        private static bool joining;
        private static readonly List<GameObject> gameButtons = new List<GameObject>();
        private static readonly List<GameObject> coopButtons = new List<GameObject>();

        private static void Postfix(Menu __instance)
        {
            menu = __instance;
            var mainBox = Traverse.Create(__instance).Field<GameObject>("mainBox").Value;
            layout = mainBox != null ? mainBox.transform.Find("Layout") : null;
            var template = layout != null ? layout.Find("Play Button") : null;
            if (template == null)
            {
                Plugin.Log.LogWarning("Co-op menu: main menu layout not found, button not added");
                return;
            }

            gameButtons.Clear();
            coopButtons.Clear();
            for (int i = 0; i < layout.childCount; i++)
                gameButtons.Add(layout.GetChild(i).gameObject);

            var coop = Clone(template, "Coop Button", CoopStatusText(), OpenCoopMenu);
            coop.transform.SetSiblingIndex(template.GetSiblingIndex() + 1);
            coopLabel = coop.GetComponentInChildren<TMP_Text>();
            gameButtons.Add(coop);

            // The column is anchored at the top and grows down; one more row pushes Quit onto
            // the bottom border, so lift it by one row.
            var row = template.GetComponent<RectTransform>().rect.height;
            var group = layout.GetComponent<VerticalLayoutGroup>();
            if (group != null)
                row += group.spacing;
            ((RectTransform)layout).anchoredPosition += new Vector2(0f, row);

            coopButtons.Add(Clone(template, "Coop Host Button", "Host game", Host));
            coopButtons.Add(AddressField(template));
            var join = Clone(template, "Coop Join Button", "Join", Join);
            joinLabel = join.GetComponentInChildren<TMP_Text>();
            coopButtons.Add(join);
            // Your look for the other players (PlayerLook): each click picks the next one.
            var colorButton = Clone(template, "Coop Color Button", ColorLabel(), null);
            var colorLabel = colorButton.GetComponentInChildren<TMP_Text>();
            colorButton.GetComponent<Button>().onClick.AddListener(() =>
            {
                PlayerLook.NextColor();
                colorLabel.text = ColorLabel();
            });
            coopButtons.Add(colorButton);
            var lookButton = Clone(template, "Coop Look Button", LookLabel(), null);
            var lookLabel = lookButton.GetComponentInChildren<TMP_Text>();
            lookButton.GetComponent<Button>().onClick.AddListener(() =>
            {
                PlayerLook.NextAccessory();
                lookLabel.text = LookLabel();
            });
            coopButtons.Add(lookButton);
            coopButtons.Add(Clone(template, "Coop Back Button", "Back", CloseCoopMenu));
            foreach (var go in coopButtons)
                go.SetActive(false);

            RefreshGamepad();
            Plugin.Log.LogInfo("Co-op menu: button added");

            // Test hooks: open the sub-menu for layout screenshots; host starts its game; client joins.
            string test = System.Environment.GetEnvironmentVariable("HMCOOP_MENUTEST");
            if (test == "coop")
                OpenCoopMenu();
            else if (test == "hostplay" && Plugin.Instance.Net != null && Plugin.Instance.Net.IsHost)
                menu.PlayGame();
            else if (test == "join")
                Plugin.Instance.StartCoroutine(JoinWhenHostIsUp());
        }

        private static System.Collections.IEnumerator JoinWhenHostIsUp()
        {
            OpenCoopMenu();
            for (int attempt = 0; attempt < 30 && Plugin.Instance.Net == null; attempt++)
            {
                if (!joining)
                    Join();
                yield return new WaitForSecondsRealtime(3f);
            }
        }

        // "Host IP:" label plus an editable address, built from a copy of the button row so it keeps
        // the menu's font, size and spacing.
        private static GameObject AddressField(Transform template)
        {
            var go = Object.Instantiate(template.gameObject, template.parent);
            go.name = "Coop Address Field";
            foreach (var loc in go.GetComponentsInChildren<LocText>(true))
                Object.DestroyImmediate(loc);
            // FancyButton requires Button, so it goes first.
            Object.DestroyImmediate(go.GetComponent<FancyButton>());
            Object.DestroyImmediate(go.GetComponent<Button>());

            var label = go.GetComponentInChildren<TMP_Text>();
            label.text = "Host IP:";
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.color = Color.black;

            var valueGo = Object.Instantiate(label.gameObject, label.transform.parent);
            valueGo.name = "Address";
            var value = valueGo.GetComponent<TMP_Text>();
            value.color = new Color(0.7f, 0.05f, 0.05f);
            var valueRect = (RectTransform)valueGo.transform;
            valueRect.anchoredPosition += new Vector2(label.preferredWidth + 25f, 0f);

            var input = go.AddComponent<TMP_InputField>();
            input.textViewport = valueRect;
            input.textComponent = value;
            input.characterLimit = 64;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.caretColor = value.color;
            input.customCaretColor = true;
            input.text = Plugin.Instance.JoinAddress;
            input.onEndEdit.AddListener(address => Plugin.Instance.JoinAddress = address.Trim());
            return go;
        }

        private static GameObject Clone(Transform template, string name, string label, UnityEngine.Events.UnityAction onClick)
        {
            var go = Object.Instantiate(template.gameObject, template.parent);
            go.name = name;
            // LocText would overwrite our label with the localized "Story Mode".
            foreach (var loc in go.GetComponentsInChildren<LocText>(true))
                Object.DestroyImmediate(loc);
            var text = go.GetComponentInChildren<TMP_Text>();
            text.text = label;
            // A wrapped label spills onto the row above it.
            text.textWrappingMode = TextWrappingModes.NoWrap;
            // Replacing the event drops the persistent (Inspector) listeners, which RemoveAllListeners keeps.
            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            if (onClick != null)
                button.onClick.AddListener(onClick);
            return go;
        }

        private static string ColorLabel() => $"Color: {PlayerLook.ColorName}";
        private static string LookLabel() => $"Look: {PlayerLook.AccessoryName}";

        private static void OpenCoopMenu()
        {
            foreach (var go in gameButtons)
                go.SetActive(false);
            foreach (var go in coopButtons)
                go.SetActive(true);
            RefreshGamepad();
        }

        private static void CloseCoopMenu()
        {
            foreach (var go in coopButtons)
                go.SetActive(false);
            foreach (var go in gameButtons)
                go.SetActive(true);
            coopLabel.text = CoopStatusText();
            RefreshGamepad();
        }

        private static void Host()
        {
            Plugin.Instance.StartNet(true);
            CloseCoopMenu();
            menu.OpenSaveSlotsOrNewGame();
        }

        private static void Join()
        {
            if (joining)
                return;
            joining = true;
            joinLabel.text = $"Joining {Plugin.Instance.JoinAddress}...";
            Plugin.Instance.StartCoroutine(Plugin.Instance.Join(error =>
            {
                joining = false;
                if (error != null)
                {
                    joinLabel.text = "Join (no host found)";
                    return;
                }
                CloseCoopMenu();
                menu.PlayGame();
            }));
        }

        private static string CoopStatusText()
        {
            var net = Plugin.Instance.Net;
            if (net == null)
                return "Co-op";
            return net.IsHost ? "Co-op (hosting)" : "Co-op (joined)";
        }

        // The gamepad navigator caches its button list in OnEnable; make it pick up the changes.
        private static void RefreshGamepad()
        {
            var gamepad = layout.GetComponent<GamepadButtonsManual>();
            if (gamepad != null)
                gamepad.Setup(false);
        }
    }
}
