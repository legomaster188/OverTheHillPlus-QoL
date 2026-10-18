using System;

namespace OverTheHillPlus.Api;

/// <summary>
/// Per-frame callbacks run by the Mod Manager. A callback that throws is logged once (with your mod id) and stops
/// being called; the other mods' callbacks keep running. Both throw ArgumentException for an invalid id or one of
/// ours, and ArgumentNullException without a callback.
/// </summary>
public static class Hooks
{
    /// <summary>Calls <paramref name="tick"/> every frame (Unity's Update), on the main thread.</summary>
    public static void OnTick(string modId, Action tick)
    {
        Check(modId, tick);
        Features.Registry.OnTick(tick, modId);
    }

    /// <summary>
    /// Calls <paramref name="gui"/> in every IMGUI event (OnGUI), before the MODS page draws, also while the page is
    /// closed: for overlays and HUDs. Each call starts with GUI.matrix and the GUI colours reset.
    /// </summary>
    public static void OnGui(string modId, Action gui)
    {
        Check(modId, gui);
        Features.Registry.OnGui(gui, modId);
    }

    private static void Check(string modId, Action action)
    {
        ApiGuard.Require(modId, "Hooks");
        if (action == null) throw new ArgumentNullException(nameof(action));
    }
}
