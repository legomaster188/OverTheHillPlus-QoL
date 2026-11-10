using UnityEngine;

namespace OverTheHillPlus;

/// <summary>
/// Placing the mods' small HUD panels (Trip Stats, HUD+, Minimap, Auto Screenshot) in a screen corner, in 1080p units
/// scaled to the screen like the driving HUD, so they line up with each other and with that HUD.
/// </summary>
internal static class HudLayout
{
    public const float Margin = 24f;
    public static readonly string[] CornerNames = { "Top left", "Top right", "Bottom left", "Bottom right" };

    /// <summary>The cream of the game's HUD text (#FFF9EE).</summary>
    public static readonly Color Cream = new(1f, 0.976f, 0.933f, 1f);

    /// <summary>The small grey of a panel's title: the cream at half strength.</summary>
    public static readonly Color Grey = new(1f, 0.976f, 0.933f, 0.55f);

    /// <summary>A panel's black at 40%, as the game's own HUD panels.</summary>
    public static readonly Color PanelBlack = new(0f, 0f, 0f, 0.4f);

    /// <summary>The corners of a panel, in HUD units.</summary>
    public const int PanelRadius = 5;

    /// <summary>The GUI scale for the HUD's units now: they follow the screen's width (1920 across is 1 : 1), like the
    /// game's own HUD (see <see cref="HudScale"/>).</summary>
    public static float Scale => HudScale.For(Screen.width);

    /// <summary>Draws a panel in the game's HUD style: black at 40% with 5 unit corners. Call between Begin and End.</summary>
    public static void Panel(Rect r) => UI.Theme.Fill(r, PanelBlack, PanelRadius);

    /// <summary>Sets GUI.matrix to HUD units (1920 across) and returns the screen size in those units.</summary>
    public static Vector2 Begin(float extraScale = 1f)
    {
        float s = Scale * Mathf.Clamp(extraScale, 0.5f, 3f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
        UI.HudKit.HudCanvas.Sync();
        return new Vector2(Screen.width / s, Screen.height / s);
    }

    /// <summary>Puts GUI.matrix back to identity (call after drawing).</summary>
    public static void End()
    {
        GUI.matrix = Matrix4x4.identity;
        UI.HudKit.HudCanvas.Sync();
    }

    /// <summary>
    /// A <paramref name="w"/> x <paramref name="h"/> panel in <paramref name="corner"/> of a screen of
    /// <paramref name="screen"/> units, pushed <paramref name="offset"/> units away from the corner along the screen
    /// edge (to stack panels). In the top right it starts below the driving HUD while that is shown.
    /// </summary>
    public static Rect Place(HudCorner corner, Vector2 screen, float w, float h, float offset = 0f)
    {
        bool right = corner is HudCorner.TopRight or HudCorner.BottomRight;
        bool bottom = corner is HudCorner.BottomLeft or HudCorner.BottomRight;
        float top = Margin;
        if (corner == HudCorner.TopRight && DrivingHud.Shown) top += DrivingHud.Height + 12f;
        var (x, y) = UI.HudKit.HudRules.Place(right, bottom, screen.x, screen.y, w, h, Margin, top, offset);
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
