using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.Shared;
using UnityEngine;

namespace OverTheHillPlus.Minimap;

/// <summary>
/// What the minimap draws, all local reads:
/// - the game's own map picture and fog of war: MapManager.Instance.MapTexture / MapWidth / TerrainOffset and its
///   FogOfWarManager's FogOfWarTexture / mapWidth / terrainOffset (the fog's alpha is how hidden a spot is: the game's
///   CalculateSmoothAlpha gives 0 round where you have been). Only the parts you have discovered are visible, as on
///   the game's map; without the fog texture no map picture is drawn at all;
/// - players: every player in the session (core's PlayerListRoster), placed with
///   OverlandNetworkRunner.GetPlayerPositionAndRotation, the same source the game's map uses, coloured with the
///   game's player colours (UIManager.GetPlayerColor);
/// - pins: the game's map pins (MapNetworkManager.PlayerRefToPinpointPosition), and the pins other mods share through
///   core (PinSources: Waypoints+'s saved pins and the session's shared pins, in their colours);
/// - the route: to the spot another mod's route leads to (PinSources.TryTarget: the pin you follow in Waypoints+),
///   else to your own map pin.
/// </summary>
internal static class MinimapSource
{
    internal struct Marker
    {
        public Vector3 Pos;
        public float Heading;
        public Color Color;
        public string Name;
    }

    internal struct Pin
    {
        public Vector3 Pos;
        public Color Color;
    }

    private struct Player
    {
        public PlayerRef Ref;
        public string Name;
        public Color Color;
    }

    public static Texture MapTexture { get; private set; }
    public static Texture FogTexture { get; private set; }
    public static float MapWidth { get; private set; }
    public static Vector3 MapOffset { get; private set; }
    public static float FogWidth { get; private set; }
    public static Vector3 FogOffset { get; private set; }
    public static bool HasMap => MapTexture != null && MapWidth > 1f;
    public static bool HasFog => FogTexture != null && FogWidth > 1f;

    /// <summary>
    /// The fog of war covers the whole map picture (same square, within 1%). Otherwise part of the picture would have
    /// no fog over it, so no picture is drawn.
    /// </summary>
    public static bool FogCoversMap =>
        HasMap && HasFog && Mathf.Abs(FogWidth - MapWidth) <= MapWidth * 0.01f &&
        Mathf.Abs(FogOffset.x - MapOffset.x) <= MapWidth * 0.01f && Mathf.Abs(FogOffset.z - MapOffset.z) <= MapWidth * 0.01f;

    public static Vector3 OwnPos { get; private set; }
    public static float OwnHeading { get; private set; }
    public static bool HasOwn { get; private set; }
    public static Vector3? OwnPin { get; private set; }
    /// <summary>Where the route line goes: another mod's route target, else your own map pin.</summary>
    public static Vector3? RouteTo { get; private set; }

    public static readonly List<Marker> Others = new();
    public static readonly List<Pin> Pins = new();
    /// <summary>Pins other mods share through core (smaller marks than the game's own pins).</summary>
    public static readonly List<Pin> SharedPins = new();
    private const int MaxSharedPins = 64;

