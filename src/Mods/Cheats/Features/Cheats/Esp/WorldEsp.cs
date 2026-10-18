using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using OverTheHillPlus.CheatsPlus;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Cheats.Esp;

/// <summary>
/// World ESP: labels points of interest (cabins, merchants, portals, scenic points, trails, item recoveries, crates,
/// undiscovered ones too), meteorites, meteorite spawn points, animals and NPCs, with distance and discovery state.
/// Purely local and read-only, so it works in any session. Object lists are rescanned in the background every 2 s.
/// </summary>
internal sealed class WorldEsp : IFeature
{
    private const string Sec = "ESP.World";

    internal static ConfigEntry<bool> Enabled, ShowDistance, ShowDetails, ShowUndiscovered, HideCompleted,
        EdgeMarkers, DeepScan, AnimalsOnlyNew;
    internal static ConfigEntry<float> MaxDistance, AnimalDistance, TextScale, MaxLabels;
    internal static ConfigEntry<Key> ToggleKey;
    private static readonly ConfigEntry<bool>[] Categories = new ConfigEntry<bool>[CatCount];

    private const int CatCount = (int)WorldEspCat.Npc + 1;

    private static readonly string[] CatNames =
    {
        "Cabins", "Merchants", "Portals", "Scenic points", "Trails", "Item recovery", "Crates", "Other POIs",
        "Meteorites", "Meteorite spawn points", "Animals", "NPCs",
    };

    private static readonly string[] CatSingular =
    {
        "Cabin", "Merchant", "Portal", "Scenic point", "Trail", "Recovery", "Crate", "POI",
        "Meteorite", "Spawn point", "Animal", "NPC",
    };

    private static readonly bool[] CatDefaults =
    {
        true, true, true, true, true, true, true, true,
        true, false, false, true,
    };

    private static readonly Color[] CatColors =
    {
        Theme.Hex(0xF2A65A), // cabin
        Theme.Hex(0x4CC38A), // merchant
        Theme.Hex(0xB07CF7), // portal
        Theme.Hex(0x4FA8F7), // scenic
        Theme.Hex(0x3DD6C6), // trail
        Theme.Hex(0xFF7A45), // recovery
        Theme.Hex(0xFFD54A), // crate
        Theme.Hex(0xC9D1DB), // other
        Theme.Hex(0xFF5CA8), // meteorite
        Theme.Hex(0x9A6A86), // meteorite spawn
        Theme.Hex(0x9BE36B), // animal
        Theme.Hex(0xF78FB3), // npc
    };

    public void Register()
    {
        var f = CheatsConfig.File;
        Enabled = f.Bind(Sec, "Enabled", false, "Draw world ESP labels (points of interest, meteorites, animals...).");
        ToggleKey = f.Bind(Sec, "ToggleKey", Key.None, "Key that toggles the world ESP (None = no key).");
        MaxDistance = f.Bind(Sec, "MaxDistance", 2000f, "Hide labels further than this (metres).");
        AnimalDistance = f.Bind(Sec, "AnimalDistance", 350f, "Separate, shorter range for animals (metres).");
        ShowDistance = f.Bind(Sec, "ShowDistance", true, "Show the distance under each label.");
        ShowDetails = f.Bind(Sec, "ShowDetails", true, "Show type / state / size details under each label.");
        ShowUndiscovered = f.Bind(Sec, "ShowUndiscovered", true, "Also label points of interest you have not discovered yet.");
        HideCompleted = f.Bind(Sec, "HideCompleted", false, "Hide completed points, opened crates, extracted meteorites, photographed animals.");
        EdgeMarkers = f.Bind(Sec, "EdgeMarkers", false, "Pin off-screen targets to the screen edge.");
        DeepScan = f.Bind(Sec, "DeepScan", false, "Also list inactive and internal (never shown) points of interest.");
        AnimalsOnlyNew = f.Bind(Sec, "AnimalsOnlyNew", false, "Only label animal species you have not photographed yet.");
        TextScale = f.Bind(Sec, "TextScale", 1f, "Label text size multiplier.");
        MaxLabels = f.Bind(Sec, "MaxLabels", 120f, "Most labels drawn at once (nearest first).");
        for (int i = 0; i < CatCount; i++)
        {
            Categories[i] = f.Bind(Sec, "Show" + ((WorldEspCat)i), CatDefaults[i], $"World ESP: show {CatNames[i].ToLowerInvariant()}.");
            Categories[i].SettingChanged += OnScanSettingChanged;
        }
        DeepScan.SettingChanged += OnScanSettingChanged;
        AnimalsOnlyNew.SettingChanged += OnScanSettingChanged;

        Registry.OnTick(Tick);
        Registry.OnGui(DrawOverlay);
        Registry.Section(Registry.PlusMode, "ESP", DrawMainCard, 10);
        Registry.Section(Registry.PlusMode, "ESP", DrawCategoriesCard, 11);
        Registry.Section(Registry.PlusMode, "ESP", DrawNearestCard, 12);
    }

