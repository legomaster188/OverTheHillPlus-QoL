using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FunCore.Managers;
using FunCore.StateMachine;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace OverTheHillPlus.Logging;

/// <summary>
/// What the game goes through, at Info: scenes loading and unloading (with the time they cost), the session starting,
/// stopping or changing mode, the map, your car (modded or stock), the top of the game's state stack and players
/// joining or leaving. Scenes come from Unity's events; the rest is sampled once a second at Info and Verbose (nothing
/// runs below that), by <see cref="StateTracker"/>. Main thread only.
/// </summary>
internal static class Lifecycle
{
    private static readonly StateTracker Tracker = new();
    private static readonly Dictionary<IntPtr, string> ClassNames = new();
    private static bool _installed;
    private static string _unloaded;
    private static double _unloadedAt = -1;
    private static string _pendingName, _pendingMode, _pendingUnloaded;
    private static double _pendingAt, _pendingSinceUnload;
    private static string _lastScene;
    private static string _lastState = "";

    /// <summary>The newest state sample (for the diagnostics snapshot), or null before the first one.</summary>
    internal static StateSample Last { get; private set; }

    /// <summary>Subscribes to Unity's scene events (once, from core's Load).</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
            SceneManager.sceneUnloaded += (UnityAction<Scene>)OnSceneUnloaded;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "Lifecycle.Install");
            Core.CorePlugin.Logger?.LogWarning("[Debug] scene loads are not logged: " + e.Message);
        }
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        try
        {
            if (!DebugLog.IsInfo) return;
            _pendingName = scene.name;
            _pendingMode = mode == LoadSceneMode.Single ? "single" : "additive";
            _pendingAt = Time.realtimeSinceStartupAsDouble;
            _pendingUnloaded = _unloaded;
            _pendingSinceUnload = _unloadedAt >= 0 ? _pendingAt - _unloadedAt : -1;
            // The line is written on the next frame's beat, when the length of the frame that loaded it is known.
        }
        catch (Exception e) { DebugLog.Error("core", e, "Lifecycle.OnSceneLoaded"); }
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        try
        {
            if (!DebugLog.IsInfo) return;
            _unloaded = scene.name;
            _unloadedAt = Time.realtimeSinceStartupAsDouble;
            DebugLog.Info("core", SceneText.Unloaded(_unloaded, _unloadedAt));
        }
        catch (Exception e) { DebugLog.Error("core", e, "Lifecycle.OnSceneUnloaded"); }
    }

    /// <summary>Every frame, from the frame clock: writes a pending scene line with the frame's length.</summary>
    internal static void OnBeat(double frameMs)
    {
        if (_pendingName == null) return;
        string name = _pendingName;
        _pendingName = null;
        DebugLog.Info("core", SceneText.Loaded(name, _pendingMode, _pendingAt, frameMs, _pendingUnloaded, _pendingSinceUnload));
    }

    /// <summary>Once a second: the active scene and the sampled state.</summary>
    internal static void Tick()
    {
        if (!DebugLog.IsInfo) return;
        try
        {
            string scene = FrameWatch.Scene;
            if (_lastScene != null && scene != _lastScene) DebugLog.Info("core", SceneText.Active(_lastScene, scene));
            _lastScene = scene;
            var sample = Sample();
            Last = sample;
            foreach (var line in Tracker.Update(sample)) DebugLog.Info("core", line);
        }
        catch (Exception e) { DebugLog.Error("core", e, "Lifecycle.Tick"); }
    }

    /// <summary>The state right now (also for the diagnostics snapshot). Each part in its own try: a missing one is blank.</summary>
    internal static StateSample Sample()
    {
        var s = new StateSample { Session = GameState.Badge, SessionText = GameState.SessionDescription };
        bool inSession = GameState.Runner != null;
        if (inSession)
        {
            try { s.Map = OverTheHillPlus.Shared.GameMap.AreaName ?? ""; } catch (Exception e) { DebugLog.Error("core", e, "Lifecycle.Sample map"); }
            try
            {
                var sp = OverlandSessionPlayers.Instance;
                foreach (var p in PlayerListRoster.AllPlayers())
                {
                    string name = PlayerListRoster.NameOf(sp, p);
                    if (GameState.LocalPlayer is { } me && me.RawEncoded == p.RawEncoded) name += " (you)";
                    s.Players[p.PlayerId] = name;
                }
            }
            catch (Exception e) { DebugLog.Error("core", e, "Lifecycle.Sample players"); }
        }
        try
        {
            var car = GameState.LocalCar;
            if (car != null)
            {
                string n = car.gameObject.name ?? "";
                if (n.EndsWith("(Clone)", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 7).TrimEnd();
                s.Car = n;
                foreach (var m in OverTheHillPlus.Cars.ModdedCars.All)
                    if (m.Drive.Driving) { s.Car = m.Def.CarName; s.CarModded = true; break; }
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "Lifecycle.Sample car"); }
        s.GameState = TopState();
        return s;
    }

    /// <summary>The class name of the top of the game's state stack ("StateDriving", ...), or "" when unknown.</summary>
    internal static string TopState()
    {
        try
        {
            var top = HybridSingleton<GameStateManager>.Instance?.PeekState();
            if (top == null) return "";
            return ClassNameOf(top);
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "Lifecycle.TopState");
            return _lastState;
        }
    }

    private static string ClassNameOf(Il2CppObjectBase obj)
    {
        IntPtr klass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
        if (!ClassNames.TryGetValue(klass, out var name))
        {
            name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "?";
            if (ClassNames.Count < 256) ClassNames[klass] = name;
        }
        _lastState = name;
        return name;
    }
}
