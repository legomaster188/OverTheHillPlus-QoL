using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.PlayerTables;

/// <summary>
/// VehicleWinchSystem.AttachedWinchesOwners holds 4 players whose winch is hooked to this car. The game's
/// AddAttachedWinchOwner puts an owner in the first empty slot and silently does nothing when there is none
/// (decoded from the ISIL), so a 5th winch on one car is untracked: DetachAllWinches (car despawned, winch points
/// disabled) leaves its rope hooked to a car that is gone, and HasOtherWinchesAttached under-reports.
/// On the host, extra owners go into a side table here: they are detached together with the others, counted as
/// attached, and moved into the array as soon as a slot frees up.
/// </summary>
internal static class WinchOwnerGuard
{
    /// <summary>Extra owners per car winch system (key: native pointer), oldest first.</summary>
    private static readonly Dictionary<IntPtr, List<PlayerRef>> Overflow = new();
    private static readonly Dictionary<(IntPtr, int), float> AddedAt = new();
    private static int _detachAllDepth;
    internal static int TrackedTotal;

    private static readonly List<VehicleWinchSystem> Systems = new();
    private static float _systemsAt = -10f, _nextPrune;

    internal static void Install()
    {
        var t = typeof(VehicleWinchSystem);
        var g = typeof(WinchOwnerGuard);
        PlayerTables.Patch(t, nameof(VehicleWinchSystem.AddAttachedWinchOwner), null, g, prefix: nameof(AddPrefix));
        PlayerTables.Patch(t, nameof(VehicleWinchSystem.RemoveAttachedWinchOwner), null, g, postfix: nameof(RemovePostfix));
        PlayerTables.Patch(t, nameof(VehicleWinchSystem.DetachAllWinches), null, g, prefix: nameof(DetachAllPrefix), finalizer: nameof(DetachAllFinalizer));
        PlayerTables.Patch(t, "get_" + nameof(VehicleWinchSystem.HasOtherWinchesAttached), null, g, postfix: nameof(HasOtherPostfix));
        PlayerTables.Patch(t, nameof(VehicleWinchSystem.Despawned), null, g, postfix: nameof(DespawnedPostfix));
    }

    internal static int OverflowCount(VehicleWinchSystem ws)
    {
        if (ws == null) return 0;
        return Overflow.TryGetValue(ws.Pointer, out var l) ? l.Count : 0;
    }

    private static bool Authority(VehicleWinchSystem ws)
    {
        try { return ws != null && ws.HasStateAuthority; } catch { return false; }
    }

    private static bool InArray(VehicleWinchSystem ws, PlayerRef owner, out bool hasFree)
    {
        hasFree = false;
        var arr = ws.AttachedWinchesOwners;
        bool found = false;
        for (int i = 0; i < arr.Length; i++)
        {
            int raw = arr.Get(i).RawEncoded;
            if (raw == owner.RawEncoded) found = true;
            else if (raw == 0) hasFree = true;
        }
        return found;
    }

    // ---- patches ----

    private static void AddPrefix(VehicleWinchSystem __instance, PlayerRef winchOwner)
    {
        if (!PlayerTables.On || !Authority(__instance) || winchOwner.RawEncoded == 0) return;
        try
        {
            if (InArray(__instance, winchOwner, out bool hasFree) || hasFree) return; // the game handles it
            if (!Overflow.TryGetValue(__instance.Pointer, out var list)) Overflow[__instance.Pointer] = list = new List<PlayerRef>();
            foreach (var p in list) if (p.RawEncoded == winchOwner.RawEncoded) return;
            list.Add(winchOwner);
            AddedAt[(__instance.Pointer, winchOwner.RawEncoded)] = Time.unscaledTime;
            TrackedTotal++;
            PlayerTables.LogOnce("winch:overflow", $"More than {__instance.AttachedWinchesOwners.Length} winches on one car: tracking the extra ones");
        }
        catch (Exception e) { PlayerTables.LogOnce("winch:add", "AddAttachedWinchOwner guard: " + e.Message); }
    }

    private static void RemovePostfix(VehicleWinchSystem __instance, PlayerRef winchOwner)
    {
        if (!PlayerTables.On || __instance == null || !Overflow.TryGetValue(__instance.Pointer, out var list)) return;
        try
        {
            list.RemoveAll(p => p.RawEncoded == winchOwner.RawEncoded);
            if (_detachAllDepth == 0) Promote(__instance, list);
            if (list.Count == 0) Overflow.Remove(__instance.Pointer);
        }
        catch (Exception e) { PlayerTables.LogOnce("winch:remove", "RemoveAttachedWinchOwner guard: " + e.Message); }
    }

    /// <summary>Moves waiting extra owners into freed array slots (the game's own add puts them in the first empty one).</summary>
    private static void Promote(VehicleWinchSystem ws, List<PlayerRef> list)
    {
        while (list.Count > 0)
        {
            InArray(ws, list[0], out bool hasFree);
            if (!hasFree) return;
            var next = list[0];
            list.RemoveAt(0);
            if (!Keep(ws, next)) continue;
            ws.AddAttachedWinchOwner(next);
        }
    }

    private static void DetachAllPrefix() => _detachAllDepth++;

