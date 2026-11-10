using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Meerkat;

/// <summary>How high the Meerkat stands: the stock car's height, or a coilover lift. The model is built at the 3 in lift.</summary>
public enum LiftStage : byte { Stock = 0, TwoInch = 1, ThreeInch = 2, FourInch = 3 }

/// <summary>The tyres: a road tread, the all-terrains it comes on, or mud-terrains.</summary>
public enum TyreKind : byte { Street = 0, AllTerrain = 1, Mud = 2 }

/// <summary>The size of the wheels: the model's 15 in with 205/75 tyres, the 4 in lift's 225/75, or the stock car's 14 in
/// with 185/60 tyres.</summary>
public enum TyreSize : byte { Model = 0, Big = 1, Small = 2 }

/// <summary>What covers the cockpit.</summary>
public enum TopKind : byte { Hard = 0, Soft = 1, None = 2 }

/// <summary>The bolt-on parts, each its own switch.</summary>
[Flags]
public enum Addons : ushort
{
    None = 0,
    Skid = 1,
    /// <summary>The roof rack with its driving lamps (on the hardtop), or the lamps on a bar over the windscreen.</summary>
    Rack = 2,
    Flares = 4,
    Flaps = 8,
    Hook = 16,
    Snorkel = 32,
    Spare = 64,
    All = Skid | Rack | Flares | Flaps | Hook | Snorkel | Spare,
}

/// <summary>The ground a tyre is on, as the tread tables know it.</summary>
public enum Ground : byte { Tarmac = 0, Trail = 1, Mud = 2, Snow = 3, Sand = 4, Rock = 5, Ice = 6 }

