using OverTheHillPlus.Config;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>Custom Cosmetics' settings in BepInEx/config/oth.customcosmetics.cfg.</summary>
internal static class CustomCosmeticsConfig
{
    public static ConfigEntry<bool> StockCars;
    public static ConfigEntry<bool> Twinkle;
    public static ConfigEntry<bool> PricesInFreePlay;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        StockCars = cfg.Bind("Cars", "StockCars", false,
            "Also add the parts to the game's own cars (the parts that list them). Off by default: only the Mod Manager's cars get parts. " +
            "Players without the mod ignore the parts either way; two players with different part sets would see each other's parts " +
            "wrongly, so keep the mod the same version for everyone. Takes effect for cars loaded after the change (restart to be sure).");
        Twinkle = cfg.Bind("Lights", "Twinkle", true,
            "String lights and Christmas lights twinkle: their bulbs fade in three sets in turn while your lamps are on. Off: they just glow.");
        PricesInFreePlay = cfg.Bind("Prices", "PricesInFreePlay", false,
            "Parts cost their price outside a career too. Off: they're free in free play; in a career they always cost money " +
            "and what you buy goes in the career's inventory (so you can sell it on the market).");
    }
}
