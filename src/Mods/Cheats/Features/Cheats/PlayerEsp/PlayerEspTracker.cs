using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using OverTheHillPlus.CheatsPlus;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Cheats.PlayerEsp;

/// <summary>
/// Read-only tracking of every player car for the player ESP. The car list is rebuilt from FusionPlayer objects
/// once a second (this does not depend on the game's 4-slot player tables, so big lobbies are covered); positions
/// and speed are sampled every frame, health four times a second. Nothing here writes to the game.
/// </summary>
internal static class PlayerEspTracker
{
    internal sealed class Target
    {
        public int Id;
        public IntPtr FpPtr;
        public string Name;
        public bool IsMe;
        public Transform T;
        public CarDamageSystem Damage;
        public Color Color;

        // Local-space box around the car (measured from its renderers once they are visible).
        public Vector3 Center = new(0f, 0.8f, 0f);
        public Vector3 Extents = new(1.0f, 0.85f, 2.3f);
        public bool Measured;
        public int MeasureTries;

        // Sampled state.
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public bool HasPosition;
        public float SpeedKmh;
        public float Health = -1f; // 0..1, -1 when unknown
        public float Distance;
        public bool Alive = true;
    }

    private static readonly Dictionary<int, Target> ById = new();
    internal static readonly List<Target> Targets = new();

    private static float _nextRefresh, _nextHealth, _nextCamera;
    private static Camera _camera;
    private static bool _loggedRefresh, _loggedSample, _loggedCamera;

    /// <summary>Where distances are measured from: the local car if there is one, otherwise the camera.</summary>
    internal static Vector3 Origin { get; private set; }
    internal static bool HasOrigin { get; private set; }

    internal static Camera Camera
    {
        get
        {
            if (_camera != null && Time.unscaledTime < _nextCamera) return _camera;
            _nextCamera = Time.unscaledTime + 1f;
            _camera = FindCamera();
            return _camera;
        }
    }

    private static Camera FindCamera()
    {
        try
        {
            var brain = SceneReferences.Instance != null ? SceneReferences.Instance.MainCameraBrain : null;
            var cam = brain != null ? brain.OutputCamera : null;
            if (cam != null && cam.isActiveAndEnabled) return cam;
        }
        catch (Exception e)
        {
            DebugLog.Error("cheats", e, "PlayerEspTracker.FindCamera");
            if (!_loggedCamera) { _loggedCamera = true; CheatsPlugin.Logger.LogDebug("PlayerEsp camera (brain): " + e.Message); }
        }
        try
        {
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled) return main;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.FindCamera"); }
        return null;
    }

    internal static void Clear()
    {
        ById.Clear();
        _nextRefresh = 0f;
        Targets.Clear();
        HasOrigin = false;
    }

