using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Core.Shared;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Trailers;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The four trailer prefabs. Each is declared to core's network prefab registry (Core/Shared/NetPrefabRegistry, which
/// registers every mod's runtime prefabs in one fixed order on every peer and gates joins on them). When the registry
/// asks, a template is built: an inactive copy of the game's WinchablePlank (root: Rigidbody, NetworkRigidbody3D,
/// NetworkObject, InterestByDistance, no game scripts) with this mod's guid, model, colliders and mass. Only meshes,
/// colliders, transforms and Rigidbody settings change: never a NetworkBehaviour, so every peer's layout matches.
/// </summary>
internal static class TrailerPrefabs
{
    /// <summary>Layer of the trailer's body and colliders: the donor plank's own (collides like Default, never
    /// occludes the camera).</summary>
    public const int BodyLayer = 22;

    private sealed class Entry
    {
        public TrailerSpec Spec;
        public int Index = -1;
        public GameObject Template;
        /// <summary>Build handed the template to core, and core did not give it up: Fusion's table may hold it, so it
        /// is never freed (not even when the entry is lost before its index is confirmed).</summary>
        public bool HandedOver, GivenUp;
        public bool MayBeInTable => HandedOver && !GivenUp;
    }

    private static readonly Dictionary<TrailerKind, Entry> Entries = new();
    private static GameObject _holder;
    private static Material _material;

    /// <summary>Why trailers are off this session (null while fine or still waiting).</summary>
    public static string FailedReason { get; private set; }
    /// <summary>What the registry waits for (shown on the MODS page), null when not waiting.</summary>
    public static string Waiting { get; private set; } = "the game to finish loading";

    /// <summary>Every trailer can be spawned.</summary>
    public static bool Ready
    {
        get
        {
            if (FailedReason != null || Entries.Count < TrailerSpecs.All.Length) return false;
            foreach (var e in Entries.Values) if (e.Index < 0) return false;
            return true;
        }
    }

    /// <summary>The template of a kind (null until registered).</summary>
    public static NetworkObject Template(TrailerKind kind)
    {
        if (!Ready || !Entries.TryGetValue(kind, out var e) || e.Template == null) return null;
        return e.Template.GetComponent<NetworkObject>();
    }

    /// <summary>Declares the four entries (plugin Load). Null on success, otherwise why not.</summary>
    public static string Declare()
    {
        ModManager.SetServerModReady(TrailersPlugin.ModId, false); // not until all four entries are in
        foreach (var spec in TrailerSpecs.All)
        {
            var e = new Entry { Spec = spec };
            Entries[spec.Kind] = e;
            string why = NetPrefabRegistry.Declare(new NetPrefabEntry
            {
                Key = spec.RegistryKey,
                Guid = spec.Guid,
                AssetPath = spec.AssetPath,
                Layout = TrailerSpecs.Layout(spec),
                Build = index => Build(e, index),
                Registered = index => OnRegistered(e, index),
                Lost = reason => Fail("a trailer prefab was lost: " + reason),
                Failed = reason =>
                {
                    e.GivenUp = true;   // core never put it into Fusion's table
                    Fail($"core gave up the {spec.Name}'s network prefab: {reason}");
                },
            });
            if (why != null) return why;
        }
        return null;
    }

    private static void OnRegistered(Entry e, int index)
    {
        e.Index = index;
        TrailersPlugin.Logger.LogInfo($"[Trailers] {e.Spec.Name} registered at prefab index {index}.");
        DebugLog.Info(TrailersPlugin.ModId, $"{e.Spec.Name} network prefab registered at index {index}");
        if (Ready)
        {
            Waiting = null;
            ModManager.SetServerModReady(TrailersPlugin.ModId, true);
            TrailersPlugin.Logger.LogInfo("[Trailers] ready: all four trailers can be spawned.");
        }
    }

    /// <summary>Switches trailers off for the session: no spawns, and the per-frame part clears what is out (the host
    /// despawns its trailers, guests forget theirs). Templates that never made it into Fusion's table are freed; one
    /// the table may hold stays (Fusion refers to it).</summary>
    public static void Fail(string why)
    {
        FreeUnregistered();
        if (FailedReason != null) return;
        FailedReason = why;
        Waiting = null;
        ModManager.SetServerModReady(TrailersPlugin.ModId, false);
        ModManager.MarkFailed(TrailersPlugin.ModId, why);
        TrailersPlugin.Logger.LogError($"[Trailers] {why}. Trailers are off this session.");
    }

