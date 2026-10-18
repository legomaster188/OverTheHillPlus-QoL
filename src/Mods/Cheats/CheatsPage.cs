using System;
using System.Linq;
using OverTheHillPlus.Features;
using OverTheHillPlus.UI;
using UnityEngine;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.CheatsPlus;

/// <summary>
/// The cards of Cheats+'s page that the old F7 menu drew itself, as sections in its Advanced fold, in the old tab
/// order (driving assists, car cheats, vehicles, items, world commands, the command list). The features' own tabs
/// (tuning, ESP, fun physics) follow them.
/// </summary>
internal static class CheatsPage
{
    private const int Plus = Registry.PlusMode;
    // Before every feature section of the same tab, as the old menu drew its own cards first.
    private const int BuiltInOrder = int.MinValue / 2;

    /// <summary>Registers the sections (from the plugin's Load, before the features register theirs).</summary>
    internal static void RegisterSections()
    {
        string id = CheatsPlugin.ModId;
        Registry.Section(Plus, "Driving", DrawDriving, BuiltInOrder, modId: id);
        Registry.Section(Plus, "Cheats", DrawCheats, BuiltInOrder, modId: id);
        Registry.Section(Plus, "Vehicles", DrawVehicles, BuiltInOrder, modId: id);
        Registry.Section(Plus, "Items", DrawItems, BuiltInOrder, modId: id);
        Registry.Section(Plus, "World", DrawWorld, BuiltInOrder, modId: id);
        Registry.Section(Plus, "Commands", DrawCommands, BuiltInOrder, modId: id);
    }

    private static string _carFilter = "";
    private static string _presetName = "";

    private static string _command = "";
    private static string _filter = "";
    private static string _weather = "";
    private static float _hour = 12f;
    private static float _timeScale = 1f;

    private static void DrawDriving()
    {
        bool phys = GameState.PhysicsAllowed;
        if (!phys)
            Banner("You're a guest here. The host's game simulates your car, so these settings kick in when you play solo or host.", Theme.Info);

        Card("Grip", null, () =>
        {
            Switch(CheatsConfig.AssistsEnabled, "Driving assists", "Master switch for everything on this tab", phys);
            SliderRow(CheatsConfig.GripMultiplier, "Tire grip", 1f, 3f, "0.00", "×", phys);
            Switch(CheatsConfig.SlipperyBoost, "Slippery-surface boost", "Mud, snow, ice, sand, water, loose rock", phys);
            SliderRow(CheatsConfig.SlipperyMultiplier, "Boost amount", 1f, 3f, "0.00", "×", phys && CheatsConfig.SlipperyBoost.Value);
        });
        Card("Control", null, () =>
        {
            Switch(CheatsConfig.TractionControl, "Traction control", "Eases off the throttle when wheels spin", phys);
            SliderRow(CheatsConfig.TractionTargetSlip, "Kicks in at slip", 0.5f, 1.5f, "0.00", "", phys && CheatsConfig.TractionControl.Value);
            SliderRow(CheatsConfig.TractionStrength, "Strength", 0f, 1f, "0.00", "", phys && CheatsConfig.TractionControl.Value, percent: true);
            Switch(CheatsConfig.AntiRoll, "Anti-rollover", "Pushes the car upright past ~12° of lean", phys);
            SliderRow(CheatsConfig.AntiRollStrength, "Strength ", 0f, 3f, "0.0", "", phys && CheatsConfig.AntiRoll.Value);
            SliderRow(CheatsConfig.Downforce, "Downforce", 0f, 1f, "0.00", " g", phys);
        });
        Card("Live", null, () =>
        {
            var car = GameState.LocalCar;
            KeyValue("Car", car == null ? "not found — load into a world" : car.gameObject.name.Replace("(Clone)", ""));
            KeyValue("Surface", Assists.Surface);
            Bar("Wheel slip", Mathf.Clamp01(Assists.MaxNormalizedSlip / 1.5f), Assists.MaxNormalizedSlip > 1f ? Theme.Bad : Theme.Good, Assists.MaxNormalizedSlip.ToString("0.00"));
            Bar("Throttle", Assists.RawThrottle / 10000f, Theme.Info, $"{Assists.RawThrottle / 100}%");
            Bar("Traction cut", 1f - Assists.TcFactor, Theme.Warn, $"{(1f - Assists.TcFactor) * 100f:0}%");
            KeyValue("Roll", $"{Assists.RollDegrees:0}°");
        });
    }

