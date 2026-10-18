using System;
using FunCore.Managers;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Trailers;

/// <summary>
/// The car hauler's load: the garage model of a car (Prefabs/Cars/MerchantDummies/Car_&lt;name&gt;_Dummy, the
/// non-networked model the lobby and the garage show) placed on the deck. It is only a picture: its scripts,
/// colliders, bodies and joints are removed before it ever wakes up, so nothing of it takes part in physics. The
/// trailer itself gets heavier (the host sets its mass, which Fusion syncs).
/// </summary>
internal static class CargoModels
{
    private static readonly Il2CppStringArray NoTags = new(0);
    private static GameObject _holder;

    /// <summary>Inactive parent for a load while it is being stripped (nothing under it wakes up).</summary>
    private static GameObject Holder
    {
        get
        {
            if (_holder != null) return _holder;
            _holder = new GameObject("OTH_TrailerCargoWorkshop");
            _holder.SetActive(false);
            _holder.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(_holder);
            return _holder;
        }
    }

    /// <summary>The car's name for the game's addresses (CarInfo.name), or null when the id is not a car.</summary>
    public static string CarName(uint itemId)
    {
        try
        {
            if (itemId == 0) return null;
            if (!PlayerInventoryManager.ItemScriptableObjectLookup(itemId, out var so) || so == null) return null;
            var ci = so.TryCast<CarInfo>();
            return ci != null ? ci.name : null;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "CargoModels.CarName"); return null; }
    }

    /// <summary>Does this player own that car (it is in their inventory)?</summary>
    public static bool Owned(uint itemId)
    {
        try
        {
            var inv = PlayerInventoryManager.Instance?.Inventory;
            return inv != null && inv.ContainsItem(itemId);
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "CargoModels.Owned"); return false; }
    }

    /// <summary>Places the car's garage model under <paramref name="deck"/> (its wheels on the deck, centred). Null when
    /// it can't be loaded.</summary>
    public static GameObject Make(uint itemId, Transform deck)
    {
        string name = CarName(itemId);
        if (name == null || deck == null) return null;
        GameObject src;
        try
        {
            var rm = ResourcesManager.Instance;
            if (rm == null) return null;
            src = rm.Load<GameObject>(GameConstants.ADDRESSABLES_PATHS.GetCarDummyPrefabAddress(name), NoTags);
        }
        catch (Exception e)
        {
            DebugLog.Error("trailers", e, "CargoModels.Make");
            TrailersPlugin.LogOnce("cargo-load", $"[Trailers] the garage model of {name} could not be loaded: {e.Message}");
            return null;
        }
        if (src == null) return null;
        // made asleep (under an inactive holder) and stripped there, so none of its scripts, bodies or colliders ever
        // run in the world; only then it goes onto the deck
        var go = Object.Instantiate(src, Holder.transform);
        go.name = "OTH_TrailerCargo_" + name;
        foreach (var j in go.GetComponentsInChildren<Joint>(true)) if (j != null) Object.DestroyImmediate(j);
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) if (rb != null) Object.DestroyImmediate(rb);
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) if (c != null) Object.DestroyImmediate(c);
        for (int pass = 0; pass < 2; pass++)   // a script another one requires goes on the second pass
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null) { try { Object.DestroyImmediate(mb); } catch (Exception ex) { DebugLog.Error("trailers", ex, "CargoModels.Make"); } }
        var t = go.transform;
        var shadow = t.Find("ShadowCube");
        if (shadow != null) shadow.gameObject.SetActive(false);
        t.SetParent(deck, false);
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
        // fit, in the deck's own space: the lowest point of its renderers on the deck, their middle over its centre
        bool any = false;
        Vector3 lo = Vector3.zero, hi = Vector3.zero;
        foreach (var r in go.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled) continue;
            var b = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                var c = deck.InverseTransformPoint(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y,
                                                               (i & 4) == 0 ? b.min.z : b.max.z));
                if (!any) { lo = hi = c; any = true; }
                else { lo = Vector3.Min(lo, c); hi = Vector3.Max(hi, c); }
            }
        }
        if (any) t.localPosition = new Vector3(-(lo.x + hi.x) * 0.5f, -lo.y + 0.02f, -(lo.z + hi.z) * 0.5f);
        return go;
    }
}
