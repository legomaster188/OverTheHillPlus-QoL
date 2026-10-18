using System.Collections.Generic;
using OverTheHillPlus.Features.Cheats.Teleport;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.TrackBuilder;

internal enum BuildPattern { Ramp, Bridge, Plank, Anchor, Wall, Parking, Slalom, Car }

internal enum PieceKind { Plank, Car, Anchor }

/// <summary>One thing to place: a plank pose (pivot + rotation), a car spot (+ yaw) or a winch anchor point.</summary>
internal struct Piece
{
    public PieceKind Kind;
    public Vector3 Pos;
    public Quaternion Rot;
    public float Yaw;
    public ItemCatalog.Item Car;
}

/// <summary>
/// Turns a pattern and its settings into world poses, relative to your car (its flat heading), with every spot
/// dropped onto the ground under it. Pure planning: nothing is placed here.
/// </summary>
internal static class BuildPlanner
{
    private const float PlankOverlap = 0.06f; // consecutive planks overlap a little so there's no lip
    private const float PlankLift = 0.02f;
    private const float CarLift = 0.8f;

    public static bool UsesCars(BuildPattern p) => p is BuildPattern.Wall or BuildPattern.Parking or BuildPattern.Slalom or BuildPattern.Car;
    public static bool UsesPlanks(BuildPattern p) => p is BuildPattern.Ramp or BuildPattern.Bridge or BuildPattern.Plank;

    public static string Name(BuildPattern p) => p switch
    {
        BuildPattern.Ramp => "Ramp",
        BuildPattern.Bridge => "Bridge",
        BuildPattern.Plank => "Plank",
        BuildPattern.Anchor => "Winch anchor",
        BuildPattern.Wall => "Wall of cars",
        BuildPattern.Parking => "Parking row",
        BuildPattern.Slalom => "Slalom",
        _ => "Car",
    };

    /// <summary>Your car's position, flat forward and right vectors and yaw; false without a car.</summary>
    public static bool TryOrigin(out Vector3 pos, out Vector3 fwd, out Vector3 right, out float yaw)
    {
        pos = default; fwd = Vector3.forward; right = Vector3.right; yaw = 0f;
        var car = GameState.LocalCar;
        if (car == null) return false;
        Transform t;
        try { t = car.cachedTransform != null ? car.cachedTransform : car.transform; }
        catch (System.Exception ex) { DebugLog.Error("hostpowers", ex, "BuildPlanner.TryOrigin"); return false; }
        if (t == null) return false;
        pos = t.position;
        var f = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        if (f.sqrMagnitude < 0.01f) f = Quaternion.Euler(0f, t.eulerAngles.y, 0f) * Vector3.forward;
        fwd = f.normalized;
        right = Vector3.Cross(Vector3.up, fwd).normalized;
        yaw = Quaternion.LookRotation(fwd, Vector3.up).eulerAngles.y;
        return true;
    }

    private static Vector3 Ground(Vector3 p, float refY) =>
        TeleportCore.Ground(new Vector3(p.x, refY, p.z), GameState.LocalCar, 0f);

    private static Piece PlankAt(Vector3 nearBottomCenter, Vector3 dir)
    {
        var rot = PlankShape.RotationFor(dir);
        var pos = nearBottomCenter - rot * PlankShape.NearBottomLocal + Vector3.up * PlankLift;
        return new Piece { Kind = PieceKind.Plank, Pos = pos, Rot = rot };
    }

    /// <summary>World centre of a planned plank (for the preview).</summary>
    public static Vector3 PlankCenter(in Piece p) => p.Pos + p.Rot * PlankShape.CenterLocal;

    private static float LaneOffset(int lane, int lanes, float pitch) => (lane - (lanes - 1) * 0.5f) * pitch;

