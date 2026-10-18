using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using OverTheHillPlus.Features.Cheats.Teleport;
using OverTheHillPlus.HostPowers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Cheats.Events;

/// <summary>
/// Host meteorite controls, all through the game's own CheatsManager RPCs (read from the decompiled bodies):
/// - RPC_SpawnAllMeteorites(size, apocalypseNow) -> MeteoriteSpawnController.DebugSpawnAllMeteoritesNow: despawns every
///   current meteorite, then spawns one at every spawn point. size &lt; 0 = the game's own size pick, otherwise a
///   MeteoriteConfig.MeteoriteSize value (0 VerySmall .. 6 VeryHuge). apocalypseNow = they fall from the sky over the
///   next 0-10 s instead of appearing on the ground.
/// - RPC_SpawnMeteoriteAtLocation(size, groundPoint, delay): one meteorite (the console's SpawnMeteoriteHere passes the
///   ground point under the camera's aim).
/// - RPC_PauseMeteoriteSpawn(pause, alsoDespawn): stops/starts the natural respawn loop.
/// "Reveal" moves each player's map pin (MapNetworkManager.PlayerRefToPinpoint*) onto the meteorite nearest to them.
/// </summary>
internal static class MeteorEvents
{
    internal static readonly string[] SizeNames = { "Very small", "Small", "Medium", "Large", "Very large", "Huge", "Very huge" };

