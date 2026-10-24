using System;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Trailers;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>Runs the trailers' per-frame parts and the two hotkeys.</summary>
internal sealed class TrailersFeature : IFeature
{
    private static IntPtr _runner;

    public void Register()
    {
        TrailerNet.Register();
        Registry.OnTick(Tick);
    }

    private static void Tick()
    {
        // a new session (or none): nothing of the last one is kept
        var r = GameState.Runner;
        var ptr = r == null ? IntPtr.Zero : r.Pointer;
        if (ptr != _runner)
        {
            _runner = ptr;
            TrailerWorld.Clear();
            TrailerPhysics.Unhook();
        }
        if (r == null) return;
        // switched off (failed mid-session, or the session doesn't run it): the host takes its trailers away,
        // guests forget theirs; nothing else runs
        bool serving = TrailerPrefabs.Ready && ModManager.ServerModActive(TrailersPlugin.ModId);
        if (!serving)
        {
            if (TrailerWorld.Count > 0)
            {
                if (GameState.IsAuthority) TrailerHost.DespawnAll(DespawnReason.ModOff);
                else TrailerWorld.Clear();
            }
            TrailerPhysics.Unhook();
            return;
        }
        TrailerPhysics.Ensure();
        TrailerWorld.Tick();
        TrailerHost.Tick();
        TrailerNet.Tick();
        Keys();
    }

    private static void Keys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (MenuUx.HotkeysCaptured || KeybindEditor.Capturing || MenuUx.Typing || GUIUtility.keyboardControl != 0) return;
        if (Pressed(kb, TrailersConfig.HitchKey.Value)) TrailersActions.ToggleHitch();
        if (Pressed(kb, TrailersConfig.ResetKey.Value)) TrailersActions.ResetBehind();
    }

    private static bool Pressed(Keyboard kb, Key key)
    {
        if (key == Key.None) return false;
        try { return kb[key].wasPressedThisFrame; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailersFeature.Pressed"); return false; }
    }
}

/// <summary>The MODS page's and the hotkeys' actions.</summary>
internal static class TrailersActions
{
    /// <summary>My trailers in this session (owner = this game's player).</summary>
    public static int MineCount(out TrailerLive hitched, out TrailerLive any)
    {
        hitched = any = null;
        int n = 0;
        int me;
        try { me = GameState.Runner.LocalPlayer.RawEncoded; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailersActions.MineCount"); return 0; }
        foreach (var t in TrailerWorld.All)
        {
            if (t.State.Owner != me) continue;
            n++;
            any ??= t;
            if (t.State.Hitched) hitched ??= t;
        }
        return n;
    }

    public static string Spawn() => TrailerNet.Ask(TrailerOp.Spawn, TrailersConfig.Kind.Value);
    public static string Remove() => TrailerNet.Ask(TrailerOp.Remove, TrailersConfig.Kind.Value);
    public static string ResetBehind() => TrailerNet.Ask(TrailerOp.Reset, TrailersConfig.Kind.Value);

    public static string ToggleHitch()
    {
        MineCount(out var hitched, out _);
        return TrailerNet.Ask(hitched != null ? TrailerOp.Unhitch : TrailerOp.Hitch, TrailersConfig.Kind.Value);
    }

    public static string Load(uint carId)
    {
        if (!CargoModels.Owned(carId)) return "Blocked: you can only load a car you own";
        return TrailerNet.Ask(TrailerOp.Load, TrailerKind.Hauler, carId);
    }

    public static string Unload() => TrailerNet.Ask(TrailerOp.Unload, TrailerKind.Hauler);
}

/// <summary>The scene is about to change (portal, region change): the host takes every trailer away first, guests
/// forget theirs. (The game's own Fusion callback; it takes the runner only.)</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.Fusion_INetworkRunnerCallbacks_OnSceneLoadStart))]
internal static class TrailersSceneLoadPatch
{
    private static void Prefix()
    {
        try { TrailerHost.SceneChanging(); }
        catch (Exception e) { DebugLog.Error("trailers", e, "TrailersSceneLoadPatch.Prefix"); TrailersPlugin.LogOnce("scene", "[Trailers] scene change: " + e.Message); }
    }
}
