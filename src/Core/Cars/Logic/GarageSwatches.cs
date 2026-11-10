using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Cars;

/// <summary>What a garage swatch sets: the body paint ([Look] Paint) or the trim ([Look] Trim).</summary>
public enum SwatchKind { Paint, Trim }

/// <summary>One colour in the garage's Liveries tab: a paint or trim preset by name (the name is what the config keeps).</summary>
public sealed class Swatch
{
    public SwatchKind Kind;
    public string Name;
    public CarRgb Rgb;
    /// <summary>A trim that takes the body colour (<see cref="Rgb"/> is not used).</summary>
    public bool BodyColour;

    /// <summary>The colour it shows with <paramref name="main"/> as the body colour.</summary>
    public CarRgb Shown(CarRgb main) => BodyColour ? main : Rgb;
}

/// <summary>A titled row of swatches (one block of the Liveries tab).</summary>
public sealed class SwatchGroup
{
    public string Title;
    public SwatchKind Kind;
    public readonly List<Swatch> Items = new();
}

/// <summary>
/// The paints and trims a modded car offers in the garage's Liveries tab, below its liveries, in the MODS page's order:
/// the car's own paints ("Paint"), the shared palette's groups (<see cref="CarPalette.Groups"/>, without names the car
/// already has), then every trim ("Trim": the car's own, then the shared ones it doesn't have). Custom colours stay on
/// the MODS page (they need a colour box). Picking one sets the same config entry the MODS page sets, so the page, the
/// save and what other players see (CarLookWire) all follow. Also the swatch icon's pixels, the description line and the
/// controller navigation over the blocks. Pure (unit-tested).
/// </summary>
public static class GarageSwatches
{
    public const string PaintTitle = "Paint", TrimTitle = "Trim";

    public static List<SwatchGroup> Groups(IReadOnlyList<PaintPreset> ownPaints, IReadOnlyList<TrimPreset> ownTrims)
    {
        var groups = new List<SwatchGroup>();
        ownPaints ??= Array.Empty<PaintPreset>();
        ownTrims ??= Array.Empty<TrimPreset>();
        var own = new SwatchGroup { Title = PaintTitle, Kind = SwatchKind.Paint };
        foreach (var p in ownPaints)
            if (p != null) own.Items.Add(new Swatch { Kind = SwatchKind.Paint, Name = p.Name, Rgb = p.Rgb });
        if (own.Items.Count > 0) groups.Add(own);
        foreach (var g in CarPalette.Groups)
        {
            var sg = new SwatchGroup { Title = g.Title, Kind = SwatchKind.Paint };
            foreach (var p in g.Paints)
                if (CarPalette.Offered(ownPaints, p.Name)) sg.Items.Add(new Swatch { Kind = SwatchKind.Paint, Name = p.Name, Rgb = p.Rgb });
            if (sg.Items.Count > 0) groups.Add(sg);
        }
        var trims = new SwatchGroup { Title = TrimTitle, Kind = SwatchKind.Trim };
        foreach (var t in ownTrims)
            if (t != null) trims.Items.Add(new Swatch { Kind = SwatchKind.Trim, Name = t.Name, Rgb = t.Rgb, BodyColour = t.BodyColour });
        foreach (var t in CarPalette.Trims)
            if (CarPalette.Offered(ownTrims, t.Name)) trims.Items.Add(new Swatch { Kind = SwatchKind.Trim, Name = t.Name, Rgb = t.Rgb, BodyColour = t.BodyColour });
        if (trims.Items.Count > 0) groups.Add(trims);
        return groups;
    }

    /// <summary>
    /// The swatch of <paramref name="kind"/> the setting picks: the one of that name (any case), else the first of that
    /// kind when the setting names nothing listed (the car falls back to its first preset then, see CarPaint), or null
    /// for Custom (set on the MODS page, not one of these).
    /// </summary>
    public static Swatch Current(IReadOnlyList<SwatchGroup> groups, SwatchKind kind, string setting)
    {
        if (groups == null) return null;
        Swatch first = null;
        foreach (var g in groups)
        {
            if (g.Kind != kind) continue;
            foreach (var s in g.Items)
            {
                first ??= s;
                if (string.Equals(s.Name, setting, StringComparison.OrdinalIgnoreCase)) return s;
            }
        }
        return string.Equals(setting?.Trim(), CarPaint.Custom, StringComparison.OrdinalIgnoreCase) ? null : first;
    }

    /// <summary>
    /// The description under a highlighted swatch: what it colours, then what covers it on this car right now (a skin
    /// from the MODS page, or a livery mounted in this tab).
    /// </summary>
    public static string Description(Swatch s, string trimWhat, bool skinOn, string mountedLivery)
    {
        if (s == null) return "";
        string what;
        if (s.Kind == SwatchKind.Paint) what = "Paints the body.";
        else if (s.BodyColour) what = "The trim takes the body colour.";
        else if (string.IsNullOrWhiteSpace(trimWhat) || string.Equals(trimWhat.Trim(), "Trim", StringComparison.OrdinalIgnoreCase)) what = "Paints the trim.";
        else what = "Paints the " + LowerFirst(trimWhat.Trim()) + ".";
        string line = what + " Other players with this car see it too.";
        if (skinOn) line += " Your skin covers it right now (take it off on the MODS page).";
        else if (!string.IsNullOrWhiteSpace(mountedLivery)) line += $" The {mountedLivery.Trim()} livery sits on top of it.";
        return line;
    }

