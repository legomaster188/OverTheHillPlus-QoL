using System;
using System.Linq;
using BepInEx.Configuration;
using OverTheHillPlus.HostPowers;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Events;

/// <summary>
/// MODS > Host Powers > Events (host only): meteor showers and single meteorites, one-click event modes (Night Race,
/// Meteor Hunt, Aurora Night, Storm) and lobby car rules (same car for everyone, damage multiplier, grip).
/// Everything goes through the game's own host-side calls, so it works on players without the mod. Nothing here runs
/// as a guest (<see cref="HostTools.Available"/> needs authority), and every action marks cheats as used.
/// </summary>
internal sealed class EventsFeature : IFeature
{
    internal const string Tab = "Events";

    internal static ConfigEntry<int> ShowerSize;
    internal static ConfigEntry<bool> ShowerFalling;
    internal static ConfigEntry<float> SingleDelay;
    internal static ConfigEntry<float> SingleDistance;
    internal static ConfigEntry<bool> HuntRevealPins;
    internal static ConfigEntry<float> RevealDelay;
    internal static ConfigEntry<string> NightRaceWeather;
    internal static ConfigEntry<string> AuroraWeather;
    internal static ConfigEntry<string> StormWeather;
    internal static ConfigEntry<bool> EnforceCar;
    internal static ConfigEntry<bool> DamageRule;
    internal static ConfigEntry<float> DamageMultiplier;
    internal static ConfigEntry<bool> GripRule;
    internal static ConfigEntry<float> GripMultiplier;

    private static readonly EventModes.Mode[] Modes =
        { EventModes.Mode.NightRace, EventModes.Mode.MeteorHunt, EventModes.Mode.AuroraNight, EventModes.Mode.Storm };

    private static float _revealAt = -1f;
    private static IntPtr _runner;
    private static float _runnerLostAt = -1f;
    private static string _carFilter = "";

    public void Register()
    {
        var f = HostPowersConfig.File;
        const string s = "Events";
        ShowerSize = f.Bind(s, "MeteorSize", -1, new ConfigDescription("Meteorite size for showers and single meteorites (-1 = the game's own pick, 0 = very small .. 6 = very huge).", new AcceptableValueRange<int>(-1, 6)));
        ShowerFalling = f.Bind(s, "ShowerFalling", true, "Meteor shower meteorites fall from the sky (off = they appear on the ground).");
        SingleDelay = f.Bind(s, "SingleMeteoriteDelay", 3f, new ConfigDescription("Seconds before a single meteorite lands.", new AcceptableValueRange<float>(0f, 15f)));
        SingleDistance = f.Bind(s, "SingleMeteoriteDistance", 40f, new ConfigDescription("How far ahead of your car 'in front of me' drops a meteorite (metres).", new AcceptableValueRange<float>(10f, 200f)));
        HuntRevealPins = f.Bind(s, "MeteorHuntRevealPins", true, "Meteor Hunt: put each player's map pin on the meteorite nearest to them.");
        RevealDelay = f.Bind(s, "MeteorHuntRevealDelay", 12f, new ConfigDescription("Meteor Hunt: seconds after the shower before the pins are placed (meteorites take up to 10 s to land).", new AcceptableValueRange<float>(1f, 60f)));
        NightRaceWeather = f.Bind(s, "NightRaceWeather", "fog,mist,haze,overcast,cloud", "Night Race: weather keywords, first match wins (comma-separated, matched against the weather names).");
        AuroraWeather = f.Bind(s, "AuroraNightWeather", "clear,sunny,fair,lovely", "Aurora Night: weather keywords, first match wins.");
        StormWeather = f.Bind(s, "StormWeather", "thunder,storm,blizzard,heavy rain,rain,snow", "Storm: weather keywords, first match wins.");
        EnforceCar = f.Bind(s, "EnforceSameCar", true, "Same car for everyone: also swap players who join later or change car in the garage.");
        DamageRule = f.Bind(s, "LobbyDamageMultiplierOn", false, "Damage multiplier for every car in the session (host). Always off at game start.");
        DamageMultiplier = f.Bind(s, "LobbyDamageMultiplier", 2f, new ConfigDescription("Damage taken from impacts, for every car (1 = normal).", new AcceptableValueRange<float>(0f, 10f)));
        GripRule = f.Bind(s, "LobbyGripOn", false, "Tyre grip for every car in the session (host, experimental). Always off at game start.");
        GripMultiplier = f.Bind(s, "LobbyGripMultiplier", 0.6f, new ConfigDescription("Tyre grip for every car (1 = normal).", new AcceptableValueRange<float>(0.2f, 3f)));

        // Session-wide rules never switch themselves on in a later game.
        DamageRule.Value = false;
        GripRule.Value = false;

        Registry.Section(Registry.PlusMode, Tab, DrawMenu, 0);
        Registry.OnTick(Tick);
    }

    internal static void ScheduleReveal(float delay) => _revealAt = Time.unscaledTime + Mathf.Max(0.5f, delay);

