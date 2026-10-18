using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;
using OverTheHillPlus.BigLobbies;

namespace OverTheHillPlus.Features.Public.PlayerTables;

/// <summary>
/// CampManager.SpawnedTents holds 4 tents. The game's SpawnTent (decoded from the ISIL) calls Runner.Spawn first and
/// only then looks for an empty slot; with none left the 5th+ tent is spawned but never recorded, so the end-of-camp
/// animation and clean-up (TweenAndDespawnTents / FixedUpdateNetwork) never remove it and it stays in the world.
/// On the host the extra tents are found right after they spawn (new TentAnimation objects), then animated and
/// despawned together with the recorded ones.
/// </summary>
internal static class TentGuard
{
    private static readonly List<NetworkObject> Extra = new();
    private static HashSet<IntPtr> _before;
    private static float _despawnAt = -1f;
    internal static int Tracked, Despawned;

    internal static int OverflowCount
    {
        get
        {
            int n = 0;
            foreach (var t in Extra) if (t != null) n++;
            return n;
        }
    }

    internal static void Install()
    {
        var t = typeof(CampManager);
        var g = typeof(TentGuard);
        PlayerTables.Patch(t, nameof(CampManager.SpawnTent), null, g, prefix: nameof(SpawnPrefix), finalizer: nameof(SpawnFinalizer));
        PlayerTables.Patch(t, nameof(CampManager.TweenAndDespawnTents), null, g, postfix: nameof(TweenPostfix));
    }

    private static bool Authority(CampManager cm)
    {
        try { return cm != null && cm.HasStateAuthority; } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "TentGuard.Authority"); return false; }
    }

    private static bool HasFreeSlot(CampManager cm)
    {
        var tents = cm.SpawnedTents;
        for (int i = 0; i < tents.Length; i++) if (tents.Get(i) == null) return true;
        return false;
    }

    private static HashSet<IntPtr> TentPointers()
    {
        var set = new HashSet<IntPtr>();
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<TentAnimation>()))
        {
            var ta = o.TryCast<TentAnimation>();
            if (ta != null) set.Add(ta.Pointer);
        }
        return set;
    }

    // ---- patches ----

    private static void SpawnPrefix(CampManager __instance)
    {
        _before = null;
        if (!PlayerTables.On || !Authority(__instance)) return;
        try { if (!HasFreeSlot(__instance)) _before = TentPointers(); } // rare: only when a 5th+ tent is about to spawn
        catch (Exception e) { DebugLog.Error("biglobbies", e, "TentGuard.SpawnPrefix"); PlayerTables.LogOnce("tent:prefix", "SpawnTent guard: " + e.Message); }
    }

    private static void SpawnFinalizer()
    {
        var before = _before;
        _before = null;
        if (before == null) return;
        try
        {
            int found = 0;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<TentAnimation>()))
            {
                var ta = o.TryCast<TentAnimation>();
                if (ta == null || before.Contains(ta.Pointer)) continue;
                var no = ta.GetComponentInParent<NetworkObject>();
                if (no == null) continue;
                Extra.Add(no);
                Tracked++;
                found++;
            }
            if (found > 0) PlayerTables.LogOnce("tent:overflow", "More than 4 tents: tracking the extra ones so they are removed when camping ends");
            else PlayerTables.LogOnce("tent:miss", "An extra tent was spawned past the 4 tent slots but could not be found to track it");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "TentGuard.SpawnFinalizer"); PlayerTables.LogOnce("tent:post", "SpawnTent guard: " + e.Message); }
    }

    /// <summary>Camping ended: play the same despawn animation on the extra tents and remove them shortly after.</summary>
    private static void TweenPostfix(CampManager __instance)
    {
        if (!PlayerTables.On || !Authority(__instance)) return;
        try
        {
            Extra.RemoveAll(t => t == null);
            if (Extra.Count == 0) return;
            float delay = 0f, step = 0.15f;
            for (int i = 0; i < Extra.Count; i++)
            {
                var ta = Extra[i].GetComponentInChildren<TentAnimation>();
                if (ta == null) continue;
                try { step = Mathf.Clamp(ta.TentsDespawnDelay, 0.05f, 1f); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "TentGuard.TweenPostfix"); }
                delay = step * (4 + i); // after the 4 recorded tents
                ta.AnimateDespawn(delay);
            }
            _despawnAt = Time.unscaledTime + delay + 2.5f;
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "TentGuard.TweenPostfix"); PlayerTables.LogOnce("tent:tween", "tent despawn animation: " + e.Message); _despawnAt = Time.unscaledTime + 3f; }
    }

    internal static void Tick()
    {
        if (_despawnAt < 0f || Time.unscaledTime < _despawnAt) return;
        _despawnAt = -1f;
        try
        {
            var runner = GameState.Runner;
            foreach (var t in Extra)
            {
                if (t == null) continue;
                try
                {
                    var r = t.Runner ?? runner;
                    if (r == null || !t.HasStateAuthority) continue;
                    r.Despawn(t);
                    Despawned++;
                }
                catch (Exception e) { DebugLog.Error("biglobbies", e, "TentGuard.Tick"); PlayerTables.LogOnce("tent:despawn1", "extra tent despawn: " + e.Message); }
            }
            if (Despawned > 0) BigLobbiesPlugin.Logger.LogInfo($"[Tables] Removed extra camp tents ({Despawned} so far)");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "TentGuard.Tick"); PlayerTables.LogOnce("tent:despawn", "extra tent despawn: " + e.Message); }
        Extra.Clear();
    }
}
