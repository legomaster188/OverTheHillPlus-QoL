using BepInEx.Configuration;

namespace OverTheHillPlus.Cargo;

/// <summary>Cargo Jobs' settings in BepInEx/config/oth.cargo.cfg. The [Jobs] settings are the host's.</summary>
internal static class CargoConfig
{
    public static ConfigEntry<int> HourlyPayCap, BoardSize, PayPerKm;
    public static ConfigEntry<float> PickupRadius, DeliveryRadius;
    public static ConfigEntry<bool> Announce, ShowHud, ShowMarker;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        HourlyPayCap = cfg.Bind("Jobs", "HourlyPayCap", 3000, new ConfigDescription(
            "Most pebbles one player earns from cargo per hour. Hosting: what the host pays each player. As a guest: the most your own game accepts. 0 = no cap.",
            new AcceptableValueRange<int>(0, 1000000)));
        BoardSize = cfg.Bind("Jobs", "BoardSize", 6, new ConfigDescription("Jobs on the board at a time.", new AcceptableValueRange<int>(1, 16)));
        PayPerKm = cfg.Bind("Jobs", "PayPerKm", 120, new ConfigDescription("Pebbles per kilometre (straight line), before the on-time bonus.", new AcceptableValueRange<int>(10, 1000)));
        PickupRadius = cfg.Bind("Jobs", "PickupRadius", 30f, new ConfigDescription("Metres from the pick-up point that count as there.", new AcceptableValueRange<float>(10f, 100f)));
        DeliveryRadius = cfg.Bind("Jobs", "DeliveryRadius", 30f, new ConfigDescription("Metres from the destination that count as delivered.", new AcceptableValueRange<float>(10f, 100f)));
        Announce = cfg.Bind("Jobs", "AnnounceDeliveries", true, "Host: announce deliveries with the game's pop-up.");
        ShowHud = cfg.Bind("Display", "ShowHud", true, "Show your current job (where to go, how far, time for the bonus) on screen.");
        ShowMarker = cfg.Bind("Display", "ShowMarker", true, "Show a marker over your job's pick-up or destination.");
    }
}