    private static void DrawCheats()
    {
        bool phys = GameState.PhysicsAllowed;
        Card("Your car", phys ? null : "Car cheats need you to be solo or hosting.", () =>
        {
            Switch(CheatsConfig.NoDamage, "No damage", null, phys);
            SliderRow(CheatsConfig.ExtraAcceleration, "Extra acceleration", 0f, 20f, "0.0", " m/s²", phys);
            Chips(3,
                ("Repair", () => { Assists.RepairNow(); SetStatus("Repaired car"); }, phys),
                ($"Flip upright ({CheatsConfig.FlipKey.Value})", () => { Assists.FlipUpright(); SetStatus("Flipped car"); }, phys),
                Cmd("Reset car", "ResetPlayer"));
            Chips(3, Cmd("Teleport to pin", "TeleportAtPin"), Cmd("Go to merchant", "GoToMerchant"), Cmd("Headlamp", "ToggleHeadlamp"));
        });
        Card("Movement", phys ? null : "Movement cheats need you to be solo or hosting.", () =>
        {
            var prevColor = GUI.color;
            if (ChipButton(new Rect(Inset, Y, IW, 32f), Movement.Flying ? $"FLYING — click or {CheatsConfig.FlyKey.Value} to land" : $"Fly mode ({CheatsConfig.FlyKey.Value})", phys))
                Movement.SetFlying(!Movement.Flying);
            GUI.color = prevColor;
            Y += 38f;
            Small($"Fly: WASD move, Space up, Ctrl down, Shift fast.   Nitro: hold {CheatsConfig.NitroKey.Value}.   Super jump: {CheatsConfig.JumpKey.Value}.");
            SliderRow(CheatsConfig.FlySpeed, "Fly speed", 5f, 80f, "0", " m/s", phys);
            SliderRow(CheatsConfig.NitroAcceleration, "Nitro power", 5f, 60f, "0", " m/s²", phys);
            SliderRow(CheatsConfig.JumpSpeed, "Jump height", 3f, 30f, "0", " m/s", phys);
            Chips(3,
                ("Launch me", () => { Movement.QueueLaunch(15f); SetStatus("Launched"); }, phys),
                ("Full stop", () => { Movement.QueueStop(); SetStatus("Stopped"); }, phys),
                ("Fling everyone else", () => SetStatus(Movement.FlingOthers(14f)), GameState.IsAuthority && GameState.Runner != null));
        });
        Card("Session", null, () =>
        {
            // Map & HUD's setting, through core (not shown while that mod is switched off).
            if (DrivingHud.ShowSetting != null)
                Switch(DrivingHud.ShowSetting, $"Driving HUD ({DrivingHud.KeySetting?.Value})", "Speed, RPM, gear, power, torque and slip", true);
            Switch(CheatsConfig.BlockAchievements, "Block achievements after cheating",
                AdvantageFlag.UsedThisSession ? "Cheats used this session: achievements paused until restart" : "No cheats used yet this session", true);
        });
        Card("Unlocks", "Saved to your profile.", () =>
        {
            Chips(3, Cmd("All cars", "UnlockAllCars"), Cmd("All upgrades", "UnlockAllUpgrades"), Cmd("All cosmetics", "UnlockAllCosmetics"));
            Chips(3, Cmd("All liveries", "UnlockAllLiveries"), Cmd("All areas", "UnlockAllAreas"), Cmd("All cabins", "UnlockAllCabins"));
            Chips(2, Cmd("Tool slots", "UnlockAllToolSlots"), Cmd("EVERYTHING", "UnlockAllAll"));
        });
        Card("Camera & screen", null, () =>
        {
            Chips(3, Cmd("Free camera", "ActivateFreeCamera"), Cmd("No cam collision", "DisableCameraCollisions"), Cmd("Hide/show UI", "ShowUI"));
            Chips(3, Cmd("FPS graph", "ToggleFPSGraph"), Cmd("Tree distance", "ToggleTreeDistanceLimit"), Cmd("Ping", "GetPing"));
        });
        Card("Dev console", "The game's own console. ` or \\ toggles it once open.", () =>
        {
            Switch(CheatsConfig.ForceDevConsole, "Unlock console for me", "Marks only you as a dev-team member", true);
            Chips(1, ("Open dev console", () => SetStatus(Cheats.TryOpenConsole()), true));
            Small(Cheats.ConsoleStatus());
        });
    }

