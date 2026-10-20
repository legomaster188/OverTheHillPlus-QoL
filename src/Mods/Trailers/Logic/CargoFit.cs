using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Trailers;

/// <summary>One tyre of a load: its centre in the car's own space (Unity axes, m) and its radius.</summary>
public readonly struct CargoTyre
{
    public readonly float X, Y, Z, Radius;
    public CargoTyre(float x, float y, float z, float radius) { X = x; Y = y; Z = z; Radius = radius; }
    public float Bottom => Y - Radius;
}

/// <summary>A wheel chock on the deck: where its origin goes (deck space, on the deck surface) and which way its toe
/// points (true: towards the deck's front, +z).</summary>
public readonly struct ChockPlace
{
    public readonly float X, Z;
    public readonly bool ToeForward;
    public ChockPlace(float x, float z, bool toeForward) { X = x; Z = z; ToeForward = toeForward; }
}

/// <summary>Where the car hauler's load goes: the car's offset in the deck's space (the "deck" anchor: on the deck
/// surface, y up, z towards the hitch) and its wheel chocks.</summary>
public sealed class CargoSeat
{
    public float X, Y, Z;
    public readonly List<ChockPlace> Chocks = new();
    /// <summary>False when the load is too long for the flat deck: with its nose clear of the headboard its rear wheels
    /// would stand on the dovetail or past the deck's end, in the air (a tractor unit on the car hauler). The hauler
    /// doesn't carry it.</summary>
    public bool Fits = true;
}

/// <summary>
/// Seats the car hauler's load. The load is the car's garage model, a picture without physics parented to the deck, so
/// it can't bounce off; what matters is that it stands where a real car would: its tyres on the deck boards (not the
/// lowest point of whatever renderers the model has: the garage models carry a shadow box reaching 0.5-1.2 m under the
/// tyres), centred across the deck on its wheels, along it on its body, with its rear wheels on the flat part of the
/// deck and its nose clear of the headboard, and chocked in front of the front wheels and behind the rear ones.
/// Pure code (unit-tested).
/// </summary>
public static class CargoFit
{
    /// <summary>How far the tyres sit into the deck surface, m (a loaded tyre's flat spot; no gap at the contact).</summary>
    public const float Sink = 0.01f;
    /// <summary>Tyres this close (along the car) to the frontmost / rearmost one are on the same axle, m.</summary>
    public const float AxleBand = 0.3f;
    /// <summary>Clearance between a chock's face and its tyre, m.</summary>
    public const float ChockGap = 0.008f;
    /// <summary>How far past the flat deck's end a rear wheel may stand (the dovetail drops under 2 cm there), m.</summary>
    public const float DovetailLip = 0.10f;

    // The chock mesh (Liveries/tools/trailers/build_trailers.py: chock()): toe towards +z, origin on its back edge,
    // base on y = 0. Its sloped face runs from (z = Length, y = ToeHeight) up to (z = TopLength, y = TopHeight).
    public const float ChockWidth = 0.24f, ChockLength = 0.17f, ChockToeHeight = 0.015f, ChockTopHeight = 0.11f, ChockTopLength = 0.05f;

    /// <summary>Lowest names of meshes that are never the visible car: the shadow box, vegetation quads, wheel helpers,
    /// track stamps (as Liveries/tools/export_car.py skips them).</summary>
    public static bool IsHelper(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var h in Helpers)
            if (name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static readonly string[] Helpers = { "ShadowCube", "QuadMesh", "collider", "Trax" };

    /// <summary>A tyre mesh of a car's model (the game's are &lt;Car&gt;_tyre_LOD0, the RustOx's tyrerear; modded cars keep
    /// the donor's objects). LOD1-3 are left out: they lie on LOD0.</summary>
    public static bool IsTyre(string name)
    {
        if (string.IsNullOrEmpty(name) || IsHelper(name)) return false;
        bool tyre = name.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!tyre) return false;
        int lod = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
        return lod < 0 || (lod + 4 < name.Length && name[lod + 4] == '0');
    }

    /// <summary>A tyre's radius from its mesh bounds' half sizes (mesh space, already scaled): the axle runs along the
    /// thinnest one, the radius is the larger of the other two.</summary>
    public static float TyreRadius(float ex, float ey, float ez)
    {
        float min = Math.Min(ex, Math.Min(ey, ez));
        if (ex == min) return Math.Max(ey, ez);
        if (ey == min) return Math.Max(ex, ez);
        return Math.Max(ex, ey);
    }

