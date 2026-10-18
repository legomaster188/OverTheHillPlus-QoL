# Modding API for other creators

The OTH Mod Manager (`OTH.Core.dll`, plugin GUID `com.oth.core`) has a small public API for mods made by other
creators: namespace `OverTheHillPlus.Api`. A mod that uses it gets:

- a row on the MODS page (INSTALLED tab, with an **OTHER CREATOR** badge) and an ON/OFF switch;
- its own page there, shown with the game's own settings rows (in its MOD BROWSER), for its settings;
- its own config file, `BepInEx/config/oth.<id>.cfg`, saved for it;
- per-frame and overlay callbacks that can't take the game down when they throw;
- messages to other players who run it, over the Mod Manager's mod network;
- for a mod that adds a car: skins for it from livery packs, listed on the Livery Packs page (`Cars`);
- a whole new car from a `CarDefinition`, built by the Mod Manager's car framework (`Cars.AddCar`; read what is
  supported first);
- a "Failed to start" line on its page when its Load throws, and a count of patches a game update broke.

The API is a stable surface over the Mod Manager's internals. The internals change between versions; this surface
does not break without a major version change of the Mod Manager.

Plain BepInEx plugins that don't use the API are listed too, under **Other creators** on the INSTALLED tab, with
their version, file and GUID. Their switch renames the plugin's file to `<name>.dll.disabled` (BepInEx only loads
`.dll` files) and back:

- A plugin that is running is in use, so switching it off is queued and done when the game next starts. A plugin
  that loads before the Mod Manager has already loaded again by then, so it stops only after the second restart; the
  row says so.
- A plugin that isn't running (switched off, or it failed to load) is renamed right away, and switching it on takes
  effect after one restart.
- A plugin that failed to load shows BepInEx's reason (for example a missing dependency) in its row's hint.

## Set up a project

A mod is a BepInEx 6 IL2CPP plugin for .NET 6. Reference BepInEx, the game's interop assemblies (BepInEx writes them
to `BepInEx/interop` on the first launch) and the Mod Manager's DLL, all with `Private="false"` so none of them are
copied next to your DLL:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <AssemblyName>MyMod</AssemblyName>
    <GameDir Condition="'$(GameDir)' == ''">C:\Program Files (x86)\Steam\steamapps\common\over the hill</GameDir>
    <BepInExDir>$(GameDir)\BepInEx</BepInExDir>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="$(BepInExDir)\core\BepInEx.Core.dll" Private="false" />
    <Reference Include="$(BepInExDir)\core\BepInEx.Unity.IL2CPP.dll" Private="false" />
    <Reference Include="$(BepInExDir)\core\Il2CppInterop.Runtime.dll" Private="false" />
    <Reference Include="$(BepInExDir)\core\0Harmony.dll" Private="false" />
    <Reference Include="$(BepInExDir)\interop\*.dll" Private="false" />
    <Reference Include="$(BepInExDir)\plugins\OverTheHillMods\OTH.Core.dll" Private="false" />
  </ItemGroup>
</Project>
```

Put your DLL in its own folder, `BepInEx/plugins/MyMod/MyMod.dll`. Not in `BepInEx/plugins/OverTheHillMods`: that
folder is the Mod Manager's own, and its installer manages the files in it.

Declare the dependency, so BepInEx loads your plugin after the Mod Manager:

```csharp
[BepInPlugin("com.yourname.mymod", "My Mod", "1.0.0")]
[BepInDependency("com.oth.core")]
public class MyModPlugin : BasePlugin { ... }
```

Your plugin's GUID must not start with `com.oth.`: those are the Mod Manager's own.

## A complete mod

```csharp
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Api;
using OverTheHillPlus.Core.Manager; // ModInfo
using UnityEngine;

namespace MyMod;

