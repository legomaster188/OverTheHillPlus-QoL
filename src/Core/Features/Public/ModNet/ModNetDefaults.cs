namespace OverTheHillPlus.Features.Public.ModNet;

/// <summary>
/// The mod network's default per loader and the one-time switch-on (pure, unit-tested). MelonLoader (the car pack): on by
/// default. Other players' paint, the Meerkat's build, tyres and guests' trailer requests ride on it, and it works there
/// since core 1.4.1 (the key sent as raw bytes, the hook put in without MelonLoader's process snapshot), checked with two
/// copies on 2026-11-09: joins, rejoins, paint, Meerkat builds, trailers. An install that has the old default (off)
/// written is switched on once; the marker makes a later switch-off stick. BepInEx: off by default as before (while a
/// modded session runs it hooks the game's network receive handler, which every join message passes through).
/// </summary>
internal static class ModNetDefaults
{
    /// <summary>Written once the one-time switch-on has run, in the mods' config folder.</summary>
    internal const string SwitchedOnMarker = "oth.core.modnet-on.done";

#if MELON
    internal static readonly bool OnByDefault = true;
#else
    internal static readonly bool OnByDefault = false;
#endif

    /// <summary>The one-time switch-on runs where the network is on by default, it never ran before, and the network is off.</summary>
    internal static bool ShouldSwitchOn(bool onByDefault, bool markerExists, bool enabledNow) => onByDefault && !markerExists && !enabledNow;
}
