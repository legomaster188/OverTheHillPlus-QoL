using System;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Shared;

/// <summary>
/// Drawing on top of the 3D view from OnGUI (shared by the pin and race mods): a marker with a label at a world spot
/// (an arrow at the screen edge while it is off screen), and a dotted route along the ground toward a spot. Call from
/// a Registry.OnGui hook; everything draws only in the Repaint event.
/// </summary>
internal static class WorldOverlay
{
    private static Camera _cam;
    private static float _nextCam;
    private static GUIStyle _label, _big, _bigShadow;

    /// <summary>The mods' pin colours (index = <see cref="BoardPin.Color"/>).</summary>
    public static readonly Color[] PinColors =
    {
        Theme.Hex(0xF2B33D), Theme.Hex(0x4FA8F7), Theme.Hex(0x4CC38A), Theme.Hex(0xE5534B),
        Theme.Hex(0xB18CFF), Theme.Hex(0x4FE0D0), Theme.Hex(0xFF8AC8), Theme.Hex(0xE8ECF2),
    };

    public static readonly string[] PinColorNames = { "Yellow", "Blue", "Green", "Red", "Purple", "Teal", "Pink", "White" };

    public static Color PinColor(int i) => PinColors[((i % PinColors.Length) + PinColors.Length) % PinColors.Length];

    public static Camera Cam
    {
        get
        {
            if (_cam == null || Time.unscaledTime >= _nextCam)
            {
                _nextCam = Time.unscaledTime + 2f;
                try { _cam = Camera.main; } catch (Exception ex) { DebugLog.Error("core", ex, "WorldOverlay.Cam"); _cam = null; }
            }
            return _cam;
        }
    }

