using System.Collections.Generic;
using OverTheHillPlus.Shared;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Waypoints;

/// <summary>
/// The pin you follow (a saved pin, a shared pin, or your own map pin), arriving at it, and what is drawn: markers and
/// a dotted ground route in the world (core's <see cref="WorldOverlay"/>), pins and the route on the map screen
/// (core's <see cref="MapMarkers"/>). Also offers your pins to the other mods through core's <see cref="PinSources"/>.
/// </summary>
internal static class WaypointsRoute
{
    private enum Kind { None, Saved, Shared }

    private const string Owner = "waypoints";

    private static Kind _kind;
    private static SavedPin _saved;
    private static string _savedArea;
    private static ushort _sharedId;
    private static bool _arrived;
    private static string _arrivedName;
    private static Vector3 _arrivedPos;
    private static float _nextMap, _nextPublish;
    private static int _publishedVersion = -1, _publishedRevision = -1;
    private static string _publishedArea;
    private static readonly List<MapMarkers.Spot> Spots = new();

    public static bool Following => _kind != Kind.None;

    public static void FollowSaved(SavedPin p)
    {
        if (p == null) return;
        _kind = Kind.Saved;
        _saved = p;
        _savedArea = GameMap.AreaKey;
        _arrived = false;
        DebugLog.Info(WaypointsPlugin.ModId, $"route to the saved pin '{p.Name}' on '{_savedArea}'");
    }

    public static void FollowShared(BoardPin p)
    {
        if (p == null) return;
        _kind = Kind.Shared;
        _sharedId = p.Id;
        _arrived = false;
        DebugLog.Info(WaypointsPlugin.ModId, $"route to the shared pin '{p.Name}' ({p.Owner})");
    }

    public static void Stop()
    {
        if (_kind != Kind.None) DebugLog.Info(WaypointsPlugin.ModId, "route stopped");
        _kind = Kind.None;
        _saved = null;
        _sharedId = 0;
    }

    public static bool IsFollowing(SavedPin p) => _kind == Kind.Saved && ReferenceEquals(_saved, p);

    public static bool IsFollowing(BoardPin p) => _kind == Kind.Shared && p != null && _sharedId == p.Id;

    /// <summary>The spot the route goes to now: the pin you follow, else your map pin (when that setting is on).</summary>
    public static bool TryTarget(out Vector3 pos, out string name, out Color color, out bool chosen)
    {
        pos = default;
        name = null;
        color = Theme.Accent;
        chosen = false;
        switch (_kind)
        {
            case Kind.Saved:
                if (_saved != null && _savedArea == GameMap.AreaKey && PinStore.Contains(_savedArea, _saved))
                {
                    pos = _saved.Position;
                    name = _saved.Name;
                    color = WorldOverlay.PinColor(_saved.Color);
                    chosen = true;
                    return true;
                }
                Stop(); // deleted, or another map
                break;
            case Kind.Shared:
                var p = SessionPins.Board.Find(_sharedId);
                if (p != null)
                {
                    pos = new Vector3(p.X, p.Y, p.Z);
                    name = p.Name;
                    color = WorldOverlay.PinColor(p.Color);
                    chosen = true;
                    return true;
                }
                Stop(); // removed from the session
                break;
        }
        if (WaypointsConfig.FollowGamePin.Value && GameMap.TryGetGamePinCached(out pos))
        {
            name = "My map pin";
            color = Theme.Text;
            return true;
        }
        return false;
    }

