using System;
using BepInEx.Configuration;
using OverTheHillPlus.CheatsPlus;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Target = OverTheHillPlus.Features.Cheats.PlayerEsp.PlayerEspTracker.Target;

namespace OverTheHillPlus.Features.Cheats.PlayerEsp;

/// <summary>
/// Player ESP: an IMGUI overlay that shows every other player's car through terrain and trees, with name, distance,
/// health, speed, a box or marker, optional tracers, off-screen arrows, a radar and a player list.
/// Purely local and read-only (nothing is sent to the game or other players), so it works in any session.
/// </summary>
internal sealed class PlayerEspFeature : IFeature
{
    private const string Sec = "ESP Players";
    private const string Tab = "ESP";

    private static ConfigEntry<bool> Enabled, IncludeSelf, Names, Distance, Health, Speed, HealthBar, Marker,
        Tracers, Arrows, PlayerColours, ColourByHealth, Fade, Radar, PlayerList;
    private static ConfigEntry<Key> ToggleKey;
    private static ConfigEntry<float> MaxDistance, TextScale, LineWidth, RadarRange, RadarSize;
    private static ConfigEntry<int> BoxStyle, TracerOrigin, RadarCorner;
    private static ConfigEntry<string> CustomColour;

    private static bool _wasEnabled;
    private static bool _loggedKey;

    private static readonly string[] BoxNames = { "None", "2D box", "Corners", "3D box" };
    private static readonly string[] OriginNames = { "Bottom", "Centre", "Top", "My car" };
    private static readonly string[] CornerNames = { "Top left", "Top right", "Bottom left", "Bottom right" };

    private static readonly (string hex, string name)[] Palette =
    {
        ("#4FA8F7", "blue"), ("#4CC38A", "green"), ("#F2B33D", "yellow"), ("#E5534B", "red"),
        ("#B873F2", "purple"), ("#40D9D9", "cyan"), ("#F280BF", "pink"), ("#FFFFFF", "white"),
    };

    public void Register()
    {
        var f = CheatsConfig.File;
        Enabled = f.Bind(Sec, "Enabled", false, "Show other players' cars through walls (local overlay only).");
        ToggleKey = f.Bind(Sec, "ToggleKey", Key.F10, "Key that turns the player ESP on/off (None = no hotkey).");
        IncludeSelf = f.Bind(Sec, "IncludeMyCar", false, "Also draw your own car (to preview the ESP in solo).");
        MaxDistance = f.Bind(Sec, "MaxDistance", 1500f, "Players further than this (metres) are not drawn.");
        Names = f.Bind(Sec, "Names", true, "Player names.");
        Distance = f.Bind(Sec, "Distance", true, "Distance in metres.");
        Health = f.Bind(Sec, "Health", true, "Car health percentage.");
        Speed = f.Bind(Sec, "Speed", true, "Car speed in km/h.");
        BoxStyle = f.Bind(Sec, "BoxStyle", 1, "0 none, 1 2D box, 2 corners, 3 3D box.");
        HealthBar = f.Bind(Sec, "HealthBar", true, "Health bar beside the box.");
        Marker = f.Bind(Sec, "Marker", false, "Diamond marker above the car.");
        Tracers = f.Bind(Sec, "Tracers", false, "Line from the screen edge to each car.");
        TracerOrigin = f.Bind(Sec, "TracerOrigin", 0, "0 bottom, 1 centre, 2 top of the screen, 3 your car.");
        Arrows = f.Bind(Sec, "OffscreenArrows", true, "Arrows at the screen edge for players behind you or off-screen.");
        PlayerColours = f.Bind(Sec, "PlayerColours", true, "Use each player's game colour.");
        CustomColour = f.Bind(Sec, "CustomColour", "#4FA8F7", "Colour used when player colours are off (#RRGGBB).");
        ColourByHealth = f.Bind(Sec, "ColourByHealth", false, "Box colour goes green to red as the car takes damage.");
        Fade = f.Bind(Sec, "FadeWithDistance", true, "Far players are drawn fainter.");
        TextScale = f.Bind(Sec, "TextScale", 1f, "Label size.");
        LineWidth = f.Bind(Sec, "LineWidth", 1.5f, "Box and line thickness in pixels.");
        Radar = f.Bind(Sec, "Radar", false, "Mini radar of players around you, turned with the camera.");
        RadarRange = f.Bind(Sec, "RadarRange", 400f, "Radar radius in metres.");
        RadarSize = f.Bind(Sec, "RadarSize", 180f, "Radar diameter in pixels (at 1080p).");
        RadarCorner = f.Bind(Sec, "RadarCorner", 0, "0 top left, 1 top right, 2 bottom left, 3 bottom right.");
        PlayerList = f.Bind(Sec, "PlayerList", false, "List of players with distance, health and speed next to the radar.");

        Registry.OnTick(Tick);
        Registry.OnGui(Gui);
        Registry.Section(Registry.PlusMode, Tab, DrawMenu, 0);
    }

