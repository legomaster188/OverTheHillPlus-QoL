using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.RealNights;

/// <summary>One lamp-bearing cosmetic on a car (a roof bar, a bull bar with lamps, a custom light bar), in the car's own space.</summary>
internal sealed class LampSpec
{
    public string Name;
    public LampKind Kind;
    public LampTint Tint;
    /// <summary>The item's bounds in the car root's space: centre, half width (x) and the front (max z) and top (max y).</summary>
    public Vector3 Centre;
    public float HalfWidth, Front, Top;
}

/// <summary>A car the scan found: its root, its lights controller and the lamps on it.</summary>
internal sealed class SeenCar
{
    public int Id;
    public IntPtr Key;
    public LightsController Lights;
    public Transform Root;
    public bool Local;
    public bool HeadlightsOn;
    public readonly List<LampSpec> Lamps = new();
    public float NextLampScan;
    /// <summary>The car's headlight intensity and range from its light config (the game's own units).</summary>
    public float ConfigIntensity, ConfigRange;
}

/// <summary>
/// Finds the cars in the world (every few seconds, never per frame) and the lamps on each: the game's roof light bars
/// (Roof_Bar/Lights_*) and bull bars with lamps (Front/Bullbar_*_Lights*), and the Custom Cosmetics parts (CC_*) that
/// are lights, by <see cref="LampRules"/>. Only items that are switched on count: the game shows the car's chosen
/// cosmetics and hides the rest, on every car in the session.
/// </summary>
internal static class CarScan
{
    private const float RefreshEvery = 2f;
    private const float LampScanEvery = 6f;
    private const int LampScansPerPass = 2;

    internal static readonly List<SeenCar> Cars = new();
    private static readonly Dictionary<IntPtr, SeenCar> ByKey = new();
    private static float _nextRefresh;
    private static int _nextId = 1;

    /// <summary>The newest car config seen, for a car whose own is missing.</summary>
    internal static float FallbackIntensity = 30f, FallbackRange = 40f;

    internal static bool Alive(Object o)
    {
        try { return o != null && o.Pointer != IntPtr.Zero && o; }
        catch (Exception e) { NightSky.Failed(e, "CarScan.Alive"); return false; }
    }

    /// <summary>Refreshes the car list when it is due (or <paramref name="force"/>).</summary>
    internal static void Tick(bool force = false)
    {
        float now = Time.unscaledTime;
        if (!force && now < _nextRefresh) return;
        _nextRefresh = now + RefreshEvery;
        try { Refresh(now); }
        catch (Exception e) { NightSky.Failed(e, "CarScan.Refresh"); }
    }

    private static void Refresh(float now)
    {
        var found = Object.FindObjectsOfType(Il2CppType.Of<LightsController>());
        var seen = new HashSet<IntPtr>();
        Transform localRoot = null;
        try { localRoot = GameState.LocalCar != null ? GameState.LocalCar.transform : null; }
        catch (Exception e) { NightSky.Failed(e, "CarScan.Local"); }

        int scans = 0;
        foreach (var o in found)
        {
            var lc = o?.TryCast<LightsController>();
            if (lc == null || !Alive(lc)) continue;
            seen.Add(lc.Pointer);
            if (!ByKey.TryGetValue(lc.Pointer, out var car))
            {
                var root = RootOf(lc.transform);
                if (root == null) continue;
                car = new SeenCar { Id = _nextId++, Key = lc.Pointer, Lights = lc, Root = root, NextLampScan = 0f };
                ByKey[lc.Pointer] = car;
                Cars.Add(car);
            }
            car.Local = localRoot != null && (localRoot.Pointer == car.Root.Pointer || localRoot.IsChildOf(car.Root) || car.Root.IsChildOf(localRoot));
            if (now >= car.NextLampScan && scans < LampScansPerPass)
            {
                scans++;
                car.NextLampScan = now + LampScanEvery;
                ScanLamps(car);
            }
        }
        for (int i = Cars.Count - 1; i >= 0; i--)
        {
            var car = Cars[i];
            if (seen.Contains(car.Key) && Alive(car.Root)) continue;
            ByKey.Remove(car.Key);
            Cars.RemoveAt(i);
        }
    }

    /// <summary>The car's root: the node above the cosmetic parts that has the game's "Parts" child.</summary>
    private static Transform RootOf(Transform from)
    {
        for (var t = from; t != null; t = t.parent)
            if (t.Find("Parts") != null) return t;
        return from?.root;
    }

    private static void ScanLamps(SeenCar car)
    {
        car.Lamps.Clear();
        try
        {
            var heads = car.Lights.headlights;
            var cfg = heads?.Config;
            if (cfg != null && cfg.Intensity > 0.01f && cfg.Range > 0.5f)
            {
                car.ConfigIntensity = cfg.Intensity;
                car.ConfigRange = cfg.Range;
                FallbackIntensity = cfg.Intensity;
                FallbackRange = cfg.Range;
            }
        }
        catch (Exception e) { NightSky.Failed(e, "CarScan.Config"); }

        var parts = car.Root.Find("Parts");
        if (parts == null) return;
        for (int i = 0; i < parts.childCount; i++)
        {
            var cosmetics = parts.GetChild(i);
            if (!cosmetics.name.EndsWith("_CosmeticParts", StringComparison.Ordinal)) continue;
            for (int g = 0; g < cosmetics.childCount; g++)
            {
                var group = cosmetics.GetChild(g);
                if (!group.gameObject.activeSelf || group.name != "Visual") continue;
                for (int s = 0; s < group.childCount; s++)
                {
                    var slot = group.GetChild(s);
                    if (!slot.gameObject.activeSelf) continue;
                    if (slot.name.StartsWith(LampRules.CustomPrefix, StringComparison.Ordinal)) Add(car, "", slot);
                    else
                        for (int k = 0; k < slot.childCount; k++)
                        {
                            var item = slot.GetChild(k);
                            if (item.gameObject.activeSelf) Add(car, slot.name, item);
                        }
                }
            }
        }
    }

    private static void Add(SeenCar car, string slot, Transform item)
    {
        var kind = LampRules.Classify(slot, item.name, out var tint);
        if (kind == LampKind.None) return;
        var renderers = item.GetComponentsInChildren<Renderer>(false);
        if (renderers == null || renderers.Length == 0) return;
        bool any = false;
        Vector3 min = default, max = default;
        foreach (var r in renderers)
        {
            if (r == null) continue;
            var b = r.bounds;
            for (int c = 0; c < 8; c++)
            {
                var corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                var p = car.Root.InverseTransformPoint(corner);
                if (!any) { min = max = p; any = true; }
                else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            }
        }
        if (!any) return;
        car.Lamps.Add(new LampSpec
        {
            Name = item.name, Kind = kind, Tint = tint,
            Centre = (min + max) * 0.5f,
            HalfWidth = (max.x - min.x) * 0.5f, Front = max.z, Top = max.y,
        });
    }

    /// <summary>The headlight values a car's lamps are scaled from.</summary>
    internal static void Reference(SeenCar car, out float intensity, out float range)
    {
        intensity = car.ConfigIntensity > 0f ? car.ConfigIntensity : FallbackIntensity;
        range = car.ConfigRange > 0f ? car.ConfigRange : FallbackRange;
    }
}
