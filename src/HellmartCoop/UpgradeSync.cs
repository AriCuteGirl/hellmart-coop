using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using ScriptableObjects;
using UnityEngine;

namespace HellmartCoop
{
    // Store upgrades and decorations bought on the computer, host-authoritative (the money is already
    // one shared wallet, CashSync).
    // Host → client (world channel 28, 1 Hz): every UpgradeData (key → level) and every decoration in
    //   PurchasableDecoreData (bought / installed, by field order). The client follows (SetLevel fires
    //   the game's OnUpgrade, so racks, generator model etc. update; decorations via UpdateVisuals).
    // Client → host (action channel 29): an upgrade bought (LevelUp), or the client's decoration state
    //   after it bought or toggled one.
    // Supplies (planks, canisters, traps) are delivered into the buyer's own inventory, so they stay the
    // buyer's: nothing to sync beyond the payment.
    public static class UpgradeSync
    {
        public const byte Channel = 28;
        public const byte ActionChannel = 29;
        private const float Interval = 1f;
        private const byte ActLevelUp = 0, ActDecor = 1;

        private static float nextSend;
        private static bool applying;
        private static float decorGraceUntil;

        private static bool IsClient => WorldClock.IsClient;

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        private static IEnumerable<UpgradeData> Upgrades()
        {
            var seen = new HashSet<string>();
            foreach (var u in Resources.FindObjectsOfTypeAll<UpgradeData>())
            {
                if (!string.IsNullOrEmpty(u.key) && seen.Add(u.key))
                    yield return u;
            }
        }

        private static UpgradeData FindUpgrade(string key)
        {
            foreach (var u in Upgrades())
            {
                if (u.key == key)
                    return u;
            }
            return null;
        }

        private static PurchasableDecoreData Decor()
        {
            var all = Resources.FindObjectsOfTypeAll<PurchasableDecoreData>();
            return all.Length > 0 ? all[0] : null;
        }

        private static List<PurchasableDecoreData.Data> DecorEntries(PurchasableDecoreData decor)
        {
            var list = new List<PurchasableDecoreData.Data>();
            if (decor == null)
                return list;
            foreach (var f in typeof(PurchasableDecoreData).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (f.FieldType == typeof(PurchasableDecoreData.Data) && f.GetValue(decor) is PurchasableDecoreData.Data d)
                    list.Add(d);
            }
            return list;
        }

        private static void WriteDecor(BinaryWriter w)
        {
            var entries = DecorEntries(Decor());
            w.Write((byte)entries.Count);
            foreach (var d in entries)
            {
                w.Write(d.Purchased);
                w.Write(d.Installed);
            }
        }

        // Returns whether anything changed.
        private static bool ReadDecor(BinaryReader r)
        {
            var entries = DecorEntries(Decor());
            int n = r.ReadByte();
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                bool purchased = r.ReadBoolean();
                bool installed = r.ReadBoolean();
                if (i >= entries.Count)
                    continue;
                if (entries[i].Purchased != purchased || entries[i].Installed != installed)
                {
                    entries[i].Purchased = purchased;
                    entries[i].Installed = installed;
                    changed = true;
                }
            }
            return changed;
        }

        private static void RefreshDecorVisuals()
        {
            try
            {
                PurchasableDecoreAttachments.UpdateVisuals();
            }
            catch (System.NullReferenceException)
            {
                // Not in the store scene.
            }
        }