    private static void DrawWorld()
    {
        if (GameState.IsPublicGuest)
            Banner("Public lobby: commands that change everyone's world are disabled here. They work solo, hosting, or in friends' private sessions.", Theme.Warn);

        Card("Time", null, () =>
        {
            int hh = (int)_hour, mm = (int)((_hour - hh) * 60f);
            _hour = RawSlider("Time of day", _hour, 0f, 23.99f, $"{hh:00}:{mm:00}", true);
            Chips(3, Cmd($"Set {hh:00}:{mm:00}", $"SetTime {hh} {mm}"), Cmd("Pause time", "PauseTime true"), Cmd("Resume time", "PauseTime false"));
            _timeScale = RawSlider("Game speed", _timeScale, 0.1f, 2f, $"{_timeScale:0.0}×", true);
            Chips(2, Cmd($"Apply {_timeScale:0.0}×", $"SetTimeScale {_timeScale:0.0}"), Cmd("Normal speed", "SetTimeScale 1"));
        });
        Card("Weather & sky", null, () =>
        {
            Chips(3, Cmd("Lovely day", "LovelyDay"), Cmd("Aurora on", "SetAurora true"), Cmd("Aurora off", "SetAurora false"));
            _weather = SimpleMenu.Text("Weather name", _weather ?? "", 40, "A name from List weathers.");
            SimpleMenu.Button("Set the weather", _weather?.Trim(), null, () => SetStatus(Cheats.Run("SetWeather " + _weather.Trim())),
                !string.IsNullOrWhiteSpace(_weather) && !Cheats.IsBlockedHere("SetWeather"));
            Chips(2, Cmd("List weathers (log)", "ListWeathers"), Cmd("Current weather (log)", "GetCurrentWeather"));
        });
        Card("Map", null, () =>
        {
            Chips(3, Cmd("Reveal map", "RevealFogOfWar"), Cmd("Reveal POIs", "RevealUnknownPoiIcons"), Cmd("Reset fog", "ResetFogOfWar"));
            Chips(3, Cmd("Go to portal", "GoToPortalEntry"), Cmd("Portal exit", "GoToPortalExit"), Cmd("Meteorites (log)", "ListMeteoritesLocations"));
        });
        Card("Car look", null, () =>
        {
            Chips(2, ("Clean car", () => { foreach (var c in new[] { "SetDustiness 0", "SetMuddiness 0", "SetSnowiness 0", "SetWetness 0" }) SetStatus(Cheats.Run(c)); }, !Cheats.IsBlockedHere("SetMuddiness")),
                Cmd("Mud monster", "SetMuddiness 1"));
            Chips(3, Cmd("Dusty", "SetDustiness 1"), Cmd("Snowy", "SetSnowiness 1"), Cmd("Soaked", "SetWetness 1"));
        });
        Card("Fun", null, () =>
        {
            Chips(3, Cmd("Meteor shower", "SpawnAllMeteorites"), Cmd("Meteorite here", "SpawnMeteoriteHere"), Cmd("Scare animals", "ScareAllAnimals"));
            Chips(3, Cmd("Switch camera", "SwitchCamera"), Cmd("Keep rolling", "LetsKeepRolling"), Cmd("Calm animals", "IdleAllAnimals"));
        });
        Card("Radio", null, () =>
        {
            Chips(3, Cmd("Play", "PlayMusic"), Cmd("Pause", "PauseMusic"), Cmd("Stop", "StopMusic"));
            Chips(4, Cmd("< Track", "PreviousTrack"), Cmd("Track >", "NextTrack"), Cmd("< Album", "PreviousAlbum"), Cmd("Album >", "NextAlbum"));
        });
    }

