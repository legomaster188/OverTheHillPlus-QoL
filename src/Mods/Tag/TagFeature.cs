using System;
using OverTheHillPlus.Features;

namespace OverTheHillPlus.Tag;

/// <summary>Plugs the round into core: the mod network channel, the per-frame tick and the on-screen HUD.</summary>
internal sealed class TagFeature : IFeature
{
    private static bool _loggedTick;

    public void Register()
    {
        TagGame.Register();
        Registry.OnTick(Tick);
        Registry.OnGui(TagHud.Draw);
    }

    private static void Tick()
    {
        try { TagGame.Tick(); }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            TagPlugin.Logger.LogError("[Tag] tick failed (further errors suppressed): " + e);
        }
    }
}