    // ---------------------------------------------------------------- tick

    private static void Tick()
    {
        HandleHotkey();
        bool on = Enabled.Value;
        if (!on)
        {
            if (_wasEnabled) PlayerEspTracker.Clear();
            _wasEnabled = false;
            return;
        }
        _wasEnabled = true;
        PlayerEspTracker.Tick();
    }

    private static void HandleHotkey()
    {
        var key = ToggleKey.Value;
        if (key == Key.None) return;
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (GUIUtility.keyboardControl != 0) return; // typing in a menu text field
            if (!kb[key].wasPressedThisFrame) return;
            Enabled.Value = !Enabled.Value;
            Menu.SetStatus(Enabled.Value ? "Player ESP on" : "Player ESP off");
        }
        catch (Exception e)
        {
            DebugLog.Error("cheats", e, "PlayerEspFeature.HandleHotkey");
            if (!_loggedKey) { _loggedKey = true; CheatsPlugin.Logger.LogWarning($"PlayerEsp hotkey {key}: {e.Message}"); }
        }
    }

    // ---------------------------------------------------------------- overlay

    private static bool Visible(Target t) =>
        t.HasPosition && (IncludeSelf.Value || !t.IsMe) && t.Distance <= MaxDistance.Value;

    private static void Gui()
    {
        if (!Enabled.Value || Event.current.type != EventType.Repaint) return;
        var targets = PlayerEspTracker.Targets;
        if (targets.Count == 0) return;

        var savedMatrix = GUI.matrix;
        var savedColor = GUI.color;
        try
        {
            GUI.matrix = Matrix4x4.identity;
            GUI.color = Color.white;
            var cam = PlayerEspTracker.Camera;
            if (cam != null)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var t = targets[i];
                    if (!Visible(t)) continue;
                    DrawTarget(cam, t);
                }
            }

            float ui = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1f));
            DrawPanels(cam, ui);
        }
        finally
        {
            GUI.matrix = savedMatrix;
            GUI.color = savedColor;
        }
    }

    private static Color BaseColour(Target t)
    {
        if (PlayerColours.Value) return t.Color;
        return ParseHex(CustomColour.Value, Theme.Info);
    }

    private static float Alpha(Target t)
    {
        if (!Fade.Value) return 1f;
        float max = Mathf.Max(1f, MaxDistance.Value);
        return Mathf.Lerp(1f, 0.4f, Mathf.Clamp01(t.Distance / max));
    }

    private static Vector2 ToScreen(Vector3 vp) => new(vp.x * Screen.width, (1f - vp.y) * Screen.height);

    private static readonly Vector3[] Corner = new Vector3[8];
    private static readonly Vector2[] CornerScreen = new Vector2[8];

    private static void DrawTarget(Camera cam, Target t)
    {
        float a = Alpha(t);
        var col = BaseColour(t);
        col.a = a;
        var boxCol = ColourByHealth.Value && t.Health >= 0f ? OverlayDraw.HealthColor(t.Health) : col;
        boxCol.a = a;
        float w = Mathf.Clamp(LineWidth.Value, 0.5f, 6f);
        float ui = Mathf.Max(1f, Screen.height / 1080f);
        int font = Mathf.Clamp(Mathf.RoundToInt(13f * TextScale.Value * ui), 8, 48);

        var centre = t.Position + t.Rotation * t.Center;
        var vpCentre = cam.WorldToViewportPoint(centre);
        if (vpCentre.z <= 0.05f)
        {
            if (Arrows.Value) OffscreenArrow(t, ToScreen(vpCentre), behind: true, col, w, font);
            return;
        }

        // Project the car's box.
        bool allInFront = true;
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        for (int k = 0; k < 8; k++)
        {
            var local = new Vector3(
                t.Center.x + ((k & 1) == 0 ? -t.Extents.x : t.Extents.x),
                t.Center.y + ((k & 2) == 0 ? -t.Extents.y : t.Extents.y),
                t.Center.z + ((k & 4) == 0 ? -t.Extents.z : t.Extents.z));
            Corner[k] = t.Position + t.Rotation * local;
            var vp = cam.WorldToViewportPoint(Corner[k]);
            if (vp.z <= 0.05f) { allInFront = false; break; }
            var s = ToScreen(vp);
            CornerScreen[k] = s;
            min = Vector2.Min(min, s);
            max = Vector2.Max(max, s);
        }

        var centreScreen = ToScreen(vpCentre);
        Rect box;
        if (allInFront)
        {
            box = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            // Keep tiny far-away boxes visible.
            if (box.width < 8f) box = new Rect(centreScreen.x - 4f, box.y, 8f, box.height);
            if (box.height < 6f) box = new Rect(box.x, centreScreen.y - 3f, box.width, 6f);
        }
        else box = new Rect(centreScreen.x - 20f, centreScreen.y - 14f, 40f, 28f);

        bool onScreen = box.xMax > 0f && box.x < Screen.width && box.yMax > 0f && box.y < Screen.height;
        if (!onScreen)
        {
            if (Arrows.Value) OffscreenArrow(t, centreScreen, behind: false, col, w, font);
            return;
        }

        // Tracer first so the box draws on top of it.
        if (Tracers.Value)
        {
            var from = TracerFrom(cam);
            if (from.HasValue)
                OverlayDraw.LineShadowed(from.Value, new Vector2(box.center.x, box.yMax), new Color(col.r, col.g, col.b, a * 0.8f), Mathf.Max(1f, w * 0.75f));
        }

        int style = allInFront ? BoxStyle.Value : 0;
        switch (style)
        {
            case 1: OverlayDraw.OutlineShadowed(box, boxCol, w); break;
            case 2: OverlayDraw.Corners(box, boxCol, w); break;
            case 3: Draw3DBox(boxCol, w); break;
        }

        bool hasBox = style != 0;
        if (HealthBar.Value && hasBox && t.Health >= 0f)
        {
            var bar = new Rect(box.x - 6f - w, box.y, 3f + w * 0.5f, box.height);
            OverlayDraw.Rect(new Rect(bar.x - 1f, bar.y - 1f, bar.width + 2f, bar.height + 2f), new Color(0f, 0f, 0f, 0.6f * a));
            float h = bar.height * t.Health;
            var hc = OverlayDraw.HealthColor(t.Health);
            hc.a = a;
            OverlayDraw.Rect(new Rect(bar.x, bar.yMax - h, bar.width, h), hc);
        }

        float top = hasBox ? box.y : centreScreen.y - 6f;
        if (Marker.Value || !hasBox)
        {
            float ms = Mathf.Clamp(6f * ui, 5f, 14f);
            OverlayDraw.Diamond(new Vector2(box.center.x, top - ms - 4f), ms, col, Mathf.Max(1f, w * 0.8f));
            top -= ms * 2f + 6f;
        }

        if (Names.Value)
        {
            var sz = OverlayDraw.Measure(t.Name, font);
            OverlayDraw.Label(box.center.x, top - sz.y - 2f, t.Name, font, new Color(col.r, col.g, col.b, a));
        }

        string info = InfoLine(t);
        if (info.Length > 0)
        {
            float y = hasBox ? box.yMax + 3f : centreScreen.y + 4f;
            OverlayDraw.Label(box.center.x, y, info, Mathf.Max(8, font - 2), new Color(0.91f, 0.93f, 0.95f, a));
        }
    }

    private static string InfoLine(Target t)
    {
        string s = "";
        if (Distance.Value) s = $"{t.Distance:0} m";
        if (Health.Value && t.Health >= 0f) s += (s.Length > 0 ? "  ·  " : "") + $"{t.Health * 100f:0}%";
        if (Speed.Value) s += (s.Length > 0 ? "  ·  " : "") + $"{t.SpeedKmh:0} km/h";
        return s;
    }

    private static void Draw3DBox(Color c, float w)
    {
        // Corner index bits: 1 = +x, 2 = +y, 4 = +z. Edges connect corners differing in one bit.
        for (int k = 0; k < 8; k++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                int j = k | bit;
                if (j == k) continue;
                OverlayDraw.LineShadowed(CornerScreen[k], CornerScreen[j], c, w);
            }
        }
    }

    private static Vector2? TracerFrom(Camera cam)
    {
        switch (TracerOrigin.Value)
        {
            case 1: return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            case 2: return new Vector2(Screen.width * 0.5f, 0f);
            case 3:
                try
                {
                    var me = GameState.LocalCar;
                    if (me == null) break;
                    var vp = cam.WorldToViewportPoint(me.transform.position);
                    if (vp.z <= 0.05f) break;
                    return ToScreen(vp);
                }
                catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspFeature.TracerFrom"); }
                break;
        }
        return new Vector2(Screen.width * 0.5f, Screen.height);
    }

    private static void OffscreenArrow(Target t, Vector2 projected, bool behind, Color col, float w, int font)
    {
        var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var dir = projected - centre;
        if (behind) dir = -dir;
        if (dir.sqrMagnitude < 1f) dir = new Vector2(0f, 1f); // straight behind: point down
        dir.Normalize();

        // Place on an ellipse inset from the screen edges.
        float ui = Mathf.Max(1f, Screen.height / 1080f);
        float rx = Screen.width * 0.5f - 70f * ui, ry = Screen.height * 0.5f - 70f * ui;
        float scale = 1f / Mathf.Sqrt(dir.x * dir.x / (rx * rx) + dir.y * dir.y / (ry * ry));
        var tip = centre + dir * scale;

        float size = Mathf.Clamp(14f * ui, 10f, 30f);
        OverlayDraw.Arrow(tip, dir, size, col, Mathf.Max(1f, w));

        var textPos = tip - dir * (size + 14f * ui);
        string label = Names.Value ? t.Name : "";
        if (Distance.Value) label += (label.Length > 0 ? " " : "") + $"{t.Distance:0} m";
        if (label.Length > 0)
            OverlayDraw.Label(textPos.x, textPos.y - font * 0.6f, label, Mathf.Max(8, font - 2), new Color(col.r, col.g, col.b, col.a));
    }

    // ---------------------------------------------------------------- radar and list (scaled UI space)

    private static void DrawPanels(Camera cam, float ui)
    {
        if (!Radar.Value && !PlayerList.Value) return;
        float sw = Screen.width / ui, sh = Screen.height / ui;
        int corner = Mathf.Clamp(RadarCorner.Value, 0, 3);
        bool right = corner == 1 || corner == 3, bottom = corner >= 2;
        const float margin = 24f;
        // Leave room for the driving HUD in the top-right corner.
        float topOffset = right && !bottom && DrivingHud.Shown ? 190f : margin;

        float size = Mathf.Round(Mathf.Clamp(RadarSize.Value, 100f, 360f) / 20f) * 20f;
        float x = right ? sw - margin - size : margin;
        float y = bottom ? sh - margin - size : topOffset;
        if (Radar.Value)
        {
            DrawRadar(cam, new Rect(x, y, size, size));
        }

        if (PlayerList.Value)
        {
            int rows = 0;
            foreach (var t in PlayerEspTracker.Targets) if (t.HasPosition && (IncludeSelf.Value || !t.IsMe)) rows++;
            const float lw = 270f, rowH = 20f;
            float lh = 28f + Mathf.Max(1, rows) * rowH + 6f;
            float lx = right ? sw - margin - lw : margin;
            float ly = Radar.Value
                ? (bottom ? y - 10f - lh : y + size + 10f)
                : (bottom ? sh - margin - lh : topOffset);
            DrawList(new Rect(lx, ly, lw, lh), rowH);
        }
    }

    private static void DrawRadar(Camera cam, Rect r)
    {
        float radius = r.width * 0.5f;
        var c = r.center;
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.72f), Mathf.RoundToInt(radius));
        // Range rings and cross.
        var faint = new Color(1f, 1f, 1f, 0.10f);
        OverlayDraw.Rect(new Rect(c.x - radius + 6f, c.y - 0.5f, r.width - 12f, 1f), faint);
        OverlayDraw.Rect(new Rect(c.x - 0.5f, c.y - radius + 6f, 1f, r.height - 12f), faint);
        Ring(c, radius * 0.5f, faint);
        Ring(c, radius - 2f, new Color(1f, 1f, 1f, 0.18f));

        // Heading from the camera (or the car when there is no camera).
        Vector3 fwd = Vector3.forward;
        try
        {
            if (cam != null) fwd = cam.transform.forward;
            else if (GameState.LocalCar != null) fwd = GameState.LocalCar.transform.forward;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspFeature.DrawRadar"); }
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        fwd.Normalize();
        var rightV = new Vector3(fwd.z, 0f, -fwd.x);

        // You: a small arrow pointing up.
        OverlayDraw.Arrow(new Vector2(c.x, c.y - 7f), new Vector2(0f, -1f), 10f, Theme.Text, 1.5f);

        GUI.Label(new Rect(c.x - 40f, r.yMax - 18f, 80f, 14f), $"{RadarRange.Value:0} m", CenteredSmall());

        if (!PlayerEspTracker.HasOrigin) return;
        var origin = PlayerEspTracker.Origin;
        float range = Mathf.Max(10f, RadarRange.Value);
        float inner = radius - 7f;
        foreach (var t in PlayerEspTracker.Targets)
        {
            if (!t.HasPosition || (t.IsMe && !IncludeSelf.Value)) continue;
            if (t.IsMe) continue; // you are the centre arrow
            var d = t.Position - origin;
            var p = new Vector2(Vector3.Dot(d, rightV), -Vector3.Dot(d, fwd)) / range * inner;
            bool outside = p.magnitude > inner;
            if (outside) p = p.normalized * inner;
            var col = BaseColour(t);
            float dot = outside ? 6f : 9f;
            if (outside) col.a = 0.6f;
            var dr = new Rect(c.x + p.x - dot * 0.5f, c.y + p.y - dot * 0.5f, dot, dot);
            Theme.Fill(new Rect(dr.x - 1.5f, dr.y - 1.5f, dr.width + 3f, dr.height + 3f), new Color(0f, 0f, 0f, 0.6f), Mathf.RoundToInt(dot * 0.5f + 1.5f));
            Theme.Fill(dr, col, Mathf.RoundToInt(dot * 0.5f));
        }
    }

    private static void Ring(Vector2 c, float radius, Color col)
    {
        const int seg = 32;
        var prev = c + new Vector2(radius, 0f);
        for (int i = 1; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            var p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            OverlayDraw.Line(prev, p, col, 1f);
            prev = p;
        }
    }

    private static GUIStyle _centeredSmall;

    private static GUIStyle CenteredSmall()
    {
        if (_centeredSmall == null)
        {
            Theme.Ensure();
            _centeredSmall = new GUIStyle(Theme.Small);
            _centeredSmall.alignment = TextAnchor.MiddleCenter;
            _centeredSmall.richText = false;
        }
        return _centeredSmall;
    }

    private static readonly System.Collections.Generic.List<Target> Sorted = new();

    private static void DrawList(Rect r, float rowH)
    {
        Theme.Ensure();
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.78f), 8);
        GUI.Label(new Rect(r.x + 10f, r.y + 6f, r.width - 20f, 16f), "PLAYERS", Theme.Section);

        Sorted.Clear();
        foreach (var t in PlayerEspTracker.Targets)
            if (t.HasPosition && (IncludeSelf.Value || !t.IsMe)) Sorted.Add(t);
        Sorted.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        float y = r.y + 26f;
        if (Sorted.Count == 0)
        {
            OverlayDraw.LabelLeft(new Rect(r.x + 10f, y, r.width - 20f, rowH), "No other players", 12, Theme.Muted);
            return;
        }
        foreach (var t in Sorted)
        {
            var col = BaseColour(t);
            Theme.Fill(new Rect(r.x + 10f, y + 5f, 4f, rowH - 10f), col, 2);
            bool far = t.Distance > MaxDistance.Value;
            OverlayDraw.LabelLeft(new Rect(r.x + 20f, y, 110f, rowH), t.Name, 12, far ? Theme.Muted : Theme.Text);
            OverlayDraw.LabelLeft(new Rect(r.x + 132f, y, 56f, rowH), $"{t.Distance:0} m", 11, Theme.Muted);
            string hp = t.Health >= 0f ? $"{t.Health * 100f:0}%" : "-";
            OverlayDraw.LabelLeft(new Rect(r.x + 188f, y, 36f, rowH), hp, 11, OverlayDraw.HealthColor(t.Health));
            OverlayDraw.LabelLeft(new Rect(r.x + 226f, y, 44f, rowH), $"{t.SpeedKmh:0}", 11, Theme.Muted);
            y += rowH;
        }
    }

    // ---------------------------------------------------------------- menu

    private static void DrawMenu()
    {
        Menu.Card("Player ESP", "See every other player's car through terrain and trees. Local overlay only: nothing is sent to the game or other players, so it works in any lobby.", () =>
        {
            Menu.Switch(Enabled, ToggleKey.Value == Key.None ? "Player ESP" : $"Player ESP ({ToggleKey.Value})", "Boxes, names and info on other players' cars", true);
            Menu.Switch(IncludeSelf, "Include my car", "Preview the overlay on your own car (useful in solo)", true);
            Menu.SliderRow(MaxDistance, "Max distance", 50f, 5000f, "0", " m", true);

            int total = 0, inRange = 0;
            foreach (var t in PlayerEspTracker.Targets)
            {
                if (!t.HasPosition || (t.IsMe && !IncludeSelf.Value)) continue;
                total++;
                if (t.Distance <= MaxDistance.Value) inRange++;
            }
            Menu.KeyValue("Tracking", Enabled.Value ? $"{total} player car(s), {inRange} within range" : "off");
            if (Enabled.Value && total == 0)
                Menu.Small("No other player cars found yet. In solo, turn on \"Include my car\" to preview.");
        });

        Menu.Card("Labels", null, () =>
        {
            Menu.Switch(Names, "Names", null, true);
            Menu.Switch(Distance, "Distance", null, true);
            Menu.Switch(Health, "Health", "Car health from its damage system", true);
            Menu.Switch(Speed, "Speed", "Estimated from movement, km/h", true);
            Menu.SliderRow(TextScale, "Text size", 0.6f, 2f, "0.0", "x", true);
            Menu.Switch(Fade, "Fade with distance", "Far players are drawn fainter", true);
        });

        Menu.Card("Shapes", null, () =>
        {
            Menu.Small("Box");
            Choice(4, BoxStyle, BoxNames);
            Menu.Switch(HealthBar, "Health bar", "Vertical bar beside the box", true);
            Menu.Switch(Marker, "Marker", "Diamond above the car (always shown when the box is off)", true);
            Menu.Switch(Arrows, "Off-screen arrows", "Point to players behind you or outside the view", true);
            Menu.Switch(Tracers, "Tracers", "Line to each car", true);
            if (Tracers.Value)
            {
                Menu.Small("Tracer start");
                Choice(4, TracerOrigin, OriginNames);
            }
            Menu.SliderRow(LineWidth, "Line width", 1f, 4f, "0.0", " px", true);
        });

        Menu.Card("Colours", null, () =>
        {
            Menu.Switch(PlayerColours, "Player colours", "Use each player's colour from the game", true);
            Menu.Switch(ColourByHealth, "Colour box by health", "Green when healthy, red when wrecked", true);
            Menu.Small(PlayerColours.Value ? "Custom colour (used when player colours are off)" : "Custom colour");
            Swatches();
        });

        Menu.Card("Radar and list", null, () =>
        {
            Menu.Switch(Radar, "Radar", "Players around you, turned with the camera", true);
            if (Radar.Value)
            {
                Menu.SliderRow(RadarRange, "Radar range", 50f, 3000f, "0", " m", true);
                Menu.SliderRow(RadarSize, "Radar size", 120f, 340f, "0", " px", true);
            }
            Menu.Switch(PlayerList, "Player list", "Name, distance, health and speed for every player", true);
            if (Radar.Value || PlayerList.Value)
            {
                Menu.Small("Position");
                Choice(4, RadarCorner, CornerNames);
            }
        });
    }

    private static void Choice(int columns, ConfigEntry<int> entry, string[] names)
    {
        var items = new (string label, Action action, bool enabled)[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int idx = i;
            string label = entry.Value == i ? $"<b><color=#F2B33D>{names[i]}</color></b>" : names[i];
            items[i] = (label, () => entry.Value = idx, true);
        }
        Menu.Chips(columns, items);
    }

    private static void Swatches()
    {
        var row = Menu.NextRow(30f);
        const float gap = 8f;
        float sw = Mathf.Min(44f, (row.width - gap * (Palette.Length - 1)) / Palette.Length);
        var current = ParseHex(CustomColour.Value, Theme.Info);
        for (int i = 0; i < Palette.Length; i++)
        {
            var r = new Rect(row.x + i * (sw + gap), row.y, sw, 28f);
            var c = ParseHex(Palette[i].hex, Color.white);
            bool selected = Approximately(c, current);
            if (selected) Theme.Fill(new Rect(r.x - 3f, r.y - 3f, r.width + 6f, r.height + 6f), Theme.Text, 8);
            Theme.Fill(r, c, 6);
            if (GUI.Button(r, GUIContent.none, Theme.Invisible)) CustomColour.Value = Palette[i].hex;
        }
    }

    private static bool Approximately(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

    private static string _parsedHex;
    private static Color _parsedColour;

    private static Color ParseHex(string hex, Color fallback)
    {
        if (hex == _parsedHex) return _parsedColour;
        Color result = fallback;
        try
        {
            string h = (hex ?? "").Trim().TrimStart('#');
            if (h.Length == 6 && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
                result = Theme.Hex(rgb);
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "PlayerEspFeature.ParseHex"); }
        // Only cache the custom colour (palette lookups happen in the menu only).
        if (hex == CustomColour?.Value) { _parsedHex = hex; _parsedColour = result; }
        return result;
    }
}
