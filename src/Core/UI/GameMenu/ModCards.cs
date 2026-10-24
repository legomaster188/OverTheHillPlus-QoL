using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace OverTheHillPlus.UI.GameMenu;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// What a mod's card in the MOD BROWSER shows (a list row of the MODS list that carries one becomes a copy of the
/// browser's own mod cell, see <see cref="GameModsTab"/>): which mod's image, who made it, its version, side and
/// status. Set by the list's draw code through SimpleMenu.ListRow's card overload. Pure data, unit tested.
/// </summary>
internal sealed class CardInfo
{
    /// <summary>The registered mod whose DLL holds the image (an embedded "card.png"); null for plugins of other
    /// creators and mods that aren't installed (they get a generated card).</summary>
    public string ModId { get; set; }
    /// <summary>The generated card's colour comes from this (the mod's id, else its name).</summary>
    public string Seed { get; set; }
    /// <summary>The name the generated card takes its initials from.</summary>
    public string Title { get; set; }
    public string Author { get; set; }
    /// <summary>Without the "v".</summary>
    public string Version { get; set; }
    /// <summary>CLIENT / HOST / SERVER (and PRIVATE), or empty.</summary>
    public string Side { get; set; }
    /// <summary>ON / OFF / UPDATE / NOT INSTALLED ..., or empty.</summary>
    public string Status { get; set; }
    /// <summary>The download size (AVAILABLE), or empty.</summary>
    public string Size { get; set; }
    /// <summary>How finished the mod is (Unknown: no label, e.g. another creator's plugin).</summary>
    public MaturityLevel Maturity { get; set; }

    /// <summary>The key the image is cached by.</summary>
    public string ImageKey => !string.IsNullOrEmpty(ModId) ? "mod:" + ModId.ToLowerInvariant() : "art:" + (Seed ?? "") + "|" + (Title ?? "");
}

/// <summary>The words on the cards. Pure logic, unit tested.</summary>
internal static class ModCards
{
    /// <summary>The author of our own mods (and the Mod Manager).</summary>
    internal const string OurAuthor = "OTH Mods";

    /// <summary>The author shown for other creators' mods (BepInEx tells us no author).</summary>
    internal const string OtherAuthor = "Other creator";

    /// <summary>The status of an installed mod: a failed start, then a change that waits for a restart, then an
    /// update, then its switch (the Mod Manager is always on).</summary>
    public static string InstalledStatus(bool core, bool on, bool pendingRestart, bool failed, bool updateAvailable)
    {
        if (failed) return "FAILED";
        if (pendingRestart && !core) return on ? "ON AFTER RESTART" : "OFF AFTER RESTART";
        if (updateAvailable) return "UPDATE";
        if (core) return "ALWAYS ON";
        return on ? "ON" : "OFF";
    }

    /// <summary>The status of an AVAILABLE entry from its row's value ("Install" is a mod that isn't installed).</summary>
    public static string AvailableStatus(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (string.Equals(value, "Install", StringComparison.OrdinalIgnoreCase)) return "NOT INSTALLED";
        return value.Trim().TrimEnd('.').ToUpperInvariant();
    }

    /// <summary>"v1.2.0", or empty.</summary>
    public static string VersionText(string version) => string.IsNullOrEmpty(version) ? "" : version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : "v" + version;

    /// <summary>The details panel's line under the author: version, maturity, side and size, as there are.</summary>
    public static string DetailLine(CardInfo c)
    {
        if (c == null) return "";
        var parts = new List<string>(4);
        string v = VersionText(c.Version);
        if (v.Length > 0) parts.Add(v);
        string m = ModMaturity.Label(c.Maturity);
        if (m.Length > 0) parts.Add(m);
        if (!string.IsNullOrEmpty(c.Side)) parts.Add(c.Side);
        if (!string.IsNullOrEmpty(c.Size)) parts.Add(c.Size);
        return string.Join("  ·  ", parts);
    }