    /// <summary>True when this OnGUI call should draw (Repaint, a camera, styles ready).</summary>
    public static bool Begin()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return false;
        if (Cam == null) return false;
        Theme.Ensure();
        if (_label == null)
        {
            _label = new GUIStyle(Theme.Small) { alignment = TextAnchor.MiddleCenter, richText = false, fontSize = 12 };
            _label.normal.textColor = Theme.Text;
            _big = new GUIStyle(Theme.Title) { alignment = TextAnchor.MiddleCenter, richText = false, fontSize = 44 };
            _big.normal.textColor = Theme.Text;
            _bigShadow = new GUIStyle(_big);
            _bigShadow.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
        }
        GUI.matrix = Matrix4x4.identity;
        return true;
    }

    /// <summary>GUI coordinates (y down) of a world spot; false when it is behind the camera.</summary>
    public static bool ToScreen(Vector3 world, out Vector2 gui, out float depth)
    {
        gui = default;
        depth = 0f;
        var cam = Cam;
        if (cam == null) return false;
        Vector3 sp;
        try { sp = cam.WorldToScreenPoint(world); } catch (Exception ex) { DebugLog.Error("core", ex, "WorldOverlay.ToScreen"); return false; }
        depth = sp.z;
        gui = new Vector2(sp.x, Screen.height - sp.y);
        return sp.z > 0.1f;
    }

    /// <summary>
    /// A dot and a label at <paramref name="world"/>; when it is off screen (or behind you), an arrow-like dot at the
    /// screen edge pointing at it, with the same label.
    /// </summary>
    public static void Marker(Vector3 world, string label, Color color, bool edgeWhenOffscreen, float size = 12f)
    {
        bool front = ToScreen(world, out var p, out _);
        float w = Screen.width, h = Screen.height;
        bool onScreen = front && p.x >= 0f && p.x <= w && p.y >= 0f && p.y <= h;
        if (!onScreen)
        {
            if (!edgeWhenOffscreen) return;
            // Direction from the screen centre; flipped when behind so it points the right way.
            var c = new Vector2(w / 2f, h / 2f);
            var d = p - c;
            if (!front) d = -d;
            if (d.sqrMagnitude < 1f) d = new Vector2(0f, h);
            float margin = 34f;
            float sx = (w / 2f - margin) / Mathf.Max(0.001f, Mathf.Abs(d.x));
            float sy = (h / 2f - margin) / Mathf.Max(0.001f, Mathf.Abs(d.y));
            p = c + d * Mathf.Min(sx, sy);
            size = 16f;
        }
        Theme.Fill(new Rect(p.x - size / 2f - 2f, p.y - size / 2f - 2f, size + 4f, size + 4f), new Color(0f, 0f, 0f, 0.6f), (int)(size / 2f + 2f));
        Theme.Fill(new Rect(p.x - size / 2f, p.y - size / 2f, size, size), color, (int)(size / 2f));
        if (string.IsNullOrEmpty(label)) return;
        var content = new GUIContent(label);
        var ls = _label.CalcSize(content);
        float ly = onScreen ? p.y - size / 2f - 24f : p.y + (p.y > h / 2f ? -size - 22f : size + 4f);
        var lr = new Rect(Mathf.Clamp(p.x - ls.x / 2f - 6f, 4f, w - ls.x - 16f), ly, ls.x + 12f, 20f);
        Theme.Fill(lr, new Color(0.07f, 0.09f, 0.11f, 0.78f), 6);
        Theme.Fill(new Rect(lr.x, lr.y, 3f, lr.height), color, 2);
        GUI.Label(lr, content, _label);
    }

    /// <summary>
    /// A dotted line on the ground from <paramref name="from"/> toward <paramref name="to"/> (the straight line, laid
    /// on the terrain), for at most <paramref name="maxLength"/> metres; dots shrink and fade with distance.
    /// </summary>
    public static void GroundDots(Vector3 from, Vector3 to, Color color, float maxLength = 250f, float spacing = 7f)
    {
        if (!View(out var view)) return;
        var flat = new Vector3(to.x - from.x, 0f, to.z - from.z);
        float total = flat.magnitude;
        if (total < spacing) return;
        var dir = flat / total;
        float len = Mathf.Min(total, Mathf.Max(spacing * 2f, maxLength));
        int n = Mathf.Min(120, Mathf.FloorToInt(len / spacing));
        for (int i = 1; i <= n; i++)
        {
            float d = i * spacing;
            float x = from.x + dir.x * d, z = from.z + dir.z * d;
            if (view.Behind(x, z, 2f)) continue; // before any game call: dots behind the camera cost nothing
            float y = GameMap.TryTerrainHeight(x, z, out float ty) ? ty : Mathf.Lerp(from.y, to.y, d / total);
            var world = new Vector3(x, y + 0.6f, z);
            if (!ToScreen(world, out var p, out _)) continue;
            if (p.x < -10f || p.y < -10f || p.x > Screen.width + 10f || p.y > Screen.height + 10f) continue;
            float camDist = Vector3.Distance(view.Pos, world);
            float size = Mathf.Clamp(260f / Mathf.Max(1f, camDist), 2.5f, 11f);
            float fade = 1f - Mathf.Clamp01((d - len * 0.6f) / (len * 0.4f + 0.01f));
            Theme.Fill(new Rect(p.x - size / 2f, p.y - size / 2f, size, size), new Color(color.r, color.g, color.b, 0.85f * fade), (int)(size / 2f));
        }
    }

    /// <summary>Rings farther than this from the camera are not drawn (each dot samples the terrain).</summary>
    public const float RingRange = 600f;

    /// <summary>A ring of dots on the ground around <paramref name="center"/> (a checkpoint's size).</summary>
    public static void GroundRing(Vector3 center, float radius, Color color, int dots = 24)
    {
        if (radius <= 0f || !View(out var view)) return;
        float dx = center.x - view.Pos.x, dz = center.z - view.Pos.z;
        if (dx * dx + dz * dz > RingRange * RingRange) return;
        if (view.Behind(center.x, center.z, radius + 2f)) return;
        for (int i = 0; i < dots; i++)
        {
            float a = i * Mathf.PI * 2f / dots;
            float x = center.x + Mathf.Cos(a) * radius, z = center.z + Mathf.Sin(a) * radius;
            if (view.Behind(x, z, 2f)) continue;
            float y = GameMap.TryTerrainHeight(x, z, out float ty) && Mathf.Abs(ty - center.y) < radius + 10f ? ty : center.y;
            var world = new Vector3(x, y + 0.5f, z);
            if (!ToScreen(world, out var p, out _)) continue;
            if (p.x < -10f || p.y < -10f || p.x > Screen.width + 10f || p.y > Screen.height + 10f) continue;
            float size = Mathf.Clamp(300f / Mathf.Max(1f, Vector3.Distance(view.Pos, world)), 2.5f, 12f);
            Theme.Fill(new Rect(p.x - size / 2f, p.y - size / 2f, size, size), color, (int)(size / 2f));
        }
    }

    /// <summary>The camera's position and flat facing, read once per drawing call (plain math after that).</summary>
    private struct ViewInfo
    {
        public Vector3 Pos;
        public float Fx, Fz;
        public bool Flat; // facing has a usable horizontal part

        /// <summary>The ground point is behind the camera by more than <paramref name="margin"/> metres.</summary>
        public bool Behind(float x, float z, float margin) => Flat && (x - Pos.x) * Fx + (z - Pos.z) * Fz < -margin;
    }

    private static bool View(out ViewInfo v)
    {
        v = default;
        var cam = Cam;
        if (cam == null) return false;
        try
        {
            var t = cam.transform;
            v.Pos = t.position;
            var f = t.forward;
            float len = Mathf.Sqrt(f.x * f.x + f.z * f.z);
            // Pitched more than about 45 degrees: the view can reach behind the camera, so no culling.
            v.Flat = len > 0.7f;
            if (v.Flat) { v.Fx = f.x / len; v.Fz = f.z / len; }
            return true;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "WorldOverlay.View"); return false; }
    }

    /// <summary>Big centred text with a shadow (countdowns).</summary>
    public static void BigText(string text, float y, Color color)
    {
        if (_big == null || string.IsNullOrEmpty(text)) return;
        var r = new Rect(0f, y, Screen.width, 60f);
        GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, _bigShadow);
        var prev = _big.normal.textColor;
        _big.normal.textColor = color;
        GUI.Label(r, text, _big);
        _big.normal.textColor = prev;
    }

    /// <summary>A small panel with lines of plain text (HUD cards).</summary>
    public static void Panel(Rect r, Color accent, Action<Rect> body)
    {
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.86f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), accent, 2);
        body?.Invoke(new Rect(r.x + 12f, r.y + 6f, r.width - 18f, r.height - 12f));
    }
}
