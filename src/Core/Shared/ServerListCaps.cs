using System;
using System.IO;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// What the game's server list can render, as found by Server Browser+ on this PC (BepInEx/config/oth.serverlist.caps):
/// whether its name labels turn the escape "\u003C" into "&lt;" (TextMeshPro's parseCtrlCharacters) with rich text on.
/// The game strips "&lt;...&gt;" tags from server names since its 2026-10-16 update, but that stripping runs before the
/// label parses escapes, so a host can publish its colours as "\u003Ccolor=...\u003E" and every player, with or
/// without mods, sees them. Server Creator+ does that only when this file says the list renders it.
/// </summary>
public static class ServerListCaps
{
    private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "oth.serverlist.caps");

    /// <summary>True / false once Server Browser+ has looked at a list row on this PC; null when never seen.</summary>
    public static bool? EscapedTagsRender
    {
        get
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                foreach (var line in File.ReadAllLines(FilePath))
                    if (line.StartsWith("escapes=", StringComparison.Ordinal)) return line.EndsWith("true", StringComparison.Ordinal);
            }
            catch (Exception e) { DebugLog.Error("core", e, "ServerListCaps.get"); }
            return null;
        }
    }

    public static void Save(bool escapedTagsRender, string details) => Save(escapedTagsRender, details, null, null);

    /// <summary>Also records the fonts a &lt;font="..."&gt; tag finds in the list's labels (an unknown font shows the
    /// tag as text) and whether &lt;sprite&gt; finds a sprite asset.</summary>
    public static void Save(bool escapedTagsRender, string details, System.Collections.Generic.IEnumerable<string> fonts, bool? sprites)
    {
        try
        {
            string text = "escapes=" + (escapedTagsRender ? "true" : "false") + "\n";
            if (fonts != null) text += "fonts=" + string.Join("|", fonts) + "\n";
            if (sprites.HasValue) text += "sprites=" + (sprites.Value ? "true" : "false") + "\n";
            AtomicFile.WriteAllText(FilePath, text + "# " + details + "\n");
        }
        catch (Exception e) { DebugLog.Error("core", e, "ServerListCaps.Save"); }
    }

    /// <summary>True only if Server Browser+ saw the list's labels render <paramref name="fontAsset"/> by name.</summary>
    public static bool FontRenders(string fontAsset)
    {
        if (string.IsNullOrEmpty(fontAsset)) return false;
        try
        {
            if (!File.Exists(FilePath)) return false;
            foreach (var line in File.ReadAllLines(FilePath))
                if (line.StartsWith("fonts=", StringComparison.Ordinal))
                    return Array.IndexOf(line.Substring(6).Split('|'), fontAsset) >= 0;
        }
        catch (Exception e) { DebugLog.Error("core", e, "ServerListCaps.FontRenders"); }
        return false;
    }

    /// <summary>Rich-text tags written so that the game's tag stripping leaves them and TextMeshPro still reads them.</summary>
    public static string EscapeTags(string markup) =>
        string.IsNullOrEmpty(markup) ? markup : markup.Replace("<", "\\u003C").Replace(">", "\\u003E");

    /// <summary>The escaped form back to plain tags (for our own renderer).</summary>
    public static string UnescapeTags(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text;
        return text.Replace("\\u003C", "<").Replace("\\u003c", "<").Replace("\\u003E", ">").Replace("\\u003e", ">");
    }
}
