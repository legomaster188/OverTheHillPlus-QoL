using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using OverTheHillPlus.HostPowers;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Cheats.Teleport;

/// <summary>
/// MODS > Host Powers > Teleport: saved spots per map (JSON), map pin, searchable points of interest, other players
/// (host), raw coordinates and a "back" button. Moving cars needs solo/host; listing works anywhere.
/// </summary>
internal sealed class TeleportFeature : IFeature
{
    private const string Section = "Teleport";

    internal static ConfigEntry<float> Clearance;
    internal static ConfigEntry<bool> StopCar;
    internal static ConfigEntry<bool> ShowMarkers;
    internal static ConfigEntry<float> MarkerRange;
    internal static ConfigEntry<Key> PinKey;
    internal static ConfigEntry<Key> BackKey;
    internal static ConfigEntry<Key> QuickSaveKey;

    // menu state
    private static string _newName = "";
    private static string _search = "";
    private static string _category = "All";
    private static bool _sortByDistance = true;
    private static int _poiLimit = 25;
    private static string _coordX = "", _coordZ = "";
    private static WaypointStore.Waypoint _pendingDelete;
    private static float _pendingDeleteAt;

    // sorted view of the POIs (re-sorted at most twice a second, not every frame)
    private static readonly List<PoiIndex.Poi> PoiView = new();
    private static float _nextPoiSort;
    private static string _viewKey = "";

    // overlay state
    private static Camera _cam;
    private static float _nextCam;
    private static string _toast = "";
    private static float _toastAt = -100f;
    private static GUIStyle _markerStyle, _toastStyle;

    public void Register()
    {
        var cfg = HostPowersConfig.File;
        Clearance = cfg.Bind(Section, "GroundClearance", 1.5f,
            new ConfigDescription("Height above the ground a teleported car is dropped from (m).", new AcceptableValueRange<float>(0.3f, 10f)));
        StopCar = cfg.Bind(Section, "StopCarOnTeleport", true, "Zero the car's speed and spin after a teleport.");
        ShowMarkers = cfg.Bind(Section, "ShowSavedSpotsOnScreen", false, "Draw your saved spots for this map as on-screen markers.");
        MarkerRange = cfg.Bind(Section, "MarkerRange", 3000f,
            new ConfigDescription("Hide on-screen markers further away than this (m).", new AcceptableValueRange<float>(100f, 10000f)));
        PinKey = cfg.Bind(Section, "TeleportToPinKey", Key.None, "Hotkey: teleport to your map pin (solo/host). None = off.");
        BackKey = cfg.Bind(Section, "TeleportBackKey", Key.None, "Hotkey: go back to where you were before the last teleport. None = off.");
        QuickSaveKey = cfg.Bind(Section, "QuickSaveSpotKey", Key.None, "Hotkey: save your current position as a waypoint. None = off.");

        Registry.Section(Registry.PlusMode, "Teleport", DrawTab);
        Registry.OnTick(Tick);
        Registry.OnGui(DrawOverlay);
    }

    // ---- hotkeys ----

    private static bool Pressed(Keyboard kb, ConfigEntry<Key> entry)
    {
        var k = entry.Value;
        if (k == Key.None) return false;
        try { return kb[k].wasPressedThisFrame; }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportFeature.Pressed"); return false; }
    }

