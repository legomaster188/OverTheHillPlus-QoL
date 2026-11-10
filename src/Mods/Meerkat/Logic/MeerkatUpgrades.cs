using System;
using System.Collections.Generic;
using OverTheHillPlus.Api;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// The Meerkat's upgrades in the career (core's Api.Career.Upgrades, for this car only) and the same build in free play:
/// the lift stages, the tyres, the top, each bolt-on part and the engine stages. A career Meerkat comes as the dealer sells
/// it (<see cref="MeerkatBuild.Dealer"/>: the 3 in lift on all-terrains with the hardtop); the other stages, tyres and tops
/// are exclusive groups (fitting one takes the others off; taking it off goes back to the dealer's), each part is bought
/// once and switched on and off for free. Base prices for a 12,000 pebble car (the career scales them by this car's price).
/// Pure (unit-tested).
/// </summary>
public static class MeerkatUpgrades
{
    public const string Lift0 = "meerkat.lift.stock", Lift2 = "meerkat.lift.2in", Lift4 = "meerkat.lift.4in";
    public const string TyreStreet = "meerkat.tyre.street", TyreMud = "meerkat.tyre.mud";
    public const string TopSoft = "meerkat.top.soft", TopOff = "meerkat.top.off";
    public const string Skid = "meerkat.skid", Rack = "meerkat.rack", Flares = "meerkat.flares", Flaps = "meerkat.flaps",
        Hook = "meerkat.hook", Snorkel = "meerkat.snorkel", Spare = "meerkat.spare";
    public const string Engine = "meerkat.engine";
    public const string LiftGroup = "meerkat.lift", TyreGroup = "meerkat.tyres", TopGroup = "meerkat.top";

    /// <summary>The template's suspension travel this car is tuned for until the live one is read (metres).</summary>
    public const float AssumedTravel = 0.25f;

    /// <summary>The parts, by their switch and upgrade id, with the order the pages list them in.</summary>
    public static readonly (Addons Part, string Id)[] PartIds =
    {
        (Addons.Skid, Skid), (Addons.Rack, Rack), (Addons.Flares, Flares), (Addons.Flaps, Flaps), (Addons.Hook, Hook),
        (Addons.Snorkel, Snorkel), (Addons.Spare, Spare),
    };

    private static UpgradeLevel L(string name, long price, string note, CarUpgradeEffect e) => new() { Name = name, Price = price, Note = note, Effect = e };

    private static UpgradeDef One(string id, string name, string category, string group, string description, UpgradeLevel level) => new()
    {
        Id = id, Name = name, Category = category, Group = group, Description = description, Cars = new[] { MeerkatDefinition.CarName },
        Levels = { level },
    };

    /// <summary>Every upgrade, with the lift stages worked out for a base travel of <paramref name="baseTravel"/> metres.</summary>
    public static List<UpgradeDef> All(float baseTravel = AssumedTravel)
    {
        CarUpgradeEffect Lift(LiftStage s, float mass) => new() { Suspension = MeerkatBuild.TravelFactor(s, baseTravel), Mass = mass };
        return new List<UpgradeDef>
        {
            One(Lift0, "Stock height", "chassis", LiftGroup, "Back to the stock springs, shocks and 14 in wheels: the roadster's own height and tyre size, quicker on tarmac, less clearance.",
                L("Stock springs and wheels", 300, "5 in lower, less travel", Lift(LiftStage.Stock, 0.99f))),
            One(Lift2, "2 inch lift", "chassis", LiftGroup, "Shorter lift springs on the long-travel shocks.",
                L("2 in coilovers", 500, "1 in lower than it came", Lift(LiftStage.TwoInch, 1.0f))),
            One(Lift4, "4 inch lift", "chassis", LiftGroup, "Longer coilovers and a size bigger tyres (225/75). The most clearance it gets.",
                L("4 in coilovers", 2400, "1 in higher, bigger tyres", new CarUpgradeEffect
                {
                    Suspension = MeerkatBuild.TravelFactor(LiftStage.FourInch, baseTravel), Mass = 1.015f, TopSpeed = 0.98f,
                })),
            One(TyreStreet, "Road tyres", "tyres", TyreGroup, "Highway tread: grippy and quiet on tarmac, poor in mud and snow.",
                L("Set of four", 650, "Better on tarmac, worse off road", new CarUpgradeEffect { TopSpeed = 1.02f })),
            One(TyreMud, "Mud-terrain tyres", "tyres", TyreGroup, "Big open blocks that clear mud and bite in snow; they hum and slide on tarmac.",
                L("Set of four", 1100, "Better in mud and snow, worse on tarmac", new CarUpgradeEffect { Mass = 1.01f, TopSpeed = 0.98f })),
            One(TopSoft, "Soft top", "body", TopGroup, "The folding cloth top instead of the hardtop.",
                L("Soft top", 450, "A little lighter", new CarUpgradeEffect { Mass = 0.985f })),
            One(TopOff, "No top", "body", TopGroup, "Both tops off: open to the sky. The rack's lamps go on a bar over the windscreen.",
                L("Top off", 50, "Lighter, open", new CarUpgradeEffect { Mass = 0.975f })),
            One(Skid, "Skid plate", "body", null, "An aluminium plate under the nose: rocks hit it, not the sump.",
                L("Skid plate", 600, "Protects the sump", new CarUpgradeEffect { Mass = 1.012f })),
            One(Rack, "Roof rack and lamps", "body", null, "A low tube rack on the hardtop with two driving lamps on its front bar (on a bar over the screen without the hardtop).",
                L("Rack and lamps", 900, "Two driving lamps", new CarUpgradeEffect { Mass = 1.008f, TopSpeed = 0.99f })),
            One(Flares, "Fender flares", "body", null, "Black flares over the arches, to cover the wider tyres.",
                L("Flares", 450, "Covers the tyres", CarUpgradeEffect.None)),
            One(Flaps, "Mud flaps", "body", null, "Rally flaps behind all four wheels.",
                L("Mud flaps", 250, "Keeps the stones down", CarUpgradeEffect.None)),
            One(Hook, "Tow hook", "tools", null, "A forged tow eye out of the front mouth.",
                L("Tow eye", 200, "Something to pull it out by", CarUpgradeEffect.None)),
            One(Snorkel, "Snorkel", "tools", null, "A raised air intake up the right windscreen pillar.",
                L("Snorkel", 650, "Breathes higher", CarUpgradeEffect.None)),
            One(Spare, "Spare wheel carrier", "tools", null, "A tube rack on the trunk lid with the spare strapped down flat.",
                L("Carrier and spare", 400, "A fifth wheel", new CarUpgradeEffect { Mass = 1.02f })),
            new UpgradeDef
            {
                Id = Engine, Name = "Engine", Category = "engine", Cars = new[] { MeerkatDefinition.CarName },
                Description = "The 1.8 twin-cam: a freer intake and exhaust, then cams and a small turbo kit.",
                Levels =
                {
                    L("Intake and exhaust", 900, "+8% power", new CarUpgradeEffect { Torque = 1.06f, Power = 1.08f, Revs = 1.02f }),
                    L("Cams and turbo kit", 4200, "+32% power, pulls hard on boost", new CarUpgradeEffect
                    {
                        Torque = 1.12f, PeakTorque = 1.16f, Power = 1.32f, Revs = 1.04f, TopSpeed = 1.03f, Mass = 1.01f,
                    }),
                },
            },
        };
    }

