# HELLMART co-op: notes

Following the coop-anything playbook (`coop-anything/AGENTS.md`, cloned at depth 1 on 2026-10-27).
Private state (test install, prefix, logs) is in `local/`, which git ignores.

## Identity (observed 2026-10-27)

| | |
|---|---|
| Game | HELLMART, GAZE IN GAMES (from `Hellmart_Data/app.info`), Steam release 2026-01-28 |
| Owner install | `/mnt/Games/Hry/Add Coop/HELLMART` (read-only for us; never modified) |
| Store / build | unknown store build id; Unity `build-guid=2853a0b9cf734c6a8502cb099a6351b6` (boot.config) |
| Hellmart.exe SHA-256 | `8a9da0d8a0257c7929a725361ef4c134b19fe64b2e063c487f4077ee69ff37c3` |
| UnityPlayer.dll SHA-256 | `aa5ef8a7b0c570dfe63d2e019b5feb2b32d3face4114bab6f5093ec152eaa943` |
| Engine | Unity 6000.0.59f2, HDRP, **Mono** (not IL2CPP): `Hellmart_Data/Managed/Assembly-CSharp.dll` |
| Steam | Facepunch.Steamworks (`SteamManager.cs`: `SteamClient.Init(appId)` wrapped in try/catch) + `Plugins/x86_64/steam_api64.dll` |
| Anti-cheat | none found |
| OS tested | Arch Linux, Proton Experimental (11.0-100), AMD RX 9060 XT, D3D11 via DXVK |

## Prior work (phase 0/1)

- coop-anything repo: no `games/` entry, no DIRECTORY hit, no issues or PRs mentioning HELLMART.
- Web search: no co-op or multiplayer mod for this game found. (The only "HELLMART co-op" hit is an
  unrelated 1990s Doom map.)
- So the route is **build from zero** (`playbook/how-coop-mods-work.md`).

## Observed

- Test install `local/test-install`: exe/dlls, `Managed/`, `Plugins/` copied; large assets symlinked
  to the owner install (122 MB on disk). Launch: `local/run_game.sh`.
- Runs under Proton Experimental in a private prefix (`local/prefix`). D3D11 renderer chosen.
  Reached the main menu (Story Mode / Extended Shift / Settings / Credits / Quit):
  `local/logs/menu-2026-10-27.png`.
- Steam init times out in this setup (`[Achievements/Steam] Init timeout -> disabled`), and the game
  falls back to `NullAchievementsService` and keeps running, so it does not require Steam to start.
- Decompiled with ilspycmd: 781 `.cs` files.
- One local player, hard-wired: `PlayerManager.Instance` is a static singleton, and the
  `PlayerReference` base class plus 98 files read it (`PlayerManager.Instance` 61x,
  `.LookController` 55x, `.Hand` 44x, `.Inventory` 38x, `.Movement` 17x, `.Interactor` 9x).
- 72 files use the `Instance` singleton pattern (DayNightManager, CustomerManager, ShelfManager,
  StorageManager, TrapManager, StoreCheckout, Telephone, ...).
- Game loop is `DayNightManager` with phases `Noon, Shift, Night, HideAndSeek, Morning, Ending`;
  saves happen through `GameDataSaver` (day, phase, cash, spawn point, shelves, storage), never in
  HideAndSeek/Ending.
- About 112 `Random.` call sites (customer spawns, anomalies), so two independent simulations would
  drift.
- `CheatEngine.cs` is a dev cheat panel (day/night slider, time speed, night-customer spawn) that is
  useful for test scenes.

### BepInEx hello plugin (2026-10-27, owner approved the download)

- BepInEx **5.4.23.5** (`BepInEx_win_x64_5.4.23.5.zip`, official GitHub release, SHA-256
  `82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4`, checked). 5.4.23.6 was skipped
  because it was only 4 days old. Installed into `local/test-install` only; `run_game.sh` sets
  `WINEDLLOVERRIDES="winhttp=n,b"`.
