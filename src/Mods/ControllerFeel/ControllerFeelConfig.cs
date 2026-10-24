using BepInEx.Configuration;

namespace OverTheHillPlus.ControllerFeel;

/// <summary>Rumble+'s settings in BepInEx/config/oth.controllerfeel.cfg.</summary>
internal static class ControllerFeelConfig
{
    public static ConfigEntry<bool> Rumble, OnlyWithPad, TriggerMotors;
    public static ConfigEntry<float> Master, Surface, Bumps, Landing, Wheelspin, Lockup, Idle, Winch, GameRumble;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        const string general = "General";
        Rumble = cfg.Bind(general, "Rumble", true, "Master switch: the effects below. Off: this mod never touches the gamepad's motors.");
        OnlyWithPad = cfg.Bind(general, "OnlyWhenUsingPad", true,
            "Rumble only while the game is set to the gamepad (not while you play with keyboard and mouse and a pad is just plugged in).");
        TriggerMotors = cfg.Bind(general, "TriggerMotors", true,
            "Use the trigger motors of a pad that has them (Xbox impulse triggers) for lock-ups and wheelspin, if the game's input system can drive them. Otherwise nothing changes.");

        const string strength = "Strength";
        Master = Bind(cfg, strength, "Master", 1f, "All effects at once (0 = none, 1 = as designed, up to 2). The game's own Controller Vibration setting also applies.");
        Surface = Bind(cfg, strength, "Surface", 1f, "Gravel, rock, snow, sand, mud and other surfaces under the wheels, by speed.");
        Bumps = Bind(cfg, strength, "Bumps", 1f, "Hits from the suspension and the bump stop (bottoming out).");
        Landing = Bind(cfg, strength, "Landing", 1f, "The thump when the wheels come back down after a jump or a drop.");
        Wheelspin = Bind(cfg, strength, "Wheelspin", 1f, "A driven wheel spinning under power.");
        Lockup = Bind(cfg, strength, "Lockup", 1f, "Braked wheels locking up and the car sliding sideways.");
        Idle = Bind(cfg, strength, "Idle", 1f, "The shake of the engine at idle.");
        Winch = Bind(cfg, strength, "Winch", 1f, "The strain of a taut winch rope.");

        GameRumble = cfg.Bind("Game", "GameRumble", 1f, new ConfigDescription(
            "How much of the game's own controller rumble stays on top of this mod's: 1 = all of it, 0 = this mod only. " +
            "Both cover bumps, wheelspin and the winch, so set 0 to avoid doubling.",
            new AcceptableValueRange<float>(0f, 1f)));
    }

    private static ConfigEntry<float> Bind(ConfigFile cfg, string section, string key, float value, string description) =>
        cfg.Bind(section, key, value, new ConfigDescription(description, new AcceptableValueRange<float>(0f, FeelSettings.MaxStrength)));

    /// <summary>The settings the mixer reads, copied from the config (cheap: ten floats).</summary>
    public static void Fill(FeelSettings s, bool triggerMotors)
    {
        s.Master = Master.Value;
        s.Surface = Surface.Value;
        s.Bumps = Bumps.Value;
        s.Landing = Landing.Value;
        s.Wheelspin = Wheelspin.Value;
        s.Lockup = Lockup.Value;
        s.Idle = Idle.Value;
        s.Winch = Winch.Value;
        s.TriggerMotors = triggerMotors && TriggerMotors.Value;
    }
}
