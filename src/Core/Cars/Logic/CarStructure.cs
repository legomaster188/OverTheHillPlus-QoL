using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Cars;

/// <summary>One wheel of a car object as the structure check read it.</summary>
public sealed class CarWheelFacts
{
    /// <summary>FL, FR, RL or RR.</summary>
    public string Corner;
    /// <summary>The wheel collider's radius, metres (0: no collider: always so on a garage model).</summary>
    public float ColliderRadius;
    /// <summary>The largest tyre drawn at this corner, metres (0: none).</summary>
    public float TyreRadius;
    /// <summary>A rim is drawn at this corner (on the wheel or on a tandem's follower).</summary>
    public bool Rim;
    /// <summary>The lowest point of the tyre drawn here (car-root y).</summary>
    public float Bottom;
    /// <summary>The wheel's bump stop has a mesh.</summary>
    public bool BumpStop;
}

/// <summary>One garage part's models on a car object, in car-root space.</summary>
public sealed class CarItemFacts
{
    /// <summary>The upgrade object's path under the cosmetic root ("Visual/Rear/Bike").</summary>
    public string Path;
    public MeshVec3 Min, Max;
}

/// <summary>What the structure check read off one car object (the networked template or the garage model).</summary>
public sealed class CarStructureFacts
{
    /// <summary>True for the networked template (it has physics, lights and colliders), false for the garage model.</summary>
    public bool Template;
    public VehicleKind Kind = VehicleKind.Car;
    public readonly List<CarWheelFacts> Wheels = new();
    /// <summary>The body's bounds (car-root space) and the ground under the model's tyres (car-root y).</summary>
    public MeshVec3 BodyMin, BodyMax;
    public float Ground;

    /// <summary>Mesh renderers drawing a mesh and those of them with no material in some slot (their first few names).</summary>
    public int Renderers;
    public readonly List<string> NoMaterial = new();

    /// <summary>The body's levels of detail with a mesh, the body renderers without one, and the body's LOD group (its LOD
    /// count; -1: no group, on the group's enabled flag).</summary>
    public int BodyLods;
    public int BodyNoMesh;
    public int LodGroupCount = -1;
    public bool LodGroupOn = true;
    /// <summary>LOD groups on the whole object.</summary>
    public int LodGroups;

    /// <summary>Slots switched on, those of them with a part on offer, and the parts on offer in those slots.</summary>
    public int SlotsShown, SlotsFilled;
    public readonly List<CarItemFacts> Items = new();
    /// <summary>Parts on offer whose objects draw nothing, not even a mesh-less marker's renderer (a part with no model at
    /// all has no object and isn't counted).</summary>
    public int ItemsNoMesh;

    /// <summary>The lamps the framework places, those found, and the ones of them switched off.</summary>
    public int LightsWanted, Lights;
    public readonly List<string> LightsMissing = new();

    /// <summary>Enabled body colliders with a mesh (or a box), and body colliders with no mesh.</summary>
    public int BodyColliders, BodyCollidersNoMesh;
    /// <summary>The body box colliders the definition asks for, and those found.</summary>
    public int BodyBoxesWanted, BodyBoxes;
}

