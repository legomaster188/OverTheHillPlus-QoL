using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Diagnostics for a modded car's cosmetics: whenever the set of shown items on one car changes, logs each shown item
/// (its slot's position against the anchor the slot was put on, and the item's drawn bounds) next to the body's bounds,
/// all in car-root space. Answers "is the item where the anchor says" from the game itself, not from a render.
/// </summary>
internal static class CarCosmeticsProbe
{
    /// <summary>Last logged signature per car object (pointer of its root).</summary>
    private static readonly Dictionary<IntPtr, string> Last = new();

    public static void Check(RuntimeCar car, Transform root, string what)
    {
        if (car == null || root == null) return;
        try
        {
            Transform cosmetics = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.EndsWith("_CosmeticParts", StringComparison.Ordinal)) { cosmetics = t; break; }
            if (cosmetics == null) return;

            var shown = new List<Transform>();
            for (int g = 0; g < cosmetics.childCount; g++)
            {
                var group = cosmetics.GetChild(g);
                if (!group.gameObject.activeSelf) continue;
                for (int s = 0; s < group.childCount; s++)
                {
                    var slot = group.GetChild(s);
                    if (!slot.gameObject.activeSelf) continue;
                    for (int k = 0; k < slot.childCount; k++)
                        if (slot.GetChild(k).gameObject.activeSelf) shown.Add(slot.GetChild(k));
                }
            }
            var sig = new StringBuilder();
            foreach (var t in shown) sig.Append(t.name).Append(';');
            var key = root.Pointer;
            if (Last.TryGetValue(key, out var old) && old == sig.ToString()) return;
            Last[key] = sig.ToString();
            if (Last.Count > 64) Last.Clear();

            var sb = new StringBuilder();
            sb.Append($"cosmetics probe ({what} {root.name}): {shown.Count} shown");
            var body = car.Layout?.Body;
            if (body.HasValue) sb.Append($"; body centre {F(body.Value.center)} size {F(body.Value.size)} (layout offset {F(car.Layout.Offset)})");
            var blob = car.Assets?.Blob;
            foreach (var item in shown)
            {
                var slot = item.parent;
                string slotPath = slot.parent.name + "/" + slot.name;
                sb.Append($"\n  {slotPath}/{item.name}: slot at {F(root.InverseTransformPoint(slot.position))}");
                string anchorName = AnchorOf(car, slotPath);
                var a = anchorName == null ? null : blob?.Anchor(anchorName);
                if (a != null && car.Layout != null)
                    sb.Append($", anchor {anchorName} wants {F(CarAssets.V(a.Position) + CarAssets.V(car.Def.TweakOf(anchorName)) + car.Layout.Offset)}");
                var b = Bounds(root, item);
                if (b.HasValue) sb.Append($", drawn {F(b.Value.min)}..{F(b.Value.max)}");
            }
            car.Info(sb.ToString());
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarCosmeticsProbe.Check"); car.LogOnce("cosm-probe", "cosmetics probe: " + e.Message); }
    }

    private static string AnchorOf(RuntimeCar car, string slotPath)
    {
        foreach (var a in CarCosmetics.For(car.Def.CosmeticTable, slotPath))
            if (a.IsSlot) return a.Anchor;
        return null;
    }

    /// <summary>The item's renderers' bounds in car-root space (axis-aligned around their corners).</summary>
    private static Bounds? Bounds(Transform root, Transform item)
    {
        Bounds? acc = null;
        foreach (var r in item.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled) continue;
            var wb = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                var c = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = root.InverseTransformPoint(c);
                if (acc == null) acc = new Bounds(p, Vector3.zero);
                else { var x = acc.Value; x.Encapsulate(p); acc = x; }
            }
        }
        return acc;
    }

    private static string F(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
}
