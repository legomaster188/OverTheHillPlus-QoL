# Over The Hill Plus QoL

Quality-of-life mod for **over the hill** (Funselektor): a cleaner, smoother game with bigger lobbies, a better
server browser and a few comfort options. Fair play: nothing in these mods gives you an advantage over other players,
and your car drives exactly like the stock game.

It is a small **core** (the mod manager) plus separate **mods**. Install the core and any mods you like; each mod can
be switched on or off, set up and updated from the **MODS** page in the game.

Open it with **MODS** in the ESC menu, the **INSTALLED MODS** / **AVAILABLE MODS** buttons in the main menu's
**MOD BROWSER**, or press **F7**. It uses the game's own settings rows, so mouse, keyboard and controller work as in the
game's menus.

## Mods

| Mod | What it does |
|---|---|
| **Mod Manager** (core) | The MODS page, installing and updating mods, the mod network and the name filter |
| Server Browser+ | Colour-coded rows, server age, favourites, filters, all regions, Steam friends, rejoin |
| Server Creator+ | Server name styles and join message on the Create Server screen, players listed in the browser |
| Big Lobbies | Lobbies of more than 4 players with colours, spots, names and cars for everyone |
| Server Utils | Host governor, ping monitor, moderation, autosave, join smoothing, share settings |
| Chat | Text chat with other players who run the mods |
| Graphics+ | Graphics options past the game's own, with profiles |
| Photo & Spectate | High-resolution screenshots, clean view, cinematic camera, spectating |
| Map & HUD | Every player on the map and compass, pause menu list, name tags, driving HUD |
| Driving QoL | Frame-rate cap, field of view, auto headlights, skip logos, Unstuck |
| Tag & Hide and Seek | Server mod: host-run tag and hide and seek rounds with a timer, scores and "it" on the map |
| Cargo Jobs | Server mod: pick-up and delivery jobs between points of interest for pebbles, checked by the host |
| Waypoints+ | Named map pins per map, shared with the session, a dotted route to the pin you follow, host pushes a pin to everyone (everyone needs it) |
| Race Mode | Checkpoint races with a countdown for everyone, sprints or laps, splits, a leaderboard per track and saved tracks (everyone needs it) |
| Server Rules & MOTD | Welcome message and rules on join, timed announcements, vote kick (host only; guests may be vanilla) |
| Discord Presence | Map, car and player count on your Discord profile (server name only if you allow it) |
| Livery Packs | Livery packs from `BepInEx/config/oth.liveries/` in the garage's livery list, without mod.io |
| Cosmetic Stacking | Shift + click a cosmetic in the garage to add it without removing the one already on; everyone sees the stack |
| Vera Crossover | Server mod: a new car, a mid-size seven-seat crossover in the game's style, with colours, skins and the game's cosmetics (everyone needs it). Model based on "Hyundai ix55- Veracruz 2011" by Nieve5677 (Sketchfab), CC BY 4.0 |
| Terrier 4WD Wagon | Server mod: a new car, a small early-80s four-wheel-drive wagon in the game's style, with period colours, skins and the game's cosmetics (everyone needs it) |
| Hilo 89 | Server mod: a new car, a late-1980s compact 4x4 pickup in the game's style: light, sturdy, with period colours, black or chrome bumpers, skins and the game's cosmetics (everyone needs it). Model based on "Toyota Hilux 1989" by Plevako (Sketchfab), CC BY 4.0 |
| Bastion 200 | Server mod: a new car, a 2000s full-size 4x4 SUV in the game's style: big, heavy and strong, with long suspension travel, period colours, chrome or black brightwork, skins and the game's cosmetics (everyone needs it). Model based on "Toyota Land Cruiser J200 2017" by David_Holiday (Sketchfab), CC BY 4.0 |
| Carpați 24 | Server mod: a new car, a classic 1970s five-door 4x4 hardtop in the game's style: heavy, low-geared and tough, with period colours, chrome or black brightwork, skins and the game's cosmetics (everyone needs it) |
| Trailers | Server mod: tow a utility trailer, a teardrop camper, a car hauler or a boat (everyone needs it) |

Everything below lists what the mods do, grouped by topic.

## Features

Every mod's page on the MODS page shows its everyday settings first, then an **Advanced** fold with every option, with
cards named after the groups below. Every feature can be switched off.

**QoL**
- Frame-rate cap above the game's own options, and a lower cap while the game is in the background.
- **Unstuck** (button, optional hotkey): puts your car back on its wheels a little above the ground under it, in the
  same spot (10 s cooldown). Solo or hosting it's instant; as a guest it asks the host, which works only when the host
  runs this mod with the mod network on and accepts requests (hosts can switch that off).