    /// <summary>After the game detached the owners it knows about, detach the extra ones too.</summary>
    private static void DetachAllFinalizer(VehicleWinchSystem __instance)
    {
        try
        {
            if (!PlayerTables.On || !Authority(__instance) || !Overflow.TryGetValue(__instance.Pointer, out var list) || list.Count == 0) return;
            foreach (var owner in list.ToArray())
            {
                var sys = FindOwnerSystem(owner, true);
                if (sys != null && IsAttachedTo(sys, __instance))
                {
                    sys.DetachOwnWinch(); // host: removes itself from our list via RemoveAttachedWinchOwner
                    Plugin.Logger.LogInfo($"[Tables] Detached extra winch of player {owner.PlayerId} with the others");
                }
            }
            Overflow.Remove(__instance.Pointer);
        }
        catch (Exception e) { PlayerTables.LogOnce("winch:detachall", "DetachAllWinches guard: " + e.Message); }
        finally { if (_detachAllDepth > 0) _detachAllDepth--; }
    }

    private static void HasOtherPostfix(VehicleWinchSystem __instance, ref bool __result)
    {
        if (__result || !PlayerTables.On || __instance == null) return;
        try { if (Overflow.TryGetValue(__instance.Pointer, out var list) && list.Count > 0) __result = true; } catch { }
    }

    private static void DespawnedPostfix(VehicleWinchSystem __instance)
    {
        try { if (__instance != null) Overflow.Remove(__instance.Pointer); } catch { }
    }

    // ---- upkeep ----

    /// <summary>Drops extra owners that are no longer hooked to that car (their own detach never reached us).</summary>
    internal static void Tick()
    {
        if (Overflow.Count == 0) { if (AddedAt.Count > 0) AddedAt.Clear(); return; }
        if (Time.unscaledTime < _nextPrune) return;
        _nextPrune = Time.unscaledTime + 1f;
        try
        {
            RefreshSystems(true);
            foreach (var kv in new List<KeyValuePair<IntPtr, List<PlayerRef>>>(Overflow))
            {
                VehicleWinchSystem target = null;
                foreach (var s in Systems) if (s != null && s.Pointer == kv.Key) { target = s; break; }
                if (target == null) { Overflow.Remove(kv.Key); continue; }
                kv.Value.RemoveAll(p => !Keep(target, p));
                Promote(target, kv.Value);
                if (kv.Value.Count == 0) Overflow.Remove(kv.Key);
            }
        }
        catch (Exception e) { PlayerTables.LogOnce("winch:tick", "winch upkeep: " + e.Message); }
    }

    private static void RefreshSystems(bool force)
    {
        if (!force && Time.unscaledTime - _systemsAt < 2f) return;
        if (Time.unscaledTime - _systemsAt < 0.5f) return;
        _systemsAt = Time.unscaledTime;
        Systems.Clear();
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<VehicleWinchSystem>()))
        {
            var s = o.TryCast<VehicleWinchSystem>();
            if (s != null) Systems.Add(s);
        }
    }

    private static VehicleWinchSystem FindOwnerSystem(PlayerRef owner, bool refresh)
    {
        RefreshSystems(refresh);
        foreach (var s in Systems)
        {
            try { if (s != null && s.Object != null && s.Object.InputAuthority.RawEncoded == owner.RawEncoded) return s; } catch { }
        }
        return null;
    }

    private static bool IsAttachedTo(VehicleWinchSystem owner, VehicleWinchSystem target)
    {
        try
        {
            if (target.Object == null) return false;
            int car = target.Object.InputAuthority.RawEncoded;
            var state = owner.WinchState;
            if (state == VehicleWinchSystem.WinchStates.AttachedToOtherPlayer)
                return owner.WinchedCarPlayerRef.RawEncoded == car;
            // StateAuthorityOnObjectAttached also counts a car winched as a movable object (matched by input authority).
            if (state == VehicleWinchSystem.WinchStates.AttachedToMovableObject)
            {
                var rb = owner.WinchedMovableObjectRigidbody;
                return rb != null && rb.Object != null && rb.Object.InputAuthority.RawEncoded == car;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Still hooked to this car, or added under 2 s ago (the owner's winch state may still be settling).</summary>
    private static bool Keep(VehicleWinchSystem target, PlayerRef owner)
    {
        if (AddedAt.TryGetValue((target.Pointer, owner.RawEncoded), out var at) && Time.unscaledTime - at < 2f) return true;
        return StillAttached(target, owner);
    }

    private static bool StillAttached(VehicleWinchSystem target, PlayerRef owner)
    {
        var sys = FindOwnerSystem(owner, false);
        return sys != null && IsAttachedTo(sys, target);
    }

    /// <summary>For the menu: most winches hooked to one car (array + extras) and the array size, 0 if unknown.</summary>
    internal static int MostOnOneCar(out int capacity)
    {
        capacity = 0;
        int most = 0;
        try
        {
            RefreshSystems(false);
            foreach (var s in Systems)
            {
                if (s == null) continue;
                try
                {
                    var arr = s.AttachedWinchesOwners; // throws before the behaviour is Spawned
                    capacity = arr.Length;
                    int n = 0;
                    for (int i = 0; i < arr.Length; i++) if (arr.Get(i).RawEncoded != 0) n++;
                    n += OverflowCount(s);
                    if (n > most) most = n;
                }
                catch { }
            }
        }
        catch { }
        return most;
    }
}
