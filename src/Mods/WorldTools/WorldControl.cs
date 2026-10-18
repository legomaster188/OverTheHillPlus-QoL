using System;
using System.Collections.Generic;
using DistantLands.Cozy.Data;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OverTheHillPlus.WorldTools;

/// <summary>
/// World Tools+'s game side, a few checks a second while you host (or play solo). Every value this writes is
/// remembered and put back when its setting returns to normal, when you stop hosting or leave the world.
///
/// Findings (Cpp2IL of the updated game, interop checked with the member dumper):
/// - Gravity: one global Physics.gravity. In Fusion host mode the host simulates every car and prop, so changing it
///   on the host changes it for everyone (a guest's own physics result is overwritten by the host's).
/// - Time scale: TimeManager.RPC_SetTimeScale is the game's own networked command (it runs on every player, vanilla
///   ones too). Its body sets OverlandNetworkRunner.Instance.RunnerSimulatePhysics3D.DeltaTimeMultiplier (the length
///   of each Fusion physics step) and Time.timeScale (animation, effects, audio). Fusion's tick rate is not touched,
///   so the session stays in step; only the simulated time per tick changes. That is why this uses the RPC and never
///   writes Time.timeScale alone. RPCs are not kept for late joiners, so it is sent again every 10 s while not 1,
///   never while the game is paused (Time.timeScale 0). If the RPC can't be sent, the host's own values are written
///   (the host simulates everything, so the game still runs at that pace for everyone).
/// - Day length: the clock runs on the host's COZY time module, CozyWeather.timeModule.perennialProfile
///   .timeMovementSpeed. TimeManager.FixedUpdateNetwork copies it into the networked TimeMovementSpeed, and every
///   guest's TimeManager.Render writes that back into its own profile, so all clocks follow the host's. Camping's
///   fast-forward multiplies TimeManager.originalTimeSpeed (the stock speed) and writes originalTimeSpeed back when
///   camping ends; the override is only written in the Regular time mode and re-applied after.
/// - Weather: TimeManager.RPC_SetWeather(profile id) with the ids of cozyWeatherProfileIds.IdToProfileName, as Host
///   Powers' weather buttons (copied here: mods don't reference each other).
/// Every change marks <see cref="AdvantageFlag"/>.
/// </summary>
internal static class WorldControl
{
    private static readonly HashSet<string> LoggedOnce = new();
    private static float _nextCheck, _nextWeather, _nextTimeScaleResend;

    // gravity
    private static bool _gravityHeld;
    private static Vector3 _stockGravity, _writtenGravity;

    // time scale
    private static float _timeScaleSent = 1f;
    private static bool _wasInSession;

    // day speed
    private static PerennialProfile _profile;
    private static float _profileStock, _profileWritten = float.NaN;

    private static List<(int id, string name)> _weathers;

    public static bool Hosting => GameState.IsAuthority && GameState.Runner != null;

    /// <summary>Null when the world tools can act here, else why not.</summary>
    public static string BlockReason
    {
        get
        {
            if (GameState.Runner == null) return "Load into a world first.";
            if (!GameState.IsAuthority) return "Host or solo only: the host's game runs the world for everyone.";
            if (Tm == null) return "The world is still loading.";
            return null;
        }
    }

    private static TimeManager Tm
    {
        get { try { return TimeManager.Instance; } catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.Tm"); return null; } }
    }

    internal static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) WorldToolsPlugin.Logger?.LogWarning("[World Tools] " + message + " (logged once)");
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        if (WorldToolsConfig.Gravity == null) return;
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 0.25f;

        // The session ended (also as a guest, whose speed the host's time-scale RPC set): normal speed in the menu.
        bool inSession = GameState.Runner != null;
        if (_wasInSession && !inSession) ResetOwnTimeScale("session ended");
        _wasInSession = inSession;

