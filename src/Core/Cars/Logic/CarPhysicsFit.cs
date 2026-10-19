using System;

namespace OverTheHillPlus.Cars;

/// <summary>
/// The physics a modded car gets on top of its profile's multipliers, so that every value the profile does not name keeps
/// the donor's own proportion to the ones it does. The game's cars are built to a pattern (read from the game's car
/// prefabs and CarInfo assets, 2026-10-18): wheel inertia about 11.6 × r² and unsprung mass about 200 × r² for a tyre of
/// radius r, bump stops 10 × the spring rate, brakes 4000 / 4500 N·m and a 300 N·m torque converter on every class, and a
/// parametric inertia box of 1.7 × 0.5 × 3.75 m whatever the car's size. A car three times the donor's weight with three
/// times its torque on bigger tyres therefore got wheels a third as heavy for their load (the wheel spin stiffness rose
/// 3.7×: the wheels twitch at rest), bump stops a third as stiff for the spring, a converter that slips above 300 N·m (a
/// 612 N·m truck pulled like a 300 N·m one: it got stuck), a handbrake that held it only up to 6° (it crept and wiggled
/// on any slope) and a body as quick to pitch as a pickup's. Pure (unit-tested).
/// </summary>
public static class CarPhysicsFit
{
    /// <summary>The game's fixed step (Time.fixedDeltaTime 0.02 s) split in VPP's 2 solver substeps.</summary>
    public const float SolverStep = 0.01f;

    /// <summary>The ratio of a tyre's radius to the donor's (1 when the model's radius is not used, see
    /// <see cref="CarWheelFit.Applies"/>).</summary>
    public static float RadiusRatio(float donorRadius, float modelRadius) =>
        CarWheelFit.Applies(donorRadius, modelRadius) ? modelRadius / donorRadius : 1f;

    /// <summary>
    /// Wheel inertia and unsprung mass multiplier: the game's own r² rule for the model's tyre, times the car's mass when
    /// it is heavier than the donor (each wheel carries that much more), so a wheel's load × r² / inertia is never above
    /// the donor's. A lighter car keeps the r² rule only (its wheels are already steadier than the donor's).
    /// </summary>
    public static float WheelMassScale(CarTuningProfile p, float donorRadius, float modelRadius)
    {
        float r = RadiusRatio(donorRadius, modelRadius);
        float m = p == null ? 1f : Math.Max(1f, p.Mass);
        return Clamp(r * r * m, 0.2f, 8f);
    }

    /// <summary>Bump stop rate multiplier: the spring's, so the bump stops stay 10× the spring as on every stock car.</summary>
    public static float BumpStopScale(CarTuningProfile p) => p == null ? 1f : p.SpringRate;

    /// <summary>Brake and handbrake torque multiplier: mass × tyre radius when heavier (the same deceleration and the same
    /// slope held as the donor), never weaker than the donor's.</summary>
    public static float BrakeScale(CarTuningProfile p, float donorRadius, float modelRadius) =>
        p == null ? 1f : Clamp(Math.Max(1f, p.Mass * RadiusRatio(donorRadius, modelRadius)), 1f, 6f);

    /// <summary>Torque converter (clutch) capacity multiplier: the engine's torque multiplier when stronger, so the
    /// converter passes the tuned torque as the donor's passes its own.</summary>
    public static float ClutchScale(CarTuningProfile p) => p == null ? 1f : Math.Max(1f, p.Torque);

    /// <summary>Engine inertia and friction multiplier: the torque's when stronger, so the engine revs (and holds its idle)
    /// the way the donor's does instead of a crank 3.5× too light for its torque.</summary>
    public static float EngineMassScale(CarTuningProfile p) => p == null ? 1f : Math.Max(1f, p.Torque);

    /// <summary>
    /// The parametric inertia box's length (or width) multiplier: the model's wheelbase (track) over the donor's, so the
    /// body pitches (rolls) on its springs at the donor's frequency and damping ratio: pitch stiffness grows with the
    /// wheelbase squared and pitch inertia with the box length squared.
    /// </summary>
    public static float ChassisScale(float modelSpan, float donorSpan)
    {
        if (!(donorSpan > 0.1f) || !(modelSpan > 0.1f)) return 1f;
        return Clamp(modelSpan / donorSpan, 0.5f, 2f);
    }

    // ---------------------------------------------------------------- analysis

    /// <summary>Damping ratio of a mass on a spring and damper: c / (2 √(k m)).</summary>
    public static float DampingRatio(float springRate, float damperRate, float mass) =>
        springRate > 0f && mass > 0f ? damperRate / (2f * MathF.Sqrt(springRate * mass)) : 0f;

    /// <summary>Steepest slope (degrees) a parked car holds on a brake torque of <paramref name="torque"/> N·m in all.</summary>
    public static float HoldSlopeDegrees(float torque, float mass, float tyreRadius)
    {
        if (!(mass > 0f) || !(tyreRadius > 0f)) return 0f;
        float s = torque / (mass * 9.81f * tyreRadius);
        return s >= 1f ? 90f : MathF.Asin(Math.Max(0f, s)) * 180f / MathF.PI;
    }

    /// <summary>How hard a tyre's grip pulls on the wheel's spin compared with the wheel's inertia: load (kg) × r² / I.
    /// The wheel's spin is integrated each solver step, so this must not be far above the donor's.</summary>
    public static float SpinStiffness(float cornerLoadKg, float tyreRadius, float wheelInertia) =>
        wheelInertia > 0f ? cornerLoadKg * tyreRadius * tyreRadius / wheelInertia : float.PositiveInfinity;

