using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Logging;

/// <summary>Core's [Debug] settings (BepInEx/config/oth.core.cfg): the debug log, the hang watchdog and hook timing.</summary>
internal static class DebugConfig
{
    public static ConfigEntry<DebugLevel> Level;
    public static ConfigEntry<float> HangSeconds;
    public static ConfigEntry<int> SpikeMs;
    public static ConfigEntry<float> SlowHookMs;
    public static ConfigEntry<bool> PatchGuards;
    public static ConfigEntry<bool> WindowsEvents;
    public static ConfigEntry<int> KeepFiles;
    public static ConfigEntry<Key> SnapshotKey;
    public static ConfigEntry<bool> SnapshotCtrlShift;
    public static ConfigEntry<bool> CarPhysicsLog;

    public static void Bind(ConfigFile cfg)
    {
        Level = cfg.Bind("Debug", "Level", DebugLevel.Errors,
            "The mods' debug log in BepInEx/oth-debug/ (the last files are kept). Off: none. Errors: hangs, frame spikes, " +
            "slow hooks, each distinct error once (with repeat counts) and crash reports. Info: plus the mods' info lines. " +
            "Verbose: plus per-hook timing every 60 s and the mods' detailed lines. Takes effect at once.");
        HangSeconds = cfg.Bind("Debug", "HangSeconds", 3f,
            "The hang watchdog notes a frame that has not finished after this many seconds (again at 10 s and 30 s), with the " +
            "steps the mods took before it. 0 turns the watchdog off.");
        SpikeMs = cfg.Bind("Debug", "SpikeMs", 250,
            "A frame longer than this (milliseconds) is noted with the mods' steps in it (at most one every 2 s). 0: off.");
        SlowHookMs = cfg.Bind("Debug", "SlowHookMs", 20f,
            "A mod's per-frame hook that runs longer than this (milliseconds) is noted (once per hook every 10 s). 0: off.");
        PatchGuards = cfg.Bind("Debug", "PatchGuards", false,
            "Also time every Harmony patch of the mods, mark it in the crash trail and catch its errors with the mod's name " +
            "(adds a little work to each patched game method). For hunting a hang or crash; after a restart.");
        WindowsEvents = cfg.Bind("Debug", "WindowsEvents", true,
            "A crash report also lists Windows' Application Error / Application Hang events of that time (read with wevtutil).");
        KeepFiles = cfg.Bind("Debug", "KeepFiles", 10, "How many debug logs (and crash reports, twice as many) to keep.");
        SnapshotKey = cfg.Bind("Debug", "SnapshotKey", Key.F12,
            "Writes a diagnostics snapshot (BepInEx/oth-reports/snapshot-<time>.txt: every mod's state, hook timing, recent errors, " +
            "the session, memory, frame rate and the last 200 steps) when pressed with Ctrl+Shift held (see SnapshotCtrlShift). None: no shortcut; " +
            "the MODS page has the same button.");
        SnapshotCtrlShift = cfg.Bind("Debug", "SnapshotCtrlShift", true,
            "The snapshot key only counts while Ctrl and Shift are held, so it is not hit by accident. Off: the key alone.");
        CarPhysicsLog = cfg.Bind("Debug", "CarPhysicsLog", false,
            "For a modded car, write to BepInEx/LogOutput.log once when it spawns: its rigidbody, brakes, converter, engine and " +
            "wheels as the physics has them, every collider (with its size and place on the car) and every light, flare and " +
            "smoke or dust emitter it carries; then, while it stands still, a line every 2 s of how much it moves (bounce, " +
            "pitch and roll, wheel spin, engine rpm). Takes effect at once.");
    }

    /// <summary>The setting, or <paramref name="fallback"/> before the config is bound.</summary>
    public static float Get(ConfigEntry<float> e, float fallback)
    {
        try { return e?.Value ?? fallback; } catch { return fallback; }
    }

    public static int Get(ConfigEntry<int> e, int fallback)
    {
        try { return e?.Value ?? fallback; } catch { return fallback; }
    }

    public static bool Get(ConfigEntry<bool> e, bool fallback)
    {
        try { return e?.Value ?? fallback; } catch { return fallback; }
    }
}
