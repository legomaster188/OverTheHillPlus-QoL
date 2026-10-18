using System;
using OverTheHillPlus.Features;

namespace OverTheHillPlus.Cargo;

/// <summary>Plugs the jobs into core: the mod network channel, the per-frame tick and the on-screen HUD.</summary>
internal sealed class CargoFeature : IFeature
{
    private static bool _loggedTick;

    public void Register()
    {
        CargoGame.Register();
        Registry.OnTick(Tick);
        Registry.OnGui(CargoHud.Draw);
    }

    private static void Tick()
    {
        try { CargoGame.Tick(); }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            CargoPlugin.Logger.LogError("[Cargo] tick failed (further errors suppressed): " + e);
        }
    }
}