    private static readonly List<Player> Players = new();
    private static PlayerRef _local;
    private static bool _hasLocal;
    private static Color _localColor = Color.white;
    private static float _nextPlayers, _nextMap, _nextPins;
    private static bool _hadMap;
    private static int _loggedPlayers = -1, _loggedPins = -1;
    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string key, string message)
    {
        if (Logged.Count < 32 && Logged.Add(key)) MinimapPlugin.Logger.LogWarning("[Minimap] " + message);
    }

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        var car = GameState.LocalCar;
        HasOwn = car != null;
        if (!HasOwn) return;
        try
        {
            var t = car.transform;
            OwnPos = t.position;
            var f = t.forward;
            OwnHeading = MinimapMath.Heading(f.x, f.z);
        }
        catch (Exception ex) { DebugLog.Error("minimap", ex, "MinimapSource.Tick"); HasOwn = false; return; }

        if (now >= _nextMap)
        {
            CrashTrail.Mark("minimap: find map");
            // The textures change with the map loaded: look again every 2 s (every 0.5 s while there is none).
            FindMap();
            _nextMap = now + (HasMap && HasFog ? 2f : 0.5f);
        }
        if (now >= _nextPlayers)
        {
            _nextPlayers = now + 1f;
            CrashTrail.Mark("minimap: list players");
            RefreshPlayers();
        }
        CrashTrail.Mark("minimap: place players");
        PlacePlayers();
        if (now >= _nextPins)
        {
            _nextPins = now + 0.25f;
            CrashTrail.Mark("minimap: read pins");
            ReadPins();
        }
    }

    private static void FindMap()
    {
        try
        {
            var mm = MapManager.Instance;
            if (mm == null) { MapTexture = null; FogTexture = null; return; }
            MapTexture = mm.MapTexture;
            MapWidth = mm.MapWidth;
            MapOffset = mm.TerrainOffset;
            var fow = mm.FogOfWarManager;
            if (fow != null)
            {
                FogTexture = fow.FogOfWarTexture;
                FogWidth = fow.mapWidth;
                FogOffset = fow.terrainOffset;
            }
            else FogTexture = null;
            bool has = HasMap && HasFog;
            if (has != _hadMap)
            {
                _hadMap = has;
                if (has)
                    DebugLog.Info(MinimapPlugin.ModId, $"map found: picture {MapTexture.width}x{MapTexture.height} over {MapWidth:0} m, fog {FogTexture.width}x{FogTexture.height} over {FogWidth:0} m, " +
                                                      $"fog covers the map: {FogCoversMap}");
                else DebugLog.Info(MinimapPlugin.ModId, "map gone (left the map or the game's map manager is not there)");
            }
        }
        catch (Exception e)
        {
            MapTexture = null;
            FogTexture = null;
            DebugLog.Error(MinimapPlugin.ModId, e, "reading the game's map");
            LogOnce("map", "reading the game's map: " + e.Message);
        }
    }

    private static void RefreshPlayers()
    {
        Players.Clear();
        _hasLocal = false;
        try
        {
            var runner = PlayerListRoster.Runner;
            if (runner == null) return;
            try { _local = runner.LocalPlayer; _hasLocal = true; } catch (Exception ex) { DebugLog.Error("minimap", ex, "MinimapSource.RefreshPlayers"); }
            var sp = OverlandSessionPlayers.Instance;
            foreach (var p in PlayerListRoster.AllPlayers())
            {
                var c = Color.white;
                try { c = UIManager.GetPlayerColor(p); } catch (Exception ex) { DebugLog.Error("minimap", ex, "MinimapSource.RefreshPlayers"); }
                if (_hasLocal && p.RawEncoded == _local.RawEncoded) { _localColor = c; continue; }
                Players.Add(new Player { Ref = p, Name = Clean(PlayerListRoster.NameOf(sp, p)), Color = c });
            }
            if (Players.Count != _loggedPlayers)
            {
                _loggedPlayers = Players.Count;
                DebugLog.Verbose(MinimapPlugin.ModId, () => $"{Players.Count} other player(s) to show on the minimap");
            }
        }
        catch (Exception e)
        {
            DebugLog.Error(MinimapPlugin.ModId, e, "listing players");
            LogOnce("players", "listing players: " + e.Message);
        }
    }

    private static void PlacePlayers()
    {
        Others.Clear();
        if (Players.Count == 0) return;
        OverlandNetworkRunner runner = null;
        OverlandSessionPlayers sp = null;
        try { runner = OverlandNetworkRunner.Instance; } catch (Exception ex) { DebugLog.Error("minimap", ex, "MinimapSource.PlacePlayers"); }
        try { sp = OverlandSessionPlayers.Instance; } catch (Exception ex) { DebugLog.Error("minimap", ex, "MinimapSource.PlacePlayers"); }
        if (runner == null) return;
        foreach (var p in Players)
        {
            try
            {
                if (sp != null && !sp.SpawnedInMap(p.Ref)) continue;
                Vector3 pos = default;
                Quaternion rot = Quaternion.identity;
                if (!runner.GetPlayerPositionAndRotation(p.Ref, out pos, out rot)) continue;
                var f = rot * Vector3.forward;
                Others.Add(new Marker { Pos = pos, Heading = MinimapMath.Heading(f.x, f.z), Color = p.Color, Name = p.Name });
            }
            catch (Exception ex) { DebugLog.Error("minimap", ex, "MinimapSource.PlacePlayers"); /* player leaving mid-read */ }
        }
    }

    private static void ReadPins()
    {
        Pins.Clear();
        OwnPin = null;
        try
        {
            var mn = MapNetworkManager.Instance;
            if (mn == null) return;
            var pins = mn.PlayerRefToPinpointPosition;
            if (_hasLocal && pins.ContainsKey(_local))
            {
                var own = pins.Get(_local);
                OwnPin = own;
                Pins.Add(new Pin { Pos = own, Color = _localColor });
            }
            foreach (var p in Players)
                if (pins.ContainsKey(p.Ref)) Pins.Add(new Pin { Pos = pins.Get(p.Ref), Color = p.Color });
            if (Pins.Count != _loggedPins)
            {
                _loggedPins = Pins.Count;
                DebugLog.Verbose(MinimapPlugin.ModId, () => $"{Pins.Count} map pin(s) read (own pin: {OwnPin.HasValue})");
            }
        }
        catch (Exception e)
        {
            DebugLog.Error(MinimapPlugin.ModId, e, "reading map pins");
            LogOnce("pins", "reading map pins: " + e.Message);
        }
        finally { ReadSharedPins(); }
    }

    /// <summary>Core's shared spots (PinSources, main thread; its list is cached until a mod publishes again) and the
    /// route target.</summary>
    private static void ReadSharedPins()
    {
        SharedPins.Clear();
        RouteTo = OwnPin;
        try
        {
            var all = PinSources.All();
            for (int i = 0; i < all.Count && SharedPins.Count < MaxSharedPins; i++)
            {
                var p = all[i];
                var c = p.HasColor ? WorldOverlay.PinColor(p.Color) : Color.white;
                SharedPins.Add(new Pin { Pos = new Vector3(p.X, p.Y, p.Z), Color = c });
            }
            if (PinSources.TryTarget(out var t)) RouteTo = new Vector3(t.X, t.Y, t.Z);
        }
        catch (Exception e)
        {
            DebugLog.Error(MinimapPlugin.ModId, e, "reading the shared pins");
            LogOnce("shared", "reading the shared pins: " + e.Message);
        }
    }

    /// <summary>Names come from other players: one short plain line, no rich-text tags.</summary>
    private static string Clean(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        name = name.Replace('\n', ' ').Replace('\r', ' ').Replace("<", "").Replace(">", "").Trim();
        return name.Length > 20 ? name.Substring(0, 20) : name;
    }
}
