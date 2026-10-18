using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using OverTheHillPlus.Features.Cheats.Events;
using OverTheHillPlus.HostPowers;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.CreateScreen;

/// <summary>
/// Create Server rows for the host's world: starting weather, starting time of day and tools for everyone. The game
/// only offers these as actions inside a running world (HostWorld), so they are stored as settings and applied once
/// the server created from that screen is up: weather and time a few seconds after the world is ready, tools to every
/// player's car (again to each car that joins later, while that server runs). Host only; marks cheats as used, like
/// the HostWorld actions themselves.
/// </summary>
internal sealed class HostStartFeature : IFeature
{
    private const string Section = "Server Start";

    internal static ConfigEntry<string> StartWeather;
    internal static ConfigEntry<int> StartHour;
    internal static ConfigEntry<bool> ToolsForEveryone;

    // label -> weather-name keywords (first match wins, matched against the game's weather names)
    private static readonly (string Label, string Keys)[] Weathers =
    {
        ("GAME DEFAULT", null),
        ("CLEAR", "clear,sunny,fair,lovely"),
        ("CLOUDY", "overcast,cloud"),
        ("FOG", "fog,mist,haze"),
        ("RAIN", "drizzle,rain,shower"),
        ("STORM", "thunder,storm,heavy rain"),
        ("SNOW", "snow,blizzard"),
    };

    private static readonly int[] Hours = { -1, 6, 9, 12, 15, 18, 21, 0 };

    private static bool _pending;
    private static float _pendingSince, _readySince = -1f, _nextCheck;
    private static IntPtr _toolsRunner;
    private static float _nextTools;
    private static readonly HashSet<IntPtr> ToolCars = new();

    public void Register()
    {
        var f = HostPowersConfig.File;
        StartWeather = f.Bind(Section, "StartWeather", "", "Weather set when a server you create from the Create Server screen starts: Clear, Cloudy, Fog, Rain, Storm, Snow (empty = the game's).");
        StartHour = f.Bind(Section, "StartHour", -1, new ConfigDescription("Time of day (hour) set when a server you create starts (-1 = the game's).", new AcceptableValueRange<int>(-1, 23)));
        ToolsForEveryone = f.Bind(Section, "ToolsForEveryone", false, "Servers you create: give every player's car all tools (also players who join later).");

        // Drawn by Server Creator+ on the Create Server screen (through core, so nothing here depends on that mod).
        ServerSettings.AddRow(new ServerSettingRow
        {
            Id = "weather",
            Label = "Starting Weather",
            Order = 110,
            ModId = HostPowersPlugin.ModId,
            Private = true,
            Options = () => Array.ConvertAll(Weathers, w => w.Label),
            Selected = () =>
            {
                for (int i = 1; i < Weathers.Length; i++)
                    if (string.Equals(Weathers[i].Label, StartWeather.Value, StringComparison.OrdinalIgnoreCase)) return i;
                return 0;
            },
            Choose = i => StartWeather.Value = i <= 0 || i >= Weathers.Length ? "" : Title(Weathers[i].Label),
        });
        ServerSettings.AddRow(new ServerSettingRow
        {
            Id = "hour",
            Label = "Starting Time",
            Order = 120,
            ModId = HostPowersPlugin.ModId,
            Private = true,
            Options = () => Array.ConvertAll(Hours, h => h < 0 ? "GAME DEFAULT" : $"{h:00}:00"),
            Selected = () => Math.Max(0, Array.IndexOf(Hours, StartHour.Value)),
            Choose = i => StartHour.Value = i >= 0 && i < Hours.Length ? Hours[i] : -1,
        });
        var tools = ServerSettingRow.Toggle("tools", "Tools for Everyone", 130, HostPowersPlugin.ModId, () => ToolsForEveryone);
        tools.Private = true;
        ServerSettings.AddRow(tools);

        ServerSettings.ServerCreated += () =>
        {
            _pending = true;
            _pendingSince = Time.unscaledTime;
            _readySince = -1f;
            _toolsRunner = IntPtr.Zero;
            ToolCars.Clear();
        };
        Registry.OnTick(Tick);
    }

    private static string Title(string s) => s.Length == 0 ? s : s.Substring(0, 1) + s.Substring(1).ToLowerInvariant();

    private static void Tick()
    {
        if (!_pending && _toolsRunner == IntPtr.Zero) return;
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 1f;
        if (_pending) ApplyStart();
        if (_toolsRunner != IntPtr.Zero) KeepTools();
    }

    /// <summary>Once the created server's world is ready (host side): weather, time, and start handing out tools.</summary>
    private static void ApplyStart()
    {
        if (Time.unscaledTime - _pendingSince > 300f) { _pending = false; return; } // never got going
        if (!HostTools.Available || !WorldReady()) { _readySince = -1f; return; }
        if (_readySince < 0f) { _readySince = Time.unscaledTime; return; }
        if (Time.unscaledTime - _readySince < 3f) return; // let the game finish its own start-up first
        _pending = false;

        string keys = null;
        foreach (var w in Weathers)
            if (w.Keys != null && string.Equals(w.Label, StartWeather.Value, StringComparison.OrdinalIgnoreCase)) keys = w.Keys;
        if (keys != null)
        {
            int id = EventModes.FindWeather(keys);
            HostPowersPlugin.Logger.LogInfo(id >= 0
                ? "[ServerStart] " + HostWorld.SetWeather(id)
                : $"[ServerStart] No weather matching '{StartWeather.Value}' in this world");
        }
        if (StartHour.Value >= 0 && StartHour.Value <= 23)
            HostPowersPlugin.Logger.LogInfo("[ServerStart] " + HostWorld.SetTime(StartHour.Value, 0));
        if (ToolsForEveryone.Value)
        {
            _toolsRunner = GameState.Runner != null ? GameState.Runner.Pointer : IntPtr.Zero;
            _nextTools = 0f;
            ToolCars.Clear();
        }
    }

    private static bool WorldReady()
    {
        try
        {
            var tm = TimeManager.Instance;
            return tm != null && tm.Object != null && tm.Object.IsValid;
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "HostStartFeature.WorldReady"); return false; }
    }

    /// <summary>While that server runs: every player car seen for the first time gets all tools.</summary>
    private static void KeepTools()
    {
        var runner = GameState.Runner;
        if (runner == null || runner.Pointer != _toolsRunner || !HostTools.Available || !ToolsForEveryone.Value)
        {
            _toolsRunner = IntPtr.Zero;
            ToolCars.Clear();
            return;
        }
        if (Time.unscaledTime < _nextTools) return;
        _nextTools = Time.unscaledTime + 5f;
        bool newCar = false;
        var cars = new List<IntPtr>();
        foreach (var p in HostTools.Players)
        {
            if (p.Car == null) continue;
            cars.Add(p.Car.Pointer);
            if (!ToolCars.Contains(p.Car.Pointer)) newCar = true;
        }
        if (!newCar) return;
        string result = HostWorld.GiveAllTools();
        HostPowersPlugin.Logger.LogInfo("[ServerStart] " + result);
        if (result.StartsWith("Failed") || result.StartsWith("Blocked")) return; // try again in a moment
        foreach (var c in cars) ToolCars.Add(c);
    }
}