[BepInPlugin("com.yourname.mymod", "My Mod", "1.0.0")]
[BepInDependency("com.oth.core")]
public class MyModPlugin : BasePlugin
{
    private const string ModId = "mymod";
    private static ConfigEntry<bool> _clock;
    private static ConfigEntry<float> _size;

    public override void Load()
    {
        // Register first: the mod stays on the MODS page, and can be switched back on, while it is off.
        Mods.Register(new ModInfo
        {
            Id = ModId,
            Name = "My Mod",
            Description = "Shows a clock in the corner of the screen.",
            Version = "1.0.0",
            DrawSimple = DrawPage,
        });
        if (!Mods.IsEnabled(ModId)) return; // switched off: nothing of it runs this session
        try
        {
            var cfg = Mods.ConfigFor(ModId); // BepInEx/config/oth.mymod.cfg
            _clock = cfg.Bind("Clock", "Show", true, "Show the clock.");
            _size = cfg.Bind("Clock", "Size", 16f, "Text size of the clock.");
            Hooks.OnGui(ModId, DrawClock);
        }
        catch (Exception e)
        {
            Mods.MarkFailed(ModId, e.Message); // the MODS page says "Failed to start: ..."
            Log.LogError(e);
        }
    }

    private static void DrawPage()
    {
        Page.Row.Toggle(_clock, "Show the clock", "A clock in the top-left corner.");
        Page.Row.Slider(_size, "Clock size", 10f, 40f, "0", " px");
    }

    private static GUIStyle _style;

