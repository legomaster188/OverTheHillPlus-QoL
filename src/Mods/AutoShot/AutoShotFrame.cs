using System;
using Il2CppInterop.Runtime;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.AutoShot;

/// <summary>
/// Auto Screenshot's per-frame work: watches your car's jumps (core's <see cref="CarAir"/>) and the game's "place
/// reached for the first time" event (PointOfInterest.POIFirstTimeReached, a static event the game raises when a point
/// of interest is reached for the first time), and takes a screenshot when <see cref="ShotRules"/> says so.
/// </summary>
internal static class AutoShotFrame
{
    private const float LandmarkDelay = 0.8f;   // let the arrival settle (and the game's banner show) first
    private const float LandmarkRange = 250f;   // only places near your own car count
    private const float ToastFor = 2.5f;

    internal static readonly ShotRules Rules = new();

    private static float _landmarkAt = -1f;
    private static Il2CppSystem.Action<PointOfInterest> _poiHandler; // kept alive while subscribed
    private static bool _loggedTick;

    internal static void Install()
    {
        CarAir.Use();
        CoreBehaviour.AddHideGuiCheck(() => AutoShotCapture.Hiding);
        CoreBehaviour.BeforeFeatureTicks += Tick;
        CoreBehaviour.AfterGuis += DrawToast;
        try
        {
            _poiHandler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<PointOfInterest>>(new Action<PointOfInterest>(OnFirstTimeReached));
            PointOfInterest.add_POIFirstTimeReached(_poiHandler);
        }
        catch (Exception e)
        {
            DebugLog.Error("autoshot", e, "AutoShotFrame.Install");
            _poiHandler = null;
            AutoShotPlugin.Logger.LogWarning("Auto Screenshot: landmark screenshots are off (could not listen for new places): " + e.Message);
        }
    }

    internal static bool LandmarksAvailable => _poiHandler != null;

    private static void OnFirstTimeReached(PointOfInterest poi)
    {
        try
        {
            if (!AutoShotConfig.Landmarks.Value || poi == null) return;
            var car = GameState.LocalCar;
            if (car == null) return;
            if ((poi.transform.position - car.transform.position).sqrMagnitude > LandmarkRange * LandmarkRange) return;
            _landmarkAt = Time.unscaledTime + LandmarkDelay;
        }
        catch (Exception ex) { DebugLog.Error("autoshot", ex, "AutoShotFrame.OnFirstTimeReached"); /* never break the game's event */ }
    }

    private static void Tick()
    {
        try { TickShots(); }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            AutoShotPlugin.Logger.LogError("Auto Screenshot tick (further errors not logged): " + e);
        }
    }

    private static void TickShots()
    {
        AutoShotCapture.Tick();
        if (AutoShotCapture.Busy) return;
        float now = Time.unscaledTime;
        Rules.Cooldown = Mathf.Clamp(AutoShotConfig.Cooldown.Value, 5f, 600f);
        Rules.MinAirTime = Mathf.Clamp(AutoShotConfig.MinAirTime.Value, 0.5f, 5f);
        // Only while you drive: not under the MODS page, in photo mode, a cutscene or a loading screen.
        if (GameState.LocalCar == null || CoreBehaviour.MenuOpen || !CameraView.Driving) return;

        if (_landmarkAt >= 0f && now >= _landmarkAt)
        {
            _landmarkAt = -1f;
            if (Rules.Landmark(now, AutoShotConfig.Landmarks.Value) && AutoShotCapture.Take(ShotReason.Landmark)) Rules.Taken(now);
            return;
        }
        if (!CarAir.InAir) return;
        var reason = Rules.Air(now, CarAir.JumpNumber, CarAir.AirTime, CarAir.TumbleDegrees, AutoShotConfig.Jumps.Value, AutoShotConfig.Flips.Value);
        if (reason.HasValue && AutoShotCapture.Take(reason.Value)) Rules.Taken(now, CarAir.JumpNumber);
    }

    private static void DrawToast()
    {
        if (!AutoShotConfig.ShowToast.Value) return;
        float age = Time.unscaledTime - AutoShotCapture.LastDoneAt;
        if (age < 0f || age > ToastFor || AutoShotCapture.Hiding) return;
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint || CameraView.HudHidden) return;
        try
        {
            Theme.Ensure();
            var screen = HudLayout.Begin();
            var r = new Rect(screen.x * 0.5f - 140f, HudLayout.Margin, 280f, 30f);
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01((ToastFor - age) / 0.4f));
            Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.85f), 8);
            var style = Theme.Small;
            var align = style.alignment;
            style.alignment = TextAnchor.MiddleCenter;
            GUI.Label(r, "Screenshot saved · " + ShotRules.Describe(AutoShotCapture.LastReason), style);
            style.alignment = align;
            GUI.color = prev;
        }
        catch (Exception e) { DebugLog.Error("autoshot", e, "AutoShotFrame.DrawToast"); AutoShotPlugin.Logger.LogDebug("Auto Screenshot note: " + e.Message); }
        finally { HudLayout.End(); }
    }
}
