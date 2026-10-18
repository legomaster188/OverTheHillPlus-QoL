using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Trailers;

/// <summary>Trailers' settings in BepInEx/config/oth.trailers.cfg.</summary>
internal static class TrailersConfig
{
    public static ConfigEntry<TrailerKind> Kind;
    public static ConfigEntry<Key> HitchKey;
    public static ConfigEntry<Key> ResetKey;
    public static ConfigEntry<int> PerPlayer;
    public static ConfigEntry<int> SessionMax;
    public static ConfigEntry<bool> GuestsMay;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Kind = cfg.Bind("Trailers", "Kind", TrailerKind.Utility, "The trailer you get: Utility, Camper, Hauler or Boat.");
        HitchKey = cfg.Bind("Trailers", "HitchKey", Key.None,
            "Hotkey: hitch your trailer when your car's back is near its coupler, or unhitch it (while slow). None = no hotkey.");
        ResetKey = cfg.Bind("Trailers", "ResetKey", Key.None,
            "Hotkey: put your trailer back behind your car and hitch it (while stopped). None = no hotkey.");
        PerPlayer = cfg.Bind("Host", "PerPlayer", 1,
            new ConfigDescription("When hosting: trailers each player may have at once.", new AcceptableValueRange<int>(1, SpawnLimits.MaxPerPlayer)));
        SessionMax = cfg.Bind("Host", "SessionMax", SpawnLimits.MaxSession,
            new ConfigDescription("When hosting: trailers in the whole session at once.", new AcceptableValueRange<int>(1, SpawnLimits.MaxSession)));
        GuestsMay = cfg.Bind("Host", "GuestsMayTow", true,
            "When hosting: guests with this mod may get, hitch and reset their own trailers. Off: only you.");
    }
}
