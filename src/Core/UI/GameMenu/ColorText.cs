using System;
using System.Globalization;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// Colours as text for the MODS pages: "#RRGGBB" codes (shown and typed in a colour's text box), a colour swatch
/// the game's text can draw (TextMeshPro's &lt;mark&gt; behind letters in the same colour) and a plain name for a
/// colour. Pure logic (no Unity types), unit tested.
/// </summary>
internal static class ColorText
{
    /// <summary>"RRGGBB" for channels 0..1.</summary>
    public static string Hex(float r, float g, float b) => Byte(r).ToString("X2") + Byte(g).ToString("X2") + Byte(b).ToString("X2");

    private static int Byte(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);

    /// <summary>Reads "#RRGGBB", "RRGGBB", "#RGB" or "RGB" (spaces around are fine). False for anything else.</summary>
    public static bool TryParse(string text, out float r, out float g, out float b)
    {
        r = g = b = 0f;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.StartsWith("#", StringComparison.Ordinal)) t = t.Substring(1);
        if (t.Length == 3) t = new string(new[] { t[0], t[0], t[1], t[1], t[2], t[2] });
        if (t.Length != 6) return false;
        if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        r = ((v >> 16) & 255) / 255f;
        g = ((v >> 8) & 255) / 255f;
        b = (v & 255) / 255f;
        return true;
    }

    /// <summary>A small solid block of colour <paramref name="hex"/> ("RRGGBB") in TextMeshPro rich text.</summary>
    public static string Swatch(string hex) => $"<mark=#{hex}FF><color=#{hex}>####</color></mark>";

    /// <summary>A plain name for a colour (for palette entries without one): its hue, or white / grey / black.</summary>
    public static string Name(float r, float g, float b)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        float d = max - min;
        if (max < 0.12f) return "Black";
        if (d < 0.08f) return max > 0.85f ? "White" : "Grey";
        float h;
        if (max == r) h = ((g - b) / d % 6f + 6f) % 6f;
        else if (max == g) h = (b - r) / d + 2f;
        else h = (r - g) / d + 4f;
        h *= 60f;
        if (h < 15f || h >= 340f) return "Red";
        if (h < 40f) return "Orange";
        if (h < 70f) return "Yellow";
        if (h < 160f) return "Green";
        if (h < 200f) return "Cyan";
        if (h < 255f) return "Blue";
        if (h < 290f) return "Purple";
        return "Pink";
    }
}
