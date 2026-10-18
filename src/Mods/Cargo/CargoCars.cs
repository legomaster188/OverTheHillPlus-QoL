using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Cargo;

/// <summary>
/// The session's player cars by player id (PlayerRef.RawEncoded), refreshed about once a second from core's shared
/// FusionPlayer scan. On the host these are the positions it simulates itself, which is what deliveries are judged by.
/// </summary>
internal static class CargoCars
{
    internal sealed class Car
    {
        public int Id;
        public VehicleBase Vehicle;
    }

    private static readonly Dictionary<int, Car> ById = new();
    private static float _next;

    public static IReadOnlyCollection<Car> All { get { Refresh(); return ById.Values; } }

    public static void Refresh(bool force = false)
    {
        float now = Time.unscaledTime;
        if (!force && now < _next && now > _next - 2f) return;
        _next = now + 1f;
        ById.Clear();
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
                ById[id] = new Car { Id = id, Vehicle = v };
            }
            catch (System.Exception ex) { DebugLog.Error("cargo", ex, "CargoCars.Refresh"); }
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
        catch (System.Exception ex) { DebugLog.Error("cargo", ex, "CargoCars.TryPos"); return false; }
    }

    public static string NameOf(int id)
    {
        try { return PlayerListRoster.NameOf(OverlandSessionPlayers.Instance, PlayerRef.FromEncoded(id)); }
        catch (System.Exception ex) { DebugLog.Error("cargo", ex, "CargoCars.NameOf"); return "a player"; }
    }

    /// <summary>A name to show: one line, no markup, short.</summary>
    public static string ShortName(int id)
    {
        string n = Multiplayer.HostNotice.Escape(NameOf(id) ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (n.Length == 0) n = "a player";
        return n.Length > 24 ? n.Substring(0, 24) : n;
    }
}
