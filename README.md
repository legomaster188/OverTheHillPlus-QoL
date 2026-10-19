# Over The Hill Plus

Mods for **over the hill**: a better server browser, lobbies of up to 16 players, chat, more graphics options, camera
and photo tools, and one new car. Take everything or pick only what you like. Every mod can be switched off in the game.

## Install (2 minutes)

### [Download the installer: OverTheHillMods-Installer.zip](https://github.com/legomaster188/OverTheHillPlus-QoL/releases/latest/download/OverTheHillMods-Installer.zip)

1. **Download and unzip.** Close the game first. Right click the zip and choose Extract All.
2. **Double-click `Install.bat`.** Windows may ask if you trust it. The installer is plain text, so you can open
   `Install.ps1` in Notepad and read everything it does. It needs no admin rights.
3. **Pick what to install.** The installer finds your game by itself. If it can't, it asks you to paste the game
   folder (the one with `over the hill.exe`). If BepInEx, the loader the mods run on, is missing, it sets that up
   too. Then you get a menu:
   - `1` Everything: all the mods below.
   - `2` Choose mods: a numbered list. Type numbers to switch mods on or off, then press Enter.
   - `3` Update: brings the mods you already have up to date.
   - `4` Uninstall: removes the mods. It asks before it touches BepInEx or your settings.
4. **Start the game.** The first start takes a few minutes while BepInEx gets ready. Then press **F7**, or press Esc
   and choose **MODS**.

### Install by hand

1. Install **BepInEx 6 for IL2CPP, build 788** into the game folder (the one with `over the hill.exe`): download
   [this zip](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip) and unzip it there. Newer builds may not work.
