using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;
using OverTheHillPlus.Core.Shared;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// Every Meerkat in the world, every frame, on every peer: the parts its owner's build has on (<see cref="MeerkatNet"/>),
/// its tyres' tread and size, and the pop-up pods turned up on their hinges while its headlamps are on (the game syncs the lights, so every
/// peer sees the same). The garage model shows this game's own build, the pods down. Pictures only: the physics of the
/// build is <see cref="MeerkatPhysics"/>'s.
/// </summary>
internal static class MeerkatLive
{
    private sealed class Car
    {
        public VehicleBase V;
        public Transform Root;
        public LightsController Lights;
        public float Pod;
        public bool HasShown;
        public MeerkatBuild Shown;
        public TyreSize ShownSize;
        public int Player;
        public bool Simulated;
        public List<MeshFilter> WheelMeshes;
        public float NextTyres;
    }

    private static readonly Dictionary<IntPtr, Car> Cars = new();
    private static readonly HashSet<IntPtr> Seen = new();
    private static readonly List<IntPtr> Gone = new();
    private static readonly Dictionary<IntPtr, Vector3> PodHome = new();
    private static readonly Dictionary<IntPtr, Quaternion> PodShut = new();
    private static readonly Dictionary<IntPtr, Vector3> WheelsHome = new();
    private static bool _garageHooked;

    // The garage model on show: what was last put on it.
    private const string GarageModelName = "Car_" + MeerkatDefinition.CarName + "_Dummy";
    private static GameObject _garage;
    private static bool _garageOurs, _garageDressed, _garageStanding;
    private static MeerkatBuild _garageBuild;
    private static float _garageNext;

    /// <summary>Seconds the pods take to go up or down.</summary>
    public const float PodSeconds = 0.55f;

    public static void Tick()
    {
        if (!_garageHooked)
        {
            _garageHooked = true;
            GarageLift.SetRideHeight(MeerkatDefinition.CarName, () => MeerkatCareer.LocalBuild(out _).BodyHeight());
        }
        MeerkatNet.Tick();
        MeerkatPhysics.Slow();
        float dt = Time.deltaTime;
        Seen.Clear();
        foreach (var fp in GameState.FusionPlayers)
        {
            VehicleBase v;
            int player;
            bool simulated;
            try
            {
                if (fp == null || fp.Object == null) continue;
                v = fp.vehicleBase;
                if (v == null || !v.isActiveAndEnabled) continue;
                if (VehicleTraits.ModdedId(v) != MeerkatDefinition.Id) continue;
                player = fp.Object.InputAuthority.RawEncoded;
                simulated = GameState.IsAuthority || fp.Object.HasInputAuthority;
            }
            catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatLive.Tick player"); continue; }
            var ptr = v.Pointer;
            if (!Cars.TryGetValue(ptr, out var car))
            {
                car = new Car { V = v, Root = v.transform };
                try { car.Lights = v.GetComponentInChildren<LightsController>(true); }
                catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatLive.lights"); }
                Cars[ptr] = car;
            }
            car.Player = player;
            car.Simulated = simulated;
            Seen.Add(ptr);
            try { Frame(car, dt); }
            catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatLive.Frame"); MeerkatPlugin.LogOnce("frame", "a Meerkat's parts failed: " + e.Message); }
        }
        Gone.Clear();
        foreach (var kv in Cars) if (!Seen.Contains(kv.Key)) Gone.Add(kv.Key);
        foreach (var k in Gone) Cars.Remove(k);
        Garage();
    }

    /// <summary>A Meerkat's build: its player's, else the car as the dealer sells it.</summary>
    internal static MeerkatBuild BuildOf(int player, out bool free)
    {
        if (MeerkatNet.TryGet(player, out var row))
        {
            free = row.Free;
            return row.Build;
        }
        free = false;
        return MeerkatBuild.Dealer;
    }

