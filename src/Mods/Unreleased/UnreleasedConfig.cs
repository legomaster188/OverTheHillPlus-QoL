using BepInEx.Configuration;

namespace OverTheHillPlus.Unreleased;

/// <summary>Unreleased Cars' settings in BepInEx/config/oth.unreleased.cfg: one switch per car.</summary>
internal static class UnreleasedConfig
{
    public static ConfigFile File;
    public static ConfigEntry<bool> AutoAdd;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        AutoAdd = cfg.Bind("Garage", "AddAllAutomatically", false,
            "Add every switched-on unreleased car to your garage by itself once a profile is loaded (solo or host).");
        foreach (var car in UnreleasedCars.All)
            car.Enabled = cfg.Bind("Cars", car.Name, true,
                $"Show the {car.Name} in your garage once you added it (the MODS page's \"Add to my garage\").");
    }
}
