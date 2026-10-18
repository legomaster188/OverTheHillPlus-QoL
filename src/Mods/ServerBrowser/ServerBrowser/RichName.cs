using System;
using System.Text;
using System.Text.RegularExpressions;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>
/// A server's name with its colours, for the browser rows. Since its 2026-10-16 update the game strips every rich-text
/// tag from server names (OverlandSessionInfo: Regex.Replace(name, "&lt;.*?&gt;", "")), so names styled with Server
/// Creator+ show plain. The raw Fusion session name still carries them ("&lt;12 hex&gt;_&lt;display name&gt;"); this keeps only
/// harmless tags (colour, bold, italic, underline, strike, mark, alpha, a small character spacing), drops the rest
/// (size, sprites, links, line breaks, ...) and closes whatever it left open. Pure (unit-tested).
/// </summary>
internal static class RichName
{
    public const int MaxLength = 400;
    private static readonly Regex Prefix = new("^[0-9a-fA-F]{12}_", RegexOptions.CultureInvariant);
    private static readonly Regex Tag = new("<(/?)([a-zA-Z]+)(=[^<>]{0,40})?>", RegexOptions.CultureInvariant);
    private static readonly Regex Color = new("^=(#[0-9a-fA-F]{3,8}|\"#[0-9a-fA-F]{3,8}\"|[a-z]{3,10})$", RegexOptions.CultureInvariant);
    private static readonly Regex Space = new("^=-?0?\\.\\d{1,3}em$|^=-?\\d{1,2}(px)?$", RegexOptions.CultureInvariant);

    /// <summary>The display part of a raw session name with safe tags kept; null when there is nothing to restore
    /// (no tags, no prefix, too long).</summary>
    public static string FromSessionName(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength * 2) return null;
        raw = Unescape(raw); // hosts may publish tags escaped (they survive the game's tag stripping)
        if (raw.Length > MaxLength || raw.IndexOf('<') < 0) return null;
        string display = Prefix.IsMatch(raw) ? raw.Substring(13) : raw;
        return Sanitize(display);
    }

    public static string Sanitize(string s)
    {
        if (s == null) return null;
        var sb = new StringBuilder(s.Length);
        var open = new System.Collections.Generic.List<string>();
        int last = 0;
        foreach (Match m in Tag.Matches(s))
        {
            sb.Append(Plain(s.Substring(last, m.Index - last)));
            last = m.Index + m.Length;
            bool close = m.Groups[1].Value == "/";
            string name = m.Groups[2].Value.ToLowerInvariant();
            string arg = m.Groups[3].Value;
            if (!Allowed(name, arg, close)) continue;
            if (close)
            {
                int at = open.LastIndexOf(name);
                if (at < 0) continue;
                // close anything opened after it first, so the nesting stays valid
                for (int i = open.Count - 1; i >= at; i--) sb.Append("</").Append(open[i]).Append('>');
                for (int i = open.Count - 1; i > at; i--) sb.Append('<').Append(open[i]).Append('>'); // reopen the inner ones
                open.RemoveAt(at);
            }
            else
            {
                sb.Append('<').Append(name).Append(arg).Append('>');
                open.Add(name);
            }
        }
        sb.Append(Plain(s.Substring(last)));
        for (int i = open.Count - 1; i >= 0; i--) sb.Append("</").Append(open[i]).Append('>');
        return sb.ToString();
    }

    private static bool Allowed(string name, string arg, bool close)
    {
        switch (name)
        {
            case "b": case "i": case "u": case "s":
                return arg.Length == 0;
            case "color": case "mark":
                return close ? arg.Length == 0 : Color.IsMatch(arg);
            case "alpha":
                return close ? arg.Length == 0 : arg.Length > 1 && arg.Length <= 4 && arg[1] == '#';
            case "cspace":
                return close ? arg.Length == 0 : Space.IsMatch(arg);
            default:
                return false;
        }
    }

    /// <summary>Text between tags: no stray '<' (would start a tag), no line breaks, and no backslash (TextMeshPro would
    /// read escape sequences in it).</summary>
    private static string Plain(string t) => t.Replace("<", "").Replace("\n", " ").Replace("\r", "").Replace('\\', '⧵');

    /// <summary>"\\u003C" / "\\u003E" (any case) back to '&lt;' / '&gt;'.</summary>
    public static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
        return s.Replace("\\u003C", "<").Replace("\\u003c", "<").Replace("\\u003E", ">").Replace("\\u003e", ">");
    }
}