    private static void Frame(Car car, float dt)
    {
        var b = BuildOf(car.Player, out _);
        var size = MeerkatPhysics.ShownSize(car.V, b, car.Simulated);
        float now = Time.unscaledTime;
        if (!car.HasShown || car.Shown != b || car.ShownSize != size)
        {
            ApplyParts(car.Root, b.PartsShown());
            SetFlaps(car.Root, b.FlapLift());
            ApplyTyres(car.WheelMeshes ??= WheelMeshesOf(car.Root), b.Tyre, size);
            car.Shown = b;
            car.ShownSize = size;
            car.HasShown = true;
            car.NextTyres = now + 1f;
        }
        else if (now >= car.NextTyres)
        {
            // Tyre Pressure and Custom Cosmetics hide a wheel behind an empty mesh, which we leave alone, and put back what it
            // drew then: the size may have changed since.
            car.NextTyres = now + 1f;
            ApplyTyres(car.WheelMeshes ??= WheelMeshesOf(car.Root), b.Tyre, size);
        }
        bool on = true;
        if (MeerkatConfig.PopUps == null || MeerkatConfig.PopUps.Value)
        {
            try { on = car.Lights != null && car.Lights.AreHeadlightsOn; }
            catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatLive.headlights"); on = false; }
        }
        float target = on ? 1f : 0f;
        if (Math.Abs(car.Pod - target) > 1e-4f)
        {
            car.Pod = Mathf.MoveTowards(car.Pod, target, dt / PodSeconds);
            SetPods(car.Root, car.Pod);
        }
    }

    /// <summary>
    /// The garage's model (core's <see cref="GarageLift.ShownModel"/>): a copy the game makes of the one built for it, so it
    /// starts as built. The player's own car wears this game's build, on the height core gives it; a car looked at in the
    /// vehicle list stands as built, as core has it. Nothing is written unless something changed: the model, the screen,
    /// the build (asked ten times a second).
    /// </summary>
    private static void Garage()
    {
        try
        {
            var model = GarageLift.ShownModel(out bool own, out _);
            if (model == null)
            {
                _garage = null;
                return;
            }
            bool fresh = _garage == null || _garage.Pointer != model.Pointer;
            if (fresh)
            {
                _garage = model;
                _garageOurs = model.name.StartsWith(GarageModelName, StringComparison.Ordinal);
                _garageDressed = false;
            }
            if (!_garageOurs) return;
            float now = Time.unscaledTime;
            if (!fresh && now < _garageNext) return;
            _garageNext = now + 0.1f;
            var b = own ? MeerkatCareer.LocalBuild(out _) : MeerkatBuild.Safari;
            // The wheels' size goes with the height: without core's lift the body stays at the model's, so do they.
            bool standing = own && GarageLift.Enabled;
            if (_garageDressed && b == _garageBuild && standing == _garageStanding) return;
            _garageDressed = true;
            _garageBuild = b;
            _garageStanding = standing;
            var root = model.transform;
            ApplyParts(root, b.PartsShown());
            SetFlaps(root, standing ? b.FlapLift() : 0f);
            ApplyTyres(WheelMeshesOf(root), b.Tyre, standing ? b.Size : TyreSize.Model);
            var wm = root.Find("Axles/WheelMeshes");
            if (wm != null) Raise(wm, standing ? b.HubHeight() : 0f);
        }
        catch (Exception e)
        {
            DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatLive.Garage");
            MeerkatPlugin.LogOnce("garage", "the garage model's build failed: " + e.Message);
        }
    }