    private static void Tick()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (PinKey.Value == Key.None && BackKey.Value == Key.None && QuickSaveKey.Value == Key.None) return;
        if (GUIUtility.keyboardControl != 0) return; // typing in a menu text field
        if (Pressed(kb, PinKey)) Report(TeleportCore.TeleportToPin());
        else if (Pressed(kb, BackKey)) Report(TeleportCore.Back());
        else if (Pressed(kb, QuickSaveKey)) Report(SaveHere(null));
    }

    private static void Report(string status)
    {
        if (string.IsNullOrEmpty(status)) return;
        Menu.SetStatus(status);
        _toast = status;
        _toastAt = Time.unscaledTime;
        HostPowersPlugin.Logger.LogInfo("[Teleport] " + status);
    }

    // ---- actions ----

    private static string SaveHere(string name)
    {
        if (!TeleportCore.TryCarPosition(out var pos, out var yaw)) return "Failed: no car (are you driving?).";
        return WaypointStore.Add(TeleportCore.AreaKey, name, pos, yaw);
    }

    private static string SavePin(string name)
    {
        if (!TeleportCore.TryGetPin(out var pin)) return "Failed: no map pin. Open the map and place a pin first.";
        float yaw = 0f;
        if (TeleportCore.TryCarPosition(out var here, out _))
        {
            var d = Vector3.ProjectOnPlane(pin - here, Vector3.up);
            if (d.sqrMagnitude > 1f) yaw = Quaternion.LookRotation(d.normalized, Vector3.up).eulerAngles.y;
        }
        return WaypointStore.Add(TeleportCore.AreaKey, string.IsNullOrWhiteSpace(name) ? "Map pin" : name, pin, yaw);
    }

    private static string GoToCoordinates()
    {
        if (!TryParse(_coordX, out float x) || !TryParse(_coordZ, out float z)) return "Failed: enter X and Z as numbers.";
        float y = 0f;
        if (TeleportCore.TryTerrainHeight(new Vector3(x, 0f, z), out float th)) y = th;
        else if (TeleportCore.TryCarPosition(out var here, out _)) y = here.y;
        return TeleportCore.TeleportSelf(new Vector3(x, y, z), null, $"{x:0}, {z:0}");
    }

    private static bool TryParse(string s, out float v) =>
        float.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && float.IsFinite(v);

    // ---- menu ----

    private static void DrawTab()
    {
        bool can = GameState.PhysicsAllowed;
        bool haveCar = GameState.LocalCar != null;
        bool canGo = can && haveCar;
        Vector3 here = default;
        bool hasHere = TeleportCore.TryCarPosition(out here, out _);

        if (!can)
            Menu.Banner("You're a guest here: the host's game simulates your car, so teleports only work solo or when hosting. You can still browse and save spots.", Theme.Info);
        else if (!haveCar)
            Menu.Banner("No car found yet. Teleports work once you're driving.", Theme.Warn);

        Menu.Card("Quick teleport", null, () =>
        {
            Menu.KeyValue("Map", TeleportCore.AreaName);
            Menu.KeyValue("Your position", hasHere ? $"{here.x:0}, {here.y:0}, {here.z:0}" : "—");
            bool hasPin = TeleportCore.TryGetPinCached(out var pin);
            Menu.KeyValue("Map pin", !hasPin ? "none (place one on the map)"
                : hasHere ? $"{pin.x:0}, {pin.z:0}  ·  {TeleportCore.Distance(Vector3.Distance(here, pin))} away"
                : $"{pin.x:0}, {pin.z:0}");
            Menu.Chips(2,
                ("Teleport to map pin", () => Report(TeleportCore.TeleportToPin()), canGo && hasPin),
                ($"Back ({TeleportCore.HistoryCount})", () => Report(TeleportCore.Back()), canGo && TeleportCore.HistoryCount > 0));
            Menu.Small($"Hotkeys (config, section Teleport): pin {KeyName(PinKey)}, back {KeyName(BackKey)}, quick save {KeyName(QuickSaveKey)}.");
        });

        DrawSaved(canGo, hasHere, here);
        DrawPois(canGo, hasHere, here);
        DrawPlayers(canGo, hasHere, here);

        Menu.Card("Coordinates", "Ground height is found for you.", () =>
        {
            _coordX = SimpleMenu.Text("X", _coordX ?? "", 12, null);
            _coordZ = SimpleMenu.Text("Z", _coordZ ?? "", 12, null);
            SimpleMenu.Button("Go there", null, null, () => Report(GoToCoordinates()), canGo);
            if (hasHere)
                SimpleMenu.Button("Use my position", null, "Fills X and Z with where your car is.", () =>
                {
                    _coordX = here.x.ToString("0", CultureInfo.InvariantCulture);
                    _coordZ = here.z.ToString("0", CultureInfo.InvariantCulture);
                });
        });

        Menu.Card("Teleport options", null, () =>
        {
            Menu.SliderRow(Clearance, "Drop height", 0.3f, 10f, "0.0", " m", true);
            Menu.Switch(StopCar, "Stop the car on arrival", "Zero speed and spin after teleporting", true);
            Menu.Switch(ShowMarkers, "Show saved spots on screen", "Markers with distance for this map's spots", true);
            Menu.SliderRow(MarkerRange, "Marker range", 100f, 10000f, "0", " m", ShowMarkers.Value);
        });
    }

    private static string KeyName(ConfigEntry<Key> k) => k.Value == Key.None ? "off" : k.Value.ToString();

    private static void DrawSaved(bool canGo, bool hasHere, Vector3 here)
    {
        string area = TeleportCore.AreaKey;
        var spots = WaypointStore.For(area);
        int others = WaypointStore.CountOtherAreas(area);
        string subtitle = $"{spots.Count} on this map" + (others > 0 ? $", {others} on other maps" : "") + ". Saved to OverTheHillPlus.waypoints.json.";
        Menu.Card("Saved spots", subtitle, () =>
        {
            _newName = Menu.TextRow("Name", _newName, 40);
            Menu.Chips(2,
                ("Save my position", () => { Report(SaveHere(_newName)); _newName = ""; }, hasHere),
                ("Save map pin", () => { Report(SavePin(_newName)); _newName = ""; }, true));

            if (spots.Count == 0) { Menu.Small("No saved spots on this map yet."); return; }
            if (_pendingDelete != null && Time.unscaledTime - _pendingDeleteAt > 4f) _pendingDelete = null;

            // Iterate a copy: buttons can change the list.
            foreach (var w in spots.ToArray())
            {
                var spot = w;
                string dist = hasHere ? TeleportCore.Distance(Vector3.Distance(here, w.Position)) : "";
                bool confirming = _pendingDelete == w;
                SimpleMenu.ActionsFor("spot:" + w.Name, w.Name + (dist.Length > 0 ? "  ·  " + dist : ""),
                    ("Go", () => Report(TeleportCore.TeleportSelf(spot.Position, spot.Yaw, spot.Name)), canGo),
                    ("Up", () => WaypointStore.Move(area, spot, -1), spots.Count > 1 && spots[0] != w),
                    ("Name", () =>
                    {
                        WaypointStore.Rename(area, spot, _newName);
                        Report($"Renamed to \"{spot.Name}\".");
                        _newName = "";
                    }, !string.IsNullOrWhiteSpace(_newName)),
                    ("Here", () =>
                    {
                        if (TeleportCore.TryCarPosition(out var p, out var yaw)) { WaypointStore.Update(area, spot, p, yaw); Report($"Moved \"{spot.Name}\" to your position."); }
                    }, hasHere),
                    (confirming ? "Sure?" : "Del", () =>
                    {
                        if (_pendingDelete == spot) { WaypointStore.Remove(area, spot); _pendingDelete = null; Report($"Deleted \"{spot.Name}\"."); }
                        else { _pendingDelete = spot; _pendingDeleteAt = Time.unscaledTime; }
                    }, true));
            }
            Menu.Small("Name: renames to the text in the Name box. Here: moves the spot to your car. Del: click twice.");
        });
    }

    private static void RefreshPoiView(bool hasHere, Vector3 here)
    {
        string key = $"{_search}|{_category}|{_sortByDistance}|{PoiIndex.All.Count}";
        if (key == _viewKey && Time.unscaledTime < _nextPoiSort) return;
        _viewKey = key;
        _nextPoiSort = Time.unscaledTime + 0.5f;
        PoiView.Clear();
        string q = (_search ?? "").Trim().ToLowerInvariant();
        foreach (var p in PoiIndex.All)
        {
            if (_category != "All" && p.Category != _category) continue;
            if (q.Length > 0 && !p.Search.Contains(q)) continue;
            PoiView.Add(p);
        }
        if (_sortByDistance && hasHere)
            PoiView.Sort((a, b) => (a.Position - here).sqrMagnitude.CompareTo((b.Position - here).sqrMagnitude));
    }

    private static void DrawPois(bool canGo, bool hasHere, Vector3 here)
    {
        PoiIndex.EnsureFresh();
        Menu.Card("Points of interest", $"{PoiIndex.All.Count} on this map: cabins, merchants, portals, scenic points, trails and more.", () =>
        {
            string before = _search;
            _search = Menu.TextRow("Search", _search, 40);
            if (_search != before) _poiLimit = 25;

            var cats = new List<string> { "All" };
            cats.AddRange(PoiIndex.Categories);
            var chips = cats.Select(c => ((c == _category ? "• " : "") + c, (Action)(() => { _category = c; _poiLimit = 25; }), true)).ToArray();
            Menu.Chips(Math.Min(5, Math.Max(1, chips.Length)), chips);
            Menu.Chips(2,
                (_sortByDistance ? "Sorted by distance" : "Sorted by name", () => _sortByDistance = !_sortByDistance, hasHere),
                ("Rescan map", () => { PoiIndex.Rebuild(); _viewKey = ""; Report($"Found {PoiIndex.All.Count} points of interest."); }, true));

            RefreshPoiView(hasHere, here);
            if (PoiView.Count == 0) { Menu.Small(PoiIndex.All.Count == 0 ? "No points of interest found (load into a map, then Rescan)." : "Nothing matches."); return; }

            int shown = Math.Min(_poiLimit, PoiView.Count);
            for (int i = 0; i < shown; i++)
            {
                var p = PoiView[i];
                string dist = hasHere ? "  ·  " + TeleportCore.Distance(Vector3.Distance(here, p.Position)) : "";
                SimpleMenu.ActionsFor("poi:" + p.Name + "/" + p.Category, $"{p.Name}  <color=#8C96A6>{p.Category}{dist}</color>",
                    ("Go", () => Report(TeleportCore.TeleportSelf(p.Position, p.Yaw, p.Name)), canGo));
            }
            if (shown < PoiView.Count)
                Menu.Chips(1, ($"Show more ({PoiView.Count - shown} hidden)", () => _poiLimit += 25, true));
            Menu.Small("Cabins, portals and merchants land on their spawn point, trails at the start line.");
        });
    }

    private static void DrawPlayers(bool canGo, bool hasHere, Vector3 here)
    {
        Menu.Card("Players", "Go to someone, or bring them to you (host).", () =>
        {
            IReadOnlyList<HostTools.Player> players;
            try { players = HostTools.Players; }
            catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportFeature.DrawPlayers"); players = Array.Empty<HostTools.Player>(); }
            int n = 0;
            foreach (var p in players)
            {
                if (p == null || p.IsMe) continue;
                n++;
                string name = (p.Name ?? "?").Replace('<', '‹').Replace('>', '›');
                string dist = "no car";
                try { if (p.Car != null) dist = hasHere ? TeleportCore.Distance(Vector3.Distance(here, p.Car.transform.position)) : ""; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportFeature.DrawPlayers"); }
                bool hasCar = p.Car != null;
                var who = p;
                SimpleMenu.ActionsFor("player:" + name, name + (dist.Length > 0 ? "  ·  " + dist : ""),
                    ("Go to", () => Report(TeleportCore.GoToPlayer(who)), canGo && hasCar),
                    ("Bring", () => Report(TeleportCore.BringPlayer(who)), canGo && hasCar));
            }
            if (n == 0) Menu.Small("No other players here.");
        });
    }

    // ---- overlay ----

    private static void DrawOverlay()
    {
        bool toast = Time.unscaledTime - _toastAt < 2.5f && _toast.Length > 0;
        if (!toast && !ShowMarkers.Value) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        EnsureStyles();
        GUI.matrix = Matrix4x4.identity;

        if (toast)
        {
            var c = _toast.StartsWith("Failed") ? Theme.Bad : _toast.StartsWith("Blocked") ? Theme.Warn : Theme.Good;
            var size = _toastStyle.CalcSize(new GUIContent(_toast));
            var r = new Rect((Screen.width - size.x) / 2f - 14f, 40f, size.x + 28f, 34f);
            Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.88f), 8);
            Theme.Fill(new Rect(r.x, r.y, 4f, r.height), c, 2);
            GUI.Label(r, _toast, _toastStyle);
        }

        if (!ShowMarkers.Value || GameState.LocalCar == null) return;
        var spots = WaypointStore.For(TeleportCore.AreaKey);
        if (spots.Count == 0) return;
        if (Time.unscaledTime >= _nextCam || _cam == null)
        {
            _nextCam = Time.unscaledTime + 2f;
            _cam = Camera.main;
        }
        if (_cam == null) return;
        Vector3 camPos = _cam.transform.position;
        float range = MarkerRange.Value;
        foreach (var w in spots)
        {
            var pos = w.Position + Vector3.up * 2f;
            float dist = Vector3.Distance(camPos, pos);
            if (dist > range) continue;
            var sp = _cam.WorldToScreenPoint(pos);
            if (sp.z <= 0f) continue;
            float x = sp.x, y = Screen.height - sp.y;
            if (x < -50f || x > Screen.width + 50f || y < -50f || y > Screen.height + 50f) continue;
            Theme.Fill(new Rect(x - 5f, y - 5f, 10f, 10f), Theme.Accent, 5);
            string text = $"{w.Name}  {TeleportCore.Distance(dist)}";
            var size = _markerStyle.CalcSize(new GUIContent(text));
            var lr = new Rect(x - size.x / 2f - 6f, y - 30f, size.x + 12f, 20f);
            Theme.Fill(lr, new Color(0.07f, 0.09f, 0.11f, 0.72f), 6);
            GUI.Label(lr, text, _markerStyle);
        }
    }

    private static void EnsureStyles()
    {
        Theme.Ensure();
        if (_markerStyle != null) return;
        _markerStyle = new GUIStyle(Theme.Small) { alignment = TextAnchor.MiddleCenter, richText = false };
        _markerStyle.normal.textColor = Theme.Text;
        _toastStyle = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, richText = false };
        _toastStyle.normal.textColor = Theme.Text;
    }
}