    public static string SizeName(int size) => size >= 0 && size < SizeNames.Length ? SizeNames[size] : "Random";

    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) HostPowersPlugin.Logger.LogWarning("[Events] " + message);
    }

    /// <summary>The spawned CheatsManager plus a spawned meteorite controller, or a reason why not.</summary>
    private static CheatsManager Manager(out string error)
    {
        error = null;
        if (!HostTools.Available) { error = "Blocked: host only."; return null; }
        CheatsManager cm = null;
        try { cm = CheatsManager.Instance; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "MeteorEvents.Manager"); }
        if (cm == null) { error = "Failed: the game's cheat manager isn't loaded in this session."; return null; }
        try
        {
            if (cm.Object == null || !cm.Object.IsValid) { error = "Failed: the cheat manager isn't spawned yet."; return null; }
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "MeteorEvents.Manager"); error = "Failed: the cheat manager isn't spawned yet."; return null; }
        // The RPC bodies dereference MeteoriteSpawnController.Instance without a null check (NullReferenceException).
        try
        {
            var msc = MeteoriteSpawnController.Instance;
            if (msc == null || msc.Object == null || !msc.Object.IsValid) { error = "Failed: this map has no meteorites."; return null; }
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "MeteorEvents.Manager"); error = "Failed: this map has no meteorites."; return null; }
        return cm;
    }

    // Host-side cooldowns: each shower despawns every meteorite and network-spawns one per spawn point.
    private const float ShowerCooldown = 5f, ClearCooldown = 2f, SpawnCooldown = 0.5f;
    private static float _nextShower, _nextClear, _nextSpawn;

    public static string Shower(int size, bool falling)
    {
        var cm = Manager(out var error);
        if (cm == null) return error;
        if (Time.unscaledTime < _nextShower) return "Blocked: wait a few seconds between meteor showers.";
        try
        {
            AdvantageFlag.MarkUsed();
            _nextShower = Time.unscaledTime + ShowerCooldown;
            cm.RPC_SpawnAllMeteorites(Mathf.Clamp(size, -1, SizeNames.Length - 1), falling);
            DebugLog.Info(HostPowersPlugin.ModId, $"meteor shower: size {SizeName(size)}, {(falling ? "falling" : "placed at every spawn point")}");
            return falling
                ? $"Meteor shower: {SizeName(size).ToLowerInvariant()} meteorites falling over the next 10 s."
                : $"Meteorites ({SizeName(size).ToLowerInvariant()}) placed at every spawn point.";
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "MeteorEvents.Shower"); HostPowersPlugin.Logger.LogWarning("[Events] shower: " + e); return "Failed: " + e.Message; }
    }

    public static string SpawnAt(Vector3 ground, int size, float delay, string where)
    {
        var cm = Manager(out var error);
        if (cm == null) return error;
        if (Time.unscaledTime < _nextSpawn) return "Blocked: one meteorite at a time, try again in a moment.";
        try
        {
            AdvantageFlag.MarkUsed();
            _nextSpawn = Time.unscaledTime + SpawnCooldown;
            cm.RPC_SpawnMeteoriteAtLocation(Mathf.Clamp(size, -1, SizeNames.Length - 1), ground, Mathf.Clamp(delay, 0f, 30f));
            DebugLog.Verbose(HostPowersPlugin.ModId, () => $"meteorite {where}: size {SizeName(size)} at {ground.x:0},{ground.z:0}, delay {delay:0.#} s");
            return delay > 0.05f ? $"Meteorite incoming {where} in {delay:0.#} s." : $"Meteorite dropped {where}.";
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "MeteorEvents.SpawnAt"); HostPowersPlugin.Logger.LogWarning("[Events] single meteorite: " + e); return "Failed: " + e.Message; }
    }

    public static string SpawnAtPin(int size, float delay)
    {
        if (!HostTools.Available) return "Blocked: host only.";
        if (!TeleportCore.TryGetPin(out var pin)) return "Failed: no map pin. Open the map and place a pin first.";
        return SpawnAt(TeleportCore.Ground(pin, null, 0f), size, delay, "at your map pin");
    }

    public static string SpawnInFront(int size, float delay, float distance)
    {
        if (!HostTools.Available) return "Blocked: host only.";
        var car = GameState.LocalCar;
        if (car == null) return "Failed: no car.";
        try
        {
            var t = car.transform;
            var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            var target = t.position + fwd.normalized * Mathf.Clamp(distance, 10f, 300f);
            return SpawnAt(TeleportCore.Ground(target, car, 0f), size, delay, $"{distance:0} m ahead");
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "MeteorEvents.SpawnInFront"); return "Failed: " + e.Message; }
    }

    public static string PauseNatural(bool pause, bool alsoDespawn)
    {
        var cm = Manager(out var error);
        if (cm == null) return error;
        if (alsoDespawn && Time.unscaledTime < _nextClear) return "Blocked: wait a moment before clearing meteorites again.";
        try
        {
            AdvantageFlag.MarkUsed();
            if (alsoDespawn) _nextClear = Time.unscaledTime + ClearCooldown;
            cm.RPC_PauseMeteoriteSpawn(pause, alsoDespawn);
            if (!pause) return "Natural meteorites resumed.";
            return alsoDespawn ? "Natural meteorites paused and current ones removed." : "Natural meteorites paused.";
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "MeteorEvents.PauseNatural"); HostPowersPlugin.Logger.LogWarning("[Events] pause meteorites: " + e); return "Failed: " + e.Message; }
    }

    // ---- reveal: everyone's map pin on their nearest meteorite ----

    private static readonly List<Vector3> Found = new();

    /// <summary>Meteorites still worth driving to (counting down, falling or on the ground). One scene scan.</summary>
    private static List<Vector3> ActiveMeteorites()
    {
        Found.Clear();
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Meteorite>()))
            {
                var m = o?.TryCast<Meteorite>();
                if (m == null) continue;
                try
                {
                    if (m.Object == null || !m.Object.IsValid) continue;
                    var st = m.Status;
                    if (st != Meteorite.MeteoriteStatus.Countdown && st != Meteorite.MeteoriteStatus.Falling
                        && st != Meteorite.MeteoriteStatus.OnGround) continue;
                    Found.Add(m.transform.position);
                }
                catch (Exception ex) { DebugLog.Error("hostpowers", ex, "MeteorEvents.ActiveMeteorites"); /* despawning */ }
            }
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "MeteorEvents.ActiveMeteorites"); LogOnce("scan", "meteorite scan: " + e.Message); }
        return Found;
    }

    public static int ActiveCount => ActiveMeteorites().Count;

    /// <summary>
    /// Puts each player's map pin on the meteorite nearest to their car. An existing pin is moved
    /// (RPC_MovePinpoint); a new one is only created while the game's pin table has room, because
    /// RPC_InstantiatePinpoint spawns the pin object before adding it to the table (a full table would throw and leak it).
    /// </summary>
    public static string RevealPins()
    {
        if (!HostTools.Available) return "Blocked: host only.";
        MapNetworkManager mnm = null;
        try { mnm = MapNetworkManager.Instance; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "MeteorEvents.RevealPins"); }
        try { if (mnm == null || mnm.Object == null || !mnm.Object.IsValid) return "Failed: the map isn't loaded."; }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "MeteorEvents.RevealPins"); return "Failed: the map isn't loaded."; }

        var meteors = ActiveMeteorites();
        if (meteors.Count == 0) return "No active meteorites to reveal.";

        int moved = 0, created = 0, skipped = 0;
        AdvantageFlag.MarkUsed();
        foreach (var p in HostTools.Players)
        {
            if (p.Car == null) { skipped++; continue; }
            try
            {
                var from = p.Car.transform.position;
                var best = meteors[0];
                float bestD = float.MaxValue;
                foreach (var m in meteors)
                {
                    float d = (m - from).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = m; }
                }
                var spot = TeleportCore.Ground(best, null, 0f);

                var objects = mnm.PlayerRefToPinpointNetworkObject;
                NetworkObject existing = null;
                if (objects.TryGet(p.Ref, out existing) && existing != null)
                {
                    mnm.RPC_MovePinpoint(p.Ref, spot);
                    moved++;
                }
                else if (!objects.ContainsKey(p.Ref) && objects.Count < objects.Capacity
                         && !mnm.PlayerRefToPinpointPosition.ContainsKey(p.Ref))
                {
                    mnm.RPC_InstantiatePinpoint(p.Ref, spot);
                    created++;
                }
                else skipped++;
            }
            catch (Exception e) { DebugLog.Error("hostpowers", e, "MeteorEvents.RevealPins"); skipped++; LogOnce("pin:" + e.GetType().Name, $"pin for {p.Name}: {e.Message}"); }
        }
        string s = $"Pinned the nearest meteorite for {moved + created} player(s)";
        return skipped > 0 ? s + $" ({skipped} skipped)." : s + ".";
    }
}
