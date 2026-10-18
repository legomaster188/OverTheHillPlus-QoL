using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Waypoints;

/// <summary>Waypoints+ settings in BepInEx/config/oth.waypoints.cfg. The pins themselves are in oth.waypoints/pins.json.</summary>
internal static class WaypointsConfig
{
    public static ConfigEntry<Key> SaveKey;
    public static ConfigEntry<int> NewPinColor;

    public static ConfigEntry<bool> ShowRoute, ShowMarkers, FollowGamePin, ClearOnArrival;
    public static ConfigEntry<float> RouteLength, MarkerRange, ArriveDistance;

    public static ConfigEntry<bool> ShowSavedOnMap, ShowSharedOnMap, ShowRouteOnMap;

    public static ConfigEntry<bool> FollowHostPush, AcceptGuestPins, AnnouncePush;
    public static ConfigEntry<int> MaxPinsPerGuest;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        SaveKey = cfg.Bind("Pins", "SaveKey", Key.None, "Hotkey: save your map pin as a named pin (your car's spot when you have no map pin). None = no hotkey.");
        NewPinColor = cfg.Bind("Pins", "NewPinColor", 0, "Colour of new pins (0-7: yellow, blue, green, red, purple, teal, pink, white).");

        ShowRoute = cfg.Bind("Route", "ShowRoute", true, "Draw a dotted line on the ground from your car toward the pin you follow.");
        RouteLength = cfg.Bind("Route", "RouteLength", 300f, "How far ahead the dotted line reaches, in metres.");
        ShowMarkers = cfg.Bind("Route", "ShowMarkers", true, "Show your saved pins and the session's pins in the world (name and distance).");
        MarkerRange = cfg.Bind("Route", "MarkerRange", 1500f, "Pins farther than this (metres) are not shown in the world (the one you follow always is).");
        FollowGamePin = cfg.Bind("Route", "FollowGamePin", true, "When you follow no pin, route to your own map pin (the game's pin) if you placed one.");
        ArriveDistance = cfg.Bind("Route", "ArriveDistance", 20f, "You have arrived when this close (metres).");
        ClearOnArrival = cfg.Bind("Route", "ClearOnArrival", true, "Stop following a pin once you arrive at it.");

        ShowSavedOnMap = cfg.Bind("Map", "ShowSavedOnMap", true, "Show your saved pins of this map on the map screen.");
        ShowSharedOnMap = cfg.Bind("Map", "ShowSharedOnMap", true, "Show the session's shared pins on the map screen.");
        ShowRouteOnMap = cfg.Bind("Map", "ShowRouteOnMap", true, "Draw the route to the pin you follow on the map screen.");

        FollowHostPush = cfg.Bind("Session", "FollowHostPush", true, "When the host pushes a pin to everyone, follow it.");
        AcceptGuestPins = cfg.Bind("Session", "AcceptGuestPins", true, "When hosting: guests with this mod may share pins with the session.");
        MaxPinsPerGuest = cfg.Bind("Session", "MaxPinsPerGuest", 6, "When hosting: how many shared pins each guest may have at once.");
        AnnouncePush = cfg.Bind("Session", "AnnouncePush", true, "When hosting: pushing a pin also shows a short notice to everyone (players without the mod too).");
    }
}
