using OverTheHillPlus.Config;

namespace OverTheHillPlus.Unreleased;

/// <summary>Unreleased Cars' settings in BepInEx/config/oth.unreleased.cfg: one switch per car.</summary>
internal static class UnreleasedConfig
{
    public static ConfigFile File;
    public static ConfigEntry<bool> AutoAdd;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        AutoAdd = UnreleasedSettings.BindAutoAdd(cfg);
        foreach (var car in UnreleasedCars.All)
            car.Enabled = UnreleasedSettings.BindCar(cfg, car.Name);
    }
}