2. Download `OverTheHillMods-All.zip` from the
   [latest release](https://github.com/legomaster188/OverTheHillPlus-QoL/releases/latest) and unzip it into the game
   folder. The DLLs must end up in `BepInEx\plugins\OverTheHillMods\`.
3. Start the game.

### Update

Run `Install.bat` again and pick `3`, or open **MODS** in the game and use **AVAILABLE MODS**. Downloads come only from
this repository's releases, over HTTPS, and each file's SHA-256 is checked before it is used. Nothing about you is sent.

### Uninstall

Run `Install.bat` and pick `4`. By hand: delete the folder `BepInEx\plugins\OverTheHillMods\`. Your settings stay in
`BepInEx\config\` (files named `oth.*`) until you delete them.

### Something wrong?

- **The installer can't find the game.** In Steam, right click the game, then Manage, then Browse local files. Copy
  that folder's address and paste it into the installer.
- **No MODS page in the game.** Close the game and run the installer again. Then open `BepInEx\LogOutput.log` in the
  game folder and look for red error lines. The first start after installing takes a few minutes, so wait for the
  main menu.
- **After a game update the mods stop working.** The mods are built for one version of the game. Run the installer and
  pick `3`. If there is no update yet, wait for one.
- **See what BepInEx says.** Open `BepInEx\config\BepInEx.cfg`, find `[Logging.Console]` and set `Enabled = true`.
  A console window then opens with the game.
- **Where are the logs?** `BepInEx\LogOutput.log` is the main one. The mods also write to `BepInEx\oth-debug\`, and
  after a crash to `BepInEx\oth-reports\`. Attach these when you report a problem.
- **"Banned from this session" when joining a server.** The host runs the Vera, the new car. The game refuses players
  who don't have it. Install the same mods as the host, the easiest way is menu option `1`.
- **My antivirus removed a file.** The DLLs are not signed. Add the game folder as an exception, then run the
  installer again.

Fan made, not affiliated with Funselektor. Please report problems with the mods here, not to the game's developers.

## Mods

The core is the mod manager. The other mods need it, and the installer always adds it.

| Mod | What it does |
|---|---|
| <img src="assets/discord/thumb_core.jpg" width="64" height="36" alt=""> **Mod Manager** (core) | The MODS page, installing and updating mods, the mod network and the name filter |
| <img src="assets/discord/thumb_biglobbies.jpg" width="64" height="36" alt=""> Big Lobbies | Lobbies of up to 16 players, with colours, spots, names and cars for everyone past the game's first 4 |
| <img src="assets/discord/thumb_serverbrowser.jpg" width="64" height="36" alt=""> Server Browser+ | Colour-coded rows, server age, favourites, filters, all regions, Steam friends, rejoin |
| <img src="assets/discord/thumb_servercreator.jpg" width="64" height="36" alt=""> Server Creator+ | Server name styles and a join message on the Create Server screen, players listed in the browser |
| <img src="assets/discord/thumb_serverutils.jpg" width="64" height="36" alt=""> Server Utils | Host governor, ping monitor, moderation, autosave, faster joins, sharing your settings as a code |
| <img src="assets/discord/thumb_chat.jpg" width="64" height="36" alt=""> Chat | Text chat with the other players who run the mods |
| <img src="assets/discord/thumb_rules.jpg" width="64" height="36" alt=""> Server Rules & MOTD | Welcome message and rules on join, timed announcements, vote kick. Only the host needs it |
| <img src="assets/discord/thumb_camera.jpg" width="64" height="36" alt=""> Camera+ | Hood, bumper, far chase and drone cameras, each with its own field of view, and a key to cycle them |
| <img src="assets/discord/thumb_cockpit.jpg" width="64" height="36" alt=""> Cockpit | Sit in the driver's seat: a dashboard, steering wheel and gauges built to fit each car, free look and head sway |
| <img src="assets/discord/thumb_photo.jpg" width="64" height="36" alt=""> Photo & Spectate | High-resolution screenshots, a clean view without the HUD, cinematic camera, watching other players |
| <img src="assets/discord/thumb_drivingqol.jpg" width="64" height="36" alt=""> Driving QoL | Frame-rate cap, field of view, auto headlights, skip the logos, an unstuck button, a live ride-height preview in the garage |
| <img src="assets/discord/thumb_graphics.jpg" width="64" height="36" alt=""> Graphics+ | Graphics options past the game's own, with profiles. Only changes what you see |
| <img src="assets/discord/thumb_realnights.jpg" width="64" height="36" alt=""> Real Nights | Dark nights with only moonlight, a starry sky, storms that go pitch black, and your lamps lighting the ground. Only you see it |
| <img src="assets/discord/thumb_discord.jpg" width="64" height="36" alt=""> Discord Presence | Your map, car and player count on your Discord profile (the server name only if you allow it) |
| <img src="assets/discord/thumb_vera.jpg" width="64" height="36" alt=""> Vera Crossover | A new car: a mid-size seven-seat crossover in the game's style, with colours, skins and the game's cosmetics. Everyone in the session needs it. See Credits |

## Features

Every mod's page on the MODS page shows its everyday settings first, then an **Advanced** fold with every option.
Every feature can be switched off.

**Lobby**
- Lobbies of up to 16 players in the Create Server screen, or type an exact size (bigger sizes are marked
  *experimental*).
- Players 5 and up get their own colour, spot and car in the pre-game lobby, a row in the pause menu list, a name
  above their car and a marker on the map.
- Your own server name, with colours, gradients, bold, italic and a live preview, that shows in everyone's server
  browser (players without the mod see it too). An optional join message pops up for everyone when a player joins.
- **Name filter**: server names and join messages with swear words or slurs are refused, and typed formatting tags
  are shown as plain text, so nobody can sneak markup into other players' server lists.
- Extra spawn points, safe joining and leaving, clean-up after players who left, and cars that fall under the map are
  put back on it.

**Servers**
- Colour-coded server list: public, private, full, and other game versions at a glance. How long each server has
  been up, favourites pinned to the top, extra filters, sort by age and quick join.
- **All regions**: pick "All regions" at the bottom of the game's region list to see the servers of every region in
  one list. Off by default.
- **Steam friends**: servers where a Steam friend is playing are highlighted and listed after your favourites. Your
  friends list is read from Steam on your own PC and is never sent to anyone.
- **Rejoin last server**: a button to get back into the last server you joined, and an optional automatic rejoin
  after a connection drop. It never retries after a kick or a ban.
- Who's in a server: the names of the players, for servers whose host runs the mod and shares them. Sharing is off by
  default.

**Hosting** (Server Utils)
- An optional performance governor for hosts (off by default): it lowers render scale, shadow distance and detail
  step by step when your frame rate drops, and puts your settings back when you stop hosting.
- Ping monitor: hosts get a live table of every guest's ping. Everyone can show a small session panel.
- AFK watch and join rules (a block list, a friends-only mode) with a moderation log, all off until you switch them on.
- **Autosave**: saves through the game's own save every few minutes. **Share settings**: copy all your settings as one
  code and paste it into a friend's game. You see every change before it is applied, and the last import can be undone.

**Chat**
- Press **T** while driving (changeable under Keybinds), type, Enter sends, Escape cancels. It needs the mod network
  and a host that runs it. Every message has to pass the word filter. Rate limited, with a mute list. Players without
  the mod never see any of it.

**Camera and photo**
- Hood, bumper, far chase and drone cameras, and a cockpit with a dashboard that fits each car.
- Screenshots at up to 4x your screen resolution without the mod's menu or the game's HUD in the picture, a clean
  view and a cinematic chase camera. Spectate another player's car with **Home**, then **]** / **[**; only your
  camera moves.

**Graphics** (only changes what you see; other players are not affected)
- Post-processing, sky and clouds, fog, lighting, shadows, anti-aliasing and upscaling, water, rain and snow, draw
  distance and detail.
- One-click profiles (Beauty, Balanced, Performance, Potato) and your own saved profiles. Real Nights adds the dark
  nights.

**Driving**
- Frame-rate cap above the game's own options, keep running when alt-tabbed, field of view slider, automatic
  headlights, skip the logo screens, hide tutorial hints.
- **Unstuck** (button, optional hotkey): puts your car back on its wheels a little above the ground, in the same spot
  (10 s cooldown). As a guest it asks the host, which works only when the host runs the mod network.

**Menu**
- The **MODS** page looks like the game's own menus: an INSTALLED tab (one row per mod with its version and an ON /
  OFF switch; a switch takes effect after a restart) and an AVAILABLE tab. Mouse, keyboard and gamepad work.
- Settings are saved in `BepInEx/config/` (one file per mod, `oth.<id>.cfg`) and can also be changed on the MODS page.

## Big lobbies: good to know

- Everyone in a lobby above 4 players should run the Big Lobbies mod. Lobbies of 4 or fewer work with anyone.
- The game was built for 4 players. With the mod, players 5 and up also show in the pre-game lobby, the pause menu
  player list, on the map and as name labels above cars. Players without the mod still see only the first 4 there.
- Sizes above what has been tested are marked *experimental* in the Create Server screen.

## Build from source

Needs the .NET 6 SDK (or newer) and the game with BepInEx installed and started once (it generates the reference
assemblies the projects build against).

```
dotnet build src/OverTheHillMods.sln -c Release -p:GameDir="C:\path\to\over the hill"
```

The build copies each DLL into the game's `BepInEx/plugins/OverTheHillMods` folder; add `-p:NoDeploy=true` to only build
(always do that while the game is running).

## Make your own mod

Mods by other creators can show up on the MODS page with their own settings page, switch and config file, and talk to
other players over the mod network: see [docs/modding-api.md](docs/modding-api.md) and the example in `examples/HelloMod`.
Plain BepInEx plugins are listed on the MODS page too, and can be switched off there.

## Credits

The Vera Crossover's body, wheels and glass are based on "Hyundai ix55- Veracruz 2011" by Nieve5677 on Sketchfab
(https://sketchfab.com/iori308408), licensed under
[CC BY 4.0](http://creativecommons.org/licenses/by/4.0/). Changes: interior, plates, badges and lettering removed,
hidden parts removed, panels turned the right way out, re-shaded and given LODs, a new UV layout and textures in the
game's style, the tyre rebuilt, and mount points added for the game's cosmetics.

## Notes

This is a fan-made mod, not made or supported by Funselektor. Please report problems with the mods here, not to the
game's developers. Include `BepInEx/LogOutput.log` when you report a problem, and the newest file of
`BepInEx/oth-debug/` (the mods' debug log). After a crash or a freeze, the next start writes
`BepInEx/oth-reports/crash-<time>.txt`: include that too.
