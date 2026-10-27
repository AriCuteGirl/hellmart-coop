using System;
using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HellmartCoop
{
    // Networking starts from the main menu's Co-op button, or at launch from environment
    // variables (used by the two-instance test):
    //   HMCOOP_ROLE = host | client
    //   HMCOOP_HOST = host IP for clients (written into the config's JoinAddress)
    //   HMCOOP_PORT = UDP/TCP port (overrides the config's Port)
    //   HMCOOP_BOT  = circle: move the local player around a 2 m circle (test bot)
    //   HMCOOP_MENUTEST = coop | hostplay | join: drive the menu without clicks (see CoopMenu)
    [BepInPlugin(Guid, "Hellmart Coop", "0.0.4")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ari.hellmart.coop";
        private const float SendInterval = 0.05f;

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        public Net Net { get; private set; }
        public Puppets Puppets => puppets;
        public string JoinAddress
        {
            get => joinAddress.Value;
            set => joinAddress.Value = value;
        }

        private int Port => int.Parse(Env("HMCOOP_PORT", port.Value.ToString()));

        private ConfigEntry<string> joinAddress;
        private ConfigEntry<int> port;
        private readonly Puppets puppets = new Puppets();
        private SaveShare.Host saveHost;
        private CircleBot bot;
        private float nextSend;
        private float nextReport;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            Log.LogInfo($"Hello from Hellmart Coop, Unity {Application.unityVersion}");
            joinAddress = Config.Bind("Network", "JoinAddress", "127.0.0.1", "Host IP that the Join button connects to.");
            port = Config.Bind("Network", "Port", 27350, "Port the host listens on (UDP for players, TCP for the save).");
            string envHost = Env("HMCOOP_HOST", "");
            if (envHost != "")
                joinAddress.Value = envHost;
            SceneManager.sceneLoaded += (scene, mode) => Log.LogInfo($"Scene loaded: {scene.name} ({mode})");
            new Harmony(Guid).PatchAll();

            // Keep simulating and sending while the window is unfocused (two windows on one desktop).
            Application.runInBackground = true;

            string role = Env("HMCOOP_ROLE", "");
            if (role == "host" || role == "client")
                StartNet(role == "host");
            if (Env("HMCOOP_BOT", "") == "circle")
            {
                bot = new CircleBot();
                Log.LogInfo("Test bot: circle");
            }
        }

        public void StartNet(bool host)
        {
            StopNet();
            int udpPort = Port;
            try
            {
                Net = new Net(host, JoinAddress, udpPort);
                Net.ClientLeft += id => puppets.Apply(new PlayerState { Id = id, InGame = false });
                WorldClock.Register(Net);
                CustomerSync.Register(Net);
                ShelfSync.Register(Net);
                StoreSync.Register(Net);
                CheckoutSync.Register(Net);
                GeneratorSync.Register(Net);
                CashSync.Register(Net);
                MonsterHunt.Register(Net);
                CoopDeath.Register(Net);
                DoorSync.Register(Net);
                AnomalySync.Register(Net);
                CleanSync.Register(Net);
                DialogueSync.Register(Net);
                StorageSync.Register(Net);
                if (host)
                    saveHost = new SaveShare.Host(udpPort);
                Log.LogInfo(host ? $"Networking as host on port {udpPort}" : $"Networking as client to {JoinAddress}:{udpPort}");
            }
            catch (Exception e)
            {
                StopNet();
                Log.LogError($"Could not start networking: {e.Message}");
            }
        }

        private void StopNet()
        {
            Net?.Dispose();
            Net = null;
            saveHost?.Dispose();
            saveHost = null;
            puppets.Clear();
            CustomerSync.Clear();
            ShelfSync.Clear();
            CheckoutSync.Clear();
            MonsterHunt.Clear();
            CoopDeath.Clear();
            DoorSync.Clear();
            AnomalySync.Clear();
            CleanSync.Clear();
            DialogueSync.Clear();
            StorageSync.Clear();
        }

        // Downloads the host's save into the co-op slot, then starts networking as a client.
        // done(null) on success, done(message) on failure.
        public IEnumerator Join(Action<string> done)
        {
            Log.LogInfo($"Joining {JoinAddress}:{Port}");
            var fetch = SaveShare.Fetch(JoinAddress, Port);
            while (!fetch.IsCompleted)
                yield return null;
            if (fetch.IsFaulted)
            {
                string error = fetch.Exception.GetBaseException().Message;
                Log.LogWarning($"Join failed: {error}");
                done(error);
                yield break;
            }
            try
            {
                SaveShare.Install(fetch.Result);
            }
            catch (Exception e)
            {
                Log.LogError($"Could not install host save: {e.Message}");
                done(e.Message);
                yield break;
            }
            StartNet(false);
            done(null);
        }

        private void Update()
        {
            var player = PlayerManager.Instance;
            bot?.Update(player);
            CoopPause.TestUpdate();

            saveHost?.Serve();
            if (Net != null)
            {
                // Without a local world (menu, loading) remote players have nowhere to stand.
                if (player != null)
                    Net.Poll(puppets.Apply);
                else
                {
                    Net.Poll(_ => { });
                    puppets.Clear();
                }
                puppets.Update();
                WorldClock.HostUpdate(Net);
                CustomerSync.HostUpdate(Net);
                CustomerSync.ClientUpdate();
                ShelfSync.HostUpdate(Net);
                StoreSync.HostUpdate(Net);
                CheckoutSync.HostUpdate(Net);
                GeneratorSync.HostUpdate(Net);
                GeneratorSync.Test(Net.IsHost);
                CashSync.HostUpdate(Net);
                if (Net.IsHost)
                    NightSync.HostTest();
                MonsterHunt.HostUpdate(Net);
                CoopDeath.Update();
                CoopDeath.DieTest();
                CoopDeath.HostReloadUpdate(Net);
                DoorSync.HostUpdate(Net);
                DoorSync.Test(Net.IsHost);
                AnomalySync.HostUpdate(Net);
                AnomalySync.Test(Net.IsHost);
                CleanSync.HostUpdate(Net);
                CleanSync.ClientUpdate(Net);
                CleanSync.Test(Net.IsHost);
                DialogueSync.HostUpdate(Net);
                StorageSync.HostUpdate(Net);
                if (!Net.IsHost)
                    StorageSync.ClientTest();
                if (!Net.IsHost)
                    DialogueSync.ClientTest();
                if (!Net.IsHost)
                {
                    MonsterHunt.ClientBait();
                    MonsterHunt.ClientHide();
                }
                if (!Net.IsHost)
                    CashSync.ClientTest();
                if (!Net.IsHost)
                {
                    ShelfSync.ClientTest();
                    StoreSync.ClientTest();
                    CheckoutTest.ClientUpdate();
                }
                if (Time.unscaledTime >= nextSend)
                {
                    nextSend = Time.unscaledTime + SendInterval;
                    Net.SendState(Capture(player));
                }
            }

            if (Time.unscaledTime >= nextReport)
            {
                nextReport = Time.unscaledTime + 5f;
                Report(player);
            }
        }

        private static PlayerState Capture(PlayerManager player)
        {
            if (player == null)
                return new PlayerState { InGame = false };
            if (CoopDeath.LocalDead)
                return new PlayerState { InGame = true, Dead = true, Position = player.transform.position };
            var look = PlayerManager.LookController.transform.eulerAngles;
            return new PlayerState
            {
                InGame = true,
                InFridge = MonsterManager.Instance != null && MonsterManager.Instance.CurrentFridge != null,
                Position = player.transform.position,
                Yaw = look.y,
                Pitch = look.x,
            };
        }

        private void Report(PlayerManager player)
        {
            var day = DayNightManager.Instance;
            // currentState is set a moment after the manager appears; CurrentPhase throws before that.
            if (player != null && day != null && Traverse.Create(day).Field("currentState").GetValue() != null)
                Log.LogInfo($"Player at {player.transform.position}, day {day.CurrentDay}, phase {day.CurrentPhase}, time {day.GameTime:F3}/{day.DayTime:F3}, paused {PauseManager.IsPaused}, timeScale {Time.timeScale}");
            if (Net == null)
                return;
            string remote = "";
            foreach (byte id in puppets.Ids)
            {
                if (puppets.TryGetPosition(id, out var pos))
                    remote += $" [{id}] {pos}";
            }
            Log.LogInfo($"Net: local id {Net.LocalId}, puppets {puppets.Count}{remote}, customers host {CustomerSync.HostCount} mirrors {CustomerSync.MirrorCount}{CustomerSync.Describe()}{ShelfSync.Describe()}{(Signage.Instance != null && MainDoor.Instance != null ? $", sign {Signage.IsOpen()} door {MainDoor.IsOpen()}" : "")}, tape {(ScannerSystem.Instance != null ? ScannerSystem.Instance.TapeProducts.Count : -1)}, register {(CashRegister.Instance != null ? $"{CashRegister.Instance.Registered}/{CashRegister.Instance.FinalPrice:F2}" : "-")}, generator {(Generator.Instance != null ? $"{Generator.Instance.CurrentState}/{Generator.Instance.Petrol:F1}" : "-")}, cash {(PlayerController.Instance != null ? PlayerController.Instance.Cash.ToString("F2") : "-")}, quota {(QuotaManager.Instance != null ? $"{QuotaManager.Instance.QuotaCurrent:F2}/{QuotaManager.Instance.QuotaGoal:F2}" : "-")}{DoorSync.Describe()}{AnomalySync.Describe()}{CleanSync.Describe()}{StorageSync.Describe()}, pending actions {Net.PendingActionCount}");
        }

        private void OnDestroy()
        {
            StopNet();
        }

        private static string Env(string name, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }

    // Proves Harmony can patch game code: fires when the local player spawns.
    [HarmonyPatch(typeof(PlayerManager), "Awake")]
    internal static class PlayerManagerAwakePatch
    {
        private static void Postfix(PlayerManager __instance)
        {
            Plugin.Log.LogInfo($"Harmony: PlayerManager.Awake on '{__instance.name}'");
        }
    }
}
