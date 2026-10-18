using System;
using BepInEx.Configuration;
using OverTheHillPlus.CheatsPlus;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Tuning;

/// <summary>Config entries for the Tuning tab (section "Tuning" in the config file).</summary>
internal static class TuningSettings
{
    public static ConfigEntry<bool> Enabled, AllCars, RemoveCaps, NoRevLimiter, IgnoreDamage, NoSpeedLimit, Turbo, TurboHud;
    public static ConfigEntry<float> Torque, Revs, TopSpeed, Gears, FinalDrive, Boost, SpoolTime, Threshold;

    internal struct Values
    {
        public float Torque, Revs, TopSpeed, Gears, FinalDrive, Boost, SpoolTime, Threshold;
        public bool RemoveCaps, NoRevLimiter, IgnoreDamage, NoSpeedLimit, Turbo;
    }

    public static void Bind(ConfigFile f)
    {
        const string S = "Tuning";
        Enabled = f.Bind(S, "Enabled", false, "Master switch for engine tuning (solo / host only). Off restores the stock values.");
        AllCars = f.Bind(S, "ApplyToAllCars", false, "Host: tune every car in the session, not just yours.");
        Torque = f.Bind(S, "TorqueMultiplier", 1f, "Engine torque multiplier (idle and peak torque, torque cap).");
        Revs = f.Bind(S, "RevRangeMultiplier", 1f, "Stretches peak rpm, max rpm, the rev limiter and the shift points.");
        RemoveCaps = f.Bind(S, "RemoveTorquePowerCaps", false, "Turn off the engine's torque and power caps.");
        NoRevLimiter = f.Bind(S, "DisableRevLimiter", false, "Turn off the rpm limiter (torque still fades past max rpm).");
        IgnoreDamage = f.Bind(S, "IgnoreDamagePowerLoss", false, "Damage no longer cuts throttle, torque or top speed.");
        NoSpeedLimit = f.Bind(S, "RemoveSpeedLimit", false, "Turn off the car's top-speed limiter.");
        TopSpeed = f.Bind(S, "TopSpeedLimitMultiplier", 1f, "Multiplier on the speed limiter's limit.");
        Gears = f.Bind(S, "GearRatioMultiplier", 1f, "Forward gear ratios. Below 1 = taller (faster), above 1 = shorter (more pull).");
        FinalDrive = f.Bind(S, "FinalDriveMultiplier", 1f, "Final drive (axle differential) ratio, high and low range.");
        Turbo = f.Bind(S, "Turbo", false, "Turbo-like boost: extra torque that builds with throttle and rpm, with lag.");
        Boost = f.Bind(S, "TurboBoost", 0.6f, "Extra torque at full boost (0.6 = +60%).");
        SpoolTime = f.Bind(S, "TurboSpoolSeconds", 0.8f, "Turbo lag: time constant for the boost to build.");
        Threshold = f.Bind(S, "TurboThreshold", 0.35f, "Where in the rev range (0..1) boost starts building.");
        TurboHud = f.Bind(S, "TurboGauge", true, "Show a small boost gauge while the turbo is on.");
    }

    public static bool AnythingOn =>
        !Near(Torque.Value, 1f) || !Near(Revs.Value, 1f) || !Near(TopSpeed.Value, 1f) || !Near(Gears.Value, 1f) ||
        !Near(FinalDrive.Value, 1f) || RemoveCaps.Value || NoRevLimiter.Value || IgnoreDamage.Value || NoSpeedLimit.Value ||
        (Turbo.Value && Boost.Value > 0f);

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;

    public static Values Snapshot() => new()
    {
        Torque = Mathf.Clamp(Torque.Value, 0.1f, 20f),
        Revs = Mathf.Clamp(Revs.Value, 0.5f, 3f),
        TopSpeed = Mathf.Clamp(TopSpeed.Value, 0.25f, 10f),
        Gears = Mathf.Clamp(Gears.Value, 0.2f, 4f),
        FinalDrive = Mathf.Clamp(FinalDrive.Value, 0.2f, 4f),
        Boost = Mathf.Clamp(Boost.Value, 0f, 5f),
        SpoolTime = Mathf.Clamp(SpoolTime.Value, 0.02f, 5f),
        Threshold = Mathf.Clamp(Threshold.Value, 0f, 0.95f),
        RemoveCaps = RemoveCaps.Value,
        NoRevLimiter = NoRevLimiter.Value,
        IgnoreDamage = IgnoreDamage.Value,
        NoSpeedLimit = NoSpeedLimit.Value,
        Turbo = Turbo.Value,
    };

