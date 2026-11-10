using System;
using System.Collections.Generic;
#if MELON
using Il2CppFunCore.Style;
using Il2CppTMPro;
#else
using FunCore.Style;
using TMPro;
#endif
using Il2CppInterop.Runtime;
using UnityEngine;
using OverTheHillPlus.UI.Native;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.HudKit;

/// <summary>The game's HUD typefaces, as its style sheet names them.</summary>
internal enum HudFont
{
    /// <summary>NunitoSans: the notifications and hints.</summary>
    Regular,
    /// <summary>NunitoSans Bold: small titles and messages.</summary>
    Bold,
    /// <summary>NunitoSans ExtraBold: the clock, gears and other readouts.</summary>
    Heavy,
    /// <summary>NunitoSans Black: big titles.</summary>
    Black,
    /// <summary>Sofia Sans Semi Condensed Bold: the key hints.</summary>
    Condensed,
}

/// <summary>
/// The look of the game's own HUD, for the mods' panels: the colours of its style sheet and its fonts. Read from the
/// sheet (FunStyleAsset) without scanning the scene, so it costs nothing while driving; the colours below are the sheet's
/// values and stay in use if a name is missing. The panel recipe is the game's too: black at 40% with 5 unit corners.
/// </summary>
internal static class HudLook
{
    // ---- colours (style sheet name in brackets) ----

    /// <summary>The cream of all HUD text (Typography/White).</summary>
    public static Color Cream = Theme.Hex(0xFFF9EE);
    /// <summary>A panel's small title: the cream at half strength (UIUnselectedHeader).</summary>
    public static Color Grey = new(1f, 0.976f, 0.933f, 0.55f);
    /// <summary>A panel (OpacityBackground: #00000066).</summary>
    public static Color Panel = new(0f, 0f, 0f, 0.4f);
    /// <summary>A panel with words to read on it: the same black, stronger.</summary>
    public static Color PanelStrong = new(0f, 0f, 0f, 0.62f);
    /// <summary>The track of a bar: the cream, faint (AreaCompletionBar/Incomplete, dimmed).</summary>
    public static Color Track = new(1f, 0.976f, 0.933f, 0.18f);
    /// <summary>The game's yellow (Typography/Yellow): what is selected or wants a look.</summary>
    public static Color Yellow = Theme.Hex(0xFFD865);
    /// <summary>Text on the yellow or cream (UI/SelectedItemText).</summary>
    public static Color Ink = Theme.Hex(0x302E2E);
    /// <summary>A good state (AreaCompletionBar/Complete).</summary>
    public static Color Green = Theme.Hex(0x29BD5A);
    /// <summary>A warning (RepairUI/DamagedVehicle).</summary>
    public static Color Amber = Theme.Hex(0xE6D028);
    /// <summary>Danger (FloodUI/Red, also the RPM's red line).</summary>
    public static Color Red = Theme.Hex(0xE23720);
    /// <summary>The RPM bar's blue (FloodUI/Blue).</summary>
    public static Color Blue = Theme.Hex(0x2875CC);

    /// <summary>Corners of a panel, in HUD units.</summary>
    public const int PanelRadius = 5;

    /// <summary>
    /// The game's colour for one of the old overlay palette's (Theme): the mods hand those to fills and styles, and on the
    /// HUD layer they should come out in the game's own cream, yellow, green and red. Any other colour stays as it is.
    /// </summary>
    internal static Color Game(Color c)
    {
        if (Rgb(c, Theme.Text)) return new Color(Cream.r, Cream.g, Cream.b, c.a);
        if (Rgb(c, Theme.Muted)) return new Color(Cream.r, Cream.g, Cream.b, c.a * 0.55f);
        if (Rgb(c, Theme.Accent)) return new Color(Yellow.r, Yellow.g, Yellow.b, c.a);
        if (Rgb(c, Theme.Good)) return new Color(Green.r, Green.g, Green.b, c.a);
        if (Rgb(c, Theme.Bad)) return new Color(Red.r, Red.g, Red.b, c.a);
        if (Rgb(c, Theme.Off) || Rgb(c, Theme.Line)) return new Color(Track.r, Track.g, Track.b, c.a * Track.a);
        if (Rgb(c, Theme.Bg)) return new Color(0f, 0f, 0f, HudRules.PanelAlpha(c.a));
        return c;
    }