- Plugin source `src/HellmartCoop` (net472, references the game's and BepInEx's own DLLs, no NuGet).
  `dotnet build -c Release` copies it to `BepInEx/plugins/HellmartCoop/`.
- Result (log `local/logs/bepinex-hello-2026-10-27.log`, screenshot `local/logs/ingame-bepinex-2026-10-27.png`):
  - BepInEx reports `Running under Unity v6000.0.59...`; preloader and chainloader both complete.
  - Plugin `Awake` ran; scene order `InitScene → MenuScene → VisualScene → TechScene (additive) → GameplayScene (additive)`.
  - Harmony postfix on `PlayerManager.Awake` fired (`on 'Player'`), so patching game code works.
  - `Update` on the plugin runs in game: `Player at (-1594.62, 0.24, 42.00), day 0, phase Morning`.
- Saves live in the private prefix; the first launch made a Day 0 slot there. The owner's real saves aren't touched.

### Two instances + puppets + Co-op menu (2026-10-27)

- Second test install `local/test-install2` (same plugin folder via symlink) and prefix `local/prefix2`
  (copy of `prefix`, so it has the same Day 0 save). `HMCOOP_INSTANCE=2 ./run_game.sh` picks it.
- Transport: plain UDP (`Net.cs`), host on port 27350, clients send pose at 20 Hz, host assigns ids
  and forwards to other clients. Packet = 31 bytes (an off-by-one length check was the first bug).
- Puppets (`Puppets.cs`): capsule + black visor, no colliders, interpolated. Primitive default
  material renders magenta in this HDRP build → use a fresh `HDRP/Lit` material from one the game
  loaded. Puppets are only applied while the local player exists (otherwise they respawned every frame).
- Test bot (`HMCOOP_BOT=circle`) warps the player around a 2 m circle with unscaled time: the spawn is
  boxed in by the bus shelter, and the intro doesn't advance scaled time.
- Result: each instance logs one `Puppet N spawned`, the remote position changes over time, and the
  screenshots show a lit red puppet moving at the bus stop (`local/logs/puppets-2026-10-27.png`,
  logs `local/logs/puppets-instance{1,2}.log`).
- Main menu: `CoopMenu.cs` clones "Play Button" (Story Mode) in `Main/Layout` (VerticalLayoutGroup),
  removes `LocText` and the Inspector click listeners, adds "Co-op" → Host game / Join <addr> / Back.
  Host/Join start networking and open the game's save slot picker. Labels don't wrap, and the column is
  lifted one row so Quit stays off the border. Verified by Ari clicking Host + Join (both connected).
  Join address and port: `BepInEx/config/ari.hellmart.coop.cfg`.
- Desktop driving: KWin blocks xdotool focus changes; `import -window` frames can lag 1–2 frames.
  Don't use the real pointer (ydotool) while Ari is at the PC.

### Join loads the host's save, client timeout, IP field (2026-10-27)

- Co-op sub-menu: Host game / Host IP: [field] / Join / Back. The field is a `TMP_InputField` on a
  copy of the button row (FancyButton/Button removed), and it saves to the config's `JoinAddress`.
- Join → `SaveShare.Fetch` (TCP, same port as UDP) → host answers on its main thread with its active
  slot's `meta.json` + `save.json` ("HMS1" + int32-length blocks) → client writes them into slot
  `coop-host` (never touches its own slots), `SaveGame.SwitchSlot("coop-host")`, starts UDP, then
  `Menu.PlayGame()`. On failure the button reads "Join (no host found)" and the menu stays open.
- Host drops a client after 5 s without packets, or right away on a "bye" (sent on clean exit),
  removes its puppet, and tells the other clients. Client ids are no longer reused.
