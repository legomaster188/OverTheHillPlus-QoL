using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Shared;

/// <summary>
/// Map facts mods share (read-only): the loaded map's key and name, your own map pin, your car's spot and the ground
/// height. Main thread only.
/// </summary>
internal static class GameMap
{
    private static readonly HashSet<string> LoggedOnce = new();
    private static string _areaKey = "", _areaName = "";
    private static float _nextAreaRefresh;

    private static bool _pinCached;
    private static Vector3 _pinCache;
    private static float _pinCachedAt = -100f;

    private static TerrainInfo[] _terrains = Array.Empty<TerrainInfo>();
    private static float _terrainsAt = -100f;

    private static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Count < 50 && LoggedOnce.Add(key)) Core.CorePlugin.Logger?.LogWarning("[Map] " + message);
    }

    // ---------------------------------------------------------------- area

    /// <summary>Stable id of the loaded map (its AreaInfo asset name), for filing pins and tracks per map.</summary>
    public static string AreaKey { get { RefreshArea(); return _areaKey; } }

    /// <summary>Display name of the loaded map.</summary>
    public static string AreaName { get { RefreshArea(); return _areaName; } }

    private static void RefreshArea()
    {
        if (Time.unscaledTime < _nextAreaRefresh) return;
        _nextAreaRefresh = Time.unscaledTime + 2f;
        string key = null, name = null;
        try
        {
            var area = SceneReferences.Instance != null ? SceneReferences.Instance.AreaInfo : null;
            if (area != null)
            {
                key = area.name;
                try { name = area.Name?.GetLocalizedString(); } catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.RefreshArea"); }
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "GameMap.RefreshArea"); LogOnce("area", "area lookup: " + e.Message); }
        if (string.IsNullOrEmpty(key))
        {
            try { key = AnalyticsManager.GetCurrentAreaName(); } catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.RefreshArea"); }
        }
        if (string.IsNullOrEmpty(key))
        {
            try { key = SceneManager.GetActiveScene().name; } catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.RefreshArea"); }
        }
        _areaKey = string.IsNullOrEmpty(key) ? "unknown" : key;
        _areaName = string.IsNullOrEmpty(name) ? _areaKey : name;
    }

    // ---------------------------------------------------------------- your car

    /// <summary>Your car's position (false when you are not driving).</summary>
    public static bool TryCarPosition(out Vector3 pos)
    {
        pos = default;
        var car = GameState.LocalCar;
        if (car == null) return false;
        try { pos = car.transform.position; return true; }
        catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.TryCarPosition"); return false; }
    }

    private static readonly Dictionary<int, Transform> PlayerCars = new();
    private static int _playerCarsFrame = -1;

    /// <summary>
    /// The car of <paramref name="player"/> (any player; the host simulates them all, guests see copies). The player →
    /// car table is built once per frame from <see cref="GameState.FusionPlayers"/>, so asking for every racer is cheap.
    /// </summary>
    public static bool TryPlayerCarPosition(PlayerRef player, out Vector3 pos)
    {
        pos = default;
        int frame = Time.frameCount;
        if (frame != _playerCarsFrame)
        {
            _playerCarsFrame = frame;
            PlayerCars.Clear();
            foreach (var fp in GameState.FusionPlayers)
            {
                try
                {
                    if (fp == null || fp.Object == null) continue;
                    var t = fp.vehicleBase != null ? fp.vehicleBase.transform : fp.transform;
                    if (t != null) PlayerCars[fp.Object.InputAuthority.RawEncoded] = t;
                }
                catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.TryPlayerCarPosition"); }
            }
        }
        if (!PlayerCars.TryGetValue(player.RawEncoded, out var car)) return false;
        try
        {
            if (car == null) return false;
            pos = car.position;
            return true;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.TryPlayerCarPosition"); return false; }
    }

    // ---------------------------------------------------------------- your map pin

    /// <summary>
    /// Your map pin, read the way the game's own "teleport to pin" debug command does
    /// (MapNetworkManager.PlayerRefToPinpointPosition, then the pin's network object). Only when the map network
    /// manager isn't there does it look for your pin flare in the world (and, solo, the one flare around).
    /// </summary>
    public static bool TryGetGamePin(out Vector3 pin)
    {
        pin = default;
        var me = GameState.LocalPlayer;
        MapNetworkManager mnm = null;
        try { mnm = MapNetworkManager.Instance; } catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.TryGetGamePin"); }

        bool spawned = false;
        try { spawned = mnm != null && mnm.Object != null && mnm.Object.IsValid; } catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.TryGetGamePin"); }
        if (spawned)
        {
            // The game's own table answers: no entry means no pin (never another player's flare).
            if (!me.HasValue) return false;
            try
            {
                var positions = mnm.PlayerRefToPinpointPosition;
                Vector3 v = default;
                if (positions.TryGet(me.Value, out v)) { pin = v; return true; }
            }
            catch (Exception e) { DebugLog.Error("core", e, "GameMap.TryGetGamePin"); LogOnce("pinpos", "pin position: " + e.Message); }
            try
            {
                var objects = mnm.PlayerRefToPinpointNetworkObject;
                NetworkObject no = null;
                if (objects.TryGet(me.Value, out no) && no != null) { pin = no.transform.position; return true; }
            }
            catch (Exception e) { DebugLog.Error("core", e, "GameMap.TryGetGamePin"); LogOnce("pinobj", "pin object: " + e.Message); }
            return false;
        }

        // No map network manager: the pin flare in the world. Ours by input authority; solo, also the only one around.
        try
        {
            bool solo;
            try { var r = GameState.Runner; solo = r == null || r.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.TryGetGamePin"); solo = false; }
            WorldSpacePinpoint only = null;
            int count = 0;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<WorldSpacePinpoint>()))
            {
                var wp = o.TryCast<WorldSpacePinpoint>();
                if (wp == null) continue;
                count++;
                only = wp;
                try
                {
                    var no = wp.networkObject;
                    if (no != null && me.HasValue && no.InputAuthority.PlayerId == me.Value.PlayerId)
                    {
                        pin = wp.transform.position;
                        return true;
                    }
                }
                catch (Exception ex) { DebugLog.Error("core", ex, "GameMap.TryGetGamePin"); }
            }
            if (solo && count == 1 && only != null) { pin = only.transform.position; return true; }
        }
        catch (Exception e) { DebugLog.Error("core", e, "GameMap.TryGetGamePin"); LogOnce("pinflare", "pin flare: " + e.Message); }
        return false;
    }

    /// <summary>Like <see cref="TryGetGamePin"/> but re-read at most twice a second (for menus, overlays and ticks).</summary>
    public static bool TryGetGamePinCached(out Vector3 pin)
    {
        if (Time.unscaledTime - _pinCachedAt > 0.5f || Time.unscaledTime < _pinCachedAt)
        {
            _pinCachedAt = Time.unscaledTime;
            _pinCached = TryGetGamePin(out _pinCache);
        }
        pin = _pinCache;
        return _pinCached;
    }

    // ---------------------------------------------------------------- ground

    private struct TerrainInfo
    {
        public Terrain Terrain;
        public float X, Y, Z, SizeX, SizeZ;
    }

    /// <summary>
    /// Terrain height under a point, if a terrain covers it. The terrains with their origin and size are re-listed
    /// every 2 s, so a point costs one SampleHeight call.
    /// </summary>
    public static bool TryTerrainHeight(float x, float z, out float height)
    {
        height = 0f;
        try
        {
            if (Time.unscaledTime - _terrainsAt > 2f || Time.unscaledTime < _terrainsAt)
            {
                _terrainsAt = Time.unscaledTime;
                var list = new List<TerrainInfo>();
                foreach (var t in Terrain.activeTerrains)
                {
                    if (t == null) continue;
                    var data = t.terrainData;
                    if (data == null) continue;
                    var o = t.GetPosition();
                    var size = data.size;
                    list.Add(new TerrainInfo { Terrain = t, X = o.x, Y = o.y, Z = o.z, SizeX = size.x, SizeZ = size.z });
                }
                _terrains = list.ToArray();
            }
            for (int i = 0; i < _terrains.Length; i++)
            {
                ref var ti = ref _terrains[i];
                if (x < ti.X || z < ti.Z || x > ti.X + ti.SizeX || z > ti.Z + ti.SizeZ) continue;
                if (ti.Terrain == null) { _terrainsAt = -100f; continue; } // unloaded: re-list next time
                height = ti.Terrain.SampleHeight(new Vector3(x, 0f, z)) + ti.Y;
                return true;
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "GameMap.TryTerrainHeight"); LogOnce("terrain", "terrain height: " + e.Message); }
        return false;
    }

    /// <summary>Flat (ground-plane) distance.</summary>
    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>"85 m" / "1.4 km".</summary>
    public static string DistanceText(float meters) =>
        meters < 1000f ? $"{Mathf.RoundToInt(meters)} m" : $"{meters / 1000f:0.0} km";
}