    /// <summary>The upgrades (id, level) a build has fitted, over the dealer's car.</summary>
    public static List<(string Id, int Level)> LevelsOf(MeerkatBuild b)
    {
        var l = new List<(string, int)>();
        if (b.Lift == LiftStage.Stock) l.Add((Lift0, 1));
        else if (b.Lift == LiftStage.TwoInch) l.Add((Lift2, 1));
        else if (b.Lift == LiftStage.FourInch) l.Add((Lift4, 1));
        if (b.Tyre == TyreKind.Street) l.Add((TyreStreet, 1));
        else if (b.Tyre == TyreKind.Mud) l.Add((TyreMud, 1));
        if (b.Top == TopKind.Soft) l.Add((TopSoft, 1));
        else if (b.Top == TopKind.None) l.Add((TopOff, 1));
        foreach (var (part, id) in PartIds) if (b.Has(part)) l.Add((id, 1));
        if (b.Engine > 0) l.Add((Engine, b.Engine));
        return l;
    }

    /// <summary>Everything a build does to the physics (the product of its fitted levels' effects): what a career's
    /// upgrades give the car, and what free play gives it from the car's page.</summary>
    public static CarUpgradeEffect EffectOf(MeerkatBuild b, float baseTravel = AssumedTravel)
    {
        var defs = All(baseTravel);
        var total = CarUpgradeEffect.None;
        foreach (var (id, level) in LevelsOf(b))
        {
            var d = defs.Find(x => x.Id == id);
            if (d == null || level < 1) continue;
            var e = d.Levels[Math.Min(level, d.Levels.Count) - 1].Effect;
            if (e != null) total = total.Times(e);
        }
        return total.Clamped();
    }

    /// <summary>The build a career has fitted on its Meerkat: <paramref name="fitted"/>(id) says whether an upgrade is
    /// owned and on, <paramref name="level"/>(id) its level.</summary>
    public static MeerkatBuild FromCareer(Func<string, bool> fitted, Func<string, int> level)
    {
        if (fitted == null || level == null) return MeerkatBuild.Dealer;
        var lift = fitted(Lift0) ? LiftStage.Stock : fitted(Lift2) ? LiftStage.TwoInch : fitted(Lift4) ? LiftStage.FourInch : LiftStage.ThreeInch;
        var tyre = fitted(TyreStreet) ? TyreKind.Street : fitted(TyreMud) ? TyreKind.Mud : TyreKind.AllTerrain;
        var top = fitted(TopSoft) ? TopKind.Soft : fitted(TopOff) ? TopKind.None : TopKind.Hard;
        var parts = Addons.None;
        foreach (var (part, id) in PartIds) if (fitted(id)) parts |= part;
        int e = fitted(Engine) ? Math.Clamp(level(Engine), 0, MeerkatBuild.MaxEngine) : 0;
        return new MeerkatBuild(lift, tyre, top, parts, (byte)e);
    }
}