/// <summary>What the check says about one car object.</summary>
public sealed class CarStructureResult
{
    public readonly List<string> Problems = new();
    /// <summary>The object in a few words ("4 wheels r 0.388, 13 slots, 97 parts, 4 LODs, 9 lights").</summary>
    public string Text = "";
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// The judge of the start-up structure check (<see cref="CarStructure"/>'s caller, CarStructureCheck, only reads the car
/// objects): whether a modded car's networked template and garage model are whole, from what was read off them, and the
/// lines the log gets. Nothing here looks at a game object. Pure (unit-tested).
/// </summary>
public static class CarStructure
{
    /// <summary>A collider radius and the tyre drawn on it may differ this much (metres): a tyre this far from its collider
    /// shows as a tyre in the ground or in the air.</summary>
    public const float RadiusTolerance = 0.02f;
    /// <summary>How far apart a garage model's tyres may stand from each other in height (metres).</summary>
    public const float LevelTolerance = 0.015f;
    /// <summary>A part's models this far from the body's box (metres) aren't on the car. Measured on the shipped cars: no
    /// part of the 22 stands more than 0.2 m off (a bike or a spare on its carrier still meets the body's box).</summary>
    public const float FarFromBody = 0.25f;
    /// <summary>A part may reach this far under the model's ground (metres): mud flaps and skid plates hang low, and a
    /// tyre's squash is about this much.</summary>
    public const float UnderGround = 0.03f;
    /// <summary>The node every antenna topper hangs under, at the tip of the antenna's bones: how a topper is known when its
    /// antenna isn't on the car's list of parts.</summary>
    public const string TopperNode = "/TopperAttachments/";
    /// <summary>The most names one kind of problem lists.</summary>
    private const int MaxNames = 5;

