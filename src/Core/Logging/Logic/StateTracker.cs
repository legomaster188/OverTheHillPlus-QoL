using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Logging;

/// <summary>What the game is doing at one moment: sampled by Lifecycle about once a second.</summary>
internal sealed class StateSample
{
    /// <summary>MENU, SOLO, HOST, FRIENDS or PUBLIC.</summary>
    public string Session = "MENU";
    /// <summary>"hosting", "single-player", ... (GameState.SessionDescription).</summary>
    public string SessionText = "";
    public string Map = "";
    /// <summary>The local player's car ("" = none) and whether it is one of the modded cars.</summary>
    public string Car = "";
    public bool CarModded;
    /// <summary>The top of the game's state stack ("" = unknown).</summary>
    public string GameState = "";
    /// <summary>Player slot to name, for the players in the session.</summary>
    public SortedDictionary<int, string> Players = new();
}

/// <summary>
/// Turns two samples into Info lines: the session starting, stopping or changing mode, the map changing, the car
/// changing (modded or stock), the game's state stack changing and players joining or leaving. The first sample gives
/// one "state at start" line. Pure.
/// </summary>
internal sealed class StateTracker
{
    private StateSample _last;

    /// <summary>The lines for what changed since the previous sample (empty when nothing did).</summary>
    public List<string> Update(StateSample now)
    {
        var lines = new List<string>();
        if (now == null) return lines;
        var prev = _last;
        _last = now;
        if (prev == null)
        {
            lines.Add("state at start: " + Describe(now));
            return lines;
        }
        if (prev.Session != now.Session) lines.Add(SessionLine(prev, now));
        if (!string.Equals(prev.Map, now.Map, StringComparison.Ordinal) && !string.IsNullOrEmpty(now.Map))
            lines.Add("map: " + Quote(prev.Map) + " -> " + Quote(now.Map));
        if (!string.Equals(prev.Car, now.Car, StringComparison.Ordinal) || prev.CarModded != now.CarModded)
            lines.Add("car: " + CarText(prev) + " -> " + CarText(now));
        if (!string.Equals(prev.GameState, now.GameState, StringComparison.Ordinal) && !string.IsNullOrEmpty(now.GameState))
            lines.Add("game state: " + Quote(prev.GameState) + " -> " + Quote(now.GameState));
        foreach (var kv in now.Players)
            if (!prev.Players.ContainsKey(kv.Key))
                lines.Add("player joined: #" + kv.Key.ToString(CultureInfo.InvariantCulture) + " " + Quote(kv.Value) +
                          " (" + now.Players.Count + " in the session)");
        foreach (var kv in prev.Players)
            if (!now.Players.ContainsKey(kv.Key))
                lines.Add("player left: #" + kv.Key.ToString(CultureInfo.InvariantCulture) + " " + Quote(kv.Value) +
                          " (" + now.Players.Count + " in the session)");
        return lines;
    }

    /// <summary>"session: MENU -> HOST (hosting), 1 player".</summary>
    public static string SessionLine(StateSample prev, StateSample now)
    {
        string verb = prev.Session == "MENU" ? "session started" : now.Session == "MENU" ? "session ended" : "session changed";
        return verb + ": " + prev.Session + " -> " + now.Session + (string.IsNullOrEmpty(now.SessionText) ? "" : " (" + now.SessionText + ")") +
               ", " + now.Players.Count + (now.Players.Count == 1 ? " player" : " players");
    }

    public static string Describe(StateSample s)
    {
        var sb = new StringBuilder();
        sb.Append("session ").Append(s.Session);
        if (!string.IsNullOrEmpty(s.SessionText)) sb.Append(" (").Append(s.SessionText).Append(')');
        sb.Append(", map ").Append(Quote(s.Map)).Append(", car ").Append(CarText(s)).Append(", ").Append(s.Players.Count).Append(" players");
        if (!string.IsNullOrEmpty(s.GameState)) sb.Append(", game state ").Append(Quote(s.GameState));
        return sb.ToString();
    }

    /// <summary>"none", "Pickup (stock)" or "Vera Crossover (modded)".</summary>
    public static string CarText(StateSample s) =>
        string.IsNullOrEmpty(s.Car) ? "none" : s.Car + (s.CarModded ? " (modded)" : " (stock)");

    private static string Quote(string s) => string.IsNullOrEmpty(s) ? "-" : "'" + s + "'";
}

/// <summary>The debug log's lines for scene loads and unloads. Pure.</summary>
internal static class SceneText
{
    /// <summary>"scene loaded: 'Map01' (single), at 41.2 s, the loading frame took 380 ms, 6.1 s after 'Menu' began to unload".</summary>
    public static string Loaded(string name, string mode, double atSeconds, double frameMs, string unloaded, double sinceUnloadSeconds)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("scene loaded: '").Append(name).Append("' (").Append(mode).Append("), at ")
            .Append(atSeconds.ToString("0.0", c)).Append(" s");
        if (frameMs >= 1) sb.Append(", its loading frame took ").Append(frameMs.ToString("0", c)).Append(" ms");
        if (!string.IsNullOrEmpty(unloaded) && sinceUnloadSeconds >= 0)
            sb.Append(", ").Append(sinceUnloadSeconds.ToString("0.0", c)).Append(" s after '").Append(unloaded).Append("' began to unload");
        return sb.ToString();
    }

    public static string Unloaded(string name, double atSeconds) =>
        "scene unloaded: '" + name + "', at " + atSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    public static string Active(string from, string to) => "active scene: '" + from + "' -> '" + to + "'";
}