    /// <summary>Metres above where a node of the garage model was built (it has no physics: its hubs go up or down with the
    /// tyres' size so they stand on the floor, <see cref="MeerkatBuild.BodyHeight"/> takes the body with them).</summary>
    private static void Raise(Transform t, float metres)
    {
        if (!WheelsHome.TryGetValue(t.Pointer, out var home))
        {
            home = t.localPosition;
            WheelsHome[t.Pointer] = home;
        }
        float scale = t.parent != null ? Mathf.Abs(t.parent.lossyScale.y) : 1f;
        if (scale < 0.01f) scale = 1f;
        var want = home + Vector3.up * (metres / scale);
        if ((t.localPosition - want).sqrMagnitude > 1e-8f) t.localPosition = want;
    }

    // ---------------------------------------------------------------- parts

    /// <summary>Switches every optional part on the car on or off (the pods are always on).</summary>
    internal static void ApplyParts(Transform root, HashSet<string> shown)
    {
        foreach (var name in MeerkatBuild.OptionalParts)
        {
            var t = CarMovingParts.Find(root, name);
            if (t == null) continue;
            bool want = shown.Contains(name);
            if (t.gameObject.activeSelf != want) t.gameObject.SetActive(want);
        }
        // the rack's and the lamp bar's colliders go with them (the garage model has none)
        for (int i = 0; i < MeerkatBuild.BoxParts.Length; i++)
        {
            var box = root.Find("Collider/" + CarBodyBox.NodeName(i));
            if (box == null) continue;
            bool want = shown.Contains(MeerkatBuild.BoxParts[i]);
            if (box.gameObject.activeSelf != want) box.gameObject.SetActive(want);
        }
    }

    private static void SetFlaps(Transform root, float up)
    {
        var t = CarMovingParts.Find(root, MeerkatBuild.MudFlaps);
        if (t == null) return;
        if (!PodHome.TryGetValue(t.Pointer, out var home))
        {
            home = t.localPosition;
            PodHome[t.Pointer] = home;
        }
        var want = home + Vector3.up * up;
        if ((t.localPosition - want).sqrMagnitude > 1e-8f) t.localPosition = want;
    }

    /// <summary>The pods at <paramref name="k"/> of the way up (eased: a quick start, a soft stop as the motor's crank does):
    /// each turns about its hinge (the part's pivot), front up, as the real car's do.</summary>
    private static void SetPods(Transform root, float k)
    {
        float e = k * k * (3f - 2f * k);
        foreach (var name in new[] { MeerkatBuild.PodL, MeerkatBuild.PodR })
        {
            var t = CarMovingParts.Find(root, name);
            if (t == null) continue;
            if (!PodShut.TryGetValue(t.Pointer, out var shut))
            {
                shut = t.localRotation;
                PodShut[t.Pointer] = shut;
            }
            // Unity's x turns forward toward down for a positive angle: the front comes up for a negative one
            t.localRotation = shut * Quaternion.AngleAxis(-MeerkatRig.PodOpenDegrees * e, Vector3.right);
        }
    }

    // ---------------------------------------------------------------- tyres

    /// <summary>The tread and size on every wheel: only on wheels that show one of our meshes (a tyre another mod put on,
    /// Tyre Pressure's treads, is left alone).</summary>
    private static void ApplyTyres(List<MeshFilter> wheels, TyreKind tyre, TyreSize size)
    {
        int slot = MeerkatRig.Slot(tyre, size);
        foreach (var mf in wheels)
        {
            if (mf == null) continue;
            var cur = mf.sharedMesh;
            if (cur == null || !MeerkatRig.BaseOf.TryGetValue(cur.Pointer, out var basePtr)) continue;
            if (!MeerkatRig.Variants.TryGetValue(basePtr, out var set)) continue;
            var want = set[slot];
            if (want != null && want.Pointer != cur.Pointer) mf.sharedMesh = want;
        }
    }

    private static List<MeshFilter> WheelMeshesOf(Transform root)
    {
        var list = new List<MeshFilter>();
        var wm = root.Find("Axles/WheelMeshes");
        if (wm == null) return list;
        foreach (var mf in wm.GetComponentsInChildren<MeshFilter>(true))
            if (mf != null) list.Add(mf);
        return list;
    }
}