    /// <summary>One line for the menu: what the route goes to.</summary>
    public static string Describe()
    {
        if (!TryTarget(out var pos, out string name, out _, out bool chosen)) return "Nothing";
        string dist = GameMap.TryCarPosition(out var car) ? " · " + GameMap.DistanceText(GameMap.FlatDistance(car, pos)) : "";
        return (chosen ? name : name + " (auto)") + dist;
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        bool hasCar = GameMap.TryCarPosition(out var car);
        bool hasTarget = TryTarget(out var target, out string name, out var color, out bool chosen);

        // Arriving.
        if (hasCar && hasTarget)
        {
            float d = GameMap.FlatDistance(car, target);
            // The same target as the last arrival: same name and (within a metre) the same spot. No string building.
            bool same = _arrived && string.Equals(_arrivedName, name, System.StringComparison.Ordinal) &&
                        GameMap.FlatDistance(_arrivedPos, target) < 1f;
            if (d <= Mathf.Max(3f, WaypointsConfig.ArriveDistance.Value))
            {
                if (!same)
                {
                    _arrived = true;
                    _arrivedName = name;
                    _arrivedPos = target;
                    WaypointsFeature.Toast($"Arrived at {name}");
                    if (chosen && WaypointsConfig.ClearOnArrival.Value) Stop();
                }
            }
            else if (same && d > WaypointsConfig.ArriveDistance.Value * 2f + 10f) _arrived = false;
        }

        // Map screen.
        bool routeOnMap = hasCar && hasTarget && WaypointsConfig.ShowRouteOnMap.Value;
        MapMarkers.SetRoute(Owner, routeOnMap, car, target, color);
        // The spot the route leads to, for the other mods (the Minimap draws the route to it).
        PinSources.SetTarget(Owner, hasTarget, new NamedPoint(name, target.x, target.y, target.z, "route"));
        if (Time.unscaledTime >= _nextMap || Time.unscaledTime < _nextMap - 5f)
        {
            _nextMap = Time.unscaledTime + 0.5f;
            Spots.Clear();
            if (WaypointsConfig.ShowSavedOnMap.Value)
                foreach (var p in PinStore.For(GameMap.AreaKey))
                    Spots.Add(new MapMarkers.Spot(p.Position, p.Name, WorldOverlay.PinColor(p.Color)));
            if (WaypointsConfig.ShowSharedOnMap.Value && SessionPins.Active)
                foreach (var p in SessionPins.Board.Pins)
                    Spots.Add(new MapMarkers.Spot(new Vector3(p.X, p.Y, p.Z), p.Name + (p.Owner.Length > 0 ? " · " + p.Owner : ""), WorldOverlay.PinColor(p.Color)));
            MapMarkers.Set(Owner, Spots);
        }

        // Offer the pins to the other mods (Race Mode's checkpoints), when they changed.
        if (Time.unscaledTime >= _nextPublish || Time.unscaledTime < _nextPublish - 5f)
        {
            _nextPublish = Time.unscaledTime + 1f;
            string area = GameMap.AreaKey;
            int rev = SessionPins.Active ? SessionPins.Board.Revision ^ SessionPins.Board.Epoch : -2;
            if (PinStore.Version != _publishedVersion || rev != _publishedRevision || area != _publishedArea)
            {
                _publishedVersion = PinStore.Version;
                _publishedRevision = rev;
                _publishedArea = area;
                var list = new List<NamedPoint>();
                foreach (var p in PinStore.For(area)) list.Add(new NamedPoint(p.Name, p.X, p.Y, p.Z, "saved pin", p.Color));
                if (SessionPins.Active)
                    foreach (var p in SessionPins.Board.Pins) list.Add(new NamedPoint(p.Name, p.X, p.Y, p.Z, "shared pin", p.Color));
                PinSources.Publish(Owner, list);
            }
        }
    }

    public static void DrawWorld()
    {
        if (!WorldOverlay.Begin()) return;
        if (!GameMap.TryCarPosition(out var car)) return;
        bool hasTarget = TryTarget(out var target, out string name, out var color, out _);
        Vector3 camPos = WorldOverlay.Cam.transform.position;

        if (WaypointsConfig.ShowMarkers.Value)
        {
            float range = WaypointsConfig.MarkerRange.Value;
            foreach (var p in PinStore.For(GameMap.AreaKey))
            {
                if (hasTarget && IsFollowing(p)) continue;
                DrawPin(p.Position, p.Name, WorldOverlay.PinColor(p.Color), camPos, range);
            }
            if (SessionPins.Active)
                foreach (var p in SessionPins.Board.Pins)
                {
                    if (hasTarget && IsFollowing(p)) continue;
                    DrawPin(new Vector3(p.X, p.Y, p.Z), p.Name, WorldOverlay.PinColor(p.Color), camPos, range);
                }
        }

        if (!hasTarget) return;
        if (WaypointsConfig.ShowRoute.Value) WorldOverlay.GroundDots(car, target, color, Mathf.Clamp(WaypointsConfig.RouteLength.Value, 20f, 2000f));
        float d = GameMap.FlatDistance(car, target);
        WorldOverlay.Marker(target + Vector3.up * 2.5f, $"{name}  {GameMap.DistanceText(d)}", color, edgeWhenOffscreen: true, size: 14f);
    }

    private static void DrawPin(Vector3 pos, string name, Color color, Vector3 camPos, float range)
    {
        float dist = Vector3.Distance(camPos, pos);
        if (dist > range) return;
        WorldOverlay.Marker(pos + Vector3.up * 2f, $"{name}  {GameMap.DistanceText(dist)}", new Color(color.r, color.g, color.b, 0.8f), edgeWhenOffscreen: false, size: 9f);
    }
}
