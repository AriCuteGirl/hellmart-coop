using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace HellmartCoop
{
    // Steam invites (real Steam, or PupBerg). Real-Steam and PupBerg players are separate networks;
    // mixed groups join by IP instead (the host listens on both).
    //
    // Host game → a friends-only Steam lobby with rich presence "connect", so friends can join from an
    //   invite (Co-op → Invite friends opens the Steam/PupBerg invite overlay) or from the friends list.
    // Joining (invite accepted, "Join game" on a friend, or the game started with +connect_lobby <id>)
    //   → join the lobby → its owner is the host → download the host's save over Steam (channel 1,
    //   reliable) → Net over Steam P2P → start the game on the host's save.
    public static class SteamLobby
    {
        private const int SaveChannel = 1;
        private const string LobbyKey = "hellmart_coop";

        private static Steamworks.Data.Lobby? lobby;
        private static ulong pendingLobby;
        private static bool hooked;

        public static bool SteamUp
        {
            get
            {
                try
                {
                    return Steamworks.SteamClient.IsValid;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static bool HasLobby => lobby.HasValue;

        // Once Steam is up: listen for invites / join requests, and check the command line.
        public static void Hook()
        {
            if (hooked || !SteamUp)
                return;
            hooked = true;
            Steamworks.SteamFriends.OnGameLobbyJoinRequested += (l, friend) =>
            {
                Plugin.Log.LogInfo($"Steam: join request for lobby {l.Id} from {friend}");
                pendingLobby = l.Id;
            };
            Steamworks.SteamFriends.OnGameRichPresenceJoinRequested += (friend, connect) =>
            {
                Plugin.Log.LogInfo($"Steam: join {friend.Name} ({connect})");
                pendingLobby = ParseConnect(connect);
            };
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id))
                    pendingLobby = id;
            }
        }

        private static ulong ParseConnect(string connect)
        {
            var parts = (connect ?? "").Split(' ');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out ulong id))
                    return id;
            }
            return 0;
        }

        // A lobby to join, if someone asked (taken by the main menu, which can start the game).
        public static ulong TakePendingLobby()
        {
            ulong id = pendingLobby;
            pendingLobby = 0;
            return id;
        }

        // ---------------- host ----------------

        private static bool creating;
        private static float nextAttempt;

        public static async void CreateLobby()
        {
            if (creating || lobby.HasValue || Time.unscaledTime < nextAttempt || !SteamUp)
                return;
            creating = true;
            nextAttempt = Time.unscaledTime + 10f;
            try
            {
                var created = await Steamworks.SteamMatchmaking.CreateLobbyAsync(4);
                if (!created.HasValue)
                {
                    Plugin.Log.LogWarning("Steam: could not create a lobby");
                    return;
                }
                var l = created.Value;
                l.SetFriendsOnly();
                l.SetJoinable(true);
                l.SetData(LobbyKey, "1");
                lobby = l;
                Steamworks.SteamFriends.SetRichPresence("connect", $"+connect_lobby {l.Id.Value}");
                Steamworks.SteamFriends.SetRichPresence("status", "Hosting HELLMART co-op");
                Plugin.Log.LogInfo($"Steam: lobby {l.Id} created, friends can join");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Steam: lobby failed: {e.Message}");
            }
            finally
            {
                creating = false;
            }
        }

        public static void Invite()
        {
            if (lobby.HasValue)
                Steamworks.SteamFriends.OpenGameInviteOverlay(lobby.Value.Id);
        }

        public static void LeaveLobby()
        {
            if (!lobby.HasValue)
                return;
            lobby.Value.Leave();
            lobby = null;
            try
            {
                Steamworks.SteamFriends.ClearRichPresence();
            }
            catch (Exception)
            {
            }
        }

        // Host: answer save requests (main thread; same bytes as the TCP save transfer).
        public static void ServeSaves()
        {
            if (!SteamUp)
                return;
            while (Steamworks.SteamNetworking.IsP2PPacketAvailable(SaveChannel))
            {
                var packet = Steamworks.SteamNetworking.ReadP2PPacket(SaveChannel);
                if (!packet.HasValue || Encoding.ASCII.GetString(packet.Value.Data) != "HMS?")
                    continue;
                byte[] reply = SaveShare.HostSaveBytes();
                Steamworks.SteamNetworking.SendP2PPacket(packet.Value.SteamId, reply, reply.Length, SaveChannel, Steamworks.P2PSend.Reliable);
                Plugin.Log.LogInfo($"Steam: sent the save ({reply.Length} bytes) to {packet.Value.SteamId}");
            }
        }

        // ---------------- client ----------------

        // Joins the lobby, fetches the host's save over Steam and installs it. done(hostId or 0, error).
        public static IEnumerator JoinLobby(ulong lobbyId, Action<ulong, string> done)
        {
            Plugin.Log.LogInfo($"Steam: joining lobby {lobbyId}");
            var join = Steamworks.SteamMatchmaking.JoinLobbyAsync(lobbyId);
            while (!join.IsCompleted)
                yield return null;
            if (join.IsFaulted || !join.Result.HasValue)
            {
                done(0, "could not join the lobby");
                yield break;
            }
            var l = join.Result.Value;
            ulong host = l.Owner.Id.Value;
            Plugin.Log.LogInfo($"Steam: in lobby {l.Id}, host {l.Owner.Name} ({host})");
            yield return FetchSave(host, done);
        }

        // Asks the host for its save over Steam and installs it. Also used for "reload together".
        public static IEnumerator FetchSave(ulong host, Action<ulong, string> done)
        {
            var hostId = (Steamworks.SteamId)host;
            Steamworks.SteamNetworking.AllowP2PPacketRelay(true);
            Steamworks.SteamNetworking.OnP2PSessionRequest = id => Steamworks.SteamNetworking.AcceptP2PSessionWithUser(id);
            var ask = Encoding.ASCII.GetBytes("HMS?");
            float until = Time.unscaledTime + 15f;
            float nextAsk = 0f;
            while (Time.unscaledTime < until)
            {
                if (Time.unscaledTime >= nextAsk)
                {
                    nextAsk = Time.unscaledTime + 2f;
                    Steamworks.SteamNetworking.SendP2PPacket(hostId, ask, ask.Length, SaveChannel, Steamworks.P2PSend.Reliable);
                }
                while (Steamworks.SteamNetworking.IsP2PPacketAvailable(SaveChannel))
                {
                    var packet = Steamworks.SteamNetworking.ReadP2PPacket(SaveChannel);
                    if (!packet.HasValue || packet.Value.SteamId.Value != host)
                        continue;
                    string error = null;
                    try
                    {
                        SaveShare.Install(SaveShare.Parse(packet.Value.Data));
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                    }
                    done(error == null ? host : 0, error);
                    yield break;
                }
                yield return null;
            }
            done(0, "the host didn't answer over Steam");
        }

        public static void Clear()
        {
            LeaveLobby();
        }
    }
}
