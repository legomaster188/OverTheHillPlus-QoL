using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using Il2CppInterop.Runtime;
using OverTheHillPlus.HostPowers;
using UnityEngine;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus;

/// <summary>
/// Host ("server-side") tools. The host's game owns the session state and simulates every car, so these
/// work on vanilla players too. Everything here requires being the host (or solo).
/// </summary>
internal static class HostTools
{
    public sealed class Player
    {
        public PlayerRef Ref;
        public string Name;
        public VehicleBase Car;
        public bool IsMe;
    }

    private static float _nextRefresh;
    private static readonly List<Player> Cache = new();

    public static IReadOnlyList<Player> Players
    {
        get
        {
            if (Time.unscaledTime < _nextRefresh) return Cache;
            _nextRefresh = Time.unscaledTime + 1f;
            Cache.Clear();
            try
            {
                var sp = OverlandSessionPlayers.Instance;
                var me = GameState.LocalPlayer;
                var seen = new HashSet<int>();

                string NameOf(PlayerRef p)
                {
                    try { var n = sp?.GetPlayerName(p); if (!string.IsNullOrEmpty(n)) return n; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "HostTools.Players"); }
                    return $"Player {p.PlayerId}";
                }

                // Primary source: every player car in the world. This doesn't depend on the game's 4-slot player
                // tables, so players beyond them (bigger lobbies) still show up.
                foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FusionPlayer>()))
                {
                    var fp = o.TryCast<FusionPlayer>();
                    if (fp == null || fp.Object == null) continue;
                    PlayerRef p;
                    try { p = fp.Object.InputAuthority; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "HostTools.Players"); continue; }
                    if (!p.IsRealPlayer || !seen.Add(p.PlayerId)) continue;
                    Cache.Add(new Player { Ref = p, Name = NameOf(p), Car = fp.vehicleBase, IsMe = me.HasValue && me.Value.PlayerId == p.PlayerId });
                }

                // Players still loading in (no car yet) from the game's own list.
                if (sp != null)
                {
                    var order = sp.OrderedPlayerRefs;
                    for (int i = 0; i < order.Length; i++)
                    {
                        var p = order.Get(i);
                        if (!p.IsRealPlayer || !seen.Add(p.PlayerId)) continue;
                        Cache.Add(new Player { Ref = p, Name = NameOf(p), Car = null, IsMe = me.HasValue && me.Value.PlayerId == p.PlayerId });
                    }
                }
                Cache.Sort((a, b) => a.IsMe != b.IsMe ? (a.IsMe ? -1 : 1) : a.Ref.PlayerId.CompareTo(b.Ref.PlayerId));
            }
            catch (Exception e) { DebugLog.Error("hostpowers", e, "HostTools.Players"); HostPowersPlugin.Logger.LogDebug("Player list: " + e.Message); }
            return Cache;
        }
    }

    public static bool Available => GameState.IsAuthority && GameState.Runner != null;

    private static string Guard(Func<string> action)
    {
        if (!Available) return "Blocked: host only.";
        try { AdvantageFlag.MarkUsed(); return action(); }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "HostTools.Guard"); HostPowersPlugin.Logger.LogWarning("Host action failed: " + e); return "Failed: " + e.Message; }
    }

    public static string BringToMe(Player p) => Guard(() =>
    {
        var me = GameState.LocalCar;
        if (me == null || p.Car == null) return "Failed: car not found.";
        var t = me.transform;
        var pos = t.position - t.forward * 8f + t.right * 4f + Vector3.up * 1f;
        p.Car.Reposition(pos, Quaternion.LookRotation(Vector3.ProjectOnPlane(t.forward, Vector3.up), Vector3.up));
        return $"Brought {p.Name} to you.";
    });

    public static string GoTo(Player p) => Guard(() =>
    {
        var me = GameState.LocalCar;
        if (me == null || p.Car == null) return "Failed: car not found.";
        var t = p.Car.transform;
        me.Reposition(t.position - t.forward * 8f + Vector3.up * 1f, Quaternion.LookRotation(Vector3.ProjectOnPlane(t.forward, Vector3.up), Vector3.up));
        return $"Teleported to {p.Name}.";
    });

    public static string Repair(Player p) => Guard(() =>
    {
        var dmg = p.Car?.GetComponent<CarDamageSystem>();
        if (dmg == null) return "Failed: no damage system.";
        dmg.Cheat_SetDamage(0);
        return $"Repaired {p.Name}.";
    });

    public static string Clean(Player p) => Guard(() =>
    {
        var m = p.Car?.GetComponent<CarNetworkedMaterialManager>();
        if (m == null) return "Failed: no dirt system.";
        m.RPC_CleanCar(p.Ref);
        return $"Washed {p.Name}'s car.";
    });

    // Same as Cheats+'s refill for your own car (mods don't reference each other).
    public static string Refill(Player p) => Guard(() =>
    {
        var inv = p.Car?.GetComponent<CarInventory>();
        if (inv == null) return "Failed: no tool inventory found.";
        inv.RPC_RefillItems();
        return "Tools refilled.";
    });

    public static string Flip(Player p) => Guard(() =>
    {
        if (p.Car == null) return "Failed: car not found.";
        var t = p.Car.transform;
        var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        p.Car.Reposition(t.position + Vector3.up * 1.5f, Quaternion.LookRotation(fwd.normalized, Vector3.up));
        return $"Flipped {p.Name} upright.";
    });

    public static string Launch(Player p) => Guard(() =>
    {
        var rb = p.Car?.cachedRigidbody;
        if (rb == null) return "Failed: car not found.";
        rb.AddForce(Vector3.up * 14f + UnityEngine.Random.insideUnitSphere * 4f, ForceMode.VelocityChange);
        return $"Launched {p.Name}!";
    });

    public static string Kick(Player p) => Guard(() =>
    {
        if (p.IsMe) return "That's you.";
        var runner = OverlandNetworkRunner.Instance;
        if (runner == null) return "Failed: no session runner.";
        runner.KickPlayer(p.Ref);
        return $"Kicked {p.Name}.";
    });

    public static string ForEveryone(Func<Player, string> action, string label)
    {
        if (!Available) return "Blocked: host only.";
        int n = 0;
        foreach (var p in Players)
            if (!action(p).StartsWith("Failed")) n++;
        return $"{label}: {n} player(s).";
    }
}

/// <summary>Host option: nobody in the session takes damage.</summary>
[HarmonyPatch(typeof(CarDamageSystem), nameof(CarDamageSystem.ApplyDamage))]
internal static class LobbyGodModePatch
{
    private static bool Prefix() => !(HostPowersConfig.LobbyGodMode.Value && HostTools.Available);
}