        bool active = Hosting && Tm != null;
        try { TickGravity(active); } catch (Exception e) { DebugLog.Error("worldtools", e, "WorldControl.Tick"); LogOnce("gravity", "gravity: " + e.Message); }
        try { TickTimeScale(active); } catch (Exception e) { DebugLog.Error("worldtools", e, "WorldControl.Tick"); LogOnce("timescale", "time scale: " + e.Message); }
        try { TickDaySpeed(active); } catch (Exception e) { DebugLog.Error("worldtools", e, "WorldControl.Tick"); LogOnce("dayspeed", "day length: " + e.Message); }
        try { TickWeather(active); } catch (Exception e) { DebugLog.Error("worldtools", e, "WorldControl.Tick"); LogOnce("weather", "weather lock: " + e.Message); }
    }

    /// <summary>
    /// Quitting to the menu or the desktop (prefixes on OverlandNetworkRunner.QuitToMenuAsync / QuitGameAsync), while
    /// the session still exists: a host tells everyone the game speed is normal again (the guests' Time.timeScale would
    /// otherwise stay slowed in their menus), and every value of ours is put back. A modded guest resets its own speed.
    /// </summary>
    public static void OnLeavingSession()
    {
        try
        {
            if (Hosting && Tm != null && !WorldMath.Near(_timeScaleSent, 1f)) SendTimeScale(1f);
        }
        catch (Exception e) { DebugLog.Error("worldtools", e, "WorldControl.OnLeavingSession"); LogOnce("quit-ts", "time scale on quit: " + e.Message); }
        _timeScaleSent = 1f;
        ResetOwnTimeScale("leaving the session");
        RestoreAll();
    }

    /// <summary>This game's speed back to 1 after a session (not while paused at 0).</summary>
    private static void ResetOwnTimeScale(string why)
    {
        try
        {
            if (Time.timeScale > 0f && !WorldMath.Near(Time.timeScale, 1f))
            {
                Time.timeScale = 1f;
                WorldToolsPlugin.Logger?.LogInfo($"[World Tools] game speed back to normal ({why})");
            }
        }
        catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.ResetOwnTimeScale"); }
    }

    /// <summary>Quitting or switching the mod's work off: put everything back now.</summary>
    public static void RestoreAll()
    {
        try { TickGravity(false); } catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.RestoreAll"); }
        try { TickTimeScale(false); } catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.RestoreAll"); }
        try { TickDaySpeed(false); } catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.RestoreAll"); }
    }

    // ---------------------------------------------------------------- gravity

    private static void TickGravity(bool active)
    {
        float g = WorldMath.ClampGravity(WorldToolsConfig.Gravity.Value);
        bool want = active && !WorldMath.Near(g, 1f);
        if (!want)
        {
            if (!_gravityHeld) return;
            // Only our own value is put back (someone else may have set gravity since).
            if ((Physics.gravity - _writtenGravity).sqrMagnitude < 1e-6f) Physics.gravity = _stockGravity;
            _gravityHeld = false;
            DebugLog.Info(WorldToolsPlugin.ModId, "gravity back to normal");
            WorldToolsPlugin.Logger?.LogInfo("[World Tools] gravity back to normal");
            return;
        }
        var cur = Physics.gravity;
        if (!_gravityHeld)
        {
            _stockGravity = cur.sqrMagnitude > 0.01f ? cur : new Vector3(0f, -9.81f, 0f);
            _gravityHeld = true;
        }
        else if ((cur - _writtenGravity).sqrMagnitude > 1e-6f)
        {
            _stockGravity = cur; // the game set its own gravity since: that is the new normal
        }
        var target = _stockGravity * g;
        if ((cur - target).sqrMagnitude < 1e-6f) { _writtenGravity = target; return; }
        Physics.gravity = target;
        _writtenGravity = target;
        AdvantageFlag.MarkUsed();
        DebugLog.Info(WorldToolsPlugin.ModId, $"gravity set to {WorldMath.GravityLabel(g)} (stock {_stockGravity.y:0.00})");
        WorldToolsPlugin.Logger?.LogInfo($"[World Tools] gravity {WorldMath.GravityLabel(g)}");
    }

    // ---------------------------------------------------------------- time scale

    private static void TickTimeScale(bool active)
    {
        float want = active ? WorldMath.ClampTimeScale(WorldToolsConfig.TimeScale.Value) : 1f;
        bool changed = !WorldMath.Near(want, _timeScaleSent);
        if (!changed)
        {
            // Late joiners never got the RPC: say it again now and then.
            if (WorldMath.Near(want, 1f) || Time.unscaledTime < _nextTimeScaleResend) return;
        }
        if (!active)
        {
            // Left the world or stopped hosting: put this game's own values back (the session may be gone, and
            // Time.timeScale would otherwise stay slowed in the menu).
            LocalTimeScale(1f, onlyOurs: true);
            _timeScaleSent = 1f;
            return;
        }
        if (Time.timeScale <= 0f) return; // paused: leave it, re-sent once it runs again
        SendTimeScale(want);
        _timeScaleSent = want;
        _nextTimeScaleResend = Time.unscaledTime + 10f;
        if (changed && !WorldMath.Near(want, 1f))
        {
            AdvantageFlag.MarkUsed();
            DebugLog.Info(WorldToolsPlugin.ModId, $"time scale set to {WorldMath.TimeScaleLabel(want)}");
            WorldToolsPlugin.Logger?.LogInfo($"[World Tools] time scale {WorldMath.TimeScaleLabel(want)}");
        }
    }

    private static void SendTimeScale(float scale)
    {
        try { Tm?.RPC_SetTimeScale(scale); }
        catch (Exception e) { DebugLog.Error("worldtools", e, "WorldControl.SendTimeScale"); LogOnce("rpc-ts", "time scale RPC: " + e.Message); }
        // The RPC also runs here; if it was refused, write the host's own values (the host simulates everything).
        if (Time.timeScale > 0f && !WorldMath.Near(Time.timeScale, scale)) LocalTimeScale(scale, onlyOurs: false);
    }

    private static void LocalTimeScale(float scale, bool onlyOurs)
    {
        try
        {
            if (Time.timeScale > 0f && (!onlyOurs || WorldMath.Near(Time.timeScale, _timeScaleSent))) Time.timeScale = scale;
        }
        catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.LocalTimeScale"); }
        try
        {
            var onr = OverlandNetworkRunner.Instance;
            RunnerSimulatePhysics3D sim = onr != null ? onr.RunnerSimulatePhysics3D : null;
            if (sim != null && (!onlyOurs || WorldMath.Near(sim.DeltaTimeMultiplier, _timeScaleSent))) sim.DeltaTimeMultiplier = scale;
        }
        catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.LocalTimeScale"); } // runner gone with its scene: nothing left to set
    }

    public static float TimeScaleNow
    {
        get { try { return Time.timeScale; } catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.TimeScaleNow"); return 1f; } }
    }

    // ---------------------------------------------------------------- day length

    private static PerennialProfile CurrentProfile(TimeManager tm)
    {
        try { return tm?.cozyWeather?.timeModule?.perennialProfile; }
        catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.CurrentProfile"); return null; }
    }

    private static WorldMath.ClockMode Mode(TimeManager tm)
    {
        try
        {
            var s = tm.TimeSpeed;
            if (s == TimeManager.TimeSpeedSetting.Paused) return WorldMath.ClockMode.Paused;
            if (s == TimeManager.TimeSpeedSetting.CampingFastForward) return WorldMath.ClockMode.CampingFastForward;
        }
        catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.Mode"); }
        return WorldMath.ClockMode.Regular;
    }

    private static void TickDaySpeed(bool active)
    {
        float m = WorldMath.ClampDaySpeed(WorldToolsConfig.DaySpeed.Value);
        var tm = active ? Tm : null;
        var profile = tm != null ? CurrentProfile(tm) : null;
        bool want = active && profile != null && !WorldMath.Near(m, 1f);

        // A different profile (new world) or none: put the old one back first.
        if (_profile != null && (!want || profile == null || profile.Pointer != _profile.Pointer)) RestoreProfile();
        if (!want) return;

        var mode = Mode(tm);
        if (_profile == null)
        {
            // The stock speed is taken once per profile, in the regular time mode, before anything of ours is in it.
            // TimeManager.originalTimeSpeed is only a fallback: the game copies the profile's value into it (Awake,
            // OnPortalLoaded), so after a map change through a portal it can hold our own value.
            if (mode != WorldMath.ClockMode.Regular) return;
            float first = profile.timeMovementSpeed;
            if (!(first > 0f))
            {
                float orig = 0f;
                try { orig = tm.originalTimeSpeed; } catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.TickDaySpeed"); }
                if (orig > 0f && !WorldMath.Near(orig, _profileWritten)) first = orig;
            }
            if (!(first > 0f)) return;
            _profile = profile;
            _profileStock = first;
            _profileWritten = float.NaN;
        }
        float stock = _profileStock;
        var target = WorldMath.DaySpeedTarget(stock, m, mode);
        if (target == null) return; // paused or camping: the game's own

        float cur = profile.timeMovementSpeed;
        if (Mathf.Abs(cur - target.Value) <= 1e-5f * Mathf.Max(1f, Mathf.Abs(target.Value))) { _profileWritten = cur; return; }
        profile.timeMovementSpeed = target.Value;
        _profileWritten = target.Value;
        AdvantageFlag.MarkUsed();
        WorldToolsPlugin.Logger?.LogInfo($"[World Tools] day length {WorldMath.DaySpeedLabel(m)} (clock speed {stock:0.###} -> {target.Value:0.###})");
    }

    private static void RestoreProfile()
    {
        try
        {
            // Unity null: destroyed with its world. Only our own value is put back.
            if (_profile != null && Mathf.Abs(_profile.timeMovementSpeed - _profileWritten) < 1e-4f)
                _profile.timeMovementSpeed = _profileStock;
        }
        catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.RestoreProfile"); }
        try
        {
            // The game may have copied our value into its stock (it writes originalTimeSpeed back when camping ends).
            var tm = Tm;
            if (tm != null && WorldMath.Near(tm.originalTimeSpeed, _profileWritten)) tm.originalTimeSpeed = _profileStock;
        }
        catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.RestoreProfile"); }
        _profile = null;
        _profileWritten = float.NaN;
        WorldToolsPlugin.Logger?.LogInfo("[World Tools] day length back to normal");
    }

    // ---------------------------------------------------------------- weather

    private static IntPtr _weathersFor;

    public static IReadOnlyList<(int id, string name)> Weathers
    {
        get
        {
            var tm = Tm;
            IntPtr ptr = tm != null ? tm.Pointer : IntPtr.Zero;
            if (_weathers != null && _weathers.Count > 0 && ptr == _weathersFor) return _weathers;
            _weathersFor = ptr;
            var list = new List<(int, string)>();
            try
            {
                var map = tm?.cozyWeatherProfileIds?.IdToProfileName;
                if (map != null)
                    foreach (var kv in map) list.Add((kv.Key, Pretty(kv.Value)));
            }
            catch (Exception e) { DebugLog.Error("worldtools", e, "WorldControl"); LogOnce("weathers", "weather list: " + e.Message); }
            list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            return _weathers = list;
        }
    }

    private static string Pretty(string s) => string.IsNullOrEmpty(s) ? "?" : s.Replace("_", " ").Replace("Profile", "").Trim();

    public static int CurrentWeatherId
    {
        get { try { return Tm?.CurrentWeatherProfileId ?? -1; } catch (Exception ex) { DebugLog.Error("worldtools", ex, "WorldControl.CurrentWeatherId"); return -1; } }
    }

    public static string WeatherName(int id)
    {
        foreach (var w in Weathers) if (w.id == id) return w.name;
        return id < 0 ? "none" : $"#{id}";
    }

    /// <summary>Sets the weather now (host only).</summary>
    public static string SetWeather(int id)
    {
        var block = BlockReason;
        if (block != null) return "Blocked: " + block;
        try
        {
            Tm.RPC_SetWeather(id);
            AdvantageFlag.MarkUsed();
            DebugLog.Info(WorldToolsPlugin.ModId, $"weather set to {WeatherName(id)}");
            return $"Weather: {WeatherName(id)}.";
        }
        catch (Exception e) { DebugLog.Error("worldtools", e, "WorldControl.SetWeather"); return "Failed: " + e.Message; }
    }

    private static void TickWeather(bool active)
    {
        if (!active || !WorldToolsConfig.LockWeather.Value) return;
        int id = WorldToolsConfig.LockedWeatherId.Value;
        if (id < 0 || Time.unscaledTime < _nextWeather) return;
        _nextWeather = Time.unscaledTime + 10f;
        if (CurrentWeatherId == id) return;
        Tm.RPC_SetWeather(id);
        AdvantageFlag.MarkUsed();
        WorldToolsPlugin.Logger?.LogInfo($"[World Tools] weather put back to {WeatherName(id)}");
    }
}