    /// <summary>Up to two letters for a generated card: the first letters of the first two words ("Server Browser+"
    /// → "SB"), or the first two letters of a one-word name ("Chat" → "CH"); letters and digits only.</summary>
    public static string Initials(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "?";
        var words = new List<string>();
        var cur = new StringBuilder();
        foreach (char ch in title)
        {
            if (char.IsLetterOrDigit(ch)) cur.Append(char.ToUpperInvariant(ch));
            else if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) words.Add(cur.ToString());
        if (words.Count == 0) return "?";
        if (words.Count == 1) return words[0].Length >= 2 ? words[0].Substring(0, 2) : words[0];
        return words[0].Substring(0, 1) + words[1].Substring(0, 1);
    }
}

/// <summary>The cards' grid: cell size and keyboard / gamepad order. Pure logic, unit tested.</summary>
internal static class CardGrid
{
    /// <summary>The columns to use: the browser's own count, 4 when it couldn't be read, at most 6.</summary>
    public static int Columns(int gameColumns) => gameColumns <= 0 ? 4 : Math.Min(gameColumns, 6);

    /// <summary>Rows of <paramref name="count"/> cards in <paramref name="columns"/> columns.</summary>
    public static int Rows(int count, int columns) => count <= 0 || columns <= 0 ? 0 : (count + columns - 1) / columns;

    /// <summary>
    /// The size of a cell: the browser's cell (<paramref name="templateW"/> × <paramref name="templateH"/>) when
    /// <paramref name="columns"/> of them fit in <paramref name="width"/>, else shrunk to fit with the same shape; a
    /// 16:9 image plus a name strip when the template's size is unknown.
    /// </summary>
    public static (float w, float h) CellSize(float width, int columns, float spacing, float padding, float templateW, float templateH)
    {
        columns = Math.Max(1, columns);
        float fit = (width - padding - spacing * (columns - 1)) / columns;
        if (fit < 40f) fit = 40f;
        if (templateW < 1f || templateH < 1f) return (fit, fit * 9f / 16f + 56f);
        float w = Math.Min(templateW, fit);
        return (w, w * templateH / templateW);
    }

    /// <summary>
    /// Where up / down / left / right go from each row of a page whose rows are <paramref name="kinds"/>, with each
    /// run of <see cref="WidgetKind.Card"/> rows laid out as a grid of <paramref name="columns"/> (-1: nowhere; the
    /// host sends an empty "up" to the tab button above the page, and an empty "left" from a card in the first column
    /// there too). Left / right go along a card's row; up / down go a row of cards up / down (to the last card when the
    /// row below is shorter), out of the grid to the rows around it, and into a grid on its first card from above and
    /// on its last row from below, in the same column when coming from another grid.
    /// </summary>
    public static (int up, int down, int left, int right)[] Nav(IReadOnlyList<WidgetKind> kinds, int columns)
    {
        columns = Math.Max(1, columns);
        int n = kinds.Count;
        var result = new (int up, int down, int left, int right)[n];
        // runs of cards: start of the run and its length for every card
        var runStart = new int[n];
        var runLen = new int[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = (-1, -1, -1, -1);
            if (kinds[i] != WidgetKind.Card) { runStart[i] = -1; continue; }
            if (i > 0 && kinds[i - 1] == WidgetKind.Card) runStart[i] = runStart[i - 1];
            else runStart[i] = i;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            if (runStart[i] < 0) continue;
            runLen[i] = i + 1 < n && runStart[i + 1] == runStart[i] ? runLen[i + 1] : i - runStart[i] + 1;
        }

        for (int i = 0; i < n; i++)
        {
            if (!RowMap.Selectable(kinds[i])) continue;
            if (runStart[i] < 0)
            {
                result[i].up = Enter(PrevSelectable(kinds, i), 0, true, runStart, runLen, columns);
                result[i].down = Enter(NextSelectable(kinds, i), 0, false, runStart, runLen, columns);
                continue;
            }
            int s = runStart[i], len = runLen[i], k = i - s;
            int col = k % columns, row = k / columns, lastRow = (len - 1) / columns;
            result[i].left = col > 0 ? i - 1 : -1;
            result[i].right = col < columns - 1 && k + 1 < len ? i + 1 : -1;
            result[i].up = row > 0 ? i - columns : Enter(PrevSelectable(kinds, s), col, true, runStart, runLen, columns);
            if (row < lastRow) result[i].down = s + Math.Min(k + columns, len - 1);
            else result[i].down = Enter(NextSelectable(kinds, s + len - 1), col, false, runStart, runLen, columns);
        }
        return result;
    }

