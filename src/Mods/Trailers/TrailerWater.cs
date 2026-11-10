using System;
using UnityEngine;

namespace OverTheHillPlus.Trailers;

/// <summary>The water's surface at a point, from the game's own water bodies (every stock lake and river, and those a map
/// mod registers with the game's registry), else from the waters a map mod publishes: what a boat on a trailer floats in.</summary>
internal static class TrailerWater
{
    private static bool _broken;

    /// <summary>The surface's height at a world point, or NaN (no water there, or the registry can't be asked).</summary>
    public static float At(Vector3 world)
    {
        if (_broken) return float.NaN;
        try
        {
            float s = 0f;
            if (WaterBodyRegistry.TryGetWaterSurface(world, out s) && float.IsFinite(s)) return s;
            if (OverTheHillPlus.Api.WorldPlaces.WaterAt(world.x, world.z, out var w, out float level) && !w.Frozen && float.IsFinite(level)) return level;
        }
        catch (Exception e)
        {
            _broken = true;
            DebugLog.Error(TrailersPlugin.ModId, e, "TrailerWater.At");
            TrailersPlugin.LogOnce("water", "[Trailers] the game's water registry can't be asked: boats stay on their trailers this session (" + e.Message + ")");
        }
        return float.NaN;
    }
}