    // ---------------------------------------------------------------- colliders

    /// <summary>Most a body collider's bottom is lifted above the body's lowest point.</summary>
    public const float MaxColliderLift = 0.15f, MinColliderLift = 0.05f;

    /// <summary>
    /// How far above the body's lowest point its collider starts. The game's cars keep theirs 0.08 to 0.23 m above their
    /// visible bottom (sills and bumpers skim a rock instead of hooking it): the donor's lift, scaled to the model's tyre,
    /// inside [<see cref="MinColliderLift"/>, <see cref="MaxColliderLift"/>].
    /// </summary>
    public static float ColliderLift(float donorLift, float donorRadius, float modelRadius)
    {
        if (float.IsNaN(donorLift) || float.IsInfinity(donorLift)) donorLift = MinColliderLift;
        return Clamp(donorLift * RadiusRatio(donorRadius, modelRadius), MinColliderLift, MaxColliderLift);
    }

    /// <summary>The lowest a body collider may reach: the body's bottom plus the lift, never below the collider's own
    /// bottom and never more than halfway up the collider.</summary>
    public static float ColliderFloor(float colliderMin, float colliderMax, float bodyMin, float lift)
    {
        float want = Math.Max(colliderMin, bodyMin + lift);
        return Math.Min(want, colliderMin + 0.5f * Math.Max(0f, colliderMax - colliderMin));
    }

    /// <summary>The lowest and highest y of a part's vertices in model space (its transform applied).</summary>
    public static void HeightRange(MeshPart part, out float min, out float max)
    {
        min = float.MaxValue; max = float.MinValue;
        if (part?.Positions == null) { min = max = 0f; return; }
        var m = MeshFrames.PartMatrix(part);
        for (int i = 0; i + 2 < part.Positions.Length; i += 3)
        {
            float y = MeshFrames.Apply(m, new MeshVec3(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2])).Y;
            if (y < min) min = y;
            if (y > max) max = y;
        }
        if (min > max) min = max = 0f;
    }

    /// <summary>A copy of a collider part in model space (identity transform) with every vertex below
    /// <paramref name="floorY"/> raised to it: the convex hull the physics builds from it starts at the floor.</summary>
    public static MeshPart Floored(MeshPart part, float floorY)
    {
        if (part?.Positions == null) return part;
        var m = MeshFrames.PartMatrix(part);
        var pos = new float[part.Positions.Length];
        for (int i = 0; i + 2 < pos.Length; i += 3)
        {
            var v = MeshFrames.Apply(m, new MeshVec3(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
            pos[i] = v.X; pos[i + 1] = Math.Max(v.Y, floorY); pos[i + 2] = v.Z;
        }
        return new MeshPart
        {
            Name = part.Name, Kind = part.Kind, Position = new MeshVec3(0f, 0f, 0f), Rotation = new MeshQuat(0f, 0f, 0f, 1f),
            Scale = new MeshVec3(1f, 1f, 1f), Positions = pos, Normals = part.Normals, Uv0 = part.Uv0, Indices = part.Indices,
        };
    }

    /// <summary>The donor's bump stop collider (its own tyre, riding 0.38 m over the hub) resized to the model's tyre:
    /// radius ratio across the wheel, width ratio along the axle.</summary>
    public static MeshVec3 BumpStopColliderScale(float donorRadius, float modelRadius, float donorWidth, float modelWidth)
    {
        float r = RadiusRatio(donorRadius, modelRadius);
        float w = donorWidth > 0.05f && modelWidth > 0.05f ? Clamp(modelWidth / donorWidth, 0.5f, 2.5f) : 1f;
        return new MeshVec3(w, r, r);
    }

    private static float Clamp(float v, float lo, float hi) => float.IsNaN(v) ? 1f : Math.Clamp(v, lo, hi);
}

/// <summary>The multipliers a modded car applies at runtime besides its profile's (<see cref="CarPhysicsFit"/>): values
/// the game writes from the car's class setup when it spawns (brakes, converter, engine, inertia box), so they are tuned
/// like the engine's torque, on top of whatever the game wrote. Pure.</summary>
public sealed class CarRuntimeFit
{
    public float Brakes = 1f, Clutch = 1f, EngineMass = 1f, ChassisX = 1f, ChassisZ = 1f;
    /// <summary>The converter's lock / unlock rpm follow the rev range.</summary>
    public float ClutchRpm = 1f;

    public static readonly CarRuntimeFit None = new();

    public static CarRuntimeFit For(CarTuningProfile p, float donorRadius, float modelRadius, float donorWheelbase, float modelWheelbase,
        float donorTrack, float modelTrack) => new()
    {
        Brakes = CarPhysicsFit.BrakeScale(p, donorRadius, modelRadius),
        Clutch = CarPhysicsFit.ClutchScale(p),
        EngineMass = CarPhysicsFit.EngineMassScale(p),
        ChassisX = CarPhysicsFit.ChassisScale(modelTrack, donorTrack),
        ChassisZ = CarPhysicsFit.ChassisScale(modelWheelbase, donorWheelbase),
        ClutchRpm = p == null ? 1f : p.RevRange,
    };

    public string Describe() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "brakes x{0:0.##}, converter x{1:0.##} (lock rpm x{2:0.##}), engine inertia x{3:0.##}, inertia box x{4:0.##} wide x{5:0.##} long",
        Brakes, Clutch, ClutchRpm, EngineMass, ChassisX, ChassisZ);
}
