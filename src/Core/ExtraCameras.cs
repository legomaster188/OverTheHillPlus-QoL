using System;
using System.Collections.Generic;

namespace OverTheHillPlus;

/// <summary>A camera another mod adds to Camera+'s cycle (e.g. the Cockpit view), through core so the two don't reference each other.</summary>
internal sealed class ExtraCamera
{
    public string Name;
    /// <summary>True when the cycle key should go through it (the mod is on and the camera enabled).</summary>
    public Func<bool> InCycle;
    public Func<bool> IsOn;
    public Action<bool> SetOn;
}

/// <summary>
/// The cameras other mods add after Camera+'s own (Game, Chase far, Hood, Bumper, Drone, then these in the order they
/// registered, then Game again). Camera+ cycles through them when it is on; the mod's own key turns its camera on and
/// off either way. Only one camera of any kind is on at a time.
/// </summary>
internal static class ExtraCameras
{
    private static readonly List<ExtraCamera> All = new();

    /// <summary>Raised before an extra camera turns on, so Camera+ goes back to the game's camera.</summary>
    internal static event Action<ExtraCamera> TurningOn;

    internal static void Register(ExtraCamera camera)
    {
        if (camera != null && !All.Contains(camera)) All.Add(camera);
    }

    /// <summary>The extra camera that is on, or null.</summary>
    internal static ExtraCamera Active
    {
        get
        {
            foreach (var c in All)
                if (c.IsOn()) return c;
            return null;
        }
    }

    /// <summary>The extra cameras the cycle goes through, in order.</summary>
    internal static List<ExtraCamera> InCycle()
    {
        var list = new List<ExtraCamera>();
        foreach (var c in All)
            if (c.InCycle()) list.Add(c);
        return list;
    }

    internal static void TurnOn(ExtraCamera camera)
    {
        StopAll();
        TurningOn?.Invoke(camera);
        camera.SetOn(true);
    }

    internal static void StopAll()
    {
        foreach (var c in All)
            if (c.IsOn()) c.SetOn(false);
    }
}
