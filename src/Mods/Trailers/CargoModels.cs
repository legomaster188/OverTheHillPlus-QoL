using System;
using System.Collections.Generic;
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

    /// <summary>Per car name: does its garage model fit the hauler's deck (<see cref="CargoSeat.Fits"/>)?</summary>
    private static readonly Dictionary<string, bool> Fit = new(StringComparer.Ordinal);

    /// <summary>Does this car fit the hauler's deck: its rear wheels on the flat part with its nose clear of the headboard
    /// (a tractor unit doesn't)? Measured once per car from its garage model; true when it can't be measured.</summary>
    public static bool FitsDeck(uint itemId, TrailerModel model)
    {
        string name = CarName(itemId);
        if (name == null) return true;
        if (Fit.TryGetValue(name, out bool f)) return f;
        var go = Build(itemId, model, out var s);
        if (go != null) { try { Object.DestroyImmediate(go); } catch (Exception ex) { DebugLog.Error("trailers", ex, "CargoModels.FitsDeck"); } }
        bool fits = s == null || s.Fits;
        Fit[name] = fits;      // also when it couldn't be measured: tried once
        return fits;
    }

    /// <summary>Places the car's garage model under <paramref name="deck"/>: its tyres on the deck boards, centred, chocked
    /// (<see cref="CargoFit"/>). Null when it can't be loaded or doesn't fit the deck.</summary>
    public static GameObject Make(uint itemId, Transform deck, TrailerModel model)
    {
        if (deck == null) return null;
        var go = Build(itemId, model, out var s);
        if (go == null) return null;
        if (!s.Fits)
        {
            TrailersPlugin.LogOnce("cargo-long-" + go.name, $"[Trailers] {go.name}: too long for the car hauler's deck (its rear wheels would hang off it), not shown.");
            try { Object.DestroyImmediate(go); } catch (Exception ex) { DebugLog.Error("trailers", ex, "CargoModels.Make"); }
            return null;
        }
        var t = go.transform;
        t.SetParent(deck, false);
        t.localPosition = new Vector3(s.X, s.Y, s.Z);
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
        if (model?.Chock != null && TrailerPrefabs.Material != null)
            foreach (var c in s.Chocks)
            {
                // children of the load (they go with it), placed in the deck's space on the deck surface
                var ch = new GameObject("Chock");
                ch.layer = TrailerPrefabs.BodyLayer;
                var ct = ch.transform;
                ct.SetParent(t, false);
                ct.localPosition = new Vector3(c.X - s.X, -s.Y, c.Z - s.Z);
                ct.localRotation = c.ToeForward ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f);
                ch.AddComponent<MeshFilter>().sharedMesh = model.Chock;
                ch.AddComponent<MeshRenderer>().sharedMaterial = TrailerPrefabs.Material;
            }
        return go;
    }

    /// <summary>The car's garage model, stripped asleep under the workshop holder, and its seat on the deck (null when it
    /// can't be loaded). Records whether it fits (<see cref="FitsDeck"/>).</summary>
    private static GameObject Build(uint itemId, TrailerModel model, out CargoSeat seatOut)
    {
        seatOut = null;
        string name = CarName(itemId);
        if (name == null) return null;
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
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
        // measured asleep, in the car's own space, from the meshes (not Renderer.bounds: the copy has never been
        // drawn), seated by its tyres: the garage model's shadow box reaches 0.5-1.2 m under them and lifted the car
        var seat = Measure(go, out int tyres);
        float flatRear = float.NaN, front = float.NaN;
        if (model != null)
        {
            var deckAt = model.Anchor("deck", Vector3.zero);
            var nan = new Vector3(0f, 0f, float.NaN);
            flatRear = model.Anchor("deck_flat_rear", nan).z - deckAt.z;
            front = model.Anchor("deck_front", nan).z - deckAt.z;
        }
        var s = CargoFit.Seat(seat.tyres, seat.lo, seat.hi, flatRear, front);
        DebugLog.Verbose(TrailersPlugin.ModId, () => $"load {name}: {tyres} tyres, seated at ({s.X:0.00}, {s.Y:0.00}, {s.Z:0.00}), {s.Chocks.Count} chocks{(s.Fits ? "" : ", too long for the deck")}");
        if (model != null) Fit[name] = s.Fits;
        seatOut = s;
        return go;
    }

    /// <summary>The load's tyres and its visible body's bounds, in its root's space. Helper meshes (shadow box,
    /// vegetation quads, wheel colliders' meshes, track stamps) are switched off and left out; so is anything switched
    /// off in the model (unmounted cosmetics).</summary>
    private static (List<CargoTyre> tyres, (float, float, float) lo, (float, float, float) hi) Measure(GameObject go, out int tyreCount)
    {
        var root = go.transform;
        var toRoot = root.worldToLocalMatrix;
        var tyres = new List<CargoTyre>();
        bool any = false;
        Vector3 lo = Vector3.zero, hi = Vector3.zero;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            Mesh mesh = null;
            var smr = r.TryCast<SkinnedMeshRenderer>();
            if (smr != null) mesh = smr.sharedMesh;
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            string objName = r.gameObject.name, meshName = mesh != null ? mesh.name : "";
            if (CargoFit.IsHelper(objName) || CargoFit.IsHelper(meshName))
            {
                r.enabled = false;
                continue;
            }
            if (mesh == null || !r.enabled || !ShownUnder(r.transform, root)) continue;
            var m = toRoot * r.transform.localToWorldMatrix;
            var b = mesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var p = m.MultiplyPoint3x4(b.center + new Vector3((i & 1) == 0 ? -b.extents.x : b.extents.x,
                    (i & 2) == 0 ? -b.extents.y : b.extents.y, (i & 4) == 0 ? -b.extents.z : b.extents.z));
                if (!any) { lo = hi = p; any = true; }
                else { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            }
            if (smr == null && (CargoFit.IsTyre(objName) || CargoFit.IsTyre(meshName)))
            {
                var c = m.MultiplyPoint3x4(b.center);
                float rad = CargoFit.TyreRadius(m.MultiplyVector(new Vector3(b.extents.x, 0f, 0f)).magnitude,
                                                m.MultiplyVector(new Vector3(0f, b.extents.y, 0f)).magnitude,
                                                m.MultiplyVector(new Vector3(0f, 0f, b.extents.z)).magnitude);
                if (rad > 0.1f) tyres.Add(new CargoTyre(c.x, c.y, c.z, rad));
            }
        }
        tyreCount = tyres.Count;
        return (tyres, (lo.x, lo.y, lo.z), (hi.x, hi.y, hi.z));
    }

    /// <summary>Switched on in the model: the object and every parent up to the model's root (the root itself sits
    /// under the inactive workshop, so activeInHierarchy can't tell).</summary>
    private static bool ShownUnder(Transform x, Transform root)
    {
        for (var p = x; p != null; p = p.parent)
        {
            if (!p.gameObject.activeSelf) return false;
            if (p.Pointer == root.Pointer) return true;
        }
        return true;
    }
}
