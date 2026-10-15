# Over The Hill Plus QoL

Quality-of-life mod for **over the hill** (Funselektor): a cleaner, smoother game with bigger lobbies, a better
server browser and a few comfort options. Fair play: nothing in this mod gives you an advantage over other players,
and your car drives exactly like the stock game.

Open the menu with **F7** (or the **PLUS** button on the main menu). Every option can be switched off.

## Features

Everything is grouped in tabs in the menu. A search box at the top finds any option by name.

**QoL**
- Frame-rate cap above the game's own options, and a lower cap while the game is in the background.
- Keep running when alt-tabbed, so a host who switches windows doesn't freeze the session for everyone.
- Field-of-view slider for the driving camera.
- Driving HUD (**F8**): speed, RPM, gear, power, torque and wheel slip.
- Longer compass range, automatic headlights at night.
- Skip the logo screens, shorter screen fades, hide tutorial hint pop-ups.
- **Share settings**: copy all your settings as one code and paste it into a friend's game (or save them to a file).
  You see every change before it is applied, and the last import can be undone. Your server name and join message
  are left out unless you choose to include them.

**Lobby**
- Lobbies of up to 16 players in the Create Server screen, or type an exact size (bigger sizes are marked
  *experimental*).
- Your own server name, with colours, gradients or bold text that show in everyone's server browser
  (players without the mod see them too).
- An optional join message: a pop-up for everyone when a player joins (`{name}` = who joined, `{host}` = you).
- **Name filter**: server names and join messages with swear words or slurs are refused (the game's default name is
  used instead), and typed formatting tags are shown as plain text, so nobody can sneak markup into other players'
  server lists. A tester in the menu shows what would be refused and why.
- Big lobbies: players 5 and up get their own colour, spot and car in the pre-game lobby, a row in the pause menu
  list, a name above their car, a marker on the map and on the HUD. Optional (experimental): players running the mod
  share the names of players past the game's 7 name slots.
- Stability for big lobbies: extra spawn points, safe joining and leaving, clean-up after players who left, guards
  for the game's fixed-size player tables, and cars that fall under the map are put back on it.

**Servers**
- Colour-coded server list: public, private, full, and other game versions at a glance.
- How long each server has been up (since the mod first saw it).
- Favourite servers pinned to the top, extra filters, sort by age and quick join.
- Smoother list refreshes with many servers online.

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
- What the session is using right now (send rates, interest range, ping per player), and a big-lobby network
  preset for hosts.

**Menu**
- Menu size, hover tooltips, keybinds you can change, and the settings search box (Ctrl+F).

## Install

1. Install **BepInEx 6 for IL2CPP** (bleeding-edge build #788 or newer that supports Unity 6) into the game folder:
   the folder that contains `over the hill.exe`.
2. Start the game once and quit, so BepInEx can set itself up.
3. Put `OverTheHillPlusQoL.dll` in `BepInEx/plugins/OverTheHillPlusQoL/`.
4. Start the game and press **F7**.

Settings are saved in `BepInEx/config/` and can also be changed from the in-game menu.

## Big lobbies: good to know

- Everyone in a lobby above 4 players should run this mod. Lobbies of 4 or fewer work with anyone.
- The game was built for 4 players. With this mod, players 5 and up also show in the pre-game lobby, the pause menu
  player list, on the map, on the HUD and as name labels above cars. Players without the mod still see only the
  first 4 there.
- Sizes above what has been tested are marked *experimental* in the Create Server screen.

## Build from source

Needs the .NET 6 SDK (or newer) and the game with BepInEx installed and started once (it generates the reference
assemblies the project builds against).

```
dotnet build OverTheHillPlusQoL -c Release -p:GameDir="C:\path\to\over the hill"
```

The build copies the plugin into the game's `BepInEx/plugins` folder; add `-p:NoDeploy=true` to only build it.

## Notes

This is a fan-made mod, not made or supported by Funselektor. Please report problems with the mod here, not to the
game's developers. Include `BepInEx/LogOutput.log` when you report a problem.