    /// <summary>The row to go to when moving onto <paramref name="target"/>: a card of a grid is entered on its last
    /// row (from below) or first row (from above), in column <paramref name="col"/> when it has one there.</summary>
    private static int Enter(int target, int col, bool fromBelow, int[] runStart, int[] runLen, int columns)
    {
        if (target < 0 || runStart[target] < 0) return target;
        int s = runStart[target], len = runLen[target];
        if (fromBelow)
        {
            int lastRowStart = (len - 1) / columns * columns;
            return s + Math.Min(lastRowStart + col, len - 1);
        }
        return s + Math.Min(col, len - 1);
    }

    private static int PrevSelectable(IReadOnlyList<WidgetKind> kinds, int i)
    {
        for (int j = i - 1; j >= 0; j--) if (RowMap.Selectable(kinds[j])) return j;
        return -1;
    }

    private static int NextSelectable(IReadOnlyList<WidgetKind> kinds, int i)
    {
        for (int j = i + 1; j < kinds.Count; j++) if (RowMap.Selectable(kinds[j])) return j;
        return -1;
    }
}

/// <summary>
/// Our cards' size and the grid's columns, from the width the page has (canvas units: the browser's canvas scales with
/// the screen, 1920 × 1080 reference, so this scales with the resolution too). A card is the picture (16:9, as the
/// mods' card.png), the name, and a strip under them with the on / off, maturity, version, side and size badges, so
/// nothing covers the picture. Cards are at least <see cref="MinCellWidth"/> wide: the browser's 1090-wide panel gets
/// two columns of about 519 (its own cells are 350 in three). Pure logic, unit tested.
/// </summary>
internal static class CardLayout
{
    /// <summary>A card is never narrower than this while it can have the width (fewer columns instead).</summary>
    public const float MinCellWidth = 420f;
    public const int MaxColumns = 4;
    /// <summary>Inside the card: padding round its parts and the gap between them.</summary>
    public const float Padding = 10f, Gap = 8f;
    /// <summary>The badge strip under the name (the badges are 32 high).</summary>
    public const float StripHeight = 34f;
    /// <summary>The name's height at the browser's own card width (350); it grows with the text.</summary>
    public const float BaseTitleHeight = 26f, BaseCellWidth = 350f;
    public const float ImageAspect = 16f / 9f;

    /// <summary>How many columns of at least <see cref="MinCellWidth"/> fit in <paramref name="width"/> (1 to <see cref="MaxColumns"/>).</summary>
    public static int Columns(float width, float spacing)
    {
        if (width <= 0f) return 1;
        int n = (int)Math.Floor((width + spacing) / (MinCellWidth + spacing));
        return Math.Clamp(n, 1, MaxColumns);
    }

    /// <summary>The width of each of <paramref name="columns"/> cards filling <paramref name="width"/> (at least 40).</summary>
    public static float CellWidth(float width, int columns, float spacing)
    {
        columns = Math.Max(1, columns);
        return Math.Max(40f, (width - spacing * (columns - 1)) / columns);
    }

    /// <summary>The name's text size factor: 1 at the browser's width, up to 1.35 on wide cards.</summary>
    public static float TextScale(float cellWidth) => Math.Clamp(cellWidth / BaseCellWidth, 1f, 1.35f);