    public static void Preset(float torque, float revs, float gears, float finalDrive, float topSpeed,
        bool caps, bool damage, bool noLimit, bool turbo, float boost = 0.6f, float spool = 0.8f)
    {
        Enabled.Value = true;
        Torque.Value = torque;
        Revs.Value = revs;
        Gears.Value = gears;
        FinalDrive.Value = finalDrive;
        TopSpeed.Value = topSpeed;
        RemoveCaps.Value = caps;
        IgnoreDamage.Value = damage;
        NoSpeedLimit.Value = noLimit;
        NoRevLimiter.Value = false;
        Turbo.Value = turbo;
        Boost.Value = boost;
        SpoolTime.Value = spool;
    }
}

/// <summary>
/// MODS > Cheats+ > Tuning: engine torque/power, rev range, caps and limiters, top speed, gear ratios, final drive and a
/// turbo-like boost. Writes the car's own VPP settings, keeps the game's originals per car, re-applies when the
/// game changes them (damage debuffs, engine upgrades) and restores them when switched off. Solo / host only.
/// </summary>
internal sealed class EngineTuningFeature : IFeature
{
    public void Register()
    {
        TuningSettings.Bind(CheatsConfig.File);
        Registry.OnTick(TuningCore.Tick);
        Registry.OnGui(DrawGauge);
        Registry.Section(Registry.PlusMode, "Tuning", DrawTab, 0);
    }

    private static string Blocked =>
        GameState.PhysicsAllowed ? null : "Tuning only works solo or as the host: the host simulates every car, so a guest's changes would just be overwritten.";

    private static void DrawTab()
    {
        bool allowed = GameState.PhysicsAllowed;
        bool on = TuningSettings.Enabled.Value;
        bool live = allowed && on;

        if (Blocked is { } why) Menu.Banner(why, Theme.Warn);

        Menu.Card("Engine tuning", "Multipliers on your car's own engine settings. Stock values are kept and put back when you turn this off.", () =>
        {
            Menu.Switch(TuningSettings.Enabled, "Engine tuning", "Master switch (solo / host)", allowed);
            Menu.Chips(5,
                ("Stock", () => { TuningSettings.Preset(1f, 1f, 1f, 1f, 1f, false, false, false, false); Menu.SetStatus("Tuning: stock values"); }, allowed),
                ("Sport", () => { TuningSettings.Preset(1.5f, 1.15f, 1f, 1f, 1.4f, false, false, false, false); Menu.SetStatus("Tuning: Sport"); }, allowed),
                ("Rally", () => { TuningSettings.Preset(1.8f, 1.1f, 1.1f, 1f, 1.3f, true, true, false, true, 0.5f, 0.6f); Menu.SetStatus("Tuning: Rally"); }, allowed),
                ("Crawler", () => { TuningSettings.Preset(2.5f, 0.9f, 1.6f, 1.3f, 1f, true, true, false, false); Menu.SetStatus("Tuning: Crawler"); }, allowed),
                ("Hyper", () => { TuningSettings.Preset(4f, 1.4f, 0.7f, 0.85f, 1f, true, true, true, true, 1f, 0.4f); Menu.SetStatus("Tuning: Hyper"); }, allowed));
            Menu.SliderRow(TuningSettings.Torque, "Torque", 0.25f, 10f, "0.00", "x", live);
            Menu.SliderRow(TuningSettings.Revs, "Rev range", 0.75f, 2f, "0.00", "x", live);
            Menu.Switch(TuningSettings.RemoveCaps, "Remove torque / power caps", null, live);
            Menu.Switch(TuningSettings.NoRevLimiter, "Disable rev limiter", "Torque still falls off past max rpm", live);
            Menu.Switch(TuningSettings.IgnoreDamage, "Ignore damage power loss", "Damage no longer cuts throttle, torque or top speed", live);
        });

        Menu.Card("Top speed & gearing", "Gear ratios below 1 are taller (higher top speed per gear), above 1 shorter (more pull, better crawling).", () =>
        {
            Menu.Switch(TuningSettings.NoSpeedLimit, "Remove top-speed limit", "Turns the speed limiter off", live);
            Menu.SliderRow(TuningSettings.TopSpeed, "Speed limit", 0.5f, 5f, "0.00", "x", live && !TuningSettings.NoSpeedLimit.Value);
            Menu.SliderRow(TuningSettings.Gears, "Gear ratios", 0.4f, 2.5f, "0.00", "x", live);
            Menu.SliderRow(TuningSettings.FinalDrive, "Final drive", 0.4f, 2.5f, "0.00", "x", live);
        });

        Menu.Card("Turbo", "Extra torque that builds with throttle and rpm, with lag and a quick blow-off.", () =>
        {
            Menu.Switch(TuningSettings.Turbo, "Turbo", null, live);
            bool t = live && TuningSettings.Turbo.Value;
            Menu.SliderRow(TuningSettings.Boost, "Boost", 0f, 3f, "0", "", t, percent: true);
            Menu.SliderRow(TuningSettings.SpoolTime, "Spool time", 0.05f, 3f, "0.00", " s", t);
            Menu.SliderRow(TuningSettings.Threshold, "Boost threshold", 0f, 0.9f, "0", "", t, percent: true);
            Menu.Switch(TuningSettings.TurboHud, "Boost gauge", "Small gauge on screen while the turbo is on", true);
        });

        Menu.Card("Status", null, DrawStatus);
    }