    private static void Tick()
    {
        // A new session (or leaving one) drops everything that referred to the old world.
        // A runner missing for a moment (scene load) isn't a new session: only a different runner, or none for 5 s, is.
        IntPtr runner = IntPtr.Zero;
        try { runner = GameState.Runner == null ? IntPtr.Zero : GameState.Runner.Pointer; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "EventsFeature.Tick"); }
        if (runner == IntPtr.Zero && _runner != IntPtr.Zero)
        {
            if (_runnerLostAt < 0f) _runnerLostAt = Time.unscaledTime;
            if (Time.unscaledTime - _runnerLostAt < 5f) return;
        }
        else _runnerLostAt = -1f;
        if (runner != _runner)
        {
            _runnerLostAt = -1f;
            _runner = runner;
            EventModes.Forget(restoreLockConfig: true);
            LobbyCarRules.Forget();
            _revealAt = -1f;
            // Session-wide rules are per session: never carry them into the next one.
            DamageRule.Value = false;
            GripRule.Value = false;
        }

        LobbyCarRules.Tick();

        if (_revealAt > 0f && Time.unscaledTime >= _revealAt)
        {
            _revealAt = -1f;
            if (HostTools.Available) Menu.SetStatus(MeteorEvents.RevealPins());
        }
    }

    // ---- menu ----

    private static void Run(Func<string> action) => Menu.SetStatus(action());

    private static void DrawMenu()
    {
        bool host = HostTools.Available;
        if (!host)
            Menu.Banner("Events are host powers: host a session (or play solo) to use them. They work on players without the mod.", Theme.Warn);
        else if (GameState.Runner == null || TimeManager.Instance == null)
            Menu.Banner("Load into a world to start events.", Theme.Info);

        DrawMeteors(host);
        DrawModes(host);
        DrawCarRules(host);
    }

    private static void DrawMeteors(bool host)
    {
        Menu.Card("Meteor shower", "Replaces every current meteorite with a fresh one at each spawn point on the map.", () =>
        {
            Menu.KeyValue("Size", MeteorEvents.SizeName(ShowerSize.Value));
            var sizes = new (string, Action, bool)[MeteorEvents.SizeNames.Length + 1];
            sizes[0] = (ShowerSize.Value < 0 ? "• Random" : "Random", () => ShowerSize.Value = -1, true);
            for (int i = 0; i < MeteorEvents.SizeNames.Length; i++)
            {
                int size = i;
                string label = MeteorEvents.SizeNames[i];
                sizes[i + 1] = (ShowerSize.Value == size ? "• " + label : label, () => ShowerSize.Value = size, true);
            }
            Menu.Chips(4, sizes);
            Menu.Switch(ShowerFalling, "Falling from the sky", "Off: they appear on the ground straight away", host);
            Menu.Chips(1, ("Start meteor shower", () => Run(() => MeteorEvents.Shower(ShowerSize.Value, ShowerFalling.Value)), host));
        });

        Menu.Card("Single meteorite", "Uses the size above. Big ones need several players to pull out.", () =>
        {
            Menu.SliderRow(SingleDelay, "Landing delay", 0f, 15f, "0.0", " s", host);
            Menu.SliderRow(SingleDistance, "Distance ahead", 10f, 200f, "0", " m", host);
            Menu.Chips(2,
                ("At my map pin", () => Run(() => MeteorEvents.SpawnAtPin(ShowerSize.Value, SingleDelay.Value)), host),
                ("In front of me", () => Run(() => MeteorEvents.SpawnInFront(ShowerSize.Value, SingleDelay.Value, SingleDistance.Value)), host));
        });

        Menu.Card("Natural meteorites", "The game's own respawn loop for meteorites.", () =>
        {
            Menu.Chips(3,
                ("Pause", () => Run(() => MeteorEvents.PauseNatural(true, false)), host),
                ("Pause and clear", () => Run(() => MeteorEvents.PauseNatural(true, true)), host),
                ("Resume", () => Run(() => MeteorEvents.PauseNatural(false, false)), host));
            Menu.Chips(1, ("Pin everyone's nearest meteorite now", () => Run(MeteorEvents.RevealPins), host));
        });
    }

    private static void DrawModes(bool host)
    {
        Menu.Card("Event modes", "One click sets up the world for everyone. End event puts time, weather, wind and aurora back.", () =>
        {
            var active = EventModes.Active;
            Menu.KeyValue("Running", active == EventModes.Mode.None ? "none" : $"{EventModes.ActiveName} ({EventModes.Detail})");
            string L(EventModes.Mode m) => active == m ? "• " + EventModes.NameOf(m) : EventModes.NameOf(m);
            (string, Action, bool) Chip(EventModes.Mode m) => (L(m), () => Run(() => EventModes.Start(m)), host);
            Menu.Chips(2, Chip(EventModes.Mode.NightRace), Chip(EventModes.Mode.MeteorHunt),
                Chip(EventModes.Mode.AuroraNight), Chip(EventModes.Mode.Storm));
            Menu.Chips(1, ("End event", () => Run(EventModes.End), host && active != EventModes.Mode.None));
            foreach (var m in Modes) Menu.Small($"{EventModes.NameOf(m)}: {EventModes.Describe(m)}");
            Menu.Switch(HuntRevealPins, "Meteor Hunt pins", "Move each player's map pin onto their nearest meteorite", host);
            Menu.SliderRow(RevealDelay, "Pins after", 1f, 60f, "0", " s", host && HuntRevealPins.Value);
            if (host && HostWorld.Weathers.Count > 0)
                Menu.Small("Weather picks: night race " + Pick(NightRaceWeather.Value) + ", aurora " + Pick(AuroraWeather.Value) + ", storm " + Pick(StormWeather.Value) + ". Change the keywords in the config (Events section).");
        });
    }

    private static string Pick(string keys)
    {
        int id = EventModes.FindWeather(keys);
        return id < 0 ? "(none found)" : HostWorld.WeatherName(id);
    }

    private static (string, Action, bool)[] _carChips = Array.Empty<(string, Action, bool)>();
    private static (string filter, uint target, bool host, int count) _carChipsKey = (null, 0, false, -1);

    /// <summary>The car buttons, rebuilt only when the filter, the chosen car or the list changes (not per GUI event).</summary>
    private static (string, Action, bool)[] CarChips(System.Collections.Generic.IReadOnlyList<ItemCatalog.Item> cars, uint target, bool host)
    {
        var key = (_carFilter ?? "", target, host, cars.Count);
        if (key == _carChipsKey) return _carChips;
        _carChipsKey = key;
        string filter = (_carFilter ?? "").Trim();
        _carChips = cars.Where(c => filter.Length == 0 || c.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(24)
            .Select(c => (c.Id == target ? "• " + c.Name : c.Name, (Action)(() => Run(() => LobbyCarRules.EveryoneIn(c))), host))
            .ToArray();
        return _carChips;
    }

    private static void DrawCarRules(bool host)
    {
        Menu.Card("Same car for everyone", "Swaps every player's car where they stand (stock upgrades). Base-game cars only.", () =>
        {
            var cars = LobbyCarRules.Cars;
            uint target = LobbyCarRules.TargetCar;
            Menu.KeyValue("Rule", target == 0 ? "off" : $"everyone in the {LobbyCarRules.TargetName}"
                + (LobbyCarRules.PendingCount > 0 ? $" ({LobbyCarRules.PendingCount} swap(s) queued)" : ""));
            if (cars.Count == 0) { Menu.Small("Load into a world to list cars."); }
            else
            {
                if (cars.Count > 12) _carFilter = Menu.TextRow("Filter", _carFilter, 24);
                var shown = CarChips(cars, target, host);
                if (shown.Length > 0) Menu.Chips(3, shown);
                else Menu.Small("No car matches the filter.");
            }
            Menu.Switch(EnforceCar, "Keep enforcing", "Also swap players who join later or change car", host);
            Menu.Chips(1, ("Give everyone their own car back", () => Run(LobbyCarRules.GiveCarsBack),
                host && (target != 0 || LobbyCarRules.RememberedCount > 0)));
        });

        Menu.Card("Damage for everyone", "Scales impact damage on every player's car. Lobby god mode (Host tab) still wins.", () =>
        {
            Menu.Switch(DamageRule, "Damage multiplier", "Original values come back when switched off", host);
            Menu.SliderRow(DamageMultiplier, "Damage", 0f, 10f, "0.0", "x", host && DamageRule.Value);
            Menu.Chips(4,
                ("Glass 5x", () => { DamageMultiplier.Value = 5f; DamageRule.Value = true; }, host),
                ("Hard 2x", () => { DamageMultiplier.Value = 2f; DamageRule.Value = true; }, host),
                ("Soft 0.5x", () => { DamageMultiplier.Value = 0.5f; DamageRule.Value = true; }, host),
                ("Off", () => DamageRule.Value = false, host));
            Menu.KeyValue("Cars", DamageRule.Value && host ? $"{LobbyCarRules.DamageCars} affected" : "-");
        });

        Menu.Card("Grip for everyone (experimental)", "Tyre grip on every player's car. Guests predict their own car with stock grip, so they may see small corrections.", () =>
        {
            Menu.Switch(GripRule, "Lobby grip", "Stacks with each driver's own Driving > grip on your car", host);
            Menu.SliderRow(GripMultiplier, "Grip", 0.2f, 3f, "0.00", "", host && GripRule.Value, percent: true);
            Menu.Chips(4,
                ("Ice 30%", () => { GripMultiplier.Value = 0.3f; GripRule.Value = true; }, host),
                ("Slick 60%", () => { GripMultiplier.Value = 0.6f; GripRule.Value = true; }, host),
                ("Glue 150%", () => { GripMultiplier.Value = 1.5f; GripRule.Value = true; }, host),
                ("Off", () => GripRule.Value = false, host));
            Menu.KeyValue("Cars", GripRule.Value && host ? $"{LobbyCarRules.GripCars} affected" : "-");
        });
    }
}