- Keep running when alt-tabbed, so a host who switches windows doesn't freeze the session for everyone.
- Field-of-view slider for the driving camera.
- Driving HUD (**F8**): speed, RPM, gear, power, torque and wheel slip.
- Longer compass range, automatic headlights at night.
- Skip the logo screens, shorter screen fades, hide tutorial hint pop-ups.
- **Autosave**: saves through the game's own save every few minutes (10 by default), a **Save now** button and an
  optional hotkey, and a save before you quit when the game wouldn't make one itself. Solo and hosting only; saving as
  a guest is a separate opt-in, off by default.
- **Share settings**: copy all your settings as one code and paste it into a friend's game (or save them to a file).
  You see every change before it is applied, and the last import can be undone. Your server name and join message
  are left out unless you choose to include them.

**Lobby**
- Lobbies of up to 16 players in the Create Server screen, or type an exact size (bigger sizes are marked
  *experimental*).
- Your own server name, with colours, gradients, bold, italic, underline, a highlight, a bigger or smaller size and
  letter spacing, with a live preview and quick looks, that show in everyone's server browser (players without the
  mod see them too).
- An optional join message: a pop-up for everyone when a player joins (`{name}` = who joined, `{host}` = you).
- **Name filter**: server names and join messages with swear words or slurs are refused (the game's default name is
  used instead), and typed formatting tags are shown as plain text, so nobody can sneak markup into other players'
  server lists. A tester in the menu shows what would be refused and why.
- Big lobbies: players 5 and up get their own colour, spot and car in the pre-game lobby, a row in the pause menu
  list, a name above their car, a marker on the map and on the HUD. Optional (experimental): players running the mod
  share the names of players past the game's 7 name slots.
- **Spectate** (**Home**, then **]** / **[** for the next / previous player): watch another player's car from your
  own game. Only your camera moves: your car stays where it is with the handbrake on, and nobody else is affected.
- Stability for big lobbies: extra spawn points, safe joining and leaving, clean-up after players who left, guards
  for the game's fixed-size player tables, and cars that fall under the map are put back on it.

**Servers**
- Colour-coded server list: public, private, full, and other game versions at a glance.
- How long each server has been up (since the mod first saw it).
- Favourite servers pinned to the top, extra filters, sort by age and quick join.
- Smoother list refreshes with many servers online, an optional automatic refresh, and the browser remembers your
  sort (and, if you want, your search text).
- **All regions**: pick "All regions" at the bottom of the game's region list to see the servers of every region in
  one list, each tagged with its region. The browser visits the regions one after another (a few seconds each), and
  joining a server in another region switches there first. Your own region is kept for hosting. Off by default.
- **Steam friends**: servers where a Steam friend is playing are highlighted, tagged with their name and listed
  right after your favourites, or show only those servers. Your friends list is read from Steam on your own PC and
  is never sent to anyone.
- **Rejoin last server**: a button (also on the main menu) to get back into the last server you joined, and an
  automatic rejoin after a connection drop. It never retries after a kick or a ban, and it pauses itself if the
  connection keeps dropping right after rejoining.
- Who's in a server: the names of the players in a server, under the area picture and on hover, for servers whose
  host runs the mod and shares them. Sharing is off by default; when you turn it on as a host, the plain, filtered
  names of everyone in your server (guests included) are visible to players with the mod in the browser. Private
  servers show "private" unless you also allow them there. You can switch it off at any time.

**Graphics** (only changes what you see; other players are not affected)
- Post-processing: bloom, tonemapping, colour grading and lens effects on top of the game's look.
- Sky and clouds: sky colours, sun, moon and stars, clouds and cloud shadows.
- Fog and atmosphere: density, visibility, height fog, sky haze, god rays and fog colour (or no fog at all, on
  your screen only).
- Lighting and quality: shadows, ambient occlusion, anti-aliasing and upscaling, render resolution, sharpening.
- Water, rain and snow, ground wetness and car dirt.
- Draw distance and detail: view distance, terrain, vegetation and textures.
- Graphics profiles: one-click presets (Beauty, Balanced, Performance, Potato) and your own saved profiles.

**Photo**
- Screenshots at up to 4x your screen resolution, without the mod's menu or the game's HUD in the picture.
- Clean view, a cinematic chase camera, and wider photo mode limits (zoom range, free pitch, effect sliders).
  In multiplayer the photo camera can roam at most 4x the game's distance from your car; unlimited roaming works in
  single-player only.

**Network**
- Hosting: an optional performance governor for hosts (off by default). While you host enough players it lowers
  render scale, shadow distance and detail step by step when your frame rate drops, and puts your settings back
  when you stop hosting. Optional host frame-rate cap and process priority. Only your own PC is affected.