    public static float TitleHeight(float cellWidth) => (float)Math.Ceiling(BaseTitleHeight * TextScale(cellWidth));

    /// <summary>The picture's height: 16:9 across the card inside its padding.</summary>
    public static float ImageHeight(float cellWidth) => Math.Max(10f, (cellWidth - 2f * Padding) / ImageAspect);

    /// <summary>The whole card: padding, picture, gap, name, gap, strip, padding.</summary>
    public static float CellHeight(float cellWidth) =>
        2f * Padding + ImageHeight(cellWidth) + 2f * Gap + TitleHeight(cellWidth) + StripHeight;

    /// <summary>Columns and card size for a page <paramref name="width"/> wide.</summary>
    public static (int columns, float w, float h) Cell(float width, float spacing)
    {
        int c = Columns(width, spacing);
        float w = CellWidth(width, c, spacing);
        return (c, w, CellHeight(w));
    }
}

/// <summary>
/// The MODS page's rect: the browser's grid panel, grown into the free space round it (upwards over the sort / filter
/// line the liveries use, hidden while a tab of ours is up). Pure logic, unit tested.
/// </summary>
internal static class PageRect
{
    /// <summary>How far the page reaches above the grid panel (the hidden sort / filter line is 35 high).</summary>
    public const float GrowTop = 34f;

    /// <summary>
    /// A RectTransform's anchored position and size delta grown by <paramref name="left"/>, <paramref name="right"/>,
    /// <paramref name="top"/> and <paramref name="bottom"/> (parent units) round its pivot: the size grows by the sums,
    /// and the position moves so the other edges stay where they were.
    /// </summary>
    public static (float x, float y, float w, float h) Grow(float x, float y, float w, float h, float pivotX, float pivotY,
        float left, float right, float top, float bottom) =>
        (x - left + pivotX * (left + right), y - bottom + pivotY * (bottom + top), w + left + right, h + top + bottom);
}

/// <summary>What colour a card's badge is: on / off and the like, and the maturity. Pure logic, unit tested.</summary>
internal enum BadgeTone
{
    /// <summary>The browser's own dark badge.</summary>
    Plain,
    /// <summary>On, installed: green.</summary>
    On,
    /// <summary>Off: grey.</summary>
    Off,
    /// <summary>Waits for a restart: amber.</summary>
    Waiting,
    /// <summary>Failed: red.</summary>
    Failed,
    /// <summary>An update: blue.</summary>
    Update,
    Stable,
    Beta,
    Rough,
}

internal static class BadgeStyle
{
    /// <summary>The tone of a card's status (<see cref="ModCards.InstalledStatus"/> / <see cref="ModCards.AvailableStatus"/>).</summary>
    public static BadgeTone Status(string status)
    {
        string s = (status ?? "").Trim().ToUpperInvariant();
        if (s.Length == 0) return BadgeTone.Plain;
        if (s == "FAILED") return BadgeTone.Failed;
        if (s.Contains("RESTART")) return BadgeTone.Waiting;
        if (s == "UPDATE") return BadgeTone.Update;
        if (s == "ON" || s == "ALWAYS ON" || s == "INSTALLED") return BadgeTone.On;
        if (s == "OFF") return BadgeTone.Off;
        return BadgeTone.Plain;
    }

    public static BadgeTone Maturity(MaturityLevel level) => level switch
    {
        MaturityLevel.Stable => BadgeTone.Stable,
        MaturityLevel.Beta => BadgeTone.Beta,
        MaturityLevel.Rough => BadgeTone.Rough,
        _ => BadgeTone.Plain,
    };