    private static string LowerFirst(string s) =>
        s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s.Substring(1) : s;

    // ---------------------------------------------------------------- the icon

    /// <summary>
    /// A paint chip on a clear background, the size of a garage icon: a rounded square of the colour, a touch lighter at
    /// the top and darker at the bottom like a painted panel under the garage lights, with a thin darker rim so white and
    /// black chips both read on the game's tiles. RGBA32, rows bottom-up (Unity's order), <paramref name="size"/> square.
    /// </summary>
    public static byte[] ChipPixels(CarRgb c, int size)
    {
        size = Math.Clamp(size, 8, 1024);
        var px = new byte[size * size * 4];
        float inset = size * 0.14f, radius = size * 0.16f, rim = Math.Max(1.5f, size / 48f);
        float lo = inset, hi = size - inset;
        for (int y = 0; y < size; y++)
        {
            float fy = y + 0.5f;
            // 0 at the bottom of the chip, 1 at the top
            float t = Math.Clamp((fy - lo) / (hi - lo), 0f, 1f);
            float shade = 0.86f + 0.22f * t;
            for (int x = 0; x < size; x++)
            {
                float fx = x + 0.5f;
                float d = RoundedBoxDistance(fx, fy, lo, hi, radius); // < 0 inside
                float cover = Math.Clamp(0.5f - d, 0f, 1f);
                int o = (y * size + x) * 4;
                if (cover <= 0f) continue;
                float edge = Math.Clamp(-d / rim, 0f, 1f); // 0 on the rim, 1 inside it
                float k = shade * (0.62f + 0.38f * edge);
                px[o] = Channel(c.R, k);
                px[o + 1] = Channel(c.G, k);
                px[o + 2] = Channel(c.B, k);
                px[o + 3] = (byte)Math.Round(cover * 255f);
            }
        }
        return px;
    }

    /// <summary>Signed distance from (x, y) to a square from lo to hi on both axes with rounded corners (negative inside).</summary>
    private static float RoundedBoxDistance(float x, float y, float lo, float hi, float r)
    {
        float cx = (lo + hi) * 0.5f, half = (hi - lo) * 0.5f;
        float qx = Math.Abs(x - cx) - (half - r);
        float qy = Math.Abs(y - cx) - (half - r);
        float ox = Math.Max(qx, 0f), oy = Math.Max(qy, 0f);
        return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0f) - r;
    }

    /// <summary>A channel scaled by <paramref name="k"/>; above 1 it moves toward white, so a light colour still gets a highlight.</summary>
    private static byte Channel(byte v, float k)
    {
        float f = k <= 1f ? v * k : v + (255f - v) * (k - 1f) * 0.6f;
        return (byte)Math.Clamp((int)Math.Round(f), 0, 255);
    }

    // ---------------------------------------------------------------- controller navigation

    /// <summary>
    /// Where up / down / left / right go from each item of a run of grid blocks laid out one under the other (block
    /// <c>b</c> has <paramref name="counts"/>[b] items in rows of <paramref name="columns"/>); items are numbered across
    /// all blocks in order, -1 is nowhere. Left / right go along a row; up / down go a row, to the last item of a shorter
    /// row below, and across to the next / previous non-empty block in the same column (its first row going down, its
    /// last row going up).
    /// </summary>
    public static (int up, int down, int left, int right)[] Nav(IReadOnlyList<int> counts, int columns)
    {
        columns = Math.Max(1, columns);
        int blocks = counts?.Count ?? 0;
        var start = new int[blocks];
        int total = 0;
        for (int b = 0; b < blocks; b++) { start[b] = total; total += Math.Max(0, counts[b]); }
        var nav = new (int up, int down, int left, int right)[total];
        for (int b = 0; b < blocks; b++)
        {
            int n = Math.Max(0, counts[b]);
            int rows = (n + columns - 1) / columns;
            for (int i = 0; i < n; i++)
            {
                int row = i / columns, col = i % columns;
                int left = col > 0 ? start[b] + i - 1 : -1;
                int right = col < columns - 1 && i + 1 < n ? start[b] + i + 1 : -1;
                int down;
                if (i + columns < n) down = start[b] + i + columns;
                else if (row < rows - 1) down = start[b] + n - 1;
                else down = Into(counts, start, b + 1, +1, col, columns);
                int up = row > 0 ? start[b] + i - columns : Into(counts, start, b - 1, -1, col, columns);
                nav[start[b] + i] = (up, down, left, right);
            }
        }
        return nav;
    }

    /// <summary>The item in column <paramref name="col"/> (or the last of a shorter row) of the first non-empty block from
    /// <paramref name="b"/> going <paramref name="step"/>: its first row going down, its last row going up.</summary>
    private static int Into(IReadOnlyList<int> counts, int[] start, int b, int step, int col, int columns)
    {
        for (; b >= 0 && b < counts.Count; b += step)
        {
            int n = counts[b];
            if (n <= 0) continue;
            int rowStart = step > 0 ? 0 : (n - 1) / columns * columns;
            return start[b] + Math.Min(rowStart + col, n - 1);
        }
        return -1;
    }
}
