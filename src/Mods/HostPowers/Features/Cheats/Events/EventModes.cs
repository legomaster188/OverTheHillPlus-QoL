using System;
using OverTheHillPlus.HostPowers;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Events;

/// <summary>
/// One-click event modes for the whole session (host). Each mode is a set of world changes made through the host
/// helpers in <see cref="HostWorld"/> (TimeManager: Cheat_SetTime / Cheat_PauseTime / Cheat_SetAurora / RPC_SetWeather /
/// WindAmount, plus the existing Host weather lock). The world as it was before the first mode is snapshotted, and
/// "End event" (or switching to another mode) puts back only what the mode changed.
/// </summary>
internal static class EventModes
{
    internal enum Mode { None, NightRace, MeteorHunt, AuroraNight, Storm }

    private sealed class Spec
    {
        public string Name;
        public int Hour = -1, Minute;       // -1 = leave the time alone
        public bool? Aurora;                // null = leave as before the event
        public Func<string> WeatherKeys;    // null = leave the weather alone
        public float Wind = -1f;            // < 0 = leave the wind alone
        public bool Shower;
    }

    private static readonly Spec[] Specs =
    {
        null,
        new() { Name = "Night Race", Hour = 23, Minute = 0, Aurora = false, WeatherKeys = () => EventsFeature.NightRaceWeather.Value },
        new() { Name = "Meteor Hunt", Shower = true },
        new() { Name = "Aurora Night", Hour = 22, Minute = 30, Aurora = true, WeatherKeys = () => EventsFeature.AuroraWeather.Value },
        new() { Name = "Storm", WeatherKeys = () => EventsFeature.StormWeather.Value, Wind = 1f },
    };

    private struct Snapshot
    {
        public int Hours, Minutes, WeatherId, LockedId;
        public bool Paused, Aurora, LockWeather;
        public float Wind;
    }

    private static Snapshot _snap;
    public static Mode Active { get; private set; }
    public static string ActiveName => NameOf(Active);
    public static string NameOf(Mode m) => m == Mode.None ? "none" : Specs[(int)m].Name;
    public static string Detail { get; private set; } = "";

    /// <summary>
    /// Called when the session changes: the old world is gone, so nothing is sent. Only the Host weather-lock setting
    /// an event changed is put back, so the next session doesn't keep locking the event's weather.
    /// </summary>
    internal static void Forget(bool restoreLockConfig = false)
    {
        if (restoreLockConfig && Active != Mode.None && Specs[(int)Active].WeatherKeys != null)
        {
            try
            {
                HostPowersConfig.LockWeather.Value = _snap.LockWeather;
                HostPowersConfig.LockedWeatherId.Value = _snap.LockedId;
            }
            catch (Exception ex) { DebugLog.Error("hostpowers", ex, "EventModes.Forget"); }
        }
        Active = Mode.None;
        Detail = "";
    }

