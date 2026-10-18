using BepInEx.Configuration;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>Custom Cosmetics' settings in BepInEx/config/oth.customcosmetics.cfg.</summary>
internal static class CustomCosmeticsConfig
{
    public static ConfigEntry<bool> StockCars;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        StockCars = cfg.Bind("Cars", "StockCars", false,
            "Also add the parts to the game's own cars (the parts that list them). Off by default: only the Mod Manager's cars get parts. " +
            "Players without the mod ignore the parts either way; two players with different part sets would see each other's parts " +
            "wrongly, so keep the mod the same version for everyone. Takes effect for cars loaded after the change (restart to be sure).");
    }
}