        // ---------------- host ----------------

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost || DayNightManager.Instance == null || Time.unscaledTime < nextSend)
                return;
            nextSend = Time.unscaledTime + Interval;
            var upgrades = new List<UpgradeData>(Upgrades());
            net.BroadcastWorld(Channel, w =>
            {
                w.Write((byte)upgrades.Count);
                foreach (var u in upgrades)
                {
                    w.Write(u.key);
                    w.Write((byte)u.Level);
                }
                WriteDecor(w);
            });
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            byte act = r.ReadByte();
            if (act == ActLevelUp)
            {
                string key = r.ReadString();
                int expected = r.ReadByte();
                var u = FindUpgrade(key);
                // Only the level the client saw before buying, so a repeat or a race can't double it.
                if (u != null && u.Level + 1 == expected)
                {
                    u.LevelUp();
                    Plugin.Log.LogInfo($"Upgrades: client {clientId} bought '{key}', level {u.Level}");
                }
            }
            else if (act == ActDecor)
            {
                if (ReadDecor(r))
                {
                    RefreshDecorVisuals();
                    Plugin.Log.LogInfo($"Upgrades: client {clientId} changed the decorations");
                }
            }
        }

        // ---------------- client ----------------

        private static void Apply(BinaryReader r)
        {
            int n = r.ReadByte();
            applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string key = r.ReadString();
                    int level = r.ReadByte();
                    var u = FindUpgrade(key);
                    if (u != null && u.Level != level)
                    {
                        u.SetLevel(level);
                        Plugin.Log.LogInfo($"Upgrades: '{key}' level {level} (host)");
                    }
                }
                if (Time.unscaledTime < decorGraceUntil)
                    return;
                if (ReadDecor(r))
                    RefreshDecorVisuals();
            }
            finally
            {
                applying = false;
            }
        }

        private static void Send(byte act, System.Action<BinaryWriter> body)
        {
            var net = Plugin.Instance.Net;
            if (net == null || net.IsHost)
                return;
            net.SendAction(ActionChannel, w =>
            {
                w.Write(act);
                body(w);
            });
        }

        [HarmonyPatch(typeof(UpgradeData), nameof(UpgradeData.LevelUp))]
        private static class ClientBuysUpgrade
        {
            private static void Postfix(UpgradeData __instance)
            {
                if (!IsClient || applying)
                    return;
                string key = __instance.key;
                int level = __instance.Level;
                Plugin.Log.LogInfo($"Upgrades: bought '{key}', telling the host");
                Send(ActLevelUp, w =>
                {
                    w.Write(key);
                    w.Write((byte)level);
                });
            }
        }

        [HarmonyPatch(typeof(DecoBasketUI), nameof(DecoBasketUI.Interact))]
        private static class ClientChangesDecor
        {
            private static void Postfix()
            {
                if (!IsClient)
                    return;
                decorGraceUntil = Time.unscaledTime + 2f;
                Send(ActDecor, WriteDecor);
            }
        }

        // Test (HMCOOP_UPGRADETEST=client): 35 s in, buy the first upgrade below its max level (LevelUp,
        // what the computer's Buy ends in) and toggle-buy the first decoration (state + UpdateVisuals, what
        // DecoBasketUI.Interact does), then send it the way the Interact hook does.
        private static float testStart = -1f;
        private static bool testDone;

        public static void ClientTest()
        {
            if (testDone || DayNightManager.Instance == null || System.Environment.GetEnvironmentVariable("HMCOOP_UPGRADETEST") != "client")
                return;
            if (testStart < 0f)
                testStart = Time.unscaledTime;
            if (Time.unscaledTime - testStart < 35f)
                return;
            testDone = true;
            foreach (var u in Upgrades())
            {
                if (u.Level < u.maxLevel)
                {
                    Plugin.Log.LogInfo($"Upgrade test: client buys '{u.key}' (level {u.Level})");
                    u.LevelUp();
                    break;
                }
            }
            var entries = DecorEntries(Decor());
            if (entries.Count > 0)
            {
                entries[0].Purchased = true;
                entries[0].Installed = !entries[0].Installed;
                RefreshDecorVisuals();
                Plugin.Log.LogInfo($"Upgrade test: client sets decoration 0 to bought, installed={entries[0].Installed}");
                decorGraceUntil = Time.unscaledTime + 2f;
                Send(ActDecor, WriteDecor);
            }
        }

        public static string Describe()
        {
            int levels = 0, decor = 0;
            foreach (var u in Upgrades())
                levels += u.Level;
            foreach (var d in DecorEntries(Decor()))
                decor += (d.Purchased ? 1 : 0) + (d.Installed ? 1 : 0);
            return $", upgrade levels {levels} decor {decor}";
        }
    }
}
