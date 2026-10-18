using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using OverTheHillPlus.ServerCreator;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerNameStyles;

/// <summary>
/// Builds the TextMesh Pro rich text for your server's name in everyone's server browser.
///
/// Tags used (all checked against the game's TMP: TMP_Text.ValidateHtmlTag handles the hashes of "mark", "size",
/// "cspace" and their closing tags, and MarkupTag has MARK, SIZE, CHARACTER_SPACE, ITALIC, UNDERLINE, BOLD):
///   &lt;size=90%&gt;  &lt;cspace=0.08em&gt;  &lt;mark=#RRGGBBAA&gt;  &lt;b&gt; &lt;i&gt; &lt;u&gt;  &lt;color=#RRGGBB&gt;
///   &lt;font="Oswald-Bold SDF"&gt;  &lt;allcaps&gt;  &lt;smallcaps&gt;
/// The browser's name label is NunitoSans at 24 with about one line of height (Server Browser+ logs its layout): a
/// size above 100% no longer fits and the game shows the name blank, so sizes stop at 100%. Fonts are the game's own
/// TextMeshPro font assets (also logged by Server Browser+); a font the viewer's game can't find falls back to the
/// label's own.
/// Every tag is closed, so nothing leaks into text the game or the mod appends after the name (age, tags).
///
/// Only the mod's own tags with fixed preset values are ever written: the visible text is the filtered, escaped name
/// from <see cref="Multiplayer.RoomNames.DisplayName"/>, and config values are looked up in the preset tables, never
/// pasted into markup. The markup is capped at <see cref="MarkupBudget"/> characters: when a combination is too
/// long, gradients use fewer colour steps first, then the least visible effects are dropped.
/// </summary>
internal static class NameStyle
{
    private const string Section = "Server Name Style";

    /// <summary>Most markup characters (tags only, not the name) a styled server name may carry.</summary>
    public const int MarkupBudget = 192;

    /// <summary>Most colour steps a gradient or rainbow gets.</summary>
    public const int MaxRuns = 8;

    internal static ConfigEntry<string> Highlight, Spacing, Font, Caps;
    internal static ConfigEntry<int> SizePercent;
    internal static ConfigEntry<bool> Italic, Underline;

    /// <summary>Background highlights. TMP draws &lt;mark&gt; as a translucent overlay, so every preset keeps alpha well below 100%.</summary>
    public static readonly (string Name, string Rgba)[] Highlights =
    {
        ("None", null),
        ("Amber", "FFC83D66"),
        ("Sky", "4FA8F766"),
        ("Rose", "FF6FB566"),
        ("Mint", "4CC38A66"),
        ("Coral", "FF5A4E66"),
        ("Violet", "9B6BFF66"),
        ("Smoke", "FFFFFF33"),
    };

    /// <summary>Above 100% the name is taller than the browser row and the game shows it blank, for every player.</summary>
    public static readonly int[] Sizes = { 80, 90, 100 };

    /// <summary>The game's own TextMeshPro fonts (menu name, asset name; null = the label's own NunitoSans).</summary>
    public static readonly (string Name, string Asset)[] Fonts =
    {
        ("Default", null),
        ("Heavy", "NunitoSans-Black SDF"),
        ("Extra bold", "NunitoSans-ExtraBold SDF"),
        ("Tall", "Oswald-Bold SDF"),
        ("Tall light", "Oswald-Regular SDF"),
        ("Condensed", "SofiaSansSemiCondensed-Bold SDF"),
        ("Bubbly", "Sniglet-Regular SDF"),
        ("Handwritten", "ShantellSans-Regular SDF"),
    };

    /// <summary>Letter case (TMP &lt;allcaps&gt; / &lt;smallcaps&gt;): capitals fill the row's height without being taller.</summary>
    public static readonly (string Name, string Tag)[] CaseStyles =
    {
        ("Normal", null),
        ("ALL CAPS", "allcaps"),
        ("Small caps", "smallcaps"),
    };

    /// <summary>Letter spacing in em (TMP &lt;cspace&gt;); the value is written as shown here.</summary>
    public static readonly (string Name, string Em)[] Spacings =
    {
        ("Tight", "-0.03em"),
        ("Normal", null),
        ("Wide", "0.08em"),
        ("Wider", "0.16em"),
    };

    /// <summary>One full look of the server name.</summary>
    internal readonly record struct Spec(string Colour, bool Bold, bool Italic, bool Underline, string Highlight, int Size, string Spacing,
        string Font = "Default", string Caps = "Normal");

    /// <summary>A composed name: the TMP markup plus what the menu preview needs.</summary>
    internal sealed class Styled
    {
        public string Text;        // TMP rich text, as published
        public string Visible;     // the name without markup
        public int Markup;         // characters of markup (Text.Length - Visible.Length)
        public Spec Applied;       // what survived the budget
        public int Runs;           // colour runs used (0 = default colour)
        public Color?[] CharColors; // per visible char; null = default text colour
        public string Dropped;     // what the budget took away ("" = nothing)
    }

