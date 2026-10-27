# Hellmart Coop

Online co-op mod for **HELLMART** (GAZE IN GAMES, Unity 6 Mono). A BepInEx 5 plugin: one player hosts,
others join, and you run the store and survive the nights together. No game files are included.

## What works

- **Co-op menu**: a Co-op button in the main menu (Host game / Host IP / Join / Back). Joining downloads
  the host's save into its own slot ("Co-op (host)"); your own saves are never touched.
- **Players** see each other (simple capsule bodies for now).
- **The host runs the world**; everyone sees the same:
  day/night clock and phases, customers (look, walking, animation), shelf stock, open sign and main
  door, generator (fuel, breaks), store cash and quota, monsters, night doors (planks, chains, monster
  hands), anomalies, dirt and trash.
- **Clients can do things**: open the sign/door, restock shelves, work the register (scan, bag, take
  cash, give change, finish the sale, answer the customer's questions, hand over cigarettes), refuel/repair the generator, nail planks,
  chain doors, hit monster hands, clear anomalies, mop and shovel, hide in fridges.
- **Pausing** only pauses you; the world keeps running.
- **Monsters** hunt whoever is closest. A caught player **spectates until morning** ("SPECTATING"
  banner). The run is lost only when everyone is dead or the quota is missed; then the host's **Load
  Checkpoint** brings everyone back to the checkpoint together.

## Known gaps

- Boxes in your hands, the back-room storage and ordering aren't synced yet.
- Scares and special story events still happen separately for each player.
- Joining is by IP (UDP + TCP port 27350, forward it on the host's router); Steam invites are planned.
- Tested with two copies on one Linux PC (Proton); details and test logs are in `NOTES.md`.

## Install

1. Install **BepInEx 5.4.23.x for Windows x64** into the game folder (next to `Hellmart.exe`).
   On Linux/Proton add the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
2. Build the plugin (needs the .NET SDK and your own copy of the game for the references):

   ```bash
   dotnet build src/HellmartCoop -c Release -p:GameDir="/path/to/HELLMART"
   ```

   The build copies `HellmartCoop.dll` to `<game>/BepInEx/plugins/HellmartCoop/`.
3. Everyone who plays needs the same plugin build.

## Play

- Host: main menu → **Co-op** → **Host game**, then pick your save.
- Friend: main menu → **Co-op** → type the host's IP in **Host IP** → **Join**.
- Port and the default join address are in `BepInEx/config/ari.hellmart.coop.cfg`.

## For developers

Test hooks (`HMCOOP_*` environment variables) drive two local instances without anyone playing;
see `NOTES.md` for each subsystem, how it syncs and what the tests showed.
