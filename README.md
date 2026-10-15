# Over The Hill Plus — QoL

Quality-of-life mod for **over the hill** (Funselektor). No cheats: nothing here changes driving, money, items or other players.

## Features

**Server browser**
- Colour-coded rows: public, private/locked, full, different game version
- Server age ("first seen" by the mod, remembered between sessions)
- Smoother list refreshes: duplicate updates skipped, bursts merged

**Lobbies**
- Create Server offers 2–16 players (sizes above the game's tested 4 are marked *experimental*; everyone above 4 players should run the mod)
- Crash guards and smart spawn points for bigger lobbies
- "Plus lobby settings" button on the Create Server screen

**Quality of life**
- Keeps running when alt-tabbed (an alt-tabbed host no longer freezes the session for everyone)
- Background frame cap, frame-rate cap up to 360, FOV override
- Driving HUD (speed, RPM, gear, power, torque) — F8
- Skip logo screens, faster screen fades, hide tutorial hints
- Compass range, auto headlights
- PLUS button on the main menu and an in-game menu (F7)

## Install

1. Install [BepInEx 6 bleeding edge, IL2CPP x64](https://builds.bepinex.dev/projects/bepinex_be) (tested with build #788) into the game folder and start the game once.
2. Put `OverTheHillPlusQoL.dll` in `over the hill/BepInEx/plugins/OverTheHillPlusQoL/`.
3. Start the game. Press **F7** or click **PLUS** on the main menu.

Settings are saved in `BepInEx/config/com.legom.overthehill.plus.qol.cfg`.

## Build

Requires the .NET 8 SDK and the game with BepInEx already run once (it generates `BepInEx/interop`).

```
dotnet build OverTheHillPlusQoL -c Release -p:GameDir="C:\path\to\over the hill"
```

The build copies the DLL into the game's `BepInEx/plugins` folder (`-p:NoDeploy=true` to skip).

Not affiliated with Funselektor.