    /// <summary>The badge's background (RGBA bytes); <see cref="BadgeTone.Plain"/> is the browser's black at 80%.</summary>
    public static (byte r, byte g, byte b, byte a) Colour(BadgeTone tone) => tone switch
    {
        BadgeTone.On => (38, 150, 82, 240),
        BadgeTone.Off => (96, 98, 104, 235),
        BadgeTone.Waiting => (201, 138, 30, 240),
        BadgeTone.Failed => (196, 52, 46, 240),
        BadgeTone.Update => (40, 110, 196, 240),
        BadgeTone.Stable => (44, 112, 150, 235),
        BadgeTone.Beta => (196, 140, 36, 235),
        BadgeTone.Rough => (176, 62, 48, 240),
        _ => (0, 0, 0, 204),
    };
}

/// <summary>
/// The card a mod without an image gets (and other creators' plugins): its initials in white on a colour from its id,
/// shaded towards the bottom, as a PNG (decoded like a mod's own card.png). Pure logic, unit tested.
/// </summary>
internal static class CardArt
{
    /// <summary>A muted colour from <paramref name="seed"/> (the same seed always gives the same colour).</summary>
    public static (byte r, byte g, byte b) Colour(string seed)
    {
        uint h = 2166136261;
        foreach (char ch in (seed ?? "").ToLowerInvariant())
        {
            h ^= ch;
            h *= 16777619;
        }
        float hue = h % 360u;
        return Hsv(hue, 0.55f, 0.58f);
    }

