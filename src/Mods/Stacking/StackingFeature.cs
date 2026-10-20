using OverTheHillPlus.Features;

namespace OverTheHillPlus.Stacking;

/// <summary>Stacking+'s per-frame work: the hint line on the garage's cosmetics screen.</summary>
internal sealed class StackingFeature : IFeature
{
    public void Register()
    {
        Registry.OnTick(StackHint.Tick);
        Registry.OnGui(StackHint.Gui);
    }
}