    /// <summary>How far apart two boxes are (metres; 0 when they touch or overlap).</summary>
    public static float Gap(MeshVec3 aMin, MeshVec3 aMax, MeshVec3 bMin, MeshVec3 bMax)
    {
        static float Axis(float a0, float a1, float b0, float b1) => Math.Max(0f, Math.Max(a0 - b1, b0 - a1));
        float x = Axis(aMin.X, aMax.X, bMin.X, bMax.X), y = Axis(aMin.Y, aMax.Y, bMin.Y, bMax.Y), z = Axis(aMin.Z, aMax.Z, bMin.Z, bMax.Z);
        return (float)Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>The part <paramref name="item"/> is mounted on: the deepest other part of the list whose object it's under (an
    /// axe on the spare wheel, a topper on the antenna). Null for a part that stands on the car itself.</summary>
    public static CarItemFacts HostOf(CarItemFacts item, IReadOnlyList<CarItemFacts> items)
    {
        CarItemFacts host = null;
        if (item?.Path == null || items == null) return null;
        foreach (var o in items)
        {
            if (o == null || o == item || o.Path == null || o.Path.Length >= item.Path.Length) continue;
            if (!CarCosmetics.IsUnder(item.Path, o.Path)) continue;
            if (host == null || o.Path.Length > host.Path.Length) host = o;
        }
        return host;
    }

    /// <summary>A part that rides on another accessory is judged by that accessory, not the body: it's meant to stand as far
    /// off as its host holds it (a topper on a 1.5 m antenna, an axe strapped on a spare wheel). It counts as on the car when
    /// its host is on the body and it's still at its host. With no host on the list only a topper gets this: nothing else
    /// says what holds the part up.</summary>
    public static bool RidesOnHost(CarItemFacts item, CarStructureFacts f)
    {
        var host = HostOf(item, f.Items);
        if (host == null) return item.Path != null && item.Path.Contains(TopperNode);
        return Gap(host.Min, host.Max, f.BodyMin, f.BodyMax) <= FarFromBody && Gap(item.Min, item.Max, host.Min, host.Max) <= FarFromBody;
    }

    /// <summary>Judges one car object.</summary>
    public static CarStructureResult Judge(CarStructureFacts f)
    {
        var r = new CarStructureResult();
        if (f == null)
        {
            r.Problems.Add("nothing was read");
            return r;
        }
        JudgeWheels(f, r);
        if (f.NoMaterial.Count > 0) r.Problems.Add($"{f.NoMaterial.Count} renderer(s) with no material ({Names(f.NoMaterial)})");
        if (f.Renderers == 0) r.Problems.Add("nothing is drawn");
        JudgeLods(f, r);
        JudgeItems(f, r);
        if (f.Template)
        {
            if (f.Lights < f.LightsWanted) r.Problems.Add($"{f.LightsWanted - f.Lights} of {f.LightsWanted} lamp(s) missing or off ({Names(f.LightsMissing)})");
            if (f.BodyColliders < 1) r.Problems.Add("no body collider");
            if (f.BodyCollidersNoMesh > 0) r.Problems.Add($"{f.BodyCollidersNoMesh} body collider(s) with no mesh");
            if (f.BodyBoxes < f.BodyBoxesWanted) r.Problems.Add($"{f.BodyBoxes} of {f.BodyBoxesWanted} body box(es)");
        }
        r.Text = Text(f);
        return r;
    }

    private static void JudgeWheels(CarStructureFacts f, CarStructureResult r)
    {
        bool tyres = VehicleKinds.HasTyres(f.Kind);
        if (f.Wheels.Count != 4) r.Problems.Add($"{f.Wheels.Count} wheel(s), not 4");
        float lowest = float.MaxValue, highest = float.MinValue;
        var noTyre = new List<string>();
        var noRim = new List<string>();
        var off = new List<string>();
        var noStop = new List<string>();
        foreach (var w in f.Wheels)
        {
            if (f.Template && w.ColliderRadius <= 0f) r.Problems.Add($"wheel {w.Corner} has no wheel collider");
            if (f.Template && !w.BumpStop) noStop.Add(w.Corner);
            if (!tyres) continue; // a sled's and a boat's wheels are hidden physics wheels: nothing is drawn on them
            if (w.TyreRadius <= 0f) { noTyre.Add(w.Corner); continue; }
            if (!w.Rim) noRim.Add(w.Corner);
            if (f.Template && w.ColliderRadius > 0f && Math.Abs(w.ColliderRadius - w.TyreRadius) > RadiusTolerance)
                off.Add($"{w.Corner} collider {M(w.ColliderRadius)} m, tyre {M(w.TyreRadius)} m");
            lowest = Math.Min(lowest, w.Bottom);
            highest = Math.Max(highest, w.Bottom);
        }
        if (noTyre.Count > 0) r.Problems.Add($"no tyre on {Names(noTyre)}");
        if (noRim.Count > 0) r.Problems.Add($"no rim on {Names(noRim)}");
        if (off.Count > 0) r.Problems.Add("tyre radius off: " + Names(off));
        if (noStop.Count > 0) r.Problems.Add($"no bump stop on {Names(noStop)}");
        if (!f.Template && highest >= lowest && highest - lowest > LevelTolerance)
            r.Problems.Add($"the tyres don't stand level ({M(highest - lowest)} m apart)");
    }

    private static void JudgeLods(CarStructureFacts f, CarStructureResult r)
    {
        if (f.BodyLods < 1) r.Problems.Add("no body mesh");
        if (f.BodyNoMesh > 0) r.Problems.Add($"{f.BodyNoMesh} body renderer(s) with no mesh");
        if (f.LodGroupCount < 0) r.Problems.Add("no LOD group on the body");
        else
        {
            if (!f.LodGroupOn) r.Problems.Add("the body's LOD group is off");
            if (f.LodGroupCount != f.BodyLods) r.Problems.Add($"the body's LOD group has {f.LodGroupCount} levels for {f.BodyLods} body LOD(s)");
        }
    }

    private static void JudgeItems(CarStructureFacts f, CarStructureResult r)
    {
        // A sled and a boat carry none of the game's cosmetics (every slot hidden is their normal state).
        if (VehicleKinds.HasTyres(f.Kind) && f.SlotsFilled == 0) r.Problems.Add("no cosmetic slot has a part");
        if (f.ItemsNoMesh > 0) r.Problems.Add($"{f.ItemsNoMesh} part(s) on offer draw nothing");
        var far = new List<string>();
        var low = new List<string>();
        foreach (var i in f.Items)
        {
            float gap = Gap(i.Min, i.Max, f.BodyMin, f.BodyMax);
            if (gap > FarFromBody && !RidesOnHost(i, f)) far.Add($"{i.Path} {M(gap)} m");
            if (i.Min.Y < f.Ground - UnderGround) low.Add($"{i.Path} {M(f.Ground - i.Min.Y)} m");
        }
        if (far.Count > 0) r.Problems.Add($"{far.Count} part(s) off the body ({Names(far)})");
        if (low.Count > 0) r.Problems.Add($"{low.Count} part(s) under the ground ({Names(low)})");
    }

    private static string Text(CarStructureFacts f)
    {
        var sb = new StringBuilder();
        sb.Append(f.Wheels.Count.ToString(CultureInfo.InvariantCulture)).Append(" wheels");
        float radius = 0f;
        int tyres = 0;
        foreach (var w in f.Wheels)
        {
            radius = Math.Max(radius, f.Template ? w.ColliderRadius : w.TyreRadius);
            if (w.TyreRadius > 0f) tyres++;
        }
        sb.Append(" r ").Append(M(radius)).Append(", ").Append(tyres.ToString(CultureInfo.InvariantCulture)).Append(" tyres, ");
        sb.Append(f.SlotsFilled.ToString(CultureInfo.InvariantCulture)).Append('/').Append(f.SlotsShown.ToString(CultureInfo.InvariantCulture)).Append(" slots, ");
        sb.Append(f.Items.Count.ToString(CultureInfo.InvariantCulture)).Append(" parts, ");
        sb.Append(f.Renderers.ToString(CultureInfo.InvariantCulture)).Append(" renderers, ");
        sb.Append(f.BodyLods.ToString(CultureInfo.InvariantCulture)).Append(" body LODs, ");
        sb.Append(f.LodGroups.ToString(CultureInfo.InvariantCulture)).Append(" LOD groups");
        if (f.Template)
            sb.Append(", ").Append(f.Lights.ToString(CultureInfo.InvariantCulture)).Append(" lamps, ")
              .Append((f.BodyColliders + f.BodyBoxes).ToString(CultureInfo.InvariantCulture)).Append(" body colliders");
        return sb.ToString();
    }

    /// <summary>The car's log line: "Vera: OK. car: ...; garage model: ...", or its problems.</summary>
    public static string Line(string car, CarStructureResult template, CarStructureResult garage)
    {
        var sb = new StringBuilder(car ?? "?");
        var problems = new List<string>();
        if (template != null) foreach (var p in template.Problems) problems.Add("car: " + p);
        if (garage != null) foreach (var p in garage.Problems) problems.Add("garage model: " + p);
        sb.Append(problems.Count == 0 ? ": OK. " : $": {problems.Count} PROBLEM(S): {string.Join("; ", problems)}. ");
        sb.Append("car: ").Append(template?.Text ?? "not read").Append("; garage model: ").Append(garage?.Text ?? "not read").Append('.');
        return sb.ToString();
    }

    /// <summary>The closing line: how many cars were proved, by name the ones that weren't.</summary>
    public static string Summary(int checkedCars, IReadOnlyCollection<string> problems, IReadOnlyCollection<string> notBuilt)
    {
        int bad = problems?.Count ?? 0, off = notBuilt?.Count ?? 0;
        var sb = new StringBuilder("summary: ");
        sb.Append((checkedCars + off).ToString(CultureInfo.InvariantCulture)).Append(" car(s), ");
        sb.Append((checkedCars - bad).ToString(CultureInfo.InvariantCulture)).Append(" whole");
        if (bad > 0) sb.Append(", ").Append(bad.ToString(CultureInfo.InvariantCulture)).Append(" with problems (").Append(string.Join(", ", problems)).Append(')');
        if (off > 0) sb.Append(", ").Append(off.ToString(CultureInfo.InvariantCulture)).Append(" not built (").Append(string.Join(", ", notBuilt)).Append(')');
        return sb.Append('.').ToString();
    }

    private static string M(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Names(List<string> names)
    {
        if (names.Count <= MaxNames) return string.Join(", ", names);
        return string.Join(", ", names.GetRange(0, MaxNames)) + $" and {names.Count - MaxNames} more";
    }
}