- Test (unattended, `HMCOOP_MENUTEST=hostplay` on the host, `=join` + bot on the client):
  - Host save had a marker key `_coopTest: host-1793059059`. Client log: `Installed host save into
    slot 'coop-host' (102 bytes)` → GameplayScene → puppets on both sides.
  - SHA-256 host `slot-1/save.json` = client `coop-host/save.json` = `fa541b3c…`; client's active slot
    became `coop-host`; client's own `slot-1` unchanged (`562b7a96…`, same as before).
  - `kill -9` on the client → host log `Client 1 timed out` + `Puppet 1 removed` within 8 s.
  - With no host running: `Join failed: connection refused`, retried, menu stayed usable.
  - Logs: `local/logs/join-host.log`, `local/logs/join-client.log`.
- Gotcha: BepInEx `LogOutput.log` can contain non-UTF-8 bytes → `grep` silently treats it as
  binary; use `grep -a`.

Known issues:
- The client gets the host's **last saved checkpoint**, not the live state. After loading, the two
  worlds run separately (day timer, customers, shelves): world sync is next.
- The `coop-host` slot shows up in the client's own save list (labelled only by day/phase).
- IP field not yet seen in a screenshot by me; Ari to try it.

### World sync: clock, co-op pause, customers, shelves (2026-10-27)

All host → client, as repeated snapshots on `Net.BroadcastWorld(channel)` (UDP, unreliable is fine).

- **Clock** (`WorldClock.cs`, channel 1, 4 Hz): day, phase, shift `IsEvening`/`ToNight`, `GameTime`,
  `DayTime`. Client: `DayNightManager.Update` skipped (no local Tick → no local transitions, no
  quota death); local `SwitchState`/`NextDay` blocked; host values applied (`SwitchState` is
  internal → reflection; `StartEvening` private → Traverse).
  Test `HMCOOP_CLOCKTEST=1` (host Noon → Shift → evening → next day): client logged each step;
  mid-shift DayTime host 0.416 / client 0.417. Logs `local/logs/clock-{host,client}.log`.
