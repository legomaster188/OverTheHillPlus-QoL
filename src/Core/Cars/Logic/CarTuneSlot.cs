using System;

namespace OverTheHillPlus.Cars;

/// <summary>
/// One tuned game value on a modded car. Remembers the game's own ("base") value and the value we last wrote, so when the
/// game changes it (engine-tier upgrade, cargo weight, damage) the new game value becomes the base and the car's
/// multiplier is applied on top of it again; on restore the base goes back only if the value is still ours.
/// Pure code (unit-tested).
/// </summary>
public sealed class CarTuneSlot
{
    private readonly Func<float> _get;
    private readonly Action<float> _set;
    private float _last;
    private bool _has;

    public float Base { get; private set; }
    public bool Held => _has;

    public CarTuneSlot(Func<float> get, Action<float> set) { _get = get; _set = set; }

    private static bool Same(float a, float b) => Math.Abs(a - b) <= 1e-5f * Math.Max(1f, Math.Abs(a));

    /// <summary>Reads the live value; adopts it as the new base when it is not the value we wrote.</summary>
    public float Sync()
    {
        float cur = _get();
        if (!_has || !Same(cur, _last))
        {
            Base = cur;
            _last = cur;
            _has = true;
        }
        return Base;
    }

    /// <summary>Syncs, then writes <paramref name="target"/> if it differs from the live value.</summary>
    public void Write(float target)
    {
        Sync();
        if (float.IsNaN(target) || float.IsInfinity(target)) target = Base;
        if (!Same(target, _last)) _set(target);
        _last = target;
    }

    /// <summary>Puts the game's value back (if the live value is still ours) and forgets the slot.</summary>
    public void Restore()
    {
        if (!_has) return;
        _has = false;
        try
        {
            if (Same(_get(), _last) && !Same(Base, _last)) _set(Base);
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarTuneSlot.Restore"); /* object gone: nothing to restore */ }
    }
}
