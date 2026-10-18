using UnityEngine;

namespace OverTheHillPlus;

/// <summary>Screen corner a HUD panel sits in.</summary>
internal enum HudCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Placing the mods' small HUD panels (Trip Stats, HUD+, Minimap, Auto Screenshot) in a screen corner, in 1080p units
/// scaled to the screen like the driving HUD, so they line up with each other and with that HUD.
/// </summary>
internal static class HudLayout
{
    public const float Margin = 24f;
    public static readonly string[] CornerNames = { "Top left", "Top right", "Bottom left", "Bottom right" };

    /// <summary>The GUI scale for 1080p units (never below 1).</summary>
    public static float Scale => Mathf.Max(1f, Screen.height / 1080f);

    /// <summary>Sets GUI.matrix to 1080p units and returns the screen size in those units.</summary>
    public static Vector2 Begin(float extraScale = 1f)
    {
        float s = Scale * Mathf.Clamp(extraScale, 0.5f, 3f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
        return new Vector2(Screen.width / s, Screen.height / s);
    }

    /// <summary>Puts GUI.matrix back to identity (call after drawing).</summary>
    public static void End() => GUI.matrix = Matrix4x4.identity;

    /// <summary>
    /// A <paramref name="w"/> x <paramref name="h"/> panel in <paramref name="corner"/> of a screen of
    /// <paramref name="screen"/> units, pushed <paramref name="offset"/> units away from the corner along the screen
    /// edge (to stack panels). In the top right it starts below the driving HUD while that is shown.
    /// </summary>
    public static Rect Place(HudCorner corner, Vector2 screen, float w, float h, float offset = 0f)
    {
        bool right = corner is HudCorner.TopRight or HudCorner.BottomRight;
        bool bottom = corner is HudCorner.BottomLeft or HudCorner.BottomRight;
        float x = right ? screen.x - w - Margin : Margin;
        float top = Margin;
        if (corner == HudCorner.TopRight && DrivingHud.Shown) top += DrivingHud.Height + 12f;
        float y = bottom ? screen.y - h - Margin - offset : top + offset;
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// <paramref name="matrix"/> turned <paramref name="degrees"/> clockwise round <paramref name="pivot"/> (in the
    /// units of <paramref name="matrix"/>), for GUI.matrix; set GUI.matrix back after drawing. Built by hand:
    /// GUIUtility.RotateAroundPivot puts the pivot in the wrong place under a scaled GUI.matrix.
    /// </summary>
    public static Matrix4x4 Rotated(Matrix4x4 matrix, float degrees, Vector2 pivot) =>
        matrix * Matrix4x4.TRS(new Vector3(pivot.x, pivot.y, 0f), Quaternion.Euler(0f, 0f, degrees), Vector3.one)
               * Matrix4x4.TRS(new Vector3(-pivot.x, -pivot.y, 0f), Quaternion.identity, Vector3.one);

    /// <summary>
    /// An opacity setting clamped to [<paramref name="min"/>, 1] in steps of 0.05: every distinct fill colour costs the
    /// UI kit a texture, so a slider being dragged must not make hundreds of them.
    /// </summary>
    public static float Opacity(float value, float min) => Mathf.Round(Mathf.Clamp(value, min, 1f) * 20f) / 20f;

    public static HudCorner Corner(int value) => value is >= 0 and <= 3 ? (HudCorner)value : HudCorner.TopLeft;
}
