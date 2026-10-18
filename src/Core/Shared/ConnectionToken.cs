using System;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// The text of a Fusion connection token (UTF-8). The game writes "platform_userId"
/// (OverlandNetworkUtils.GetOverlandConnectionToken, format "{0}_{1}") and refuses joins without one; it also keys its
/// session bans on the whole token text and compares it for the developer badge.
/// <para><b>Suffix:</b> mods append markers after the game's part, each "|othp:&lt;name&gt;:&lt;value&gt;" (core's
/// network-prefab registry adds "|othp:np1:&lt;hash&gt;", see <see cref="NetPrefabPlan"/>). So the token reads
/// "platform_userId|othp:..." and anything that reads the player's id must use <see cref="PlatformPart"/>. The whole
/// token stays as it is for the game's own checks: a player's markers are the same on every join, so the game's ban
/// keys keep matching.</para>
/// Pure code (unit-tested).
/// </summary>
internal static class ConnectionToken
{
    public const char Separator = '|';
    public const string MarkerStart = "|othp:";

    /// <summary>The game's own part ("platform_userId"): everything before the first '|'.</summary>
    public static string PlatformPart(string token)
    {
        if (token == null) return null;
        int cut = token.IndexOf(Separator);
        return cut < 0 ? token : token.Substring(0, cut);
    }

    /// <summary>The value of marker <paramref name="name"/>, or null when the token has none.</summary>
    public static string Marker(string token, string name)
    {
        if (string.IsNullOrEmpty(token) || !ValidName(name)) return null;
        string start = MarkerStart + name + ":";
        int at = token.IndexOf(start, StringComparison.Ordinal);
        if (at < 0) return null;
        int from = at + start.Length;
        int end = token.IndexOf(Separator, from);
        return end < 0 ? token.Substring(from) : token.Substring(from, end - from);
    }

    /// <summary>The token with marker <paramref name="name"/> set to <paramref name="value"/> (an older one of the same
    /// name is replaced; other markers are kept). Idempotent.</summary>
    public static string WithMarker(string token, string name, string value)
    {
        if (!ValidName(name)) throw new ArgumentException("marker names are letters, digits, '.' and '_'", nameof(name));
        if (value == null || value.IndexOf(Separator) >= 0) throw new ArgumentException("marker values can't contain '|'", nameof(value));
        return (WithoutMarker(token, name) ?? "") + MarkerStart + name + ":" + value;
    }

    /// <summary>The token without marker <paramref name="name"/>.</summary>
    public static string WithoutMarker(string token, string name)
    {
        if (token == null || !ValidName(name)) return token;
        string start = MarkerStart + name + ":";
        int at = token.IndexOf(start, StringComparison.Ordinal);
        if (at < 0) return token;
        int end = token.IndexOf(Separator, at + 1);
        return end < 0 ? token.Substring(0, at) : token.Substring(0, at) + token.Substring(end);
    }

    private static bool ValidName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (char c in name)
            if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '.' || c == '_'))) return false;
        return true;
    }
}