- **Co-op pause** (`CoopPause.cs`, Ari's request): in co-op, the pause menu and tutorial pop-ups
  only lock your own controls. `timeScale` stays 1, `IsPaused` is hidden from world code
  (TimeUtility, DayNightManager.IsTimePaused, Monster, DoorCore, DoorShake, DoorInvasion,
  Generator), and `PauseObject` only freezes the local player. The death screen is unchanged.
  Test `HMCOOP_PAUSETEST=1`: paused=True, timeScale 1, clock kept running on both; Ari saw the
  snow keep falling. Logs `local/logs/pause-{host,client}.log`.
- **Customers** (`CustomerSync.cs`, ch 2 appearance every 2 s, ch 3 motion 10 Hz): host tracks
  `CustomerManager.SpawnCustomer`. Appearance = prefab name + activeSelf bit per transform
  (looks are chosen by enabling child models). Motion = position, yaw, layer-0 state hash,
  animator float/int/bool params. Client: `CustomerSpawner`, `NightCustomerGenerator`,
  `SpawnCustomerEvent` OnActivate and `DayCustomerEvents.Spawn` skipped; mirrors = prefab copy
  instantiated under an inactive holder (no Awake), MonoBehaviours/NavMeshAgent/trigger
  colliders/audio off; removed after 1.5 s without updates.
  Test `HMCOOP_CLOCKTEST=shift HMCOOP_CUSTOMERTEST=1`: host's own spawner + 2 test spawns → client
  3 mirrors, positions follow the same path (e.g. id 2 host −5.82,37.70 → −3.59,29.02;
  client −4.65,33.14 → −2.41,24.47), no transform-count mismatch.
- **Shelves** (`ShelfSync.cs`, ch 4, 1 Hz, 50 shelves/packet): per shelf (id = FNV-1a of scene
  path) product `saveKey` + count; client clears / sets product / fills / trims to match.
  Test `HMCOOP_SHELFTEST=1`: host took 1 cereal from Rack (2), stocked Rack (3) with 2 → client
  `Rack (3) now groceries_cereal x2 (host)`, both sides 216 shelves / 125 products.
  Logs `local/logs/world-{host,client}.log`.

### Client → host actions: sign, door, restocking (2026-10-27)

- `Net.SendAction(channel)` / `Net.OnAction(channel)`: client resends every 0.3 s until the host
  acks the seq; host dedups per client. (`MsgAction` 5, `MsgAck` 6.)
- **Sign + door** (`StoreSync.cs`, world ch 5 at 4 Hz, action ch 6): client's `Signage/MainDoor
  .Open/.Close` (only real changes) → action → host calls the same method. Host state is broadcast;
  the client ignores it for 1.5 s after its own action.
- **Restocking** (`ShelfSync.cs`, action ch 7): `Pack.TransferProductTo` (the box-in-hand call) on
  the client → "+1/−1 of product K on shelf S" for the shelf side(s) → host applies it (SetConfig if
  empty, FillPack +1 / ReduceProduct −1; ignored if the shelf holds another product). The shelf ignores
  snapshots for 2 s after the client touched it.
- Test (`HMCOOP_STORETEST=1 HMCOOP_SHELFTEST=client` on the client, host plain `hostplay`):
  - client opened sign + door → host `client 1 opened the sign/door` → the host's own Noon rule
    switched to Shift → client followed. Real customers started spawning on the host (mirrored).
  - client moved a cereal from Rack (2) to Rack (6) → host `took … Rack (2), now 1` +
    `added … Rack (6), now 1`; both 125 products; 0 pending actions; no NullReferenceException.
  - 90 s later customers had bought stock: both sides 117 products, 4 customers / 4 mirrors.
  - Logs `local/logs/actions-{host,client}.log`.
- Fix: customers parent the products they pick up → appearance bits now skip transforms under a
  `Product` (counts no longer drift 1253 vs 1261).
- Gotcha: `Pack.AddProduct` on an empty pack sets the product *and* lays out slots; calling
  `SetConfig` first leaves slots unset → NRE in `Pack.UpdateProductPositions` every frame.

### Checkout from the client (2026-10-27)

`CheckoutSync.cs` (world ch 8 at 5 Hz, action ch 9). The sale always happens on the host.
- Host → client: cash offered by the customer at the counter, register (registered, open, final
  price, customer's cash), HaveProducts, tape items (id from `ScannerSystem.AddToTape` postfix,
  product key, position, yaw, scanned).
- Client: each tape item = `ProductData.CreateProduct()` + `SetForm(Default)` + its own
  `ScannerSystem.AddToTape`, so the client's normal scan/bag works; items gone on the host are
  removed (`ForceRemoveProduct`); register fields set via Traverse + Open/CloseRegister +
  UpdateRegisterScreen; on host reset the client deletes its change and resets CheckoutScreen
  (no ResetCash: quota is the host's). The customer copy's `CustomerCash` subtree stays enabled
  so it can be clicked.
- Client → host: seat on/off (`StoreCheckout.Activate/Deactivate`; host's `IsActive` getter reports
  true while a client sits), scan id (`ScannerSystem.ScanItem`), bag id (`ForceBag`), take cash
  (`CustomerCash.GiveCash`, replayed on the host's active cash, or held up to 10 s if the click came
  early), change value (`CashRegister.AddChange` → host `ChangeCash.TakeChange` with that value),
  take back (`BackChange`), complete (`StoreCheckout.CompletePurchase`, skipped locally).
- Gotcha: each body model in a customer prefab has its own `CustomerCash`; use
  `GetComponentInChildren<CustomerCash>(false)` (active only), not `(true)`.
- Test (`HMCOOP_CHECKOUTTEST=1` + `HMCOOP_STORETEST=1` on the client, host plain): bot sits, scans,
  bags, takes cash, gives change, completes; repeats until a sale needed change.
  Result: PASSED, 2 sales: 3.00 paid exactly; then 5.00 for 3.00 → change [1.00, 1.00] → host
  `completed the purchase (allowed True), store cash 0.50 -> 1.00`, register reset on both sides.
  No exceptions. Logs `local/logs/checkout-{host,client}.log`.
- Observed in an earlier run: cash clicks arrived from the client window while the bot was waiting,
  probably Ari clicking the customer copy's cash by hand, so the real click path sends the action.
  Not confirmed with Ari.

### Night, part 1: generator, cash/quota, monsters (2026-10-27)

- **Generator** (`GeneratorSync.cs`, world 10 / action 11): client skips `Generator.Update` and
  local `ChangeState`; follows host state + petrol (`ChangeState`/`ChangePetrol` via reflection).
  Client `Fill` and `HandleQTESuccess` → host. Test `HMCOOP_GENTEST=host/client`: host broke it →
  client `Working -> Broken (host)`; client fixed + refuelled 10 → host `Broken -> Working`,
  `now 15.0`; both Working/15.0.
- **Cash + quota** (`CashSync.cs`, world 12 / action 13): one store wallet (host
  `PlayerController.Cash`) and the host's quota; client HUD updated through the game's own events;
  client `Pay` → host `Pay`; client `EarnQuota`/`QuotaUp` dropped. Test: client paid 1.00 → host
  −1.00, then a client sale tip +0.50 → both −0.50, quota 1.00/25.00 both.
- **Night directors host-only** (`NightSync.cs`): client skips InvasionGenerator/AnomalyGenerator
  OnActivate, MarketManager.StartInvasion, MonsterManager.SpawnLikeAFake/CreateUndergroundMonster,
  DoorInvasion.SummonMonster.
- **Monsters on clients**: `CustomerSync` now also mirrors every `Monster` (tracked at
  `Monster.Start`, kind byte in the appearance message). Test `HMCOOP_MONSTERTEST=1`: host monster
  at (1.22,0.09,3.27) → client mirror, both at (5.60,0.27,3.11).
- **Monster hunts both players** (`MonsterHunt.cs`, world 14): before each host `Monster.Update`
  the nearest player is picked; for a remote player the host's player + look transforms stand in at
  the remote pose during the call (restored in a finalizer). `MonsterKills.Play` against a remote
  player is redirected to that client (repeated, deduped); the host skips its death screen for it and
  sets the monster back to Search after 5 s. The client freezes its monster copy, enables its
  Cinemachine cameras, plays the same variant, then shows DeadScreen. Test `HMCOOP_BAIT=1` (client
  warps next to the monster): host `caught client 1 (EatPlayer)`, client `caught by monster 2
  (EatPlayer)` + kill view (claw on screen) + death screen; host not killed by the monster.
- Seen in that test: a monster appearing switches the phase to HideAndSeek (`SetMonster`), and
  `NightKiller` kills any player outside the store for 5 s at night ("You broke the rule"). The host
  test player stood at the bus stop and died of that, which is the game's own rule, per player.
- Host save backed up before monster tests: `local/save-backup/`.

### Co-op death: spectate until morning (2026-10-27, Ari's choice)

`CoopDeath.cs` (world ch 15) + `Dead` flag in the player packet (StateSize 32).
- Deaths: `PlayerController.Die` (night rule outside the store) → spectate; the host's own monster
  kill (single `Monster.HandleAttack` hook: remote kills tick in MonsterHunt, the host's own kill
  becomes spectate + monster back to Search); a kill sent to a client → spectate.
- Spectating: `PlayerController.SwitchToWatchCamera` with our CinemachineCamera (priority 50)
  following a living player's eyes. Others drop the dead player's puppet; host monsters ignore a dead
  player and never replay a kill on a spectating host.
- Morning (host clock) revives the dead next to a living player. A missed quota (Die inside
  `ShiftStateAsset.OnTransition`) or everyone dead → host declares the run lost (repeated broadcast)
  → everyone gets the game's DeadScreen; no revive after that.
- Tests (`HMCOOP_MONSTERTEST=1` host now steps inside the store at 25 s, spawns at 30, removes the
  monster at 44, Morning at 46; `HMCOOP_BAIT=1` client at 36 s):
  - both outside at hide-and-seek → both `died (Die())` → `run lost (everyone died)` → DeadScreen.
  - client caught (`KickBackPlayer`), monster then caught the host too → run lost (found and fixed:
    Morning revived players after a lost run).
  - client caught (`EatPlayer`) → spectating; host alive; Morning → client
    `morning, back in the game`, both sides 1 puppet again. No exceptions.

- Spectator banner (Ari's request, `SpectatorBanner.cs`): "SPECTATING / until morning" at the top,
  own overlay canvas, the game's subtitle font (MonologueUI's TMP font). The spectated player's puppet
  is hidden: the spectator camera sits at eye height, inside its black visor box (that was the black
  screen, spotted by Ari). Revive only on the change *to* Morning. After a client kill the monster
  copy is unfrozen so it follows the host again. Screenshot `local/logs/spectating-2026-10-27.png`.

Not synced yet / known gaps:
- Hiding in the fridge isn't synced: the host's monster doesn't know when a remote player is hiding.
- Remote players can't hide in the fridge yet (MonsterManager.IsHeroHidden is the host's).
- Doors at night (DoorCore/DoorInvasion/DoorShake), barricades, chains, monster hands, anomalies
  are not synced yet.
- Customers who want cigarettes (DefaultCustomer `wannaCigarette`) ask before paying; handing
  cigarettes from the client is not forwarded yet (the host log says so when it happens).
- The host's own cashier work while a client is seated isn't arbitrated (two cashiers at once).
- Boxes in hands are not synced (each player's own box is local).
- Mirrored customers can't be served by the client (scripts off); checkout is host-only.
- Screamer and unique events (StoreLooker, Poor, GlassWashing, ToiletKey, night camera event…)
  still run locally on the client and log "local-only spawn".
- Storage (back room racks), cash, quota, doors, generator, monsters, trash/dirt not synced.
- Dialogue on the host still pauses the clock for everyone (`IsTimePaused` reads DialogueUI).
- Test-only env hooks: HMCOOP_CLOCKTEST, HMCOOP_PAUSETEST, HMCOOP_CUSTOMERTEST, HMCOOP_SHELFTEST.

## Inferred

- BepInEx 5 (Mono) + Harmony works (verified above): no IL2CPP, no anti-cheat.
- The game's own Facepunch.Steamworks could carry lobbies and P2P (invite friends, no port
  forwarding), the same way the Hitman Absolution co-op uses Steam invites.
- Authority model: **host-authoritative**. The host runs DayNight, customers, monsters, anomalies and
  saves. Clients get the state and send their actions (pick up, place on shelf, scan, cash, doors).
  Remote players are puppets. Every `PlayerManager.Instance` reader stays local, which is fine as
  long as remote players never go through it.

## Unknown

- The Steam app id serialized on `SteamManager` (needed to check if lobbies work under the real id).
- How monsters and HideAndSeek target the player (single target), and how much per-player logic that needs.
- Store build id / version string.

## Proposed build order

1. ~~BepInEx 5 + hello plugin~~ done 2026-10-27.
2. ~~Two-instance local test with plain UDP~~ done 2026-10-27. Steam lobbies later.
3. Puppets: ~~position, rotation~~ done; held item, flashlight, a real body model next.
3b. ~~Client timeout, in-game IP field, client loads the host's save~~ done 2026-10-27.
4a. ~~Host clock, co-op pause, customers (walk/anim/looks), shelf stock~~ done 2026-10-27.
4b. ~~Client → host actions: sign/door, restocking~~ done 2026-10-27.
4c. ~~Checkout from the client~~ done 2026-10-27 (incl. change).
4d. Cigarettes at checkout, boxes in hand, storage, cash/quota display, screamer/unique events.
4. Host authority over DayNight time and phase, customers (spawn, path, checkout), shelves and stock.
5. Interactions from client → host (Harmony prefixes on `PlayerInteractor` / `Hand` actions).
6. Night threats and HideAndSeek: monsters pick the nearest player.
7. Bot-driven two-client QA (`coop-anything/tools`), then a package with host/join.
