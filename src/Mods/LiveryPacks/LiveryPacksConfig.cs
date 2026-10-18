using BepInEx.Configuration;

namespace OverTheHillPlus.LiveryPacks;

/// <summary>Livery Packs' settings in BepInEx/config/oth.liverypacks.cfg.</summary>
internal static class LiveryPacksConfig
{
    public static ConfigEntry<bool> LoadPacks;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        LoadPacks = cfg.Bind("LiveryPacks", "LoadPacks", true,
            "Add the packs in BepInEx/config/oth.liveries/ to the garage's livery list (Livery Mods tab, with \"Show local mods\" on).");
    }
}
