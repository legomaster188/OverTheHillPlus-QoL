using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Tag;

/// <summary>
/// The session's player cars by player id (PlayerRef.RawEncoded), refreshed about once a second from core's shared
/// FusionPlayer scan. Car and rigidbody pointers map back to players for the collision and physics-step hooks.
/// </summary>
internal static class TagCars
{
    internal sealed class Car
    {
        public int Id;
        public VehicleBase Vehicle;
        public FusionPlayer Player;
    }

    private static readonly Dictionary<int, Car> ById = new();
    private static readonly Dictionary<IntPtr, int> ByCar = new(), ByBody = new(), ByTransform = new();
    private static float _next;

    public static IReadOnlyCollection<Car> All { get { Refresh(); return ById.Values; } }

    public static void Refresh(bool force = false)
    {
        float now = Time.unscaledTime;
        if (!force && now < _next && now > _next - 2f) return;
        _next = now + 1f;
        ById.Clear();
        ByCar.Clear();
        ByBody.Clear();
        ByTransform.Clear();
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                var p = fp.Object.InputAuthority;
                if (!p.IsRealPlayer) continue;
                var v = fp.vehicleBase;
                if (v == null) continue;
                int id = p.RawEncoded;
                if (ById.ContainsKey(id)) continue;
                ById[id] = new Car { Id = id, Vehicle = v, Player = fp };
                try { ByTransform[fp.transform.Pointer] = id; ByTransform[v.transform.Pointer] = id; } catch (Exception ex) { DebugLog.Error("tag", ex, "TagCars.Refresh"); }
                ByCar[v.Pointer] = id;
                try { var rb = v.cachedRigidbody; if (rb != null) ByBody[rb.Pointer] = id; } catch (Exception ex) { DebugLog.Error("tag", ex, "TagCars.Refresh"); }
            }
            catch (Exception ex) { DebugLog.Error("tag", ex, "TagCars.Refresh"); }
        }
    }

    /// <summary>Your own player id, or -1 outside a session.</summary>
    public static int Me
    {
        get { var p = GameState.LocalPlayer; return p.HasValue ? p.Value.RawEncoded : -1; }
    }

    public static bool TryPos(int id, out Vector3 pos)
    {
        pos = default;
        if (!ById.TryGetValue(id, out var c)) return false;
        try
        {
            if (c.Vehicle == null) return false;
            pos = c.Vehicle.transform.position;
            return float.IsFinite(pos.x) && float.IsFinite(pos.y) && float.IsFinite(pos.z);
        }
        catch (Exception ex) { DebugLog.Error("tag", ex, "TagCars.TryPos"); return false; }
    }

    /// <summary>Player id of a car (the physics step's instance), or -1. Cheap: one dictionary lookup.</summary>
    public static int OfCar(IntPtr car) => car != IntPtr.Zero && ByCar.TryGetValue(car, out int id) ? id : -1;

    /// <summary>Player id of a player object's or car's transform (what the compass tracks), or -1.</summary>
    public static int OfTransform(IntPtr t) => t != IntPtr.Zero && ByTransform.TryGetValue(t, out int id) ? id : -1;

    public static int OfBody(IntPtr body) => body != IntPtr.Zero && ByBody.TryGetValue(body, out int id) ? id : -1;

    public static string NameOf(int id)
    {
        try { return PlayerListRoster.NameOf(OverlandSessionPlayers.Instance, PlayerRef.FromEncoded(id)); }
        catch (Exception ex) { DebugLog.Error("tag", ex, "TagCars.NameOf"); return "a player"; }
    }

    /// <summary>A name to show: one line, no markup, short.</summary>
    public static string ShortName(int id)
    {
        string n = Multiplayer.HostNotice.Escape(NameOf(id) ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (n.Length == 0) n = "a player";
        return n.Length > 24 ? n.Substring(0, 24) : n;
    }
}