    public static void Bind()
    {
        var f = ServerCreatorConfig.File;
        Highlight = f.Bind(Section, "Highlight", "None",
            "Background highlight behind your server's name: None, Amber, Sky, Rose, Mint, Coral, Violet, Smoke.");
        SizePercent = f.Bind(Section, "SizePercent", 100,
            new ConfigDescription("Text size of your server's name in the server browser, in percent. Above 100% the name no longer fits the row and shows blank for every player.",
                new AcceptableValueRange<int>(80, 100)));
        Italic = f.Bind(Section, "Italic", false, "Italic server name.");
        Underline = f.Bind(Section, "Underline", false, "Underlined server name.");
        Spacing = f.Bind(Section, "LetterSpacing", "Normal", "Letter spacing of your server's name: Tight, Normal, Wide, Wider.");
        Font = f.Bind(Section, "Font", "Default",
            "Font of your server's name (the game's own fonts): Default, Heavy, Extra bold, Tall, Tall light, Condensed, Bubbly, Handwritten.");
        Caps = f.Bind(Section, "LetterCase", "Normal", "Letter case of your server's name: Normal, ALL CAPS, Small caps.");
    }

    /// <summary>The look chosen in the menu (defaults if the settings could not be bound).</summary>
    public static Spec Current() => new(
        ServerCreatorConfig.RoomNameStyle?.Value ?? "None",
        ServerCreatorConfig.RoomNameBold?.Value ?? false,
        Italic?.Value ?? false,
        Underline?.Value ?? false,
        Highlight?.Value ?? "None",
        SnapSize(SizePercent?.Value ?? 100),
        Spacing?.Value ?? "Normal",
        Font?.Value ?? "Default",
        Caps?.Value ?? "Normal");

    public static int SnapSize(int pct)
    {
        int best = 100;
        foreach (int s in Sizes)
            if (Math.Abs(s - pct) < Math.Abs(best - pct)) best = s;
        return best;
    }

    public static string HighlightRgba(string name)
    {
        foreach (var h in Highlights) if (h.Name == name) return h.Rgba;
        return null;
    }

    public static string SpacingEm(string name)
    {
        foreach (var s in Spacings) if (s.Name == name) return s.Em;
        return null;
    }

    public static string FontAsset(string name)
    {
        foreach (var f in Fonts) if (f.Name == name) return f.Asset;
        return null;
    }

    public static string CaseTag(string name)
    {
        foreach (var c in CaseStyles) if (c.Name == name) return c.Tag;
        return null;
    }

    // The menu preview composes on every OnGUI event: remember the last result.
    private static string _lastName;
    private static Spec _lastSpec;
    private static Styled _last;

    public static Styled Compose(string name, Spec spec)
    {
        name ??= "";
        if (_last != null && _lastName == name && _lastSpec == spec) return _last;
        var result = ComposeUncached(name, spec);
        _lastName = name;
        _lastSpec = spec;
        _last = result;
        return result;
    }

    private static Styled ComposeUncached(string name, Spec spec)
    {
        int len = name.Length;
        int runs = len == 0 ? 0 : WantedRuns(spec.Colour, len);
        var applied = spec with
        {
            Highlight = HighlightRgba(spec.Highlight) == null ? "None" : spec.Highlight,
            Spacing = SpacingEm(spec.Spacing) == null ? "Normal" : spec.Spacing,
            Size = SnapSize(spec.Size),
            Font = FontAsset(spec.Font) == null ? "Default" : spec.Font,
            Caps = CaseTag(spec.Caps) == null ? "Normal" : spec.Caps,
        };
        var dropped = new List<string>();
        var built = Build(name, applied, runs);

        // Degrade until it fits: fewer gradient steps first (barely visible), then the subtlest effects.
        var ladder = new (string what, Func<(Spec s, int r), (Spec s, int r)> step)[]
        {
            ("some gradient steps", x => (x.s, Math.Min(x.r, 6))),
            ("some gradient steps", x => (x.s, Math.Min(x.r, 4))),
            ("letter spacing", x => (x.s with { Spacing = "Normal" }, x.r)),
            ("underline", x => (x.s with { Underline = false }, x.r)),
            ("italic", x => (x.s with { Italic = false }, x.r)),
            ("letter case", x => (x.s with { Caps = "Normal" }, x.r)),
            ("font", x => (x.s with { Font = "Default" }, x.r)),
            ("some gradient steps", x => (x.s, Math.Min(x.r, 3))),
            ("some gradient steps", x => (x.s, Math.Min(x.r, 2))),
            ("size", x => (x.s with { Size = 100 }, x.r)),
            ("highlight", x => (x.s with { Highlight = "None" }, x.r)),
            ("bold", x => (x.s with { Bold = false }, x.r)),
            ("the gradient (now one colour)", x => (x.s, Math.Min(x.r, 1))),
        };
        foreach (var (what, step) in ladder)
        {
            if (built.Text.Length - len <= MarkupBudget) break;
            var next = step((applied, runs));
            if (next.s == applied && next.r == runs) continue;
            applied = next.s;
            runs = next.r;
            built = Build(name, applied, runs);
            if (!dropped.Contains(what)) dropped.Add(what);
        }
        if (built.Text.Length - len > MarkupBudget) // cannot happen with the presets; never publish an over-long name
            built = (name, new Color?[len], 0);

        return new Styled
        {
            Text = built.Text,
            Visible = name,
            Markup = built.Text.Length - len,
            Applied = applied,
            Runs = built.Runs,
            CharColors = built.Colors,
            Dropped = string.Join(", ", dropped),
        };
    }