    private static void OnScanSettingChanged(object sender, EventArgs e) => _rescanSoon = true;

    internal static bool CategoryOn(WorldEspCat c)
    {
        var e = Categories[(int)c];
        return e != null && e.Value;
    }

    // ---------------------------------------------------------------- per frame

    private static bool _rescanSoon;
    private static bool _wasEnabled;
    private static float _menuWantedUntil;
    private static bool _markedUsed;

    private static void Tick()
    {
        var key = ToggleKey.Value;
        if (key != Key.None)
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null && kb[key].wasPressedThisFrame)
                {
                    Enabled.Value = !Enabled.Value;
                    Menu.SetStatus(Enabled.Value ? "World ESP on" : "World ESP off");
                }
            }
            catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEsp.Tick"); }
        }

        // Scan while drawing, or briefly after the ESP tab was drawn (for the nearest list).
        bool wanted = Enabled.Value || Time.unscaledTime < _menuWantedUntil;
        if (!wanted)
        {
            if (_wasEnabled) { WorldEspScanner.Clear(); _wasEnabled = false; }
            return;
        }
        if (!_wasEnabled || _rescanSoon)
        {
            _wasEnabled = true;
            _rescanSoon = false;
            WorldEspScanner.ForceRefresh();
            return;
        }
        WorldEspScanner.Tick();
    }

    // ---------------------------------------------------------------- overlay

    private static readonly List<WorldEspTarget> Visible = new();
    private static GUIStyle _nameStyle, _infoStyle;
    private static float _styleScale = -1f;
    private static Camera _cam;
    private static float _nextCamSearch;

    private static Camera FindCamera()
    {
        try
        {
            if (_cam != null && _cam.isActiveAndEnabled && Time.unscaledTime < _nextCamSearch) return _cam;
            _nextCamSearch = Time.unscaledTime + 2f;
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled) return _cam = main;
            Camera best = null;
            foreach (var c in Camera.allCameras)
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                if (best == null || c.depth > best.depth) best = c;
            }
            return _cam = best;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEsp.FindCamera"); return _cam = null; }
    }

    private static void EnsureStyles(float scale)
    {
        if (_nameStyle != null && Mathf.Abs(scale - _styleScale) < 0.01f) return;
        _styleScale = scale;
        _nameStyle = new GUIStyle
        {
            fontSize = Mathf.RoundToInt(13f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter,
            clipping = TextClipping.Overflow,
            wordWrap = false,
            richText = false,
        };
        _nameStyle.normal.textColor = Color.white;
        _infoStyle = new GUIStyle(_nameStyle)
        {
            fontSize = Mathf.RoundToInt(11f * scale),
            fontStyle = FontStyle.Normal,
        };
        _infoStyle.normal.textColor = Color.white;
    }

    private static Color ColorOf(WorldEspTarget t) => t.Tint.a > 0f ? t.Tint : CatColors[(int)t.Cat];

    private static bool PassesFilters(WorldEspTarget t)
    {
        if (t.Dead || !CategoryOn(t.Cat)) return false;
        if (t.State == WorldEspState.Undiscovered && !ShowUndiscovered.Value && t.Cat != WorldEspCat.Animal) return false;
        if (t.State == WorldEspState.Completed && HideCompleted.Value) return false;
        return true;
    }

    /// <summary>Current world position; moving targets read their transform (and are dropped once destroyed).</summary>
    private static bool UpdatePosition(WorldEspTarget t)
    {
        if (ReferenceEquals(t.Live, null)) return !t.Dead; // static target
        try
        {
            if (t.Live == null) { t.Dead = true; return false; }
            t.Pos = t.Live.position;
            return true;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEsp.UpdatePosition"); t.Dead = true; return false; }
    }

    private static void DrawOverlay()
    {
        if (!Enabled.Value) return;
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint) return;

        var cam = FindCamera();
        if (cam == null) return;

        Vector3 camPos;
        try { camPos = cam.transform.position; }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEsp.DrawOverlay"); _cam = null; return; }

        bool edges = EdgeMarkers.Value;
        float maxDist = MaxDistance.Value, animalDist = Mathf.Min(AnimalDistance.Value, maxDist);
        Visible.Clear();
        foreach (var t in WorldEspScanner.All)
        {
            if (!PassesFilters(t) || !UpdatePosition(t)) continue;
            float d = Vector3.Distance(camPos, t.Pos);
            if (d > (t.Cat == WorldEspCat.Animal ? animalDist : maxDist)) continue;
            t.Dist = d;
            try { t.View = cam.WorldToViewportPoint(t.Pos + Vector3.up * LabelLift(t.Cat)); }
            catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEsp.DrawOverlay"); continue; }
            // Off-screen targets only count against the label cap when edge markers will draw them.
            if (!edges && (t.View.z <= 0f || t.View.x < 0f || t.View.x > 1f || t.View.y < 0f || t.View.y > 1f)) continue;
            Visible.Add(t);
        }
        if (Visible.Count == 0) return;
        if (!_markedUsed) { _markedUsed = true; try { OverTheHillPlus.AdvantageFlag.MarkUsed(); } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEsp.DrawOverlay"); } }

        Visible.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        int cap = Mathf.Clamp(Mathf.RoundToInt(MaxLabels.Value), 5, 500);

        var prevMatrix = GUI.matrix;
        var prevColor = GUI.color;
        var prevContent = GUI.contentColor;
        GUI.matrix = Matrix4x4.identity;
        try
        {
            float sw = Screen.width, sh = Screen.height;
            float scale = Mathf.Clamp(sh / 1080f, 0.75f, 3f) * Mathf.Clamp(TextScale.Value, 0.5f, 3f);
            EnsureStyles(scale);
            Theme.Ensure();

            // Draw far to near so the nearest labels end up on top.
            int n = Mathf.Min(cap, Visible.Count);
            for (int i = n - 1; i >= 0; i--)
            {
                var t = Visible[i];
                try { DrawTarget(t, sw, sh, scale, maxDist); }
                catch (Exception e) { DebugLog.Error("cheats", e, "WorldEsp.DrawOverlay"); WorldEspScanner.LogOnce("draw", e); }
            }
        }
        finally
        {
            GUI.matrix = prevMatrix;
            GUI.color = prevColor;
            GUI.contentColor = prevContent;
        }
    }

    private static float LabelLift(WorldEspCat c) => c switch
    {
        WorldEspCat.Animal => 1.2f,
        WorldEspCat.Npc => 2.1f,
        WorldEspCat.Meteorite => 2f,
        WorldEspCat.Crate => 1.2f,
        _ => 3f,
    };

    private static readonly Color Shade = new(0f, 0f, 0f, 0.65f);

    /// <summary>
    /// Rounded fill tinted through GUI.color, so Theme's texture cache only ever sees the fixed palette colours
    /// (a per-distance alpha passed as the colour would create a new texture for every value).
    /// </summary>
    private static void Dot(Rect r, Color baseColor, float alpha, float radius)
    {
        baseColor.a = baseColor == Shade ? Shade.a : 1f;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
        Theme.Fill(r, baseColor, Mathf.Clamp(Mathf.RoundToInt(radius), 1, 16));
        GUI.color = Color.white;
    }

    private static void DrawTarget(WorldEspTarget t, float sw, float sh, float scale, float maxDist)
    {
        var v = t.View;
        bool behind = v.z <= 0f;
        bool onScreen = !behind && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;
        var col = ColorOf(t);
        float fade = Mathf.Lerp(1f, 0.55f, Mathf.Clamp01(t.Dist / Mathf.Max(1f, maxDist)));
        if (t.State == WorldEspState.Completed) fade *= 0.6f;

        if (!onScreen)
        {
            if (EdgeMarkers.Value) DrawEdge(t, v, behind, sw, sh, scale, col, fade);
            return;
        }

        float x = v.x * sw, y = (1f - v.y) * sh;
        float dot = 8f * scale;
        bool hollow = t.State == WorldEspState.Undiscovered;
        Dot(new Rect(x - dot / 2f - 1.5f, y - dot / 2f - 1.5f, dot + 3f, dot + 3f), Shade, 0.9f * fade, dot / 2f + 1.5f);
        Dot(new Rect(x - dot / 2f, y - dot / 2f, dot, dot), col, fade, dot / 2f);
        if (hollow)
        {
            float h = dot * 0.45f;
            Dot(new Rect(x - h / 2f, y - h / 2f, h, h), Shade, fade, h / 2f);
        }

        string name = t.Name;
        if (t.State == WorldEspState.Undiscovered && t.Cat != WorldEspCat.Animal) name += " ?";
        float lineH = _nameStyle.fontSize + 4f;
        var nameRect = new Rect(x - 200f, y - dot / 2f - 4f - lineH - (HasInfoLine() ? _infoStyle.fontSize + 3f : 0f), 400f, lineH);
        Shadowed(nameRect, name, _nameStyle, new Color(col.r, col.g, col.b, fade));

        if (HasInfoLine())
        {
            string info = CachedInfoLine(t);
            if (info.Length > 0)
            {
                var infoRect = new Rect(x - 200f, nameRect.yMax - 2f, 400f, _infoStyle.fontSize + 4f);
                Shadowed(infoRect, info, _infoStyle, new Color(0.86f, 0.89f, 0.93f, 0.9f * fade));
            }
        }
    }

    private static bool HasInfoLine() => ShowDistance.Value || ShowDetails.Value;

    private static int _infoSettingsKey;

    /// <summary>The second label line only changes when the shown distance or the settings change.</summary>
    private static string CachedInfoLine(WorldEspTarget t)
    {
        int distKey = t.Dist >= 1000f ? 100000 + Mathf.RoundToInt(t.Dist / 100f) : Mathf.RoundToInt(t.Dist);
        int settingsKey = (ShowDistance.Value ? 1 : 0) | (ShowDetails.Value ? 2 : 0);
        if (t.InfoCache != null && t.InfoDistKey == distKey && _infoSettingsKey == settingsKey) return t.InfoCache;
        _infoSettingsKey = settingsKey;
        t.InfoDistKey = distKey;
        return t.InfoCache = InfoLine(t);
    }

    private static string InfoLine(WorldEspTarget t)
    {
        var parts = new List<string>(4);
        if (ShowDetails.Value)
        {
            parts.Add(CatSingular[(int)t.Cat]);
            if (!string.IsNullOrEmpty(t.Extra)) parts.Add(t.Extra);
            string st = StateText(t);
            if (st != null) parts.Add(st);
        }
        if (ShowDistance.Value) parts.Add(FormatDistance(t.Dist));
        return string.Join("  ·  ", parts);
    }

    private static string StateText(WorldEspTarget t)
    {
        if (t.Cat == WorldEspCat.Animal || t.Cat == WorldEspCat.Meteorite) return null; // already in Extra
        return t.State switch
        {
            WorldEspState.Undiscovered => "undiscovered",
            WorldEspState.Discovered => "discovered",
            WorldEspState.Reached => "visited",
            WorldEspState.Completed => "done",
            WorldEspState.Incoming => "incoming",
            WorldEspState.Active => "active",
            _ => null,
        };
    }

    internal static string FormatDistance(float d) => d >= 1000f ? $"{d / 1000f:0.0} km" : $"{d:0} m";

    private static void DrawEdge(WorldEspTarget t, Vector3 v, bool behind, float sw, float sh, float scale, Color col, float fade)
    {
        // Direction from screen centre in viewport space; flip when behind the camera.
        float dx = v.x - 0.5f, dy = v.y - 0.5f;
        if (behind) { dx = -dx; dy = -dy; }
        if (Mathf.Abs(dx) < 0.0001f && Mathf.Abs(dy) < 0.0001f) dy = -1f;
        float m = Mathf.Max(Mathf.Abs(dx) / 0.47f, Mathf.Abs(dy) / 0.45f);
        float ex = (0.5f + dx / m) * sw, ey = (1f - (0.5f + dy / m)) * sh;

        float dot = 10f * scale;
        Dot(new Rect(ex - dot / 2f - 1.5f, ey - dot / 2f - 1.5f, dot + 3f, dot + 3f), Shade, 0.8f * fade, 4f);
        Dot(new Rect(ex - dot / 2f, ey - dot / 2f, dot, dot), col, 0.85f * fade, 3f);
        var r = new Rect(ex - 100f, ey + dot / 2f + 1f, 200f, _infoStyle.fontSize + 4f);
        Shadowed(r, $"{t.Name}  {FormatDistance(t.Dist)}", _infoStyle, new Color(col.r, col.g, col.b, 0.85f * fade));
    }

    private static void Shadowed(Rect r, string text, GUIStyle style, Color color)
    {
        GUI.contentColor = new Color(0f, 0f, 0f, color.a * 0.85f);
        GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);
        GUI.contentColor = color;
        GUI.Label(r, text, style);
    }

    // ---------------------------------------------------------------- menu

    private static void DrawMainCard()
    {
        _menuWantedUntil = Time.unscaledTime + 1.5f;
        Menu.Card("World ESP", "Labels points of interest, meteorites, animals and NPCs through terrain. Local only: nobody else sees it.", () =>
        {
            Menu.Switch(Enabled, "World ESP", "Draw labels in the world" + (ToggleKey.Value != Key.None ? $" (toggle: {ToggleKey.Value})" : ""), true);
            Menu.SliderRow(MaxDistance, "Max distance", 100f, 6000f, "0", " m", true);
            Menu.SliderRow(AnimalDistance, "Animal distance", 25f, 2000f, "0", " m", true);
            Menu.SliderRow(MaxLabels, "Max labels", 10f, 400f, "0", "", true);
            Menu.SliderRow(TextScale, "Text size", 0.6f, 2f, "0.0", "x", true);
            Menu.Switch(ShowDistance, "Show distance", null, true);
            Menu.Switch(ShowDetails, "Show details", "Type, discovery state, meteorite size, animal rarity", true);
            Menu.Switch(ShowUndiscovered, "Show undiscovered", "Points not yet found are marked with ? and a hollow dot", true);
            Menu.Switch(HideCompleted, "Hide completed", "Done points, opened crates, extracted meteorites, photographed animals", true);
            Menu.Switch(EdgeMarkers, "Edge markers", "Pin off-screen targets to the screen edge", true);
            Menu.Switch(AnimalsOnlyNew, "Animals: only new species", "Skip animals already in your photo album", true);
            Menu.Switch(DeepScan, "Deep scan", "Include inactive and internal points (can be noisy)", true);
            Menu.Chips(2,
                ("Rescan now", () => { WorldEspScanner.ForceRefresh(); Menu.SetStatus("World ESP rescanned"); }, true),
                ("Copy nearest to log", LogNearest, true));
            Menu.Small($"Last scan {WorldEspScanner.LastScanMs:0.0} ms, refreshed every 2 s in four staggered steps.");
        });
    }

    private static void DrawCategoriesCard()
    {
        Menu.Card("World ESP categories", null, () =>
        {
            for (int i = 0; i < CatCount; i++)
            {
                var c = CatColors[i];
                string hex = ColorUtility.ToHtmlStringRGB(c);
                int count = WorldEspScanner.Count((WorldEspCat)i);
                string label = $"<color=#{hex}>●</color>  {CatNames[i]}";
                string hint = Categories[i].Value ? $"{count} found" : "off";
                Menu.Switch(Categories[i], label, hint, true);
            }
            Menu.Chips(3,
                ("All on", () => SetAll(true), true),
                ("All off", () => SetAll(false), true),
                ("Treasure hunt", TreasurePreset, true));
        });
    }

    private static void SetAll(bool on)
    {
        foreach (var c in Categories) c.Value = on;
        Menu.SetStatus(on ? "All world ESP categories on" : "All world ESP categories off");
    }

    /// <summary>Only the things worth driving to: undiscovered points, crates, meteorites, recoveries.</summary>
    private static void TreasurePreset()
    {
        for (int i = 0; i < CatCount; i++) Categories[i].Value = false;
        foreach (var c in new[] { WorldEspCat.Cabin, WorldEspCat.Merchant, WorldEspCat.Portal, WorldEspCat.Scenic, WorldEspCat.Trail,
                     WorldEspCat.Recovery, WorldEspCat.Crate, WorldEspCat.OtherPoi, WorldEspCat.Meteorite })
            Categories[(int)c].Value = true;
        HideCompleted.Value = true;
        ShowUndiscovered.Value = true;
        Enabled.Value = true;
        Menu.SetStatus("World ESP: treasure hunt preset");
    }

    private static readonly List<(WorldEspTarget t, float d)> Nearest = new();

    private static void CollectNearest(int max)
    {
        Nearest.Clear();
        var cam = FindCamera();
        Vector3 origin;
        Vector3 fwd = Vector3.forward;
        try
        {
            var car = GameState.LocalCar;
            if (car != null) { origin = car.transform.position; fwd = car.transform.forward; }
            else if (cam != null) { origin = cam.transform.position; fwd = cam.transform.forward; }
            else return;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEsp.CollectNearest"); return; }
        _nearestFwd = fwd;
        _nearestOrigin = origin;
        foreach (var t in WorldEspScanner.All)
        {
            if (!PassesFilters(t) || !UpdatePosition(t)) continue;
            Nearest.Add((t, Vector3.Distance(origin, t.Pos)));
        }
        Nearest.Sort((a, b) => a.d.CompareTo(b.d));
        if (Nearest.Count > max) Nearest.RemoveRange(max, Nearest.Count - max);
    }

    private static Vector3 _nearestFwd, _nearestOrigin;
    private static float _nextNearest;

    private static string Bearing(WorldEspTarget t)
    {
        var dir = t.Pos - _nearestOrigin;
        dir.y = 0f;
        var f = _nearestFwd;
        f.y = 0f;
        if (dir.sqrMagnitude < 1f || f.sqrMagnitude < 0.0001f) return "here";
        float a = Vector3.SignedAngle(f, dir, Vector3.up);
        float abs = Mathf.Abs(a);
        if (abs < 12f) return "ahead";
        if (abs > 160f) return "behind";
        return $"{abs:0}° {(a > 0 ? "right" : "left")}";
    }

    private static void DrawNearestCard()
    {
        Menu.Card("Nearest world targets", "Closest enabled targets to your car (or the camera), with bearing relative to where you face.", () =>
        {
            if (Time.unscaledTime >= _nextNearest || Event.current.type == EventType.Layout && Nearest.Count == 0)
            {
                _nextNearest = Time.unscaledTime + 0.25f;
                CollectNearest(10);
            }
            if (Nearest.Count == 0)
            {
                Menu.Small(GameState.Runner == null && GameState.LocalCar == null
                    ? "Load into the world to see targets."
                    : "Nothing found for the enabled categories.");
                return;
            }
            foreach (var (t, d) in Nearest)
            {
                string hex = ColorUtility.ToHtmlStringRGB(ColorOf(t));
                string undisc = t.State == WorldEspState.Undiscovered && t.Cat != WorldEspCat.Animal ? " <color=#8C96A6>(undiscovered)</color>" : "";
                Menu.KeyValue($"{FormatDistance(d)}  ·  {Bearing(t)}", $"<color=#{hex}>{CatSingular[(int)t.Cat]}</color>  {Escape(t.Name)}{undisc}");
            }
        });
    }

    private static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("<", "‹").Replace(">", "›");

    private static void LogNearest()
    {
        CollectNearest(50);
        if (Nearest.Count == 0) { Menu.SetStatus("Failed: no world targets found"); return; }
        CheatsPlugin.Logger.LogInfo($"World ESP: {Nearest.Count} nearest targets");
        foreach (var (t, d) in Nearest)
            CheatsPlugin.Logger.LogInfo("  " + CatSingular[(int)t.Cat].PadRight(13) + (t.Name ?? "").PadRight(33) + FormatDistance(d).PadLeft(9) + "  " + t.State.ToString().PadRight(13) + (t.Extra ?? "") + $"  @ {t.Pos.x:0},{t.Pos.y:0},{t.Pos.z:0}");
        Menu.SetStatus($"Logged {Nearest.Count} targets to the BepInEx log");
    }
}