    /// <summary>Per frame while the ESP is on.</summary>
    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now >= _nextRefresh)
        {
            _nextRefresh = now + 1f;
            Refresh();
        }
        bool readHealth = now >= _nextHealth;
        if (readHealth) _nextHealth = now + 0.25f;
        Sample(readHealth);
    }

    private static void Refresh()
    {
        try
        {
            var runner = GameState.Runner;
            int localId = -1;
            try { if (runner != null) localId = runner.LocalPlayer.PlayerId; } catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.Refresh"); }
            var sp = OverlandSessionPlayers.Instance;

            foreach (var t in Targets) t.Alive = false;

            foreach (var o in GameState.FusionPlayers)
            {
                var fp = o.TryCast<FusionPlayer>();
                if (fp == null || fp.Object == null) continue;

                int id;
                bool isMe;
                try
                {
                    var p = fp.Object.InputAuthority;
                    if (!p.IsRealPlayer) continue;
                    id = p.PlayerId;
                    isMe = id == localId;
                    try { if (fp.HasInputAuthority) isMe = true; } catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.Refresh"); }
                }
                catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.Refresh"); continue; }

                var vb = fp.vehicleBase;
                if (vb == null) continue;

                if (!ById.TryGetValue(id, out var t))
                {
                    t = new Target { Id = id };
                    ById[id] = t;
                    Targets.Add(t);
                }

                if (t.FpPtr != fp.Pointer)
                {
                    // New car object (joined, respawned or switched car): start over.
                    t.FpPtr = fp.Pointer;
                    t.T = vb.transform;
                    t.Damage = null;
                    try { t.Damage = vb.GetComponent<CarDamageSystem>(); } catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.Refresh"); }
                    t.Measured = false;
                    t.MeasureTries = 0;
                    t.HasPosition = false;
                    t.SpeedKmh = 0f;
                    t.Health = -1f;
                }

                t.IsMe = isMe;
                t.Alive = true;
                t.Name = NameOf(sp, fp, id);
                t.Color = ColorOf(fp, id);
                if (!t.Measured && t.MeasureTries < 10) Measure(t);
            }

            for (int i = Targets.Count - 1; i >= 0; i--)
            {
                var t = Targets[i];
                if (!t.Alive) { ById.Remove(t.Id); Targets.RemoveAt(i); }
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("cheats", e, "PlayerEspTracker.Refresh");
            if (!_loggedRefresh) { _loggedRefresh = true; CheatsPlugin.Logger.LogWarning("PlayerEsp refresh: " + e); }
        }
    }

    private static string NameOf(OverlandSessionPlayers sp, FusionPlayer fp, int id)
    {
        try
        {
            if (sp != null)
            {
                var n = sp.GetPlayerName(fp.Object.InputAuthority);
                if (!string.IsNullOrEmpty(n)) return Clean(n);
            }
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.NameOf"); }
        return $"Player {id}";
    }

    /// <summary>Strips rich-text tags and control characters; caps length so a hostile name can't wreck the overlay.</summary>
    private static string Clean(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool inTag = false;
        foreach (char c in s)
        {
            if (c == '<') { inTag = true; continue; }
            if (inTag) { if (c == '>') inTag = false; continue; }
            if (char.IsControl(c)) continue;
            sb.Append(c);
            if (sb.Length >= 24) break;
        }
        var r = sb.ToString().Trim();
        return r.Length == 0 ? "?" : r;
    }

    private static readonly Color[] Fallback =
    {
        new(0.31f, 0.66f, 0.97f), new(0.30f, 0.76f, 0.54f), new(0.95f, 0.70f, 0.24f), new(0.90f, 0.33f, 0.29f),
        new(0.72f, 0.45f, 0.95f), new(0.25f, 0.85f, 0.85f), new(0.95f, 0.50f, 0.75f), new(0.70f, 0.85f, 0.30f),
    };

    private static Color ColorOf(FusionPlayer fp, int id)
    {
        try
        {
            var c = UIManager.GetPlayerColor(fp.Object.InputAuthority);
            // Players beyond the game's colour table come back black/transparent: use our own palette instead.
            if (c.a > 0.05f && c.r + c.g + c.b > 0.15f) { c.a = 1f; return c; }
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.ColorOf"); }
        return Fallback[Math.Abs(id) % Fallback.Length];
    }

    /// <summary>Fits a local-space box around the car's visible meshes (once; retried while the car is hidden).</summary>
    private static void Measure(Target t)
    {
        t.MeasureTries++;
        try
        {
            if (t.T == null) return;
            var rs = t.T.GetComponentsInChildren<Renderer>(false);
            if (rs == null) return;
            bool any = false;
            Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || !r.enabled) continue;
                if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
                var b = r.bounds;
                if (b.size.sqrMagnitude < 0.0001f || b.size.magnitude > 20f) continue;
                var bmin = b.min;
                var bmax = b.max;
                for (int k = 0; k < 8; k++)
                {
                    var corner = new Vector3((k & 1) == 0 ? bmin.x : bmax.x, (k & 2) == 0 ? bmin.y : bmax.y, (k & 4) == 0 ? bmin.z : bmax.z);
                    var l = t.T.InverseTransformPoint(corner);
                    min = Vector3.Min(min, l);
                    max = Vector3.Max(max, l);
                }
                any = true;
            }
            if (!any) return;
            var ext = (max - min) * 0.5f;
            // World AABBs inflate a little when the car is tilted; keep the result inside sane car sizes.
            ext.x = Mathf.Clamp(ext.x, 0.5f, 2.2f);
            ext.y = Mathf.Clamp(ext.y, 0.4f, 2.0f);
            ext.z = Mathf.Clamp(ext.z, 1.0f, 4.5f);
            var center = (max + min) * 0.5f;
            if (center.magnitude > 5f) return;
            t.Center = center;
            t.Extents = ext;
            t.Measured = true;
        }
        catch (Exception e)
        {
            DebugLog.Error("cheats", e, "PlayerEspTracker.Measure");
            t.MeasureTries = 99;
            CheatsPlugin.Logger.LogDebug("PlayerEsp measure: " + e.Message);
        }
    }

    private static void Sample(bool readHealth)
    {
        float dt = Time.deltaTime;
        HasOrigin = false;
        try
        {
            var me = GameState.LocalCar;
            if (me != null) { Origin = me.transform.position; HasOrigin = true; }
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.Sample"); }
        if (!HasOrigin)
        {
            var cam = Camera;
            if (cam != null) { try { Origin = cam.transform.position; HasOrigin = true; } catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.Sample"); } }
        }

        for (int i = 0; i < Targets.Count; i++)
        {
            var t = Targets[i];
            try
            {
                if (t.T == null) { t.HasPosition = false; continue; }
                var pos = t.T.position;
                if (t.HasPosition && dt > 0.0001f)
                {
                    float moved = (pos - t.Position).magnitude;
                    if (moved > 60f) t.SpeedKmh = 0f; // teleport / respawn
                    else
                    {
                        float kmh = moved / dt * 3.6f;
                        float k = 1f - Mathf.Exp(-dt * 6f);
                        t.SpeedKmh = Mathf.Lerp(t.SpeedKmh, kmh, k);
                    }
                }
                t.Position = pos;
                t.Rotation = t.T.rotation;
                t.HasPosition = true;
                t.Distance = HasOrigin ? Vector3.Distance(Origin, pos) : 0f;

                if (readHealth && t.Damage != null)
                {
                    try
                    {
                        float max = t.Damage.MaxDamageAmount;
                        t.Health = max > 0.001f ? Mathf.Clamp01(1f - t.Damage.DamageAmount / max) : -1f;
                    }
                    catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspTracker.Sample"); t.Health = -1f; }
                }
            }
            catch (Exception e)
            {
                DebugLog.Error("cheats", e, "PlayerEspTracker.Sample");
                t.HasPosition = false;
                if (!_loggedSample) { _loggedSample = true; CheatsPlugin.Logger.LogDebug("PlayerEsp sample: " + e.Message); }
            }
        }
    }
}
