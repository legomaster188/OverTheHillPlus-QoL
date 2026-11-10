using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// One wheel collider's radius under the 4 in lift's bigger tyres and the stock height's smaller ones, shared with other writers (Tyre Pressure's pressure and
/// flats, Air Ride). Ours is a factor on the radius as it was before we wrote. Rules, so that two writers that each take
/// a foreign change as their new stock never ratchet the radius up or down:
/// <list type="bullet">
/// <item>While another mod holds the wheel (its fresh grip claim, <c>GroundGrip.ClaimedByOther</c>) we never write.</item>
/// <item>A change since our last write is someone else's: we adopt it (its value already carries what we wrote, or
///   replaces it) and don't write again until our own factor changes.</item>
/// <item>A new factor is written once, on the stock we last knew (the adopted value without our old factor).</item>
/// <item>All four wheels or none: the caller passes <c>foreign</c> for every wheel when any one of them is held, so a car
///   never stands on mixed sizes (one flat, or one axle aired down, keeps the whole car where it is).</item>
/// <item>One instance per car for as long as the car lives (<see cref="Dead"/>): a new one takes the radius it finds as
///   stock, and on the small wheels that would shrink it again.</item>
/// </list>
/// Pure (unit-tested).
/// </summary>
public sealed class MeerkatRadius
{
    public const float Quant = 0.0005f;

    /// <summary>The radius without our factor, as we last knew it.</summary>
    public float Stock { get; private set; }
    /// <summary>What we wrote last (or adopted).</summary>
    public float Written { get; private set; }
    /// <summary>The factor in <see cref="Written"/> (1: none of ours).</summary>
    public float Factor { get; private set; } = 1f;
    /// <summary>The wheel carries our factor (or a foreign value we adopted over it).</summary>
    public bool Have { get; private set; }

    /// <summary>True when the wheel's radius carries this factor (false while another mod's claim keeps ours off it).</summary>
    public bool Carries(float factor) => Math.Abs(factor - Factor) < 1e-4f;

    /// <summary>The size a car's wheels carry, whatever its build asks for: what to show on them. No wheels, or wheels that
    /// don't agree, are the model's size (the radius a car spawns with).</summary>
    public static TyreSize Carried(IReadOnlyList<MeerkatRadius> wheels)
    {
        if (wheels == null || wheels.Count == 0) return TyreSize.Model;
        var size = MeerkatBuild.SizeOf(wheels[0].Factor);
        for (int i = 1; i < wheels.Count; i++)
            if (MeerkatBuild.SizeOf(wheels[i].Factor) != size) return TyreSize.Model;
        return size;
    }

    /// <summary>The cars of a table to forget: those that are gone for good, never one that only missed a pass.</summary>
    public static List<TKey> Dead<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> table, Func<TValue, bool> alive)
    {
        var dead = new List<TKey>();
        foreach (var kv in table)
            if (!alive(kv.Value)) dead.Add(kv.Key);
        return dead;
    }

    /// <summary>
    /// One check: <paramref name="cur"/> is the wheel's radius now, <paramref name="factor"/> the build's
    /// (MeerkatBuild.TyreRadiusFactor), <paramref name="foreign"/> true while another mod's claim on the wheel is fresh.
    /// Returns the radius to write, or NaN for none.
    /// </summary>
    public float Next(float cur, float factor, bool foreign)
    {
        if (!(cur > 0.05f) || float.IsInfinity(cur)) return float.NaN;
        if (!(factor > 0.1f) || float.IsInfinity(factor)) factor = 1f;
        bool changedByOther = Have && Math.Abs(cur - Written) > Quant;
        if (changedByOther || (Have && foreign))
        {
            // adopt: what is on the wheel now is someone else's, with our factor in it as far as we can tell
            Stock = cur / Factor;
            Written = cur;
        }
        if (foreign) return float.NaN;
        if (!Have)
        {
            Stock = cur;
            Factor = 1f;
            Written = cur;
        }
        if (Math.Abs(factor - Factor) < 1e-4f) return float.NaN;
        bool stock = Math.Abs(factor - 1f) < 1e-4f;
        float want = stock ? Stock : Stock * factor;
        Factor = stock ? 1f : factor;
        Have = !stock;
        Written = want;
        if (Math.Abs(want - cur) < Quant) { Written = cur; return float.NaN; }
        return want;
    }
}