    private static void DrawStatus()
    {
        bool host = GameState.IsAuthority && GameState.Runner != null && !GameState.Runner.IsSinglePlayer;
        Menu.Switch(TuningSettings.AllCars, "Apply to every car", host ? "You host: tune your friends' cars too" : "Host only", host);

        var rec = TuningCore.LocalRecord;
        if (!TuningCore.Active || rec == null)
        {
            Menu.Small(!TuningSettings.Enabled.Value ? "Tuning is off: stock values." :
                !GameState.PhysicsAllowed ? "Not active here (joined as a guest)." :
                !TuningSettings.AnythingOn ? "Everything is at stock (1.00x)." :
                GameState.LocalCar == null ? "Waiting for your car..." : "Starting...");
        }
        else
        {
            try
            {
                var eng = rec.Eng;
                if (eng != null && rec.PeakTorque != null)
                {
                    Menu.KeyValue("Peak torque", $"{rec.PeakTorque.Base:0} -> {eng.peakRpmTorque:0} Nm");
                    if (rec.MaxRpm != null) Menu.KeyValue("Max rpm", $"{rec.MaxRpm.Base:0} -> {eng.maxRpm:0}");
                    Menu.KeyValue("Caps / limiter", $"torque cap {(eng.torqueCap ? "on" : "off")}, power cap {(eng.powerCap ? "on" : "off")}, rev limiter {(eng.rpmLimiter ? "on" : "off")}");
                }
                var sc = rec.Sc;
                if (sc != null)
                    Menu.KeyValue("Speed limiter", sc.speedLimiter ? $"{sc.speedLimit * 3.6f:0} km/h" : "off");
                if (TuningSettings.Turbo.Value)
                    Menu.Bar("Boost", rec.Spool, Theme.Accent, $"+{(rec.BoostFactor - 1f) * 100f:0}%");
                Menu.KeyValue("Cars tuned", TuningCore.Cars.Count.ToString());
            }
            catch (Exception e) { DebugLog.Error("cheats", e, "EngineTuningFeature.DrawStatus"); Menu.Small("Readout failed: " + e.Message); }
        }
        if (TuningCore.LastError != null) Menu.Small("Last error: " + TuningCore.LastError);

        Menu.Chips(1, ("Restore stock now", () =>
        {
            TuningSettings.Enabled.Value = false;
            TuningCore.RestoreEverything();
            Menu.SetStatus("Tuning off: stock engine restored");
        }, true));
    }

    // ---- boost gauge overlay ----

    private static void DrawGauge()
    {
        if (!TuningSettings.TurboHud.Value || !TuningSettings.Turbo.Value || !TuningCore.Active) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        var rec = TuningCore.LocalRecord;
        if (rec == null) return;

        Theme.Ensure();
        var prevMatrix = GUI.matrix;
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        const float w = 250f, h = 46f;
        float top = DrivingHud.Shown ? 24f + DrivingHud.Height + 10f : 24f; // sit under the driving HUD when it is shown
        var r = new Rect(Screen.width / scale - w - 24f, top, w, h);
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.82f), 10);
        GUI.Label(new Rect(r.x + 14f, r.y + 4f, 80f, 18f), "BOOST", Theme.Small);
        GUI.Label(new Rect(r.xMax - 94f, r.y + 2f, 80f, 20f), $"+{(rec.BoostFactor - 1f) * 100f:0}%", Theme.Value);
        var bar = new Rect(r.x + 14f, r.y + 26f, w - 28f, 10f);
        Theme.Fill(bar, Theme.Off, 5);
        float t = Mathf.Clamp01(rec.Spool);
        if (t > 0.005f) Theme.Fill(new Rect(bar.x, bar.y, bar.width * t, bar.height), t > 0.85f ? Theme.Bad : Theme.Accent, 5);

        GUI.matrix = prevMatrix;
    }
}
