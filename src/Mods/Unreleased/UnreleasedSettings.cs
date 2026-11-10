using OverTheHillPlus.Config;

namespace OverTheHillPlus.Unreleased;

/// <summary>
/// Unreleased Cars' settings entries, apart from the game types (so the installer's settings export can run them):
/// the auto-add switch and one switch per car.
/// </summary>
internal static class UnreleasedSettings
{
    /// <summary>The cars' names, the same as <see cref="UnreleasedCars.All"/> (keep both lists in step).</summary>
    public static readonly string[] CarNames = { "Grizzly", "Baguette", "Brigand", "Cordillera" };

    public static ConfigEntry<bool> BindAutoAdd(ConfigFile cfg) =>
        cfg.Bind("Garage", "AddAllAutomatically", false,
            "Add every switched-on unreleased car to your garage by itself once a profile is loaded (solo or host).");

    public static ConfigEntry<bool> BindCar(ConfigFile cfg, string name) =>
        cfg.Bind("Cars", name, true,
            $"Show the {name} in your garage once you added it (the MODS page's \"Add to my garage\").");
}
