using System;
using UnityEngine;

namespace OverTheHillPlus.FullMap;

/// <summary>
/// Reveals the map through the game's own FogOfWarManager.ClearFullFogOfWar (what the game's RevealFogOfWar command
/// and TestAreaUnlocker call): it clears the fog texture, shows it and saves the fog file itself. Nothing else is
/// written by this mod. "Keep map revealed" does the same once per new map (a world load or a new fog texture), after
/// the map has settled for a moment.
/// </summary>
internal static class MapReveal
{
    private const float SettleSeconds = 3f;

    private static (IntPtr fog, IntPtr texture) _cleared, _pending;
    private static float _pendingSince, _nextTick;
    private static bool _loggedError;

    /// <summary>A world map with its fog texture is loaded.</summary>
    internal static bool MapReady => TryFog(out _, out _);

    internal static void Install() => CoreBehaviour.AfterFeatureTicks += Tick;

    internal static string RevealNow()
    {
        if (!TryFog(out var fog, out var key)) return "No map loaded yet: load into the world first.";
        return Clear(fog, key, manual: true) ? "Whole map revealed." : "Revealing the map failed (see the log).";
    }

    private static void Tick()
    {
        if (FullMapConfig.KeepRevealed == null || !FullMapConfig.KeepRevealed.Value) return;
        float now = Time.unscaledTime;
        if (now < _nextTick) return;
        _nextTick = now + 1f;
        if (!TryFog(out var fog, out var key))
        {
            // The map went away (back to the menu, or between worlds): forget it, so a new map that happens to get
            // the same objects (pointers can be reused) is still revealed.
            _pending = default;
            _cleared = default;
            return;
        }
        if (key == _cleared) return;
        if (key != _pending)
        {
            _pending = key;
            _pendingSince = now;
            return;
        }
        if (now - _pendingSince < SettleSeconds) return;
        if (!Clear(fog, key, manual: false)) _cleared = key; // don't retry a failing map every second
    }

    /// <summary>
    /// Clears the fog. A failure is always logged for a press of the button; for "Keep map revealed" only the first
    /// one is (it would otherwise repeat for every map).
    /// </summary>
    private static bool Clear(FogOfWarManager fog, (IntPtr, IntPtr) key, bool manual)
    {
        try
        {
            AdvantageFlag.MarkUsed(); // core: achievements stay locked for the session
            fog.ClearFullFogOfWar();
            _cleared = key;
            DebugLog.Info(FullMapPlugin.ModId, manual ? "map revealed (button)" : "map revealed (keep revealed, after the map settled)");
            FullMapPlugin.Logger.LogInfo("[FullMap] map revealed.");
            return true;
        }
        catch (Exception e)
        {
            if (manual) FullMapPlugin.Logger.LogError("[FullMap] ClearFullFogOfWar failed: " + e.GetBaseException().Message);
            else if (!_loggedError)
            {
                _loggedError = true;
                FullMapPlugin.Logger.LogError("[FullMap] ClearFullFogOfWar failed (further automatic errors suppressed): " +
                                              e.GetBaseException().Message);
            }
            return false;
        }
    }

    private static bool TryFog(out FogOfWarManager fog, out (IntPtr fog, IntPtr texture) key)
    {
        fog = null;
        key = default;
        try
        {
            var map = MapManager.Instance;
            if (map == null) return false;
            fog = map.FogOfWarManager;
            var texture = fog?.FogOfWarTexture;
            if (texture == null) return false;
            key = (fog.Pointer, texture.Pointer);
            return true;
        }
        catch (Exception ex) { DebugLog.Error("fullmap", ex, "MapReveal.TryFog"); return false; }
    }
}
