using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Minimap;

/// <summary>Draws the corner minimap (north up, centred on your car) on Repaint only.</summary>
internal static class MinimapDraw
{
    private const float Inset = 8f;
    private static readonly Color FogTint = new(0.07f, 0.09f, 0.12f, 1f);
    private static readonly Color Outline = new(0f, 0f, 0f, 0.85f);
    private static readonly Color NameShadow = new(0f, 0f, 0f, 0.8f);
    private static Texture2D _arrow;
    private static Matrix4x4 _base;

    internal static void Draw()
    {
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint) return;
        if (!MinimapSource.HasOwn || CameraView.HudHidden) return;

        CrashTrail.Mark("minimap: draw panel");
        Theme.Ensure();
        var screen = HudLayout.Begin();
        _base = GUI.matrix;
        float size = Mathf.Clamp(MinimapConfig.Size.Value, 140f, 440f);
        var rect = HudLayout.Place(MinimapConfig.Corner.Value, screen, size, size);
        float alpha = HudLayout.Opacity(MinimapConfig.Opacity.Value, 0.3f);
        var panel = new MinimapMath.Box(rect.x, rect.y, rect.width, rect.height);
        float meters = MinimapMath.ClampMeters(MinimapConfig.Meters.Value);

        Theme.Fill(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), new Color(Theme.Line.r, Theme.Line.g, Theme.Line.b, alpha), 8);
        Theme.Fill(rect, new Color(FogTint.r, FogTint.g, FogTint.b, alpha), 6);

        var prevColor = GUI.color;
        try
        {
            // The map picture only together with its fog of war, which covers the same square, and the fog always at
            // full strength (the opacity setting only fades the picture): never more of the map than the game's shows.
            if (MinimapSource.HasMap && MinimapSource.HasFog && MinimapSource.FogCoversMap)
            {
                GUI.color = new Color(1f, 1f, 1f, alpha);
                CrashTrail.Mark("minimap: draw map texture");
                DrawLayer(MinimapSource.MapTexture, MinimapSource.MapOffset, MinimapSource.MapWidth, meters, panel);
                GUI.color = FogTint;
                CrashTrail.Mark("minimap: draw fog texture");
                DrawLayer(MinimapSource.FogTexture, MinimapSource.FogOffset, MinimapSource.FogWidth, meters, panel);
                GUI.color = prevColor;
            }
            else
            {
                GUI.color = prevColor;
                var style = Theme.Small;
                var align = style.alignment;
                style.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(rect.x, rect.yMax - 26f, rect.width, 18f), "map not loaded", style);
                style.alignment = align;
            }

            var mapOffset = MinimapSource.HasMap ? MinimapSource.MapOffset : Vector3.zero;
            float mapWidth = MinimapSource.HasMap ? MinimapSource.MapWidth : 4096f;
            var own = MinimapMath.WorldToUv(MinimapSource.OwnPos.x, MinimapSource.OwnPos.z, mapOffset.x, mapOffset.z, mapWidth);
            var view = MinimapMath.ViewAround(own.u, own.v, meters, mapWidth);

            (float x, float y) ToPanel(Vector3 w)
            {
                var uv = MinimapMath.WorldToUv(w.x, w.z, mapOffset.x, mapOffset.z, mapWidth);
                return MinimapMath.ToPanel(uv.u, uv.v, view, panel);
            }

            var me = ToPanel(MinimapSource.OwnPos);

            if (MinimapConfig.ShowRoute.Value && MinimapSource.RouteTo.HasValue)
            {
                var pin = ToPanel(MinimapSource.RouteTo.Value);
                var end = MinimapMath.ClampToEdge(pin.x, pin.y, panel, Inset);
                Route(me.x, me.y, end.x, end.y, alpha);
            }
            if (MinimapConfig.ShowPins.Value)
            {
                CrashTrail.Mark("minimap: draw shared pins");
                foreach (var p in MinimapSource.SharedPins)
                {
                    var at = ToPanel(p.Pos);
                    var c = MinimapMath.ClampToEdge(at.x, at.y, panel, Inset);
                    if (c.clamped) continue; // off the panel: only the game's own pins are kept at the edge
                    PinMark(new Vector2(c.x, c.y), p.Color, 5f);
                }
            }
            if (MinimapConfig.ShowPins.Value)
                foreach (var p in MinimapSource.Pins)
                {
                    var at = ToPanel(p.Pos);
                    var c = MinimapMath.ClampToEdge(at.x, at.y, panel, Inset);
                    PinMark(new Vector2(c.x, c.y), p.Color, c.clamped ? 5f : 7f);
                }
            foreach (var o in MinimapSource.Others)
            {
                var at = ToPanel(o.Pos);
                var c = MinimapMath.ClampToEdge(at.x, at.y, panel, Inset);
                if (c.clamped) Theme.Fill(new Rect(c.x - 4f, c.y - 4f, 8f, 8f), o.Color, 4);
                else
                {
                    Arrow(new Vector2(c.x, c.y), o.Heading, o.Color, 13f);
                    if (MinimapConfig.ShowNames.Value && !string.IsNullOrEmpty(o.Name)) Name(c.x + 9f, c.y - 9f, o.Name, rect.xMax);
                }
            }
            CrashTrail.Mark("minimap: draw own arrow");
            Arrow(new Vector2(me.x, me.y), MinimapSource.OwnHeading, Theme.Accent, 16f);

            var nStyle = Theme.Small;
            var nAlign = nStyle.alignment;
            nStyle.alignment = TextAnchor.UpperCenter;
            GUI.Label(new Rect(rect.center.x - 10f, rect.y + 2f, 20f, 16f), "N", nStyle);
            nStyle.alignment = nAlign;
        }
        finally { GUI.color = prevColor; }
    }

    private static void DrawLayer(Texture tex, Vector3 offset, float width, float meters, MinimapMath.Box panel)
    {
        if (tex == null) return;
        var own = MinimapMath.WorldToUv(MinimapSource.OwnPos.x, MinimapSource.OwnPos.z, offset.x, offset.z, width);
        var view = MinimapMath.ViewAround(own.u, own.v, meters, width);
        if (!MinimapMath.Visible(view, panel, out var uv, out var on)) return;
        if (uv.W <= 0f || uv.H <= 0f) return;
        // Not GUI.DrawTextureWithTexCoords: through this game's interop it crashes the game outright (crash trail,
        // 2026-10-17). Instead the whole texture is drawn with plain GUI.DrawTexture, scaled and shifted so the visible
        // part (texture v grows upwards) lands on the panel, inside a GUI group that clips the rest away.
        float fullW = on.W / uv.W, fullH = on.H / uv.H;
        GUI.BeginGroup(new Rect(on.X, on.Y, on.W, on.H));
        try { GUI.DrawTexture(new Rect(-uv.U * fullW, -(1f - (uv.V + uv.H)) * fullH, fullW, fullH), tex); }
        finally { GUI.EndGroup(); }
    }

    private static void Route(float x0, float y0, float x1, float y1, float alpha)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        if (len < 12f) return;
        int dots = Mathf.Min(60, (int)(len / 7f));
        var col = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.9f * alpha);
        for (int i = 1; i < dots; i++)
        {
            float t = i / (float)dots;
            Theme.Fill(new Rect(x0 + dx * t - 1.5f, y0 + dy * t - 1.5f, 3f, 3f), col, 1);
        }
    }

    private static void PinMark(Vector2 at, Color color, float half)
    {
        GUI.matrix = HudLayout.Rotated(_base, 45f, at);
        Theme.Fill(new Rect(at.x - half - 1.5f, at.y - half - 1.5f, half * 2f + 3f, half * 2f + 3f), Outline, 2);
        Theme.Fill(new Rect(at.x - half, at.y - half, half * 2f, half * 2f), color, 2);
        GUI.matrix = _base;
    }

    private static void Arrow(Vector2 at, float heading, Color color, float size)
    {
        var tex = ArrowTexture();
        GUI.matrix = HudLayout.Rotated(_base, heading, at);
        var prev = GUI.color;
        GUI.color = Outline;
        GUI.DrawTexture(new Rect(at.x - size * 0.5f - 2f, at.y - size * 0.5f - 2f, size + 4f, size + 4f), tex);
        GUI.color = color;
        GUI.DrawTexture(new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), tex);
        GUI.color = prev;
        GUI.matrix = _base;
    }

    private static void Name(float x, float y, string name, float maxX)
    {
        float w = Mathf.Min(120f, maxX - x - 2f);
        if (w < 24f) return;
        var prev = GUI.color;
        GUI.color = NameShadow;
        GUI.Label(new Rect(x + 1f, y + 1f, w, 16f), name, Theme.Small);
        GUI.color = prev;
        GUI.Label(new Rect(x, y, w, 16f), name, Theme.Small);
    }

    /// <summary>A white arrow pointing up (tinted with GUI.color when drawn), made once.</summary>
    private static Texture2D ArrowTexture()
    {
        if (_arrow != null) return _arrow;
        const int n = 32;
        CrashTrail.Mark("minimap: make arrow texture");
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        tex.hideFlags = HideFlags.HideAndDontSave;
        var clear = new Color(1f, 1f, 1f, 0f);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            // Texture rows go upwards: row n-1 is the tip. An arrowhead with a notch at the back.
            float fy = (y + 0.5f) / n, fx = (x + 0.5f) / n - 0.5f;
            float halfWidth = (1f - fy) * 0.45f;
            bool inside = Mathf.Abs(fx) <= halfWidth && fy >= 0.08f + Mathf.Abs(fx) * 0.5f;
            tex.SetPixel(x, y, inside ? Color.white : clear);
        }
        tex.Apply(false, false);
        _arrow = tex;
        return _arrow;
    }
}