    private static (byte, byte, byte) Hsv(float hue, float s, float v)
    {
        float c = v * s;
        float x = c * (1f - Math.Abs(hue / 60f % 2f - 1f));
        float m = v - c;
        (float r, float g, float b) = hue switch
        {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return (B(r + m), B(g + m), B(b + m));
    }

    private static byte B(float f) => (byte)Math.Clamp((int)Math.Round(f * 255f), 0, 255);

    /// <summary>The generated card as RGB pixels, top row first.</summary>
    public static byte[] Pixels(string seed, string title, int w, int h)
    {
        var (r, g, b) = Colour(seed);
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            float shade = 1f - 0.35f * y / Math.Max(1, h - 1);
            byte cr = (byte)(r * shade), cg = (byte)(g * shade), cb = (byte)(b * shade);
            int row = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                px[row + x * 3] = cr;
                px[row + x * 3 + 1] = cg;
                px[row + x * 3 + 2] = cb;
            }
        }
        DrawText(px, w, h, ModCards.Initials(title));
        return px;
    }

    /// <summary>The generated card as a PNG file.</summary>
    public static byte[] Png(string seed, string title, int w, int h) => EncodePng(Pixels(seed, title, w, h), w, h);

    // 5 × 7 glyphs, rows top to bottom.
    private static readonly Dictionary<char, string> Font = new()
    {
        ['A'] = "01110|10001|10001|11111|10001|10001|10001", ['B'] = "11110|10001|10001|11110|10001|10001|11110",
        ['C'] = "01110|10001|10000|10000|10000|10001|01110", ['D'] = "11110|10001|10001|10001|10001|10001|11110",
        ['E'] = "11111|10000|10000|11110|10000|10000|11111", ['F'] = "11111|10000|10000|11110|10000|10000|10000",
        ['G'] = "01110|10001|10000|10111|10001|10001|01111", ['H'] = "10001|10001|10001|11111|10001|10001|10001",
        ['I'] = "01110|00100|00100|00100|00100|00100|01110", ['J'] = "00111|00010|00010|00010|00010|10010|01100",
        ['K'] = "10001|10010|10100|11000|10100|10010|10001", ['L'] = "10000|10000|10000|10000|10000|10000|11111",
        ['M'] = "10001|11011|10101|10101|10001|10001|10001", ['N'] = "10001|10001|11001|10101|10011|10001|10001",
        ['O'] = "01110|10001|10001|10001|10001|10001|01110", ['P'] = "11110|10001|10001|11110|10000|10000|10000",
        ['Q'] = "01110|10001|10001|10001|10101|10010|01101", ['R'] = "11110|10001|10001|11110|10100|10010|10001",
        ['S'] = "01111|10000|10000|01110|00001|00001|11110", ['T'] = "11111|00100|00100|00100|00100|00100|00100",
        ['U'] = "10001|10001|10001|10001|10001|10001|01110", ['V'] = "10001|10001|10001|10001|10001|01010|00100",
        ['W'] = "10001|10001|10001|10101|10101|10101|01010", ['X'] = "10001|10001|01010|00100|01010|10001|10001",
        ['Y'] = "10001|10001|01010|00100|00100|00100|00100", ['Z'] = "11111|00001|00010|00100|01000|10000|11111",
        ['0'] = "01110|10001|10011|10101|11001|10001|01110", ['1'] = "00100|01100|00100|00100|00100|00100|01110",
        ['2'] = "01110|10001|00001|00010|00100|01000|11111", ['3'] = "11110|00001|00001|01110|00001|00001|11110",
        ['4'] = "00010|00110|01010|10010|11111|00010|00010", ['5'] = "11111|10000|11110|00001|00001|10001|01110",
        ['6'] = "00110|01000|10000|11110|10001|10001|01110", ['7'] = "11111|00001|00010|00100|01000|01000|01000",
        ['8'] = "01110|10001|10001|01110|10001|10001|01110", ['9'] = "01110|10001|10001|01111|00001|00010|01100",
        ['?'] = "01110|10001|00001|00010|00100|00000|00100",
    };

    /// <summary>Draws <paramref name="text"/> in white, centred, its letters a third of the card's height tall.</summary>
    private static void DrawText(byte[] px, int w, int h, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        int scale = Math.Max(1, h / 3 / 7);
        int glyphW = 5 * scale, gap = 2 * scale;
        int total = text.Length * glyphW + (text.Length - 1) * gap;
        if (total > w)
        {
            scale = Math.Max(1, w / (text.Length * 7));
            glyphW = 5 * scale;
            gap = 2 * scale;
            total = text.Length * glyphW + (text.Length - 1) * gap;
        }
        int x0 = (w - total) / 2, y0 = (h - 7 * scale) / 2;
        for (int c = 0; c < text.Length; c++)
        {
            if (!Font.TryGetValue(text[c], out var glyph)) glyph = Font['?'];
            var rows = glyph.Split('|');
            for (int gy = 0; gy < 7; gy++)
            for (int gx = 0; gx < 5; gx++)
            {
                if (rows[gy][gx] != '1') continue;
                for (int sy = 0; sy < scale; sy++)
                for (int sx = 0; sx < scale; sx++)
                {
                    int x = x0 + c * (glyphW + gap) + gx * scale + sx, y = y0 + gy * scale + sy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    int at = (y * w + x) * 3;
                    px[at] = px[at + 1] = px[at + 2] = 255;
                }
            }
        }
    }

    /// <summary>RGB pixels (top row first) as a PNG: 8-bit truecolour, no filtering, zlib-compressed.</summary>
    public static byte[] EncodePng(byte[] rgb, int w, int h)
    {
        var raw = new byte[(w * 3 + 1) * h];
        for (int y = 0; y < h; y++)
        {
            raw[y * (w * 3 + 1)] = 0; // filter: none
            Buffer.BlockCopy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
        }
        byte[] z;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(raw, 0, raw.Length);
            z = ms.ToArray();
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)w);
        BigEndian(ihdr, 4, (uint)h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // truecolour
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BigEndian(len, 0, (uint)data.Length);
        s.Write(len, 0, 4);
        var head = Encoding.ASCII.GetBytes(type);
        s.Write(head, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc(head, 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        var c = new byte[4];
        BigEndian(c, 0, crc);
        s.Write(c, 0, 4);
    }

    private static uint[] _crcTable;

    private static uint Crc(byte[] data, uint crc)
    {
        if (_crcTable == null)
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            _crcTable = t;
        }
        foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void BigEndian(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }
}