- What the session is using right now (send rates, interest range, ping per player), and a big-lobby network
  preset for hosts.
- Ping monitor: hosts get a live table of every guest's ping (now, average, jitter, peak, direct/relayed),
  colour coded. Everyone can show a small session panel (your ping to the host, players, host name, session time)
  with an optional hotkey.
- High-ping rule (host, off by default): warn, or remove, guests whose ping stays above a limit for a set time,
  with a grace period after joining. Guests running this mod with the mod network on are told first.
- Mod network (experimental, off by default): lets players who run this mod exchange small messages through the
  host (unstuck requests, big-lobby names, chat, ping and AFK notices). It only talks to players who announce that they
  run it, so vanilla players are never sent anything.

**Chat**
- Text chat with other players who run this mod (needs the mod network, and a host that runs it). Press **T**
  while driving (changeable under Keybinds), type, Enter sends, Escape cancels; driving keys are switched off
  while the box is open. Names show in each player's colour; messages fade out after a while.
- Every message has to pass the word filter: one that fails is not sent (or not shown), never censored. Rate
  limited, a mute list (by name), and an on/off switch. Vanilla players never see any of it.

**Moderation** (host)
- AFK watch (off by default): guests who haven't driven, turned or touched the controls for a set time are
  warned with a pop-up everyone sees (and an on-screen notice for guests running this mod with the mod network on).
  Removing them after a response time is a separate switch, also off by default. Guests running this mod also count
  as active while they spectate, chat or use the keyboard or mouse.
- Join rules: a block list by name or Steam id (blocked players are removed as they join), and a friends-only mode
  that lets in your Steam friends plus a manual allow list (or the allow list only).
- A moderation log of everything the rules did, and a player list with idle times and a block button.

**Menu**
- The **MODS** page looks like the game's own menus: an INSTALLED tab (one row per mod with its version and an ON / OFF
  switch; a switch takes effect after a restart) and an AVAILABLE tab. Open a mod for its settings: the everyday ones
  first, then an **Advanced** fold with the rest.
- Mouse, keyboard (arrows, Enter, Esc, Q / E for tabs) and gamepad (d-pad, A / B, bumpers). Hover tooltips, keybinds
  you can change and a settings search box (Ctrl+F).
- Menu style: **Native** (default) draws the pages like the game's own menus, with its fonts, panels and yellow
  highlight bar. **Classic** is a plain dark panel. If the native look can't be built, it switches to Classic by itself.

## Install

1. Install **BepInEx 6 for IL2CPP** (bleeding-edge build #788 or newer that supports Unity 6) into the game folder:
   the folder that contains `over the hill.exe`.
2. Start the game once and quit, so BepInEx can set itself up.
3. Download `OTH.Core.dll` from the [latest release](https://github.com/legomaster188/OverTheHillPlus-QoL/releases/latest)
   and put it in `BepInEx/plugins/OverTheHillMods/`. Add any mods you want (`OTH.Chat.dll`, `OTH.Graphics.dll`, ...)
   to the same folder. Every mod needs the core.
4. Start the game, press **Esc** and choose **MODS** (or press **F7**).

After that you don't need to download anything by hand: open **AVAILABLE MODS** in the MOD BROWSER to install more
mods, update the ones you have, or uninstall them. Downloads come only from this repository's releases, over HTTPS,
and each file's SHA-256 and size are checked before it is put in place. Nothing about you is sent. Changes apply the next
time the game starts.

Settings are saved in `BepInEx/config/` (one file per mod, `oth.<id>.cfg`) and can also be changed on the MODS page.

**Coming from the single-file version (`OverTheHillPlus`)?** Remove `BepInEx/plugins/OverTheHillPlus/` and install the
core. On the first start your old settings are moved into the new files (the old file is kept as `.migrated`). While the
old plugin is still installed the new mods stay off and the MODS page says so.

## Big lobbies: good to know

- Everyone in a lobby above 4 players should run this mod. Lobbies of 4 or fewer work with anyone.
- The game was built for 4 players. With this mod, players 5 and up also show in the pre-game lobby, the pause menu
  player list, on the map, on the HUD and as name labels above cars. Players without the mod still see only the
  first 4 there.
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

## Notes

This is a fan-made mod, not made or supported by Funselektor. Please report problems with the mod here, not to the
game's developers. Include `BepInEx/LogOutput.log` when you report a problem, and the newest file of
`BepInEx/oth-debug/` (the mods' debug log: hangs, slow frames and each error once, with the mod it belongs to). After a
crash or a freeze, the next start writes `BepInEx/oth-reports/crash-<time>.txt`: include that too. For more detail,
set `Level = Verbose` under `[Debug]` in `BepInEx/config/oth.core.cfg` and play until it happens again.