    private static bool Rgb(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b;

    /// <summary>A panel's black for a mod's Opacity setting (its default 0.82 is the game's 40%).</summary>
    internal static Color PanelAt(float opacity) => new(0f, 0f, 0f, HudRules.PanelAlpha(opacity));

    // ---- fonts ----

    private static readonly TMP_FontAsset[] Fonts = new TMP_FontAsset[5];

    /// <summary>Bumped when fonts change, so the canvas re-applies them to its pooled texts.</summary>
    internal static int Version { get; private set; }

    /// <summary>What was found, for the log and the MODS page.</summary>
    internal static string Summary { get; private set; } = "not read yet";

    private static float _nextTry, _nextAlive;
    private static bool _ready, _complete;
    private static int _tries;

    internal static TMP_FontAsset Font(HudFont f) => Fonts[(int)f];

    /// <summary>True when fonts are there to draw with; reads them if not (at most every few seconds).</summary>
    internal static bool Ensure()
    {
        float now = Time.unscaledTime;
        if (_ready)
        {
            if (now < _nextAlive) return true;
            _nextAlive = now + 5f;
            if (FontsAlive())
            {
                // The sheet wasn't there yet (early in the main menu): stand-in fonts, so look again a few times.
                if (!_complete && _tries < 5) Read();
                return true;
            }
            _ready = false; // an unloaded bundle took a font with it
            _nextTry = 0f;
        }
        if (now < _nextTry) return false;
        _nextTry = now + 3f;
        Read();
        return _ready;
    }

    internal static void Reset()
    {
        _ready = _complete = false;
        _tries = 0;
        _nextTry = 0f;
        for (int i = 0; i < Fonts.Length; i++) Fonts[i] = null;
        Version++;
    }

    private static bool Alive(Object o)
    {
        try { return o is not null && (bool)o; }
        catch (Exception ex) { DebugLog.Error("core", ex, "HudLook.Alive"); return false; }
    }

    private static bool FontsAlive()
    {
        for (int i = 0; i < Fonts.Length; i++) if (!Alive(Fonts[i])) return false;
        return true;
    }

    private static void Read()
    {
        _tries++;
        var byName = new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
        int colours = 0;
        try { ReadSheet(byName, ref colours); }
        catch (Exception e) { DebugLog.Error("core", e, "HudLook.ReadSheet"); }

        var regular = Named(byName, "NunitoSans SDF");
        _complete = Alive(regular);
        var bold = Named(byName, "NunitoSans-Bold SDF");
        var heavy = Named(byName, "NunitoSans-ExtraBold SDF");
        var black = Named(byName, "NunitoSans-Black SDF");
        var condensed = Named(byName, "SofiaSansSemiCondensed-Bold SDF");

        // The HUD's own texts stand in when the sheet isn't there: the clock is the readout font.
        if (!Alive(heavy) || !Alive(regular))
        {
            try
            {
                var hud = HUDManager.Instance;
                var clock = hud != null ? hud.timeText : null;
                if (clock != null)
                {
                    var f = clock.font;
                    if (!Alive(heavy)) heavy = f;
                    if (!Alive(regular)) regular = f;
                }
            }
            catch (Exception e) { DebugLog.Error("core", e, "HudLook.Read"); }
        }
        if (!Alive(regular)) regular = Alive(NativeLook.Body) ? NativeLook.Body : null;
        if (!Alive(regular))
        {
            try { var d = TMP_Settings.defaultFontAsset; if (Alive(d)) regular = d; }
            catch (Exception e) { DebugLog.Error("core", e, "HudLook.Read"); }
        }
        if (!Alive(regular))
        {
            Summary = "no game font yet";
            return;
        }
        if (!Alive(bold)) bold = Alive(heavy) ? heavy : regular;
        if (!Alive(heavy)) heavy = bold;
        if (!Alive(black)) black = heavy;
        if (!Alive(condensed)) condensed = bold;

        Fonts[(int)HudFont.Regular] = regular;
        Fonts[(int)HudFont.Bold] = bold;
        Fonts[(int)HudFont.Heavy] = heavy;
        Fonts[(int)HudFont.Black] = black;
        Fonts[(int)HudFont.Condensed] = condensed;
        _ready = true;
        _nextAlive = Time.unscaledTime + 5f;
        Version++;
        Summary = $"fonts {Name(regular)}, {Name(bold)}, {Name(heavy)}, {Name(black)}, {Name(condensed)}; {colours} colours from the style sheet";
    }

    private static TMP_FontAsset Named(Dictionary<string, TMP_FontAsset> byName, string name) =>
        byName.TryGetValue(name, out var f) ? f : null;

    private static string Name(Object o)
    {
        try { return o == null ? "-" : o.name; }
        catch (Exception ex) { DebugLog.Error("core", ex, "HudLook.Name"); return "?"; }
    }

    /// <summary>Every text style's font by the font's name, and the HUD's colours by their style name.</summary>
    private static void ReadSheet(Dictionary<string, TMP_FontAsset> fonts, ref int colours)
    {
        var asset = FunStyleAsset.Instance;
        if (asset == null)
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<FunStyleAsset>()))
            {
                asset = o.TryCast<FunStyleAsset>();
                if (asset != null) break;
            }
        if (asset == null) return;
        var sheet = asset.GetActiveStyleSheet() ?? asset.GetStyleSheetDefault();
        if (sheet == null) return;
        for (int type = 0; type < 16; type++)
        {
            Il2CppSystem.Collections.Generic.List<string> ids;
            try { ids = sheet.GetIdListOfType(type); }
            catch (Exception ex) { DebugLog.Error("core", ex, "HudLook.ReadSheet"); continue; }
            if (ids == null) continue;
            for (int i = 0; i < ids.Count; i++)
            {
                try
                {
                    var el = sheet.GetStyleElement(ids[i]);
                    if (el == null) continue;
                    var c = el.TryCast<FunStyleColorData>();
                    if (c != null)
                    {
                        if (Apply(c.styleName, c.color)) colours++;
                        continue;
                    }
                    var t = el.TryCast<FunStyleTextData>();
                    if (t == null) continue;
                    var f = t.fontAsset;
                    if (Alive(f)) fonts[f.name] = f;
                }
                catch (Exception ex) { DebugLog.Error("core", ex, "HudLook.ReadSheet"); /* one bad element */ }
            }
        }
    }

    private static bool Apply(string name, Color c)
    {
        switch (name)
        {
            case "Typography/White": Cream = c; Grey = new Color(c.r, c.g, c.b, 0.55f); Track = new Color(c.r, c.g, c.b, 0.18f); return true;
            case "Typography/Yellow": Yellow = c; return true;
            case "UI/SelectedItemText": Ink = c; return true;
            case "AreaCompletionBar/Complete": Green = c; return true;
            case "RepairUI/DamagedVehicle": Amber = c; return true;
            case "FloodUI/Red": Red = c; return true;
            case "FloodUI/Blue": Blue = c; return true;
            default: return false;
        }
    }
}
