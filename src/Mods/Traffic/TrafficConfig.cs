using BepInEx.Configuration;

namespace OverTheHillPlus.Traffic;

/// <summary>AI Traffic's settings in BepInEx/config/oth.traffic.cfg.</summary>
internal static class TrafficConfig
{
    private const string Section = "Traffic";

    internal enum RoadChoice { Auto, MapRoads, DrivenPaths, BetweenPlaces }

    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<int> CarsPerPlayer;
    public static ConfigEntry<int> MaxCars;
    public static ConfigEntry<float> SpawnDistance;
    public static ConfigEntry<float> DespawnDistance;
    public static ConfigEntry<float> MaxSpeed;
    public static ConfigEntry<float> LaneOffset;
    public static ConfigEntry<RoadChoice> Roads;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Enabled = cfg.Bind(Section, "Enabled", false, "Driverless AI cars wander the roads (host or solo only).");
        CarsPerPlayer = cfg.Bind(Section, "CarsPerPlayer", 2, new ConfigDescription(
            "AI cars around each player.", new AcceptableValueRange<int>(0, 10)));
        MaxCars = cfg.Bind(Section, "MaxCars", 6, new ConfigDescription(
            "Most AI cars in the session at once (each one is a networked car for everyone).", new AcceptableValueRange<int>(0, 16)));
        SpawnDistance = cfg.Bind(Section, "SpawnDistance", 150f, new ConfigDescription(
            "New AI cars appear at least this far from every player (m).", new AcceptableValueRange<float>(60f, 600f)));
        DespawnDistance = cfg.Bind(Section, "DespawnDistance", 500f, new ConfigDescription(
            "AI cars farther than this from every player are taken away (m).", new AcceptableValueRange<float>(200f, 2000f)));
        MaxSpeed = cfg.Bind(Section, "MaxSpeed", 45f, new ConfigDescription(
            "Top speed of the AI cars (km/h).", new AcceptableValueRange<float>(15f, 90f)));
        LaneOffset = cfg.Bind(Section, "LaneOffset", 2f, new ConfigDescription(
            "How far right of the road's middle the AI cars drive (m), leaving room to pass.", new AcceptableValueRange<float>(0f, 4f)));
        Roads = cfg.Bind(Section, "Roads", RoadChoice.Auto,
            "Which roads to drive: Auto (the map's road splines, else paths players drove this session, else straight " +
            "lines between places), MapRoads, DrivenPaths or BetweenPlaces.");
    }

    public static TrafficPlanner.Settings PlannerSettings()
    {
        var s = TrafficPlanner.Settings.Default;
        s.CarsPerPlayer = CarsPerPlayer.Value;
        s.MaxCars = MaxCars.Value;
        s.SpawnMin = SpawnDistance.Value;
        s.SpawnMax = SpawnDistance.Value + 180f;
        s.DespawnDistance = DespawnDistance.Value;
        return s.Normalized();
    }
}