    private static void DrawClock()
    {
        if (!_clock.Value) return;
        _style ??= new GUIStyle(GUI.skin.label);
        _style.fontSize = Mathf.RoundToInt(_size.Value);
        GUI.Label(new Rect(20f, 20f, 300f, 60f), DateTime.Now.ToString("HH:mm"), _style);
    }
}
```

A larger example, with an Advanced section, a per-frame hook and network messages, is in `examples/HelloMod`. Build
it with `dotnet build examples/HelloMod -c Release -p:GameDir="<your game folder>"`; add `-p:NoDeploy=true` to build
without copying it into the game.

## Reference

Everything is called from your plugin's Load or from the callbacks below, on the main thread.

### When a call fails

Every call that names a mod checks the id: 2 to 40 characters from `a-z`, `0-9`, `_`, `.` and `-`, and not `core` or
the id of one of the Mod Manager's own mods (installed or not; for example `chat` or `graphics`). How a refusal shows:

| Call | Refused id | Other problems |
|---|---|---|
| `Mods.Register` | returns false and logs why | a null `ModInfo`, or an id another mod already registered: returns false and logs why |
| `Mods.IsEnabled` | returns false | |
| `Mods.MarkFailed` | logs and does nothing (it never throws: it is meant for your catch block) | |
| `Mods.ConfigFor`, `Mods.PatchAll` | throws `ArgumentException` | `PatchAll` without a Harmony or an assembly: `ArgumentNullException` |
| `Page.Section`, `Hooks.OnTick`, `Hooks.OnGui` | throws `ArgumentException` | a null callback: `ArgumentNullException` |
| `Net.Register` | throws `ArgumentException` | a channel outside 1000-65000, taken, or over the channel caps: returns false and logs why |
| `Net.SendToHost`, `Net.SendTo` | | a channel outside 1000-65000: `SendResult.NotAllowed` |
| `Net.Broadcast` | | a channel outside 1000-65000: returns 0 |
| `Page.Row.*` | | a null `ConfigEntry` (Toggle, Slider) or no labels (Choice): the row is not drawn |

Log lines start with `[Api]` in `BepInEx/LogOutput.log`.

### Mods

| Member | What it does |
|---|---|
| `bool Register(ModInfo info)` | Lists your mod on the MODS page. Call it first in Load. False (and logged) when the id is refused. |
| `bool IsEnabled(string id)` | False when the player switched your mod off. Read it once in Load; when false, return without patching or hooking anything. A change takes effect after a restart. |
| `ConfigFile ConfigFor(string id)` | Your own config file, `BepInEx/config/oth.<id>.cfg`. Changes are saved for you a moment later. |
| `void MarkFailed(string id, string error)` | Your page shows "Failed to start" with the first line of the error. Call it from the catch around your Load. |
| `int PatchAll(string id, Harmony harmony, Assembly assembly)` | Applies your Harmony patch classes one by one: one whose target moved after a game update is skipped and logged, the rest still apply, and your page says how many features are unavailable. Returns that number. |

`ModInfo` (namespace `OverTheHillPlus.Core.Manager`):

| Field | Meaning |
|---|---|
| `Id` | 2 to 40 characters from `a-z`, `0-9`, `_`, `.` and `-`. It names your config file and the MODS page switch, so keep it forever. `core` and the ids of the Mod Manager's own mods are taken, also those you don't have installed. |
| `Name`, `Description`, `Version` | Shown on the MODS page. |
| `DrawSimple` | Draws your everyday settings, first on your page (use `Page.Row`). Null: none. |
| `DrawAdvanced` | Draws at the top of your page's Advanced fold. Null: none. |
| `Private` | Ignored for other creators' mods. |

Your mod's card in the MOD BROWSER shows an image from your DLL: embed a 16:9 PNG (640 x 360) with the resource name
`card.png` (`<EmbeddedResource Include="card.png" LogicalName="card.png" />` in your .csproj). Without one your card
gets your mod's initials on a colour from its id.

### Page

Use these only while the MODS page draws: inside `DrawSimple`, `DrawAdvanced` or a `Page.Section` block. Rows go top
to bottom; they work with mouse, keyboard and gamepad, and a row's hint shows (in the details panel) while it is
selected.

The page shows in the game's MOD BROWSER with clones of the game's own settings rows: your draw code runs once a frame
as a recording pass, so draw the same rows every frame and do the work in the callbacks, as you would for any of these
widgets. Each `Page` call becomes one game row (`Row.Toggle` / `Row.Choice` a `< value >` row, `Row.Slider` a slider,
`Row.Button` a button row, `Row.Text` a text box (the game's own), `Card` a row that folds its rows open and shut).
**Rows get the native look; raw GUI gets the full view.** Anything you draw yourself with `GUI` calls (or by moving the
layout cursor by hand) can't be shown with the game's widgets: your page then gets a **More settings** row at the bottom
that opens the full view (the Mod Manager's own window, drawn the old way) on your page, where those parts show; Esc
there comes back to the MOD BROWSER. Use the `Page` rows to stay in the game's own look.

| Member | What it does |
|---|---|
| `Section(string modId, string title, Action draw, int order = 0)` | Adds a block to your page's Advanced fold, under the heading `title`. Call it once, from Load. |
| `Card(string title, string subtitle, Action body)` | A card that folds open and shut. |
| `Heading(string text)` | A small upper-case title above some rows. |
| `Note(string text)` | A short muted line of explanation. |
| `Row.Toggle(ConfigEntry<bool> entry, string label, string hint = null, bool enabled = true)` | An Off / On row. |
| `Row.Choice(string label, string hint, int index, string[] labels, Action<int> set, bool enabled = true)` | A `< value >` row; `set` gets the picked index. |
| `Row.Slider(ConfigEntry<float> entry, string label, float min, float max, string format = "0.00", string suffix = "", bool enabled = true)` | A slider. |
| `bool Row.Button(string label, string value, string hint, Action action, bool enabled = true)` | Runs `action` when pressed. |
| `string Row.Text(string label, string value, int maxLength, string hint = null)` | A text box; returns the text as edited. |

### Hooks

| Member | What it does |
|---|---|
| `OnTick(string modId, Action tick)` | Every frame (Unity's Update). |
| `OnGui(string modId, Action gui)` | Every IMGUI event (OnGUI), also while the MODS page is closed: for overlays. Each call starts with `GUI.matrix` and the GUI colours reset. |

A callback that throws is logged once, with your mod id, and is not called again this session. The other mods'
callbacks keep running.

### Net

Messages between players who run your mod. Nothing is ever sent to a player who doesn't run the Mod Manager: a send
to them is skipped and its result says why.

- **Channels:** pick one id per kind of message from **1000 to 65000** (`Net.FirstChannel`..`Net.LastChannel`). The
  lower ids belong to the Mod Manager's own mods, and `Register` refuses them. Keep the id forever once released, and
  pick one unlikely to clash with another creator's mod.
- **How many:** at most 8 channels per mod (`Net.MaxChannelsPerMod`) and 32 for all other creators' mods together
  (`Net.MaxChannelsTotal`). The Mod Manager's own channels are always offered to the other side first, so yours never
  push one of them out.
- **Limits:** payloads are at most `Net.MaxPayload` (8192) bytes and your channel's `MaxPayload`; each sender is rate
  limited per channel (at most 20 messages a second and a burst of 40) and overall. Messages over a limit are dropped
  and counted.
- **Versions:** each message carries your channel's `Version`. Receivers drop messages older than their `MinVersion`
  and deliver newer ones, so add fields at the end and never reorder them.

| Member | What it does |
|---|---|
| `bool Register(string modId, int channel, ChannelOptions options, Action<Message> handler)` | Registers a channel once, from Load. |
| `SendResult SendToHost(int channel, byte type, byte[] payload)` | Guest to host. |
| `SendResult SendTo(PlayerRef to, int channel, byte type, byte[] payload)` | Host to a guest, or guest to guest through the host when the channel has `AllowRelay`. |
| `int Broadcast(int channel, byte type, byte[] payload)` | Host to every guest running the channel, or guest to everyone through the host. Returns how many it went to. |
| `IsHost`, `IsGuest` | Whether you host or joined a session. |
| `HostHas(int channel)`, `PeerHas(PlayerRef player, int channel)` | Whether the other side runs (and currently accepts) a channel. |
| `OnPeerReady`, `OnPeerGone`, `OnHostReady`, `OnSessionReset` | Session events. |

`ChannelOptions`: `Name` (for logs), `Version` and `MinVersion` (1-255), `MaxPayload`, `RatePerSecond`, `Burst`,
`AllowRelay` (guests may reach other guests through the host), `HostOnlySender` (guests drop anything not sent by the
host itself) and `Active` (the channel is offered to the other side only while it returns true).

`Message`: `Channel`, `Type` (your own byte), `Version`, `From` (the sender; for a relayed message, the original
sender), `FromHost`, `Relayed`, `Payload`.

Treat every payload as untrusted: the sender may run a modified copy of your mod. Check lengths and values before
you use them.

### Cars (skins for modded cars)

For mods that add a car the game doesn't have (like the Mod Manager's Vera Crossover, Terrier 4WD Wagon, Carpați 24, Hilo 89 and Bastion 200, which register here through the car framework below). The game's own livery system
only knows its stock cars, so a modded car loads its skins itself; `Cars` finds them and lets the Livery Packs page
(and any other mod) list and switch them without referencing your mod.

A skin is an entry of a livery pack in the game's own format: a folder with `manifest.json`
`{"liveries":[{"car":"Vera","name":"Rally Blue","texture":"rally_blue.png","icon":"rally_blue_icon.png"}]}` and the
textures, on your car's UV layout. The `car` value is your car's livery name (any case). Packs are read from the
game's `Documents/My Games/over the hill/LocalLiveries/<pack>/` and `BepInEx/config/oth.liveries/<pack>/` (a pack
folder name found in the first wins), with the same rules as Livery Packs: no links, one `manifest.json` at the top,
textures named by plain file names inside the pack. A skin's texture must also be a PNG, square, 1024, 2048 or 4096
pixels a side, at most 16 MB. Packs and skins that break a rule are left out with one log line (`[Cars] skipped ...`).
A skin's key is `<pack folder>/<entry name>` (at most 96 bytes of UTF-8, one `/`, no `..`): save it as the player's
choice, and send it (never a path) when other players should see the same skin.

| Member | What it does |
|---|---|
| `bool Register(ModdedCar car)` | Registers your car, once, from Load. False (and logged) for an invalid or taken id or livery name, or a missing callback. |
| `IReadOnlyList<ModdedCar> Registered` | The registered cars (copies). |
| `IReadOnlyList<Skin> SkinsFor(string carId)` | The skins found for a car, by pack then name. The folders are read on the first call and on `Rescan`. |
| `Skin FindSkin(string carId, string key)` | One skin by key, or null. |
| `void Rescan()` | Reads the folders again, then raises `Rescanned`. |
| `event Action Rescanned` | Skins may have changed: forget skin files you read before. |
| `string CurrentSkin(string carId)` | The key the car wears now (your callback), or null. |
| `bool ApplySkin(string carId, string key)` | Calls your callback with a key from `SkinsFor` (null or "" for none). False for an unknown car or key. |
| `IReadOnlyList<string> SkinFolders` | Where skins are looked for. |
| `string AddCar(CarDefinition car, Assembly assets, ManualLogSource log = null)` | Adds a whole car (see "Adding a car" below): null when it starts, otherwise why not. It registers the car's skins here itself. |
| `void DrawCarRows(CarDefinition car)` | The car's rows (state, Drive the car, garage switch, paint, trim, engine sound, skin) for your `DrawSimple`. |

`ModdedCar`: `Id` (2 to 40 characters from `a-z`, `0-9`, `_`, `.`, `-`; usually your mod id), `DisplayName`,
`LiveryCar` (letters, digits and `_`, e.g. `Vera`), `CurrentSkin` (`Func<string>`) and `ApplySkin`
(`Action<string>`: wear this skin, or your own paint for null; save the choice yourself).
`Skin`: `Key`, `Name`, `Pack`, `TexturePath`, `Side`, `Bytes`. Check the file again right before you read it (it can
change after the scan), load it yourself (for example `ImageConversion.LoadImage`) and put it on your car.

```csharp
Cars.Register(new Cars.ModdedCar
{
    Id = "mytruck", DisplayName = "My Truck", LiveryCar = "MyTruck",
    CurrentSkin = () => MyConfig.Skin.Value,
    ApplySkin = key => { MyConfig.Skin.Value = key ?? ""; MyTruckLook.Refresh(); },
});
```

### Adding a car (CarDefinition)

The Mod Manager's own cars (the Vera Crossover, the Terrier 4WD Wagon, the Carpați 24, the Hilo 89 and the Bastion 200) are built by its car framework from a
`CarDefinition` (namespace `OverTheHillPlus.Cars`) and a model in the Vera's mesh format. `Cars.AddCar` gives you the
same framework.

**What you get**, the same as the Mod Manager's cars: a networked car built at runtime from a stock donor (its network
and physics skeleton, with your model, wheels, collider, lights and cosmetic slots), a free garage entry copied from a
stock base car (class, upgrades without liveries), saves that never name it (a sidecar file brings it back), guests
joining in their own car where the session doesn't run it, tuning as multipliers on the donor, the model's tyre size on
the donor's suspension, paint and trim presets plus custom colours, skins from livery packs, a stock engine sound per
player, all of it synced to other players, and the rows for your page (`Cars.DrawCarRows`).

**What is not supported** (be honest with your players):

- **A network prefab key.** Every player's game must add the same cars in the same order, so the order is a fixed,
  append-only list that ships inside the Mod Manager (`NetPrefabPlan.Order`). `AddCar` refuses a `RegistryKey` that
  isn't in it. Today it holds only the Mod Manager's own keys, and each key belongs to one mod id (`vera`, `trailers`,
  `tercel`, `aro`, `hilo`, `bastion`): **a car from another creator needs a key added for its mod id in a Mod Manager release first** (ask for
  one). Until then, `AddCar` returns why and your mod is marked failed.
- Your own engine sounds (only the game's stock labels, `CarAudioManager.VehicleType`), your own physics beyond the
  multipliers of `CarTuningProfile`, a different number of wheels, a load bed's own cosmetics (`HasBed` only keeps the
  base car's), your own liveries in the game's livery parts (use skins), and cosmetics the base car doesn't have.
- Every player in a session needs your mod, the same version and the same donor / garage base settings; anyone else is
  refused at connect (that is the join gate, not a bug).

**The model:** embedded resources `<AssetName>/<AssetName>.mesh` (format: `Liveries/vera/vera_format.md`; parts `body`
with optional `body_lod1..3`, `glass`, one `wheel` and one `tyre` (left side, hub at the origin), `collider`; anchors
`wheel_FL/FR/RL/RR`, `headlight_L/R`, `tail_L/R`, `reverse` and the `cosm_*` cosmetic anchors), `_albedo_base.png`,
`_ao.png`, `_lights.png`, `_mask.png` (mask R wipers, G glass and lenses, B what takes snow, mud and damage), and the
optional `_zones.png` (R paint, G trim; without it the colour rows are off) and `_icon.png` (the garage icon). The
model stands on the ground (y = 0) in Unity axes, front towards +z.

**Cosmetics:** the game's cosmetic slots move to your `cosm_*` anchors: `cosm_Front`, `cosm_Rear`, `cosm_Front_Bumper`,
`cosm_Rear_Bumper`, `cosm_Hood`, `cosm_Roof`, `cosm_Roof_Bar`, `cosm_Side`, `cosm_Side_Step`, `cosm_Antenna`,
`cosm_Winch` (slots), and the item anchors `cosm_Mudflap_FL/FR/RL/RR`, `cosm_Side_Left/Right`, `cosm_Snorkel` and the
optional `cosm_Plate_Rear` (rear licence plates). A slot whose required anchors are missing is hidden; the rollcage
always is. Add item anchors as data with `ItemAnchors` (`new CosmeticItemAnchor("cosm_X", "Visual/Roof/Kayak",
optional: true)`), hide slots with `HiddenSlots`, nudge anchors with `CosmeticTweaks` (metres, at most 1).

| `CarDefinition` field | |
|---|---|
| `Id` | your mod id (registered with `Mods.Register`, `Side = ModSide.Server`) |
| `DisplayName`, `Description`, `PageNote` | garage name and description, the line on your page |
| `ItemId` | the car's inventory item id: any uint no stock item uses (checked at startup) |
| `CarName` | `CarInfo.name` and the skins' `"car"` value: letters, digits, `_`, not a stock car's name |
| `FusionGuid` | 32 hex digits, generated once |
| `RegistryKey`, `Protocol` | the key in the fixed order (see above); bump `Protocol` whenever the car's network layout or tuning changes |
| `DefaultDonor`, `DefaultGarageBase` | e.g. `Car_Brick` and `Brick` (players can change them in your config) |
| `AssetName` | the resources' prefix (a-z, 0-9, `_`) |
| `Tuning` | `CarTuningProfile` multipliers on the donor (mass, torque, revs, gears, final drive, suspension travel / spring / damper, tyre grip, steering lock) and a centre-of-mass drop |
| `Paints`, `Trims`, `PaintCustomDefault`, `TrimCustomDefault`, `TrimWhat`, `TrimHint` | presets (the first is the default; `TrimPreset.Body()` follows the body colour) |
| `EngineSounds`, `DefaultEngine`, `EngineHint` | `new EngineSound("Brick", "Brick (small four)")`, ... |
| `Cosmetics`, `ItemAnchors`, `HiddenSlots`, `CosmeticTweaks`, `HasBed` | see Cosmetics above |

`definition.Validate()` returns the first problem (null when it is fine); `AddCar` checks it too, refuses a mod that
isn't registered with `Side = ModSide.Server`, and refuses the car when its skins can't be registered (a livery name
another car took). The Mod Manager keeps its own copy of the definition: changing yours after `AddCar` changes
nothing.

```csharp
var car = new CarDefinition { Id = "mytruck", DisplayName = "My Truck", /* ... every field above ... */ };
Mods.Register(new ModInfo { Id = "mytruck", Name = "My Truck", Side = ModSide.Server, Version = "1.0.0",
                            DrawSimple = () => Cars.DrawCarRows(car) });