    private static void DrawVehicles()
    {
        bool phys = GameState.PhysicsAllowed;
        Card("Your car", null, () =>
        {
            Chips(3,
                ("Push (kick)", () => SetStatus(Vehicles.KickPush()), GameState.LocalCar != null),
                ("Refill tools", () => SetStatus(Vehicles.RefillTools()), GameState.LocalCar != null),
                ("Repair", () => { Assists.RepairNow(); SetStatus("Repaired car"); }, phys));
        });
        Card("Towing", "Hook your winch to the nearest other player, or let go.", () =>
        {
            Chips(2,
                ("Tow nearest player", () => SetStatus(Vehicles.TowNearestPlayer()), GameState.LocalCar != null),
                ("Detach winch", () => SetStatus(Vehicles.DetachWinches()), GameState.LocalCar != null));
            Switch(CheatsConfig.LongWinch, "Long winch", "Attach from much farther away", true);
        });

        Card("Presets", "Saves your car + all assist, cheat and movement settings under a name.", () =>
        {
            var current = Presets.CurrentCar();
            KeyValue("Current car", current?.Name ?? "unknown");
            Chips(1, ("Random car", () => SetStatus(Presets.RandomCar()), ItemCatalog.Cars().Any()));
            _presetName = SimpleMenu.Text("Preset name", _presetName ?? "", 40, null);
            SimpleMenu.Button("Save preset", null, "Saves your car and settings under that name.", () => SetStatus(Presets.Save(_presetName)),
                !string.IsNullOrWhiteSpace(_presetName));
            foreach (var p in Presets.All.ToList())
            {
                var preset = p;
                SimpleMenu.Actions($"{p.Name}  <color=#8C96A6>{p.CarName ?? "settings only"}</color>",
                    ("Load", () => SetStatus(Presets.Apply(preset)), true),
                    ("Delete", () => SetStatus(Presets.Delete(preset)), true));
            }
            if (Presets.All.Count == 0) Small("No presets yet.");
        });

        var cars = ItemCatalog.Cars().ToList();
        Card("Switch car / spawn cars", cars.Count == 0
            ? "Load into a world to list cars."
            : "Switch: swap your car right here. Spawn: drop a driverless copy in front of you (host only).", () =>
        {
            if (cars.Count == 0) return;
            _carFilter = SimpleMenu.Text("Filter", _carFilter ?? "", 40, "Shows only the cars whose name has this in it.");
            SimpleMenu.Button("Remove spawned cars", Vehicles.DummyCount.ToString(), null, () => SetStatus(Vehicles.DespawnDummies()), Vehicles.DummyCount > 0);
            bool canSpawn = GameState.IsAuthority && GameState.LocalCar != null;
            foreach (var car in cars)
            {
                if (!string.IsNullOrEmpty(_carFilter) && car.Name.IndexOf(_carFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var c = car;
                SimpleMenu.Actions(car.Name,
                    ("Switch", () => SetStatus(Vehicles.SwitchCar(c)), true),
                    ("Spawn", () => SetStatus(Vehicles.SpawnDummy(c)), canSpawn));
            }
        });
    }

    private static string _pebbleAmount = "10000";

    private static string _itemFilter = "";

    private static int _itemType = -1;

    private static readonly string[] ItemTypeNames = { "All", "Upgrades", "Cosmetics", "Liveries", "Items", "Cars" };

    private static void DrawItems()
    {
        Card("Pebbles", "The game's money. Saved to your own profile, so it works anywhere.", () =>
        {
            int balance = Economy.Balance;
            KeyValue("Balance", balance < 0 ? "— (load into a world or the garage)" : $"<b>{balance:N0}</b>");
            Chips(4,
                ("+1,000", () => SetStatus(Economy.AddPebbles(1000)), balance >= 0),
                ("+10,000", () => SetStatus(Economy.AddPebbles(10000)), balance >= 0),
                ("+100,000", () => SetStatus(Economy.AddPebbles(100000)), balance >= 0),
                ("Reset to 0", () => SetStatus(Economy.SetPebbles(0)), balance > 0));
            _pebbleAmount = SimpleMenu.Text("Amount", _pebbleAmount ?? "", 12, null);
            bool ok = int.TryParse((_pebbleAmount ?? "").Replace(",", ""), out int amount) && balance >= 0;
            SimpleMenu.Actions("Amount", ("Add", () => SetStatus(Economy.AddPebbles(amount)), ok), ("Set to", () => SetStatus(Economy.SetPebbles(amount)), ok));
        });

        var all = ItemCatalog.All;
        Card("Item catalog", all.Count == 0 ? "Load into a world or the garage to read the catalog." : "Gives the item to your inventory. Cars are easier with the Unlocks card under Cheats, above.", () =>
        {
            if (all.Count == 0) return;
            _itemFilter = SimpleMenu.Text("Filter", _itemFilter ?? "", 40, "Shows only the items whose name has this in it.");
            SimpleMenu.Choice("Type", null, _itemType + 1, ItemTypeNames, i => _itemType = i - 1);

            int shown = 0;
            foreach (var item in all)
            {
                if (!MatchesType(item, _itemType)) continue;
                if (!string.IsNullOrEmpty(_itemFilter) && item.Name.IndexOf(_itemFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++shown > 150) { Small("More items: use the filter."); break; }
                bool owned = Economy.Owns(item);
                var it = item;
                string label = (owned ? $"{item.Name}  <color=#4CC38A>owned</color>" : item.Name) + $"  <color=#8C96A6>{item.Type}" +
                               (item.Price > 0 ? $"  ·  {item.Price:N0}" : "") + "</color>";
                SimpleMenu.ActionsFor("item:" + item.Name + "|" + item.Type, label, ("Give", () => SetStatus(Economy.Give(it)), true));
            }
            if (shown == 0) Small("No items match.");
        });
    }

    private static bool MatchesType(ItemCatalog.Item item, int filter) => filter switch
    {
        0 => item.Type == InventoryItemScriptableObject.ItemType.VehicleUpgrade,
        1 => item.Type == InventoryItemScriptableObject.ItemType.VehicleCosmetic,
        2 => item.Type is InventoryItemScriptableObject.ItemType.Livery or InventoryItemScriptableObject.ItemType.SpecialLivery,
        3 => item.Type == InventoryItemScriptableObject.ItemType.UsableItem,
        4 => item.Type == InventoryItemScriptableObject.ItemType.Vehicle,
        _ => true,
    };

    private static void DrawCommands()
    {
        Card("Run a command", "Type a command with its arguments, e.g. SetTime 14 30. Press Use on any row to fill it in.", () =>
        {
            _command = SimpleMenu.Text("Command", _command ?? "", 160, "A command and its arguments.");
            SimpleMenu.Button("Run it", null, null, () => SetStatus(Cheats.Run(_command)), !string.IsNullOrWhiteSpace(_command));
            _filter = SimpleMenu.Text("Filter", _filter ?? "", 40, "Shows only the commands whose name has this in it.");
        });

        var names = Cheats.Commands.Keys
            .Where(n => string.IsNullOrEmpty(_filter) || n.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(n => n).ToList();
        Card("All commands", $"{names.Count} shown", () =>
        {
            foreach (var n in names)
            {
                bool blocked = Cheats.IsBlockedHere(n);
                string label = (blocked ? $"<color=#6B7482>{n}</color>" : n) +
                               (Cheats.IsAffectingOthers(n) ? (blocked ? "  <color=#F2B33D>blocked here</color>" : "  <color=#8C96A6>affects everyone</color>") : "");
                string name = n;
                SimpleMenu.Actions(label, ("Use", () => _command = name + " ", true));
            }
        });
    }

    private static (string, Action, bool) Cmd(string label, string command)
    {
        string name = command.Split(' ')[0];
        return (label, () => SetStatus(Cheats.Run(command)), !Cheats.IsBlockedHere(name));
    }
}
