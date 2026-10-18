using System;
using Il2CppInterop.Runtime;
using OverTheHillPlus.HostPowers;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.TrackBuilder;

/// <summary>
/// Size and pivot of the game's placed plank ("Prefabs/Props/PlacedPlank", the prefab MapNetworkManager spawns in
/// RPC_PlacePortablePlank), measured once from its box colliders in the prefab's own space, so patterns can lay
/// planks end to end with the bottom face on the ground whatever the prefab's pivot and long axis are.
/// </summary>
internal static class PlankShape
{
    private const string PrefabPath = "Prefabs/Props/PlacedPlank";

    /// <summary>True once real numbers were read; until then the defaults below are a rough guess.</summary>
    public static bool Measured { get; private set; }
    /// <summary>The long axis is the prefab's local X (otherwise local Z).</summary>
    public static bool LongX { get; private set; }
    public static float Length { get; private set; } = 4f;
    public static float Width { get; private set; } = 0.6f;
    public static float Thickness { get; private set; } = 0.12f;
    /// <summary>Bounding box of the colliders in the prefab root's local space.</summary>
    public static Vector3 Min { get; private set; } = new(-0.3f, 0f, -2f);
    public static Vector3 Max { get; private set; } = new(0.3f, 0.12f, 2f);

    private static float _nextTry;
    private static bool _logged;

    /// <summary>Local point at the middle of the bottom face's near (start) end.</summary>
    public static Vector3 NearBottomLocal =>
        LongX ? new Vector3(Min.x, Min.y, (Min.z + Max.z) * 0.5f) : new Vector3((Min.x + Max.x) * 0.5f, Min.y, Min.z);

    public static Vector3 CenterLocal => (Min + Max) * 0.5f;

    /// <summary>Rotation that lays the plank's long axis along <paramref name="dir"/> with its top facing up.</summary>
    public static Quaternion RotationFor(Vector3 dir)
    {
        if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
        var rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
        // LookRotation points local +Z along dir; Euler(0,-90,0) first turns local +X onto +Z.
        return LongX ? rot * Quaternion.Euler(0f, -90f, 0f) : rot;
    }

    /// <summary>Reads the prefab (throttled: at most every 5 s until it works).</summary>
    public static void Ensure()
    {
        if (Measured || Time.unscaledTime < _nextTry) return;
        _nextTry = Time.unscaledTime + 5f;
        GameObject prefab = null;
        try
        {
            var mnm = MapNetworkManager.Instance;
            if (mnm != null) prefab = mnm.portablePlankPrefab;
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PlankShape.Ensure"); }
        try
        {
            if (prefab == null) prefab = Resources.Load(PrefabPath, Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "PlankShape.Ensure"); LogOnce("load: " + e.Message); }
        if (prefab != null) MeasureFrom(prefab);
    }

    /// <summary>Measures from a prefab or a spawned plank (both work: everything is taken relative to its root).</summary>
    public static bool MeasureFrom(GameObject go)
    {
        if (go == null) return false;
        try
        {
            var root = go.transform;
            var boxes = go.GetComponentsInChildren<BoxCollider>(true);
            bool any = false;
            Vector3 mn = new(float.MaxValue, float.MaxValue, float.MaxValue), mx = -mn;
            if (boxes != null)
            {
                foreach (var b in boxes)
                {
                    if (b == null || b.isTrigger) continue;
                    var bt = b.transform;
                    var half = b.size * 0.5f;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = b.center + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                        var local = root.InverseTransformPoint(bt.TransformPoint(corner));
                        mn = Vector3.Min(mn, local);
                        mx = Vector3.Max(mx, local);
                        any = true;
                    }
                }
            }
            if (!any)
            {
                // No box collider: fall back to the Plank component's length along Z.
                float len = 0f;
                try { len = Plank.Length; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PlankShape.MeasureFrom"); } // static on the type (a fixed plank length)
                if (len < 0.5f || len > 30f) { LogOnce("no box collider on the plank prefab, using a 4 m guess"); return false; }
                mn = new Vector3(-0.3f, 0f, -len / 2f);
                mx = new Vector3(0.3f, 0.12f, len / 2f);
            }
            var size = mx - mn;
            if (size.x <= 0.01f || size.z <= 0.01f || Mathf.Max(size.x, size.z) > 40f) { LogOnce($"odd plank size {size}"); return false; }
            Min = mn;
            Max = mx;
            LongX = size.x > size.z;
            Length = LongX ? size.x : size.z;
            Width = LongX ? size.z : size.x;
            Thickness = size.y;
            Measured = true;
            string body = "no rigidbody";
            try
            {
                var rb = go.GetComponentInChildren<Rigidbody>(true);
                if (rb != null) body = rb.isKinematic ? "kinematic rigidbody" : "DYNAMIC rigidbody (raised planks may fall)";
            }
            catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PlankShape.MeasureFrom"); }
            HostPowersPlugin.Logger.LogInfo($"[Build] Plank: {Length:0.00} m long ({(LongX ? "X" : "Z")}), {Width:0.00} m wide, {Thickness:0.00} m thick, bottom at {Min.y:0.00}, {body}");
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("hostpowers", e, "PlankShape.MeasureFrom");
            LogOnce("measure: " + e.Message);
            return false;
        }
    }

    private static void LogOnce(string msg)
    {
        if (_logged) return;
        _logged = true;
        HostPowersPlugin.Logger.LogWarning("[Build] Plank shape: " + msg);
    }
}