if (Mods.IsEnabled("mytruck"))
{
    string why = Cars.AddCar(car, typeof(MyPlugin).Assembly, Log); // null: the car is on its way
}
```

### Advantage and TextFilter

| Member | What it does |
|---|---|
| `Advantage.MarkUsed()` | Call it the moment your mod gives the player something the game wouldn't (moving or spawning cars, handing out items, changing physics...). Steam achievements stay locked until the game restarts, whichever mods are installed. |
| `Advantage.UsedThisSession` | True once any mod called `MarkUsed`. |
| `TextFilter.IsBlocked(string text, out string reason)` | The Mod Manager's word filter, the one used for player names and chat. Run text a player typed through it before other players see it, on the sending side and again when it arrives. |

## Good to know

- **Multiplayer:** a mod that changes what other players see or how the session plays should act only while you
  host (`Net.IsHost`), and only for players who run it (`Net.PeerHas`). Guests in a public session must not change
  anything for the others.
- **Switching off:** the MODS page switch takes effect after a restart; your Load reads `Mods.IsEnabled` once.
- **Errors:** wrap your Load in a try/catch that calls `Mods.MarkFailed`. Use `Mods.PatchAll` instead of
  `Harmony.PatchAll` so one broken patch after a game update doesn't stop the rest.
- **Performance:** `OnTick` and `OnGui` run every frame. Keep them cheap, and avoid allocating in them. The Mod Manager
  times them: a callback slower than 20 ms is noted in its debug log under your mod id.
- **Debug log:** `BepInEx/oth-debug/<time>.log`, level `[Debug] Level` in `BepInEx/config/oth.core.cfg` (Off, Errors,
  Info, Verbose; Errors by default). You can write to it from any thread with `OverTheHillPlus.DebugLog`:
  `Info(id, text)`, `Verbose(id, () => text)` (built only at Verbose), `Error(id, exception, context)` (each distinct
  error once, repeats counted). To guard a Harmony postfix of yours, keep `Guard.Site(id, "name")` in a static field
  and call `site.Run(action)` with a cached delegate: it catches and logs the exception, and hangs or crashes while it
  runs are blamed on your mod in the crash report (`BepInEx/oth-reports/`).
- **What isn't covered:** the Mod Manager's AVAILABLE tab only lists its own mods; it doesn't download other
  creators' mods.
- **Game state other mods change too:** if your mod patches one of these, keep to the order below.

  | What | Who changes it | Order |
  | --- | --- | --- |
  | `RuntimePlayer.Serialize` (a guest's join data: its car id) | core's car framework turns a modded car the session doesn't run into the player's stock car | core's prefix runs first (`Priority.First`); a mod that swaps a car id should prefix with `Priority.Last` so it sees the id that is sent |
  | `InventoryUtility.GetAllVehicles` (the garage list) | core's car framework adds and removes modded cars | postfix, after the game built the list |
  | the inventory while the game writes a save | core's car framework takes modded cars out for the write and puts them back after it | a car stand-in is always a stock car: never a modded car |
  | session properties at `NetworkRunner.StartGame` | the mod network (`othp.net`), the server mods' list (`othp.req`), the player names (`othp.pl`) | fixed ranks: under Photon's 10-property limit the last one is dropped first, whatever order the prefixes run in |
