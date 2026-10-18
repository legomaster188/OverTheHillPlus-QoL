using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Trailers;

/// <summary>
/// Tracks in snow, mud and sand. The game draws them from a stamp object on each car wheel (WheelTransformUpdater's
/// <c>traxColliderGameObject</c>, a mesh on the Trax layer that MicroSplat's trax camera renders), which it switches
/// on while the wheel is on the ground. The trailers' wheels get a copy of that stamp, switched on the same way: on
/// every peer, from the wheel's ground contact in <see cref="TrailerPhysics.DrawWheels"/>.
/// </summary>
internal static class TrailerTrax
{
    /// <summary>A wheel must have been on the ground this long before it leaves tracks, s (a bounce leaves none).</summary>
    private const float Delay = 0.2f;

    private static GameObject _holder, _template;
    private static float _nextSearch;

    public static void Update(TrailerLive.Wheel w, bool onGround, float dt)
    {
        w.GroundedFor = onGround ? w.GroundedFor + dt : 0f;
        bool want = onGround && w.GroundedFor >= Delay;
        if (want == w.TraxOn) return;
        try
        {
            if (want && w.Trax == null) w.Trax = Make(w.Node);
            if (w.Trax == null) return;   // no stamp to copy yet: try again next frame
            w.Trax.SetActive(want);
            w.TraxOn = want;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerTrax.Update"); TrailersPlugin.LogOnce("trax", "[Trailers] track stamp: " + ex.Message); }
    }

    private static GameObject Make(Transform wheel)
    {
        if (!HaveTemplate() || wheel == null) return null;
        var go = Object.Instantiate(_template, wheel, false);   // keeps the stamp's place on the car's wheel
        go.name = "TraxCollider";
        go.SetActive(false);
        return go;
    }

    /// <summary>Copies a car's wheel stamp once, as an inactive template. Looks at the players' cars (every peer has
    /// its own), at most every 3 s.</summary>
    private static bool HaveTemplate()
    {
        if (_template != null) return true;
        if (Time.unscaledTime < _nextSearch) return false;
        _nextSearch = Time.unscaledTime + 3f;
        foreach (var fp in GameState.FusionPlayers)
        {
            if (fp == null) continue;
            foreach (var w in fp.GetComponentsInChildren<WheelTransformUpdater>(true))
            {
                var stamp = w != null ? w.traxColliderGameObject : null;
                if (stamp == null) continue;
                if (_holder == null)
                {
                    _holder = new GameObject("OTH_TrailerTrax");
                    _holder.SetActive(false);
                    _holder.hideFlags = HideFlags.HideAndDontSave;
                    Object.DontDestroyOnLoad(_holder);
                }
                _template = Object.Instantiate(stamp, _holder.transform, false);
                _template.name = "OTH_TraxStamp";
                _template.SetActive(false);
                var t = _template.transform;
                TrailersPlugin.Logger.LogInfo($"[Trailers] copied the car wheels' track stamp (layer {_template.layer}, local {t.localPosition}, scale {t.localScale}).");
                DebugLog.Info(TrailersPlugin.ModId, $"track stamp copied from {w.name}: layer {_template.layer}, local {t.localPosition}, scale {t.localScale}");
                return true;
            }
        }
        return false;
    }
}
