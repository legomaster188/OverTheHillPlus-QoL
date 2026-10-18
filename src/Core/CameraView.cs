using System;
using UnityEngine;

namespace OverTheHillPlus;

/// <summary>
/// What the game's camera and input are doing right now, for the HUD and camera mods (HUD+, Minimap, Trip Stats,
/// Camera+, Auto Screenshot): they step aside while the game's photo mode, free camera, a cutscene or a loading screen
/// is up. Asked many times per frame, worked out once per frame from the game's input map stack
/// (GameInput.GetActiveMaps, topmost first) and FreeCameraManager.
/// </summary>
internal static class CameraView
{
    private static int _frame = -1;
    private static bool _photo, _cutscene, _driving = true, _drivingOnTop = true;
    private static bool _loggedError;

    /// <summary>True while the game's photo mode or free camera is active.</summary>
    internal static bool PhotoOrFreeCamera
    {
        get { Refresh(); return _photo; }
    }

    /// <summary>True while a cutscene (e.g. a scenic point's) or a loading screen is playing.</summary>
    internal static bool Cutscene
    {
        get { Refresh(); return _cutscene; }
    }

    /// <summary>
    /// True when you are driving: the topmost input map that is not just an overlay (the MODS page's Terminal map, a
    /// pause or other menu, the map screen, an unfocused window) is the Driving map. False in cutscenes, photo mode,
    /// the free camera, spectating and loading screens.
    /// </summary>
    internal static bool Driving
    {
        get { Refresh(); return _driving; }
    }

    /// <summary>True when the Driving map itself is on top (no menu or MODS page over it): mouse and stick drive the game.</summary>
    internal static bool DrivingOnTop
    {
        get { Refresh(); return _drivingOnTop; }
    }

    /// <summary>
    /// True when the mods' own HUD panels should not draw: photo mode or free camera, a cutscene or loading screen, a
    /// clean view asked for (e.g. the photo mod's "hide HUD"), or a screenshot being taken.
    /// </summary>
    internal static bool HudHidden => CoreBehaviour.GuiHidden || CoreBehaviour.CleanView || PhotoOrFreeCamera || Cutscene;

    private static void Refresh()
    {
        int f = Time.frameCount;
        if (f == _frame) return;
        _frame = f;
        Compute();
    }

    /// <summary>Maps that sit on top of driving without taking the camera away.</summary>
    private static bool Overlay(GameInput.Map m) =>
        m == GameInput.Map.Terminal || m == GameInput.Map.UI || m == GameInput.Map.GameHasNoFocus ||
        m == GameInput.Map.DeviceChange || m == GameInput.Map.MapScreen || m == GameInput.Map.RadialMenuNavigation ||
        m == GameInput.Map.MusicMenu || m == GameInput.Map.FeedbackMenu;

    private static void Compute()
    {
        _photo = false;
        _cutscene = false;
        CrashTrail.Mark("cameraview: free camera");
        try
        {
            var fcm = FreeCameraManager.Instance;
            if (fcm != null && fcm.IsActive) _photo = true;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CameraView.Compute"); }
        try
        {
            CrashTrail.Mark("cameraview: input maps");
            var maps = GameInput.GetActiveMaps();
            bool found = false;
            _driving = false;
            _drivingOnTop = maps != null && maps.Length > 0 && maps[0] == GameInput.Map.Driving;
            if (maps != null)
                for (int i = 0; i < maps.Length; i++)
                {
                    var m = maps[i];
                    if (m == GameInput.Map.FreeCamera || m == GameInput.Map.PhotoModeFreeCamera || m == GameInput.Map.PhotoModeUI) _photo = true;
                    else if (m == GameInput.Map.CutscenePlaying || m == GameInput.Map.LoadingScreen) _cutscene = true;
                    if (!found && !Overlay(m))
                    {
                        found = true;
                        _driving = m == GameInput.Map.Driving;
                    }
                }
            if (_photo) _driving = _drivingOnTop = false;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "CameraView.Compute");
            // Without the input stack, behave as before it was asked: driving unless photo mode says otherwise.
            _driving = _drivingOnTop = !_photo;
            if (_loggedError) return;
            _loggedError = true;
            Core.CorePlugin.Logger?.LogWarning("[CameraView] reading the game's input maps failed: " + e.Message);
        }
    }
}