    /// <summary>Colour runs a style wants: 0 = default colour, 1 = solid, up to 8 for gradients (one per letter at most).</summary>
    private static int WantedRuns(string colour, int len)
    {
        if (colour == null || colour == "None") return 0;
        if (Multiplayer.RoomNames.Solid.ContainsKey(colour)) return 1;
        if (colour == "Rainbow" || Multiplayer.RoomNames.Gradients.ContainsKey(colour)) return Math.Min(MaxRuns, len);
        return 0;
    }

    private static (string Text, Color?[] Colors, int Runs) Build(string name, Spec s, int runs)
    {
        var colors = new Color?[name.Length];
        var sb = new StringBuilder(name.Length + MarkupBudget);
        var close = new Stack<string>();

        void Open(string tag, string closing)
        {
            sb.Append(tag);
            close.Push(closing);
        }

        if (s.Size != 100) Open($"<size={s.Size}%>", "</size>");
        // A font the list's labels can't find shows the whole tag as text, so only fonts Server Browser+ saw render.
        string font = FontAsset(s.Font);
        if (font != null && Core.Shared.ServerListCaps.FontRenders(font)) Open($"<font=\"{font}\">", "</font>");
        string caps = CaseTag(s.Caps);
        if (caps != null) Open($"<{caps}>", $"</{caps}>");
        string em = SpacingEm(s.Spacing);
        if (em != null) Open($"<cspace={em}>", "</cspace>");
        string rgba = HighlightRgba(s.Highlight);
        if (rgba != null) Open($"<mark=#{rgba}>", "</mark>");
        if (s.Bold) Open("<b>", "</b>");
        if (s.Italic) Open("<i>", "</i>");
        if (s.Underline) Open("<u>", "</u>");

        int used = AppendColoured(sb, name, s.Colour, runs, colors);

        while (close.Count > 0) sb.Append(close.Pop());
        return (sb.ToString(), colors, used);
    }

    /// <summary>Writes the visible text in up to <paramref name="runs"/> colour runs; returns the runs written.</summary>
    private static int AppendColoured(StringBuilder sb, string text, string colour, int runs, Color?[] colors)
    {
        if (runs <= 0 || text.Length == 0)
        {
            sb.Append(text);
            return 0;
        }
        if (Multiplayer.RoomNames.Solid.TryGetValue(colour, out string hex))
        {
            var c = ParseHex(hex);
            for (int i = 0; i < colors.Length; i++) colors[i] = c;
            sb.Append("<color=").Append(hex).Append('>').Append(text).Append("</color>");
            return 1;
        }

        bool rainbow = colour == "Rainbow";
        Multiplayer.RoomNames.Gradients.TryGetValue(colour ?? "", out var g);
        int runLen = Math.Max(1, (int)Math.Ceiling(text.Length / (double)runs));
        int count = (int)Math.Ceiling(text.Length / (double)runLen);
        int written = 0;
        for (int i = 0, r = 0; i < text.Length; r++)
        {
            int end = Math.Min(text.Length, i + runLen);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end++; // never split an emoji
            Color c = rainbow
                ? Color.HSVToRGB((i * 0.09f) % 1f, 0.7f, 1f)
                : Color.Lerp(g.a, g.b, count <= 1 ? 0f : r / (float)(count - 1));
            for (int k = i; k < end; k++) colors[k] = c;
            sb.Append("<color=#").Append(Hex2(c.r)).Append(Hex2(c.g)).Append(Hex2(c.b)).Append('>')
              .Append(text, i, end - i).Append("</color>");
            written++;
            i = end;
        }
        return written;
    }

    private static string Hex2(float v) => Mathf.RoundToInt(Mathf.Clamp01(v) * 255f).ToString("X2");

    /// <summary>"#RRGGBB" or "RRGGBBAA" (own parser: ColorUtility may be stripped from the IL2CPP build).</summary>
    public static Color ParseHex(string hex)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        if ((hex.Length != 6 && hex.Length != 8) ||
            !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint v))
            return Color.white;
        if (hex.Length == 6) v = (v << 8) | 0xFF;
        return new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }
}
