using BepInEx.Configuration;

namespace OverTheHillPlus.UnlockAll;

/// <summary>Unlock All's settings in BepInEx/config/oth.unlockall.cfg.</summary>
internal static class UnlockAllConfig
{
    public static ConfigEntry<bool> UpgradesOwned;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        UpgradesOwned = cfg.Bind("Unlocks", "UpgradesOwned", true,
            "Unlocked upgrades are owned (true) or only unlocked in the shop, still to buy (false). Cosmetics are always owned.");
    }

    /// <summary>Owned unless the setting is missing (the mod failed to start) and then still owned.</summary>
    public static bool Owned => UpgradesOwned == null || UpgradesOwned.Value;
}