    private static bool TakeSnapshot(TimeManager tm)
    {
        try
        {
            _snap = new Snapshot
            {
                Hours = tm.Hours,
                Minutes = tm.Minutes,
                Paused = tm.TimeIsPaused,
                Aurora = tm.AuroraTonight,
                Wind = tm.WindAmount,
                WeatherId = tm.CurrentWeatherProfileId,
                LockWeather = HostPowersConfig.LockWeather.Value,
                LockedId = HostPowersConfig.LockedWeatherId.Value,
            };
            return true;
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "EventModes.TakeSnapshot"); HostPowersPlugin.Logger.LogWarning("[Events] world snapshot: " + e.Message); return false; }
    }

    /// <summary>First weather whose name contains one of the comma-separated keywords (in keyword order), or -1.</summary>
    internal static int FindWeather(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) return -1;
        var list = HostWorld.Weathers;
        foreach (var raw in keys.Split(','))
        {
            var k = raw.Trim();
            if (k.Length == 0) continue;
            foreach (var w in list)
                if (w.name != null && w.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return w.id;
        }
        return -1;
    }

    public static string Start(Mode mode)
    {
        if (mode == Mode.None) return End();
        if (!HostTools.Available) return "Blocked: host only.";
        var tm = TimeManager.Instance;
        if (tm == null) return "Failed: load into a world first.";
        try { if (tm.Object == null || !tm.Object.IsValid) return "Failed: the world isn't ready yet."; }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "EventModes.Start"); return "Failed: the world isn't ready yet."; }

        if (Active == Mode.None)
        {
            if (!TakeSnapshot(tm)) return "Failed: couldn't read the current time and weather.";
        }
        AdvantageFlag.MarkUsed();

        var prev = Active == Mode.None ? null : Specs[(int)Active];
        var spec = Specs[(int)mode];
        var notes = new System.Collections.Generic.List<string>();

        try
        {
            // Time: locked (paused) at the mode's hour, or back to how it was if the previous mode changed it.
            if (spec.Hour >= 0)
            {
                HostWorld.SetTime(spec.Hour, spec.Minute);
                HostWorld.PauseTime(true);
                notes.Add($"{spec.Hour:00}:{spec.Minute:00} locked");
            }
            else if (prev != null && prev.Hour >= 0)
            {
                HostWorld.SetTime(_snap.Hours, _snap.Minutes);
                HostWorld.PauseTime(_snap.Paused);
            }

            bool aurora = spec.Aurora ?? _snap.Aurora;
            if (spec.Aurora.HasValue || prev?.Aurora != null) HostWorld.Aurora(aurora);
            if (spec.Aurora == true) notes.Add("aurora");

            if (spec.WeatherKeys != null)
            {
                int id = FindWeather(spec.WeatherKeys());
                if (id >= 0)
                {
                    HostWorld.SetWeather(id);
                    HostPowersConfig.LockedWeatherId.Value = id;
                    HostPowersConfig.LockWeather.Value = true;
                    notes.Add(HostWorld.WeatherName(id) + " locked");
                }
                else notes.Add("no matching weather (see Weather keywords)");
            }
            else if (prev?.WeatherKeys != null) RestoreWeather();

            if (spec.Wind >= 0f) { HostWorld.SetWind(spec.Wind); notes.Add($"wind {spec.Wind:0.0}"); }
            else if (prev != null && prev.Wind >= 0f) HostWorld.SetWind(_snap.Wind);

            if (spec.Shower)
            {
                var r = MeteorEvents.Shower(EventsFeature.ShowerSize.Value, true);
                if (r.StartsWith("Failed") || r.StartsWith("Blocked")) notes.Add("shower: " + r);
                else
                {
                    notes.Add("meteor shower");
                    if (EventsFeature.HuntRevealPins.Value)
                    {
                        EventsFeature.ScheduleReveal(EventsFeature.RevealDelay.Value);
                        notes.Add($"pins in {EventsFeature.RevealDelay.Value:0} s");
                    }
                }
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("hostpowers", e, "EventModes.Start");
            HostPowersPlugin.Logger.LogWarning("[Events] start " + spec.Name + ": " + e);
            Active = mode;
            Detail = "partly applied";
            return $"{spec.Name}: partly applied ({e.Message}). End event restores what changed.";
        }

        Active = mode;
        Detail = string.Join(", ", notes);
        DebugLog.Info(HostPowersPlugin.ModId, $"event {spec.Name} started: {Detail}");
        HostPowersPlugin.Logger.LogInfo($"[Events] {spec.Name} started: {Detail}");
        return $"{spec.Name}: {Detail}.";
    }

    private static void RestoreWeather()
    {
        HostPowersConfig.LockWeather.Value = _snap.LockWeather;
        HostPowersConfig.LockedWeatherId.Value = _snap.LockedId;
        if (_snap.WeatherId >= 0) HostWorld.SetWeather(_snap.WeatherId);
    }

    public static string End()
    {
        if (Active == Mode.None) return "No event running.";
        var spec = Specs[(int)Active];
        string name = spec.Name;
        if (!HostTools.Available || TimeManager.Instance == null)
        {
            Forget(restoreLockConfig: true);
            return "Event cleared (not hosting any more, nothing to restore).";
        }
        try
        {
            if (spec.Hour >= 0)
            {
                HostWorld.SetTime(_snap.Hours, _snap.Minutes);
                HostWorld.PauseTime(_snap.Paused);
            }
            if (spec.Aurora.HasValue) HostWorld.Aurora(_snap.Aurora);
            if (spec.WeatherKeys != null) RestoreWeather();
            if (spec.Wind >= 0f) HostWorld.SetWind(_snap.Wind);
        }
        catch (Exception e)
        {
            DebugLog.Error("hostpowers", e, "EventModes.End");
            HostPowersPlugin.Logger.LogWarning("[Events] end " + name + ": " + e);
            Forget(restoreLockConfig: true);
            return $"{name} ended, but restoring failed: {e.Message}";
        }
        Forget();
        DebugLog.Info(HostPowersPlugin.ModId, $"event {name} ended: time, weather, wind and aurora put back");
        return $"{name} ended: time, weather, wind and aurora put back.";
    }

    public static string Describe(Mode mode) => mode switch
    {
        Mode.NightRace => "Night locked at 23:00 with fog (weather locked). Headlights on!",
        Mode.MeteorHunt => "A falling meteor shower everywhere, then everyone's map pin on their nearest meteorite.",
        Mode.AuroraNight => "22:30 locked, clear sky, aurora on.",
        Mode.Storm => "Stormy weather locked plus full wind.",
        _ => "",
    };
}
