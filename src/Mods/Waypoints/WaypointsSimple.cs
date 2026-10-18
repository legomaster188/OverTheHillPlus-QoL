using System.Collections.Generic;
using OverTheHillPlus.Shared;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Waypoints;

/// <summary>Waypoints+ on its MODS page: the route, saving pins, your pins on this map and the session's pins.</summary>
internal static class WaypointsSimple
{
    private static string _newName = "";
    private static object _confirmDelete;
    private static float _confirmAt;
    private static int _importable = -1;
    private static float _importCheckedAt = -100f;

    internal static void Draw()
    {
        if (WaypointsConfig.ShowRoute == null) return; // failed to start
        bool hasCar = GameMap.TryCarPosition(out var car);
        string area = GameMap.AreaKey;
        if (Time.unscaledTime - _confirmAt > 4f) _confirmDelete = null;

        SimpleMenu.Heading("Route");
        SimpleMenu.Button("Stop following", WaypointsRoute.Describe(), "The dotted route leads to this pin (in the world and on the map).",
            WaypointsRoute.Stop, WaypointsRoute.Following);
        SimpleMenu.Toggle(WaypointsConfig.ShowRoute, "Dotted route on the ground", "A line of dots from your car toward the pin.");
        SimpleMenu.Toggle(WaypointsConfig.FollowGamePin, "Route to my map pin", "When you follow no saved or shared pin.");
        SimpleMenu.Toggle(WaypointsConfig.FollowHostPush, "Follow the host's pushed pin", "When the host pushes a pin to everyone.");

        SimpleMenu.Heading("Save a pin");
        _newName = SimpleMenu.Text("Name", _newName, PinBoard.MaxName, "Name for the next pin (empty: Pin 1, Pin 2, ...).");
        int color = Mathf.Clamp(WaypointsConfig.NewPinColor.Value, 0, PinBoard.ColorCount - 1);
        SimpleMenu.Choice("Colour", "Colour of the next pin.", color, WorldOverlay.PinColorNames, i => WaypointsConfig.NewPinColor.Value = i);
        bool hasPin = GameMap.TryGetGamePinCached(out var gamePin);
        SimpleMenu.Button("Save my map pin", hasPin ? Dist(hasCar, car, gamePin) : "No map pin", "Place a pin on the game's map first.",
            () => Saved(PinStore.Add(area, _newName, gamePin, WaypointsConfig.NewPinColor.Value)), hasPin);
        SimpleMenu.Button("Save my car's spot", hasCar ? "Here" : "No car", "Saves where your car is now.",
            () => Saved(PinStore.Add(area, _newName, car, WaypointsConfig.NewPinColor.Value)), hasCar);

        var saved = PinStore.For(area);
        int others = PinStore.CountOtherAreas(area);
        SimpleMenu.Heading($"Saved on {GameMap.AreaName} ({saved.Count})");
        if (saved.Count == 0) SimpleMenu.Note("No pins saved on this map yet." + (others > 0 ? $" {others} on other maps." : ""));
        bool share = SessionPins.Active;
        foreach (var p in new List<SavedPin>(saved))
        {
            var items = new List<(string, System.Action, bool)>();
            bool following = WaypointsRoute.IsFollowing(p);
            items.Add((following ? "Following" : "Route", () => WaypointsRoute.FollowSaved(p), !following));
            if (share)
            {
                if (SessionPins.IsHost) items.Add(("Push", () => Status(SessionPins.ShareAndPush(p.Name, p.Position, p.Color)), true));
                items.Add(("Share", () => Status(SessionPins.Share(p.Name, p.Position, p.Color)), true));
            }
            bool confirming = ReferenceEquals(_confirmDelete, p);
            items.Add((confirming ? "Sure?" : "Delete", () =>
            {
                if (confirming) { PinStore.Remove(area, p); _confirmDelete = null; Status($"Deleted \"{p.Name}\"."); }
                else { _confirmDelete = p; _confirmAt = Time.unscaledTime; }
            }, true));
            SimpleMenu.ActionsFor("pin:" + p.Name, p.Name + "  ·  " + Dist(hasCar, car, p.Position), items.ToArray());
        }

        if (Time.unscaledTime - _importCheckedAt > 5f)
        {
            _importCheckedAt = Time.unscaledTime;
            _importable = PinStore.ImportableCount();
        }
        if (_importable > 0)
            SimpleMenu.Button("Import old waypoints", $"{_importable} spot(s)", "Copies the spots saved by the older all-in-one mod (OverTheHillPlus.waypoints.json).",
                () => { Status(PinStore.ImportOld()); _importCheckedAt = -100f; });

        SimpleMenu.Heading("Shared with the session");
        SimpleMenu.Note(SessionPins.Status);
        if (!share) return;
        foreach (var p in new List<BoardPin>(SessionPins.Board.Pins))
        {
            var pos = new Vector3(p.X, p.Y, p.Z);
            var items = new List<(string, System.Action, bool)>();
            bool following = WaypointsRoute.IsFollowing(p);
            items.Add((following ? "Following" : "Route", () => WaypointsRoute.FollowShared(p), !following));
            items.Add(("Save", () => Status(PinStore.Add(area, p.Name, pos, p.Color)), true));
            if (SessionPins.IsHost) items.Add((SessionPins.Board.PushedId == p.Id ? "Pushed" : "Push", () => Status(SessionPins.Push(p)), true));
            if (SessionPins.CanRemove(p)) items.Add(("Remove", () => Status(SessionPins.Remove(p)), true));
            string who = p.Owner.Length > 0 ? "  ·  " + p.Owner : "";
            SimpleMenu.ActionsFor("shared:" + p.Name + who, p.Name + who + "  ·  " + Dist(hasCar, car, pos), items.ToArray());
        }
    }

    private static void Saved(string result)
    {
        if (result.StartsWith("Saved")) _newName = "";
        Status(result);
    }

    private static void Status(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        Menu.SetStatus(s);
        WaypointsFeature.Toast(s);
    }

    private static string Dist(bool hasCar, Vector3 car, Vector3 pos) =>
        hasCar ? GameMap.DistanceText(GameMap.FlatDistance(car, pos)) : "";
}