    /// <summary>How far behind (or in front of) a tyre's centre a chock's origin goes so its face clears the tyre by
    /// <see cref="ChockGap"/> at every height (the tyre pressed <see cref="Sink"/> into the deck).</summary>
    public static float ChockDistance(float radius)
    {
        float r = Math.Max(radius, 0.1f);
        float d = 0f;
        const int samples = 12;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            float z = ChockLength + (ChockTopLength - ChockLength) * t;
            float y = ChockToeHeight + (ChockTopHeight - ChockToeHeight) * t;
            d = Math.Max(d, z + HalfChord(r, y + Sink));
        }
        // the chock's flat top (z 0..TopLength at TopHeight) is behind the face's top: already covered
        return d + ChockGap;
    }

    /// <summary>Half the width of a circle of radius r cut at height h above its bottom.</summary>
    public static float HalfChord(float r, float h)
    {
        if (h <= 0f) return 0f;
        if (h >= r) return r;
        float a = r - h;
        return MathF.Sqrt(Math.Max(r * r - a * a, 0f));
    }

    /// <summary>
    /// The seat of a load. <paramref name="tyres"/>: its tyres (car space; none: fall back to the body's lowest point).
    /// <paramref name="lo"/>/<paramref name="hi"/>: its visible body's bounds (car space, helpers left out).
    /// <paramref name="flatRear"/>/<paramref name="front"/>: the deck's flat part from the dovetail to the headboard,
    /// along z in deck space (NaN: no limit).
    /// </summary>
    public static CargoSeat Seat(IReadOnlyList<CargoTyre> tyres, (float x, float y, float z) lo, (float x, float y, float z) hi,
                                 float flatRear = float.NaN, float front = float.NaN)
    {
        var s = new CargoSeat();
        bool haveTyres = tyres != null && tyres.Count > 0;
        if (!haveTyres)
        {
            s.X = -(lo.x + hi.x) * 0.5f;
            s.Y = -lo.y;
            s.Z = -(lo.z + hi.z) * 0.5f;
            return s;
        }
        float bottom = float.MaxValue, minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var t in tyres)
        {
            bottom = Math.Min(bottom, t.Bottom);
            minX = Math.Min(minX, t.X);
            maxX = Math.Max(maxX, t.X);
            minZ = Math.Min(minZ, t.Z);
            maxZ = Math.Max(maxZ, t.Z);
        }
        s.Y = -bottom - Sink;
        s.X = -(minX + maxX) * 0.5f;
        s.Z = -(lo.z + hi.z) * 0.5f;

        float rearR = 0f, frontR = 0f;
        foreach (var t in tyres)
        {
            if (t.Z <= minZ + AxleBand) rearR = Math.Max(rearR, t.Radius);
            if (t.Z >= maxZ - AxleBand) frontR = Math.Max(frontR, t.Radius);
        }
        float rearD = ChockDistance(rearR), frontD = ChockDistance(frontR);

        // rear chocks on the flat deck, nose clear of the headboard (the nose wins: a load never pokes through it)
        if (float.IsFinite(flatRear))
        {
            float rearBack = minZ + s.Z - rearD;
            if (rearBack < flatRear) s.Z += flatRear - rearBack;
        }
        if (float.IsFinite(front))
        {
            float nose = Math.Max(hi.z, maxZ + frontD) + s.Z;
            if (nose > front) s.Z -= nose - front;
        }
        // the rearmost wheels must still stand on the flat deck (a little onto the dovetail's lip is fine)
        if (float.IsFinite(flatRear) && minZ + s.Z < flatRear - DovetailLip) s.Fits = false;

        foreach (var t in tyres)
        {
            if (t.Z <= minZ + AxleBand) AddChock(s, t.X + s.X, t.Z + s.Z - rearD, true, flatRear, front);
            if (t.Z >= maxZ - AxleBand && maxZ - minZ > AxleBand) AddChock(s, t.X + s.X, t.Z + s.Z + frontD, false, flatRear, front);
        }
        return s;
    }

    private static void AddChock(CargoSeat s, float x, float z, bool toeForward, float flatRear, float front)
    {
        // its footprint: origin..origin+Length along its toe; only on the flat deck (on the dovetail it would float)
        float a = toeForward ? z : z - ChockLength, b = toeForward ? z + ChockLength : z;
        if (float.IsFinite(flatRear) && a < flatRear - 0.005f) return;
        if (float.IsFinite(front) && b > front + 0.005f) return;
        s.Chocks.Add(new ChockPlace(x, z, toeForward));
    }
}

/// <summary>
/// The trailers' adjustable coupler: the head bolts into a vertical channel at the drawbar's tip, at whole steps of
/// <see cref="Step"/> within <see cref="Range"/> up or down (the bolt holes; Liveries/tools/trailers/build_trailers.py:
/// COUPLER_RANGE, COUPLER_STEP). Hitching sets it to the car's ball height, so a trailer rides level behind any car
/// instead of nose-down behind a low ball or nose-up behind a high one. Pure code (unit-tested).
/// </summary>
public static class CouplerFit
{
    public const float Range = 0.20f;
    public const float Step = 0.05f;

    /// <summary>The head's offset from its middle setting, m: the ball's height over the ground minus the coupler's
    /// (both at the trailer's level pose), to the nearest hole, within the channel. 0 when either is unknown.</summary>
    public static float Offset(float ballAboveGround, float couplerAboveGround)
    {
        float raw = ballAboveGround - couplerAboveGround;
        if (!float.IsFinite(raw)) return 0f;
        float snapped = MathF.Round(raw / Step) * Step;
        return Math.Clamp(snapped, -Range, Range);
    }

    /// <summary>The head's offset a hitch anchor stands for (the anchor's height above the model's own hitch), kept
    /// within the channel and at a hole: what every peer draws from the state's anchor.</summary>
    public static float FromAnchor(float anchorY, float modelHitchY)
    {
        float d = anchorY - modelHitchY;
        if (!float.IsFinite(d)) return 0f;
        return Math.Clamp(MathF.Round(d / Step) * Step, -Range, Range);
    }
}