    private static void FreeUnregistered()
    {
        foreach (var e in Entries.Values)
        {
            if (e.Index >= 0 || e.MayBeInTable || e.Template == null) continue;
            try { Object.Destroy(e.Template); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerPrefabs.FreeUnregistered"); }
            e.Template = null;
        }
    }

    // ---------------------------------------------------------------- build (asked by core's registry)

    private static NetPrefabBuild Build(Entry e, int index)
    {
        if (FailedReason != null) return NetPrefabBuild.Fail(FailedReason);
        string modFailed = ModManager.FailedReason(TrailersPlugin.ModId);
        if (modFailed != null) return NetPrefabBuild.Fail(modFailed);
        NetworkPrefabTable table;
        try { table = NetworkProjectConfig.Global?.PrefabTable; } catch (Exception ex2) { DebugLog.Error("trailers", ex2, "TrailerPrefabs.Build"); table = null; }
        if (table == null) return NetPrefabBuild.Later(Waiting = "Fusion's project config");
        try
        {
            string why = BuildTemplate(e, table, index);
            if (why != null)
            {
                // Later entries' indices depend on this one: a trailer that can't be built switches all of them off.
                if (e.Template != null) Object.Destroy(e.Template);
                e.Template = null;
                Fail(why);
                return NetPrefabBuild.Fail(why);
            }
            e.HandedOver = true;
            return NetPrefabBuild.Done(e.Template.GetComponent<NetworkObject>());
        }
        catch (Exception ex)
        {
            if (e.Template != null) Object.Destroy(e.Template);
            e.Template = null;
            DebugLog.Error(TrailersPlugin.ModId, ex, "build " + e.Spec.Name);
            Fail($"could not build the {e.Spec.Name}: {ex.Message}");
            TrailersPlugin.Logger.LogError("[Trailers] build failed: " + ex);
            return NetPrefabBuild.Fail(ex.Message);
        }
    }

    private static string BuildTemplate(Entry e, NetworkPrefabTable table, int index)
    {
        var model = TrailerAssets.Model(e.Spec.Kind);
        if (model == null) return "a trailer model is missing";
        var donorId = table.GetId(TrailerSpecs.Donor);
        if (!donorId.IsValid) return $"the game's {TrailerSpecs.Donor} prefab is not in Fusion's table";
        var donor = table.Load(donorId, true);
        if (donor == null) return $"the game's {TrailerSpecs.Donor} prefab could not be loaded";
        if (donor.GetComponent<Rigidbody>() == null || donor.GetComponent<NetworkObjectPrefabData>() == null)
            return $"the game's {TrailerSpecs.Donor} is not the physics prop it used to be";

        if (_holder == null)
        {
            _holder = new GameObject("OTH_TrailerTemplates");
            _holder.SetActive(false); // nothing below it ever wakes up (no Awake, no OnEnable)
            _holder.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(_holder);
        }
        var tpl = Object.Instantiate(donor.gameObject, _holder.transform);
        tpl.SetActive(false); // Fusion's runtime-prefab pattern: an inactive template, activated per instance
        tpl.name = "OTH_Trailer_" + e.Spec.Kind;
        e.Template = tpl;

        var pd = tpl.GetComponent<NetworkObjectPrefabData>();
        pd.Guid = new NetworkObjectGuid(e.Spec.Guid);
        var no = tpl.GetComponent<NetworkObject>();
        if (no == null) return "the donor has no NetworkObject";
        no.NetworkTypeId = NetworkObjectTypeId.FromPrefabId(NetworkPrefabId.FromIndex(index));

        // ---- the plank's look and collider go; its winch attach point stays (vanilla winches can pull a trailer)
        Material donorMat = null;
        var rt = tpl.transform;
        for (int i = rt.childCount - 1; i >= 0; i--)
        {
            var c = rt.GetChild(i);
            if (c.name == "AttachPoint") continue;
            foreach (var r in c.GetComponentsInChildren<MeshRenderer>(true))
                if (donorMat == null && r != null && r.sharedMaterial != null) donorMat = r.sharedMaterial;
            Object.DestroyImmediate(c.gameObject);
        }
        if (_material == null)
        {
            if (donorMat == null) return "the donor has no material to copy";
            var m = new Material(donorMat) { name = "OTH_Trailer" };
            if (m.HasProperty("_Main_Texture")) m.SetTexture("_Main_Texture", TrailerAssets.Albedo);
            else m.mainTexture = TrailerAssets.Albedo;
            if (m.HasProperty("_AO_Texture")) m.SetTexture("_AO_Texture", TrailerAssets.White);
            if (m.HasProperty("_Tiling_Offset")) m.SetColor("_Tiling_Offset", new Color(1f, 1f, 0f, 0f));
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(m);
            _material = m;
        }

        // ---- our model: body, wheels (one node per wheel anchor, right side yawed 180), convex colliders
        TrailerAssets.EnsureMeshes(model);
        var body = Child(rt, "Body", Vector3.zero, Quaternion.identity);
        body.gameObject.AddComponent<MeshFilter>().sharedMesh = model.Body;
        var mr = body.gameObject.AddComponent<MeshRenderer>();
        mr.sharedMaterial = _material;
        foreach (var a in model.Wheels)
        {
            var w = Child(rt, a.Name, TrailerAssets.V(a.Position), new Quaternion(a.Rotation.X, a.Rotation.Y, a.Rotation.Z, a.Rotation.W));
            var spin = Child(w, "Spin", Vector3.zero, Quaternion.identity);
            spin.gameObject.AddComponent<MeshFilter>().sharedMesh = model.Wheel;
            spin.gameObject.AddComponent<MeshRenderer>().sharedMaterial = _material;
        }
        for (int i = 0; i < model.Colliders.Count; i++)
        {
            var c = Child(rt, "Collider" + i, Vector3.zero, Quaternion.identity);
            var mc = c.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = model.Colliders[i];
            mc.convex = true;
        }
        var cargo = Child(rt, "Cargo", model.Anchor("deck", new Vector3(0f, 0.8f, 0f)), Quaternion.identity);
        cargo.gameObject.layer = BodyLayer;

        var attach = rt.Find("AttachPoint");
        if (attach != null) attach.localPosition = model.Anchor("attach", model.Anchor("hitch", Vector3.zero) * 0.6f);

        // ---- the body: light, with a low centre of mass; Fusion's NetworkRigidbody interpolates (no Unity interpolation)
        var rb = tpl.GetComponent<Rigidbody>();
        rb.mass = e.Spec.Mass;
        rb.linearDamping = 0.02f;
        rb.angularDamping = 0.1f;
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.None;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.centerOfMass = new Vector3(0f, e.Spec.ComHeight, e.Spec.ComForward);
        rb.maxAngularVelocity = 12f;
        return null;
    }

    private static Transform Child(Transform parent, string name, Vector3 pos, Quaternion rot)
    {
        var go = new GameObject(name);
        go.layer = BodyLayer;
        var t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        t.localRotation = rot;
        return t;
    }
}
