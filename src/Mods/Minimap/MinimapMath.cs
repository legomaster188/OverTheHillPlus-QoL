using System;

namespace OverTheHillPlus.Minimap;

/// <summary>
/// The minimap's pure maths (no game types, unit tested): world position to map texture coordinates, the part of the
/// map the panel shows, and where a point lands on the panel.
///
/// The game's map (MapManager.MapTexture) and fog of war cover the terrain square that starts at TerrainOffset (x, z)
/// and is MapWidth metres wide: FogOfWarManager.WorldToGridPosition is (position - offset) / width (times the texture
/// size), so texture u grows with world x and v with world z (north up).
/// </summary>
internal static class MinimapMath
{
    public const float MinMeters = 100f, MaxMeters = 4000f;

    /// <summary>A square window onto the map in texture coordinates (0-1).</summary>
    internal readonly struct View
    {
        public readonly float UMin, VMin, Size;

        public View(float uMin, float vMin, float size)
        {
            UMin = uMin; VMin = vMin; Size = size;
        }

        public float UMax => UMin + Size;
        public float VMax => VMin + Size;
    }

    /// <summary>A rectangle on screen (y grows downwards, as in IMGUI).</summary>
    internal readonly struct Box
    {
        public readonly float X, Y, W, H;

        public Box(float x, float y, float w, float h)
        {
            X = x; Y = y; W = w; H = h;
        }

        public float XMax => X + W;
        public float YMax => Y + H;
    }

    public static (float u, float v) WorldToUv(float x, float z, float offsetX, float offsetZ, float mapWidth)
    {
        if (!(mapWidth > 0f)) return (0.5f, 0.5f);
        return ((x - offsetX) / mapWidth, (z - offsetZ) / mapWidth);
    }

    public static float ClampMeters(float m) => float.IsNaN(m) ? 600f : Math.Max(MinMeters, Math.Min(MaxMeters, m));

    /// <summary>The window <paramref name="metersAcross"/> wide centred on (<paramref name="u"/>, <paramref name="v"/>).</summary>
    public static View ViewAround(float u, float v, float metersAcross, float mapWidth)
    {
        float size = mapWidth > 0f ? ClampMeters(metersAcross) / mapWidth : 1f;
        return new View(u - size * 0.5f, v - size * 0.5f, size);
    }

    /// <summary>Where texture point (<paramref name="u"/>, <paramref name="v"/>) lands on <paramref name="panel"/> (may be outside it).</summary>
    public static (float x, float y) ToPanel(float u, float v, View view, Box panel)
    {
        float fx = view.Size > 0f ? (u - view.UMin) / view.Size : 0.5f;
        float fy = view.Size > 0f ? (v - view.VMin) / view.Size : 0.5f;
        return (panel.X + fx * panel.W, panel.Y + (1f - fy) * panel.H);
    }

    public static bool Inside(float x, float y, Box panel, float inset = 0f) =>
        x >= panel.X + inset && x <= panel.XMax - inset && y >= panel.Y + inset && y <= panel.YMax - inset;

    /// <summary>
    /// A point outside the panel pulled onto its edge (inset by <paramref name="inset"/>) along the line from the
    /// panel's centre, so something off the map still shows which way it is. Points inside come back unchanged.
    /// </summary>
    public static (float x, float y, bool clamped) ClampToEdge(float x, float y, Box panel, float inset)
    {
        if (Inside(x, y, panel, inset)) return (x, y, false);
        float cx = panel.X + panel.W * 0.5f, cy = panel.Y + panel.H * 0.5f;
        float dx = x - cx, dy = y - cy;
        float hx = panel.W * 0.5f - inset, hy = panel.H * 0.5f - inset;
        if (hx <= 0f || hy <= 0f) return (cx, cy, true);
        float t = Math.Min(Math.Abs(dx) > 1e-6f ? hx / Math.Abs(dx) : float.MaxValue, Math.Abs(dy) > 1e-6f ? hy / Math.Abs(dy) : float.MaxValue);
        if (t > 1f) t = 1f;
        return (cx + dx * t, cy + dy * t, true);
    }

    /// <summary>A rectangle of texture coordinates (0-1), as GUI.DrawTextureWithTexCoords takes it.</summary>
    internal readonly struct Uv
    {
        public readonly float U, V, W, H;

        public Uv(float u, float v, float w, float h)
        {
            U = u; V = v; W = w; H = h;
        }
    }

    /// <summary>
    /// The part of the map texture (0-1) the view covers, and the matching part of the panel, so the texture is only
    /// drawn where it exists (no stretched edge pixels outside the map). False when the view is entirely off the map.
    /// </summary>
    public static bool Visible(View view, Box panel, out Uv tex, out Box onPanel)
    {
        float u0 = Math.Max(0f, view.UMin), u1 = Math.Min(1f, view.UMax);
        float v0 = Math.Max(0f, view.VMin), v1 = Math.Min(1f, view.VMax);
        tex = default;
        onPanel = default;
        if (!(view.Size > 0f) || u1 <= u0 || v1 <= v0) return false;
        var (x0, yBottom) = ToPanel(u0, v0, view, panel);
        var (x1, yTop) = ToPanel(u1, v1, view, panel);
        tex = new Uv(u0, v0, u1 - u0, v1 - v0);
        onPanel = new Box(x0, yTop, x1 - x0, yBottom - yTop);
        return true;
    }

    /// <summary>Compass heading in degrees (0 = north / +z, clockwise) of a forward vector's x and z.</summary>
    public static float Heading(float fx, float fz)
    {
        if (Math.Abs(fx) < 1e-6f && Math.Abs(fz) < 1e-6f) return 0f;
        float h = (float)(Math.Atan2(fx, fz) * 180.0 / Math.PI);
        return h < 0f ? h + 360f : h;
    }
}