/// <summary>
/// One Meerkat's build: the lift, the tyres, the top, the bolt-on parts and the engine stage. Everything the car shows and
/// how it drives follows from it: the parts on the model (<see cref="PartsShown"/>), the ride height against the model
/// (<see cref="RideHeight"/>), the tyres' size (<see cref="TyreRadius"/>) and grip (<see cref="Grip"/>), and the physics'
/// multipliers (<see cref="MeerkatUpgrades.EffectOf"/>). Pure (unit-tested).
/// </summary>
public readonly struct MeerkatBuild : IEquatable<MeerkatBuild>
{
    public readonly LiftStage Lift;
    public readonly TyreKind Tyre;
    public readonly TopKind Top;
    public readonly Addons Parts;
    /// <summary>0 stock, 1 intake and exhaust, 2 cams and turbo kit.</summary>
    public readonly byte Engine;

    public const byte MaxEngine = 2;

    public MeerkatBuild(LiftStage lift, TyreKind tyre, TopKind top, Addons parts, byte engine)
    {
        Lift = lift <= LiftStage.FourInch ? lift : LiftStage.ThreeInch;
        Tyre = tyre <= TyreKind.Mud ? tyre : TyreKind.AllTerrain;
        Top = top <= TopKind.None ? top : TopKind.Hard;
        Parts = parts & Addons.All;
        Engine = Math.Min(engine, MaxEngine);
    }

    /// <summary>The safari build the car is modelled as (free play's default): the 3 in lift, all-terrains, the hardtop,
    /// the skid plate, the rack and lamps, the flares, the flaps and the tow hook.</summary>
    public static MeerkatBuild Safari => new(LiftStage.ThreeInch, TyreKind.AllTerrain, TopKind.Hard,
        Addons.Skid | Addons.Rack | Addons.Flares | Addons.Flaps | Addons.Hook, 0);

    /// <summary>The car as the career's dealer sells it: lifted on all-terrains with the hardtop, no bolt-on parts.</summary>
    public static MeerkatBuild Dealer => new(LiftStage.ThreeInch, TyreKind.AllTerrain, TopKind.Hard, Addons.None, 0);

    public bool Has(Addons a) => (Parts & a) == a;

    public MeerkatBuild With(Addons a, bool on) => new(Lift, Tyre, Top, on ? Parts | a : Parts & ~a, Engine);
    public MeerkatBuild WithLift(LiftStage l) => new(l, Tyre, Top, Parts, Engine);
    public MeerkatBuild WithTyre(TyreKind t) => new(Lift, t, Top, Parts, Engine);
    public MeerkatBuild WithTop(TopKind t) => new(Lift, Tyre, t, Parts, Engine);
    public MeerkatBuild WithEngine(byte e) => new(Lift, Tyre, Top, Parts, e);

    // ---------------------------------------------------------------- the stance

    /// <summary>The lift of a stage in inches over the stock car.</summary>
    public static int Inches(LiftStage s) => s switch { LiftStage.Stock => 0, LiftStage.TwoInch => 2, LiftStage.FourInch => 4, _ => 3 };

    /// <summary>Metres the body rides higher (negative: lower) than the model, which is built at 3 in.</summary>
    public static float RideHeight(LiftStage s) => (Inches(s) - 3) * 0.0254f;

    public float RideHeight() => RideHeight(Lift);

    /// <summary>The model's tyres (205/75 R15), the bigger ones the 4 in lift takes (225/75 R15) and the stock car's
    /// (185/60 R14), radius in metres.</summary>
    public const float ModelTyreRadius = 0.344f, BigTyreRadius = 0.359f, StockTyreRadius = 0.289f;

    /// <summary>The 4 in lift runs a size bigger all-terrains or mud-terrains (the road tyres stay the model's size).</summary>
    public bool BigTyres => Lift == LiftStage.FourInch && Tyre != TyreKind.Street;

    /// <summary>The stock height is the stock car: its 14 in wheels and 185/60 tyres in every tread. The model's tyres
    /// would stand 1 mm from the arch's top there; these leave the real car's 5.6 cm.</summary>
    public bool StockWheels => Lift == LiftStage.Stock;

    public TyreSize Size => StockWheels ? TyreSize.Small : BigTyres ? TyreSize.Big : TyreSize.Model;

    public float TyreRadius() => RadiusOf(Size);

    public static float RadiusOf(TyreSize s) => s == TyreSize.Small ? StockTyreRadius : s == TyreSize.Big ? BigTyreRadius : ModelTyreRadius;

    /// <summary>The size whose radius a wheel collider's factor gives (anything else: the model's).</summary>
    public static TyreSize SizeOf(float factor)
    {
        if (Math.Abs(factor - StockTyreRadius / ModelTyreRadius) < 1e-3f) return TyreSize.Small;
        if (Math.Abs(factor - BigTyreRadius / ModelTyreRadius) < 1e-3f) return TyreSize.Big;
        return TyreSize.Model;
    }

    /// <summary>The wheel colliders' radius against the model's (1 = the model's tyre).</summary>
    public float TyreRadiusFactor() => TyreRadius() / ModelTyreRadius;

    /// <summary>Metres the hubs stand higher (negative: lower) than the model's, the tyres on the ground.</summary>
    public float HubHeight() => TyreRadius() - ModelTyreRadius;

    /// <summary>Metres the body stands higher (negative: lower) than the model's over the ground: the lift and the tyres.
    /// The stock height comes to -13.1 cm, where the model's lift over the real car is 13.0.</summary>
    public float BodyHeight() => RideHeight() + HubHeight();

    /// <summary>
    /// The suspension travel multiplier a lift stage needs on a car whose base travel (the template's wheel colliders,
    /// at the model's 3 in) is <paramref name="baseTravel"/> metres: the body rides higher by exactly the change in travel
    /// (the spring's sag doesn't change), so the stage's height over or under the model is added to the travel.
    /// <para>A known error: the multiplier lands on the car's live travel (core's TunedCar scales what the wheel
    /// colliders have), which includes a game suspension stage when one is fitted (+0.08 or +0.16 m added to the base,
    /// LiftPreviewMath). The body then moves by the stage's height times (1 + stage / base travel) instead of exactly
    /// its height: with the 0.25 m assumed base, a +0.16 m game stage makes the stock height 12.5 cm lower than the
    /// model instead of 7.6 (2 in: -8.3 instead of -5.1 cm; 4 in: +4.2 instead of +2.5 cm), while the garage preview
    /// (GarageLift.SetRideHeight) adds the plain height. Without a game stage it is exact. A layer can only multiply
    /// (CarUpgradeEffect.Suspension) and the career registers one multiplier per stage for every car, so it isn't
    /// computed per car here.</para>
    /// </summary>
    public static float TravelFactor(LiftStage s, float baseTravel)
    {
        if (!(baseTravel > 0.05f) || float.IsInfinity(baseTravel)) baseTravel = MeerkatUpgrades.AssumedTravel;
        return Math.Max(0.3f, (baseTravel + RideHeight(s)) / baseTravel);
    }

    /// <summary>How far the mud flaps ride up their brackets (metres): built 17 cm off the ground on the lifted car, at the
    /// stock height (13 cm lower) they would hang 4 cm off it.</summary>
    public float FlapLift() => StockWheels ? 0.05f : 0f;

    // ---------------------------------------------------------------- what shows

    public const string PodL = "mv_popup_L", PodR = "mv_popup_R", Hardtop = "mv_ao_hardtop", SoftTop = "mv_alt_softtop",
        RoofRack = "mv_ao_rack", LampBar = "mv_alt_podbar", SkidPlate = "mv_ao_skid", FenderFlares = "mv_ao_flares",
        MudFlaps = "mv_ao_flaps", TowHook = "mv_ao_hook", SnorkelPart = "mv_opt_snorkel", SparePart = "mv_opt_spare";

    /// <summary>Every optional part of the model (the pods are always on).</summary>
    public static readonly string[] OptionalParts =
        { Hardtop, SoftTop, RoofRack, LampBar, SkidPlate, FenderFlares, MudFlaps, TowHook, SnorkelPart, SparePart };

    /// <summary>The parts that stand over the body's collider, in the order of the definition's body boxes: each box is
    /// there only while its part is shown.</summary>
    public static readonly string[] BoxParts = { RoofRack, LampBar };

    /// <summary>The optional parts this build shows. The rack needs the hardtop's roof; without it the lamps sit on a bar
    /// over the windscreen header.</summary>
    public HashSet<string> PartsShown()
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        if (Top == TopKind.Hard) s.Add(Hardtop);
        if (Top == TopKind.Soft) s.Add(SoftTop);
        if (Has(Addons.Rack)) s.Add(Top == TopKind.Hard ? RoofRack : LampBar);
        if (Has(Addons.Skid)) s.Add(SkidPlate);
        if (Has(Addons.Flares)) s.Add(FenderFlares);
        if (Has(Addons.Flaps)) s.Add(MudFlaps);
        if (Has(Addons.Hook)) s.Add(TowHook);
        if (Has(Addons.Snorkel)) s.Add(SnorkelPart);
        if (Has(Addons.Spare)) s.Add(SparePart);
        return s;
    }

    // ---------------------------------------------------------------- grip

    /// <summary>The tyre's grip on a ground against the all-terrains the car's tuning is set for (1 = the same): road
    /// tyres grip better on tarmac and worse in mud and snow, mud-terrains the other way round. Every tread is better
    /// somewhere and worse somewhere; none is more than 18 % better.</summary>
    public static float Grip(TyreKind t, Ground g)
    {
        if (t == TyreKind.Street)
            return g switch
            {
                Ground.Tarmac => 1.07f, Ground.Trail => 0.95f, Ground.Mud => 0.80f, Ground.Snow => 0.86f, Ground.Sand => 0.86f,
                Ground.Rock => 0.98f, _ => 1.00f,
            };
        if (t == TyreKind.Mud)
            return g switch
            {
                Ground.Tarmac => 0.92f, Ground.Trail => 1.03f, Ground.Mud => 1.18f, Ground.Snow => 1.08f, Ground.Sand => 1.05f,
                Ground.Rock => 1.04f, _ => 0.95f,
            };
        return 1f;
    }

    /// <summary>True when the tyres change the grip anywhere (the all-terrains don't: the car's own tuning is theirs).</summary>
    public bool WritesGrip => Tyre != TyreKind.AllTerrain;

    // ---------------------------------------------------------------- equality, text

    public bool Equals(MeerkatBuild o) => Lift == o.Lift && Tyre == o.Tyre && Top == o.Top && Parts == o.Parts && Engine == o.Engine;
    public override bool Equals(object obj) => obj is MeerkatBuild o && Equals(o);
    public override int GetHashCode() => HashCode.Combine((int)Lift, (int)Tyre, (int)Top, (int)Parts, Engine);
    public static bool operator ==(MeerkatBuild a, MeerkatBuild b) => a.Equals(b);
    public static bool operator !=(MeerkatBuild a, MeerkatBuild b) => !a.Equals(b);

    public override string ToString() =>
        $"lift {Inches(Lift)} in, {Tyre}{(BigTyres ? " (big)" : "")}, top {Top}, parts {Parts}, engine stage {Engine}";
}