    /// <summary>Plans <paramref name="pattern"/> with the current settings; returns an error message or null.</summary>
    public static string Plan(BuildPattern pattern, List<Piece> into)
    {
        into.Clear();
        if (!TryOrigin(out var o, out var fwd, out var right, out var yaw)) return "No car: get in your car first.";
        if (UsesCars(pattern) && BuildCars.Selected == null) return "No car models found yet (load into a world).";
        PlankShape.Ensure();
        float d = TrackBuilderFeature.Distance.Value;
        float refY = o.y;
        float len = Mathf.Max(0.5f, PlankShape.Length - PlankOverlap);
        float lanePitch = PlankShape.Width + TrackBuilderFeature.LaneGap.Value;

        switch (pattern)
        {
            case BuildPattern.Ramp:
            {
                int lanes = Count(TrackBuilderFeature.RampLanes);
                int segs = Count(TrackBuilderFeature.RampSegments);
                float a = TrackBuilderFeature.RampAngle.Value * Mathf.Deg2Rad;
                var dir = (fwd * Mathf.Cos(a) + Vector3.up * Mathf.Sin(a)).normalized;
                for (int j = 0; j < lanes; j++)
                {
                    var start = Ground(o + fwd * d + right * LaneOffset(j, lanes, lanePitch), refY);
                    for (int k = 0; k < segs; k++) into.Add(PlankAt(start + dir * (k * len), dir));
                }
                break;
            }
            case BuildPattern.Bridge:
            {
                int lanes = Count(TrackBuilderFeature.BridgeLanes);
                int n = Count(TrackBuilderFeature.BridgePlanks);
                bool level = TrackBuilderFeature.BridgeLevel.Value;
                for (int j = 0; j < lanes; j++)
                {
                    var side = right * LaneOffset(j, lanes, lanePitch);
                    var start = Ground(o + fwd * d + side, refY);
                    var endFlat = o + fwd * (d + n * len) + side;
                    var end = level ? new Vector3(endFlat.x, start.y, endFlat.z) : Ground(endFlat, Mathf.Max(refY, start.y));
                    var dir = end - start;
                    dir = dir.sqrMagnitude < 0.01f ? fwd : dir.normalized;
                    for (int k = 0; k < n; k++) into.Add(PlankAt(start + dir * (k * len), dir));
                }
                break;
            }
            case BuildPattern.Plank:
                into.Add(PlankAt(Ground(o + fwd * d, refY), fwd));
                break;
            case BuildPattern.Anchor:
                into.Add(new Piece { Kind = PieceKind.Anchor, Pos = Ground(o + fwd * d, refY), Rot = Quaternion.identity });
                break;
            case BuildPattern.Car:
                AddCar(into, Ground(o + fwd * d, refY), yaw, 0);
                break;
            case BuildPattern.Wall:
            {
                int n = Count(TrackBuilderFeature.CarCount);
                float s = TrackBuilderFeature.CarSpacing.Value;
                // Facing you, side by side across your path: car widths line up, so the wall is tight.
                for (int i = 0; i < n; i++) AddCar(into, Ground(o + fwd * d + right * LaneOffset(i, n, s), refY), yaw + 180f, i);
                break;
            }
            case BuildPattern.Parking:
            {
                int n = Count(TrackBuilderFeature.CarCount);
                float s = TrackBuilderFeature.CarSpacing.Value;
                float side = TrackBuilderFeature.ParkingOffset.Value;
                // A row beside your path, noses pointing away from it, like parked cars.
                float face = side >= 0f ? 90f : -90f;
                for (int i = 0; i < n; i++) AddCar(into, Ground(o + fwd * (d + i * s) + right * side, refY), yaw + face, i);
                break;
            }
            case BuildPattern.Slalom:
            {
                int n = Count(TrackBuilderFeature.CarCount);
                float gap = TrackBuilderFeature.SlalomGap.Value;
                float off = TrackBuilderFeature.SlalomOffset.Value;
                for (int i = 0; i < n; i++)
                    AddCar(into, Ground(o + fwd * (d + i * gap) + right * ((i & 1) == 0 ? off : -off), refY), yaw, i);
                break;
            }
        }
        return into.Count == 0 ? "Nothing to place." : null;
    }

    private static void AddCar(List<Piece> into, Vector3 ground, float yaw, int index)
    {
        into.Add(new Piece
        {
            Kind = PieceKind.Car,
            Pos = ground + Vector3.up * CarLift,
            Yaw = Mathf.Repeat(yaw, 360f),
            Rot = Quaternion.Euler(0f, yaw, 0f),
            Car = BuildCars.ModelFor(index),
        });
    }

    private static int Count(BepInEx.Configuration.ConfigEntry<float> e) => Mathf.Max(1, Mathf.RoundToInt(e.Value));
}
