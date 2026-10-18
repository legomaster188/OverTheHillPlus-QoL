using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OverTheHillPlus.Features.Public.Filter;

namespace OverTheHillPlus.Features.Public.ServerPlayers;

/// <summary>
/// The custom session property modded hosts use to publish who is in their server, and its compact format.
///
/// Photon limits (from the decompiled Fusion.Realtime RealtimeClientExtensions.UpdateRoomProperties): at most 10
/// custom session properties, at most 500 bytes for all of them serialized together, and only keys that existed
/// when the session started can be updated later. The game itself uses three short ones (AreaInfoName,
/// GameVersion, RoomProperties, roughly 100 bytes), so this value is capped at <see cref="MaxValueBytes"/>.
/// Vanilla clients ignore keys they don't know (OverlandSessionInfo only looks up its own three).
///
/// Format: "p1" followed by "|name" per player, the host's name prefixed with '*', and "|+N" at the end when N
/// names did not fit. "p1!" means the host chose not to share names. Names are stripped of rich text, cleaned of
/// control/format characters and '|', shortened, and passed through the public name filter before publishing;
/// clients clean and filter what they receive again, since another host's mod may not be this one.
///
/// In core because more than one mod uses it: Server Browser+ reads the names (friends in servers, list updates),
/// the server's player list publishes and shows them.
/// </summary>
internal static class PlayerNamesProperty
{
    /// <summary>Session property key (short: keys count toward Photon's 500-byte budget).</summary>
    public const string Key = "othp.pl";
    public const string Version = "p1";
    public const string HiddenValue = "p1!";
    public const int MaxValueBytes = 300;
    public const int MaxNameChars = 24;
    public const int MaxNames = 64;
    private const string BlockedName = "Player";

    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    internal sealed class Names
    {
        /// <summary>The property is there but the host keeps names private.</summary>
        public bool Hidden;
        public readonly List<string> List = new();
        public int HostIndex = -1;
        /// <summary>Players the host could not fit into the property.</summary>
        public int More;
        public int Total => List.Count + More;
    }

    /// <summary>A name made safe to publish or show: plain text, one line, short, and allowed by the name filter.</summary>
    public static string Clean(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string s = raw.IndexOf('<') >= 0 ? Tags.Replace(raw, "") : raw;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            var cat = char.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.Format) continue; // zero-width characters could hide words from the filter
            if (char.IsControl(c) || c == '|' || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator)
            {
                sb.Append(' ');
                continue;
            }
            // Leftover angle brackets (an unclosed tag) become look-alikes so nothing renders as markup.
            sb.Append(c == '<' ? '‹' : c == '>' ? '›' : c);
        }
        s = Spaces.Replace(sb.ToString(), " ").Trim().TrimStart('*', '+', ' ');
        if (s.Length > MaxNameChars)
        {
            s = s.Substring(0, MaxNameChars);
            if (char.IsHighSurrogate(s[s.Length - 1])) s = s.Substring(0, s.Length - 1);
            s = s.TrimEnd();
        }
        if (s.Length == 0) return BlockedName;
        return NameFilter.IsBlockedStrict(s, out _) ? BlockedName : s; // other people's names: never lenient
    }

    /// <summary>Builds the property value from (name, isHost) pairs, cleaning every name and keeping to the byte cap.</summary>
    public static string Encode(IReadOnlyList<(string name, bool host)> players, int maxBytes = MaxValueBytes)
    {
        maxBytes = Math.Max(16, Math.Min(maxBytes, MaxValueBytes));
        var sb = new StringBuilder(Version, 128);
        int bytes = Encoding.UTF8.GetByteCount(Version);
        const int reserve = 6; // room for "|+NNN"
        int written = 0;
        for (int i = 0; i < players.Count && written < MaxNames; i++)
        {
            string token = (players[i].host ? "*" : "") + Clean(players[i].name);
            int tb = Encoding.UTF8.GetByteCount(token) + 1;
            if (bytes + tb > maxBytes - reserve) break;
            sb.Append('|').Append(token);
            bytes += tb;
            written++;
        }
        int more = players.Count - written;
        if (more > 0) sb.Append("|+").Append(Math.Min(more, 999));
        return sb.ToString();
    }

    /// <summary>Parses a received value. Null when it isn't in a format this version understands.</summary>
    public static Names Decode(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var result = new Names();
        if (value == HiddenValue) { result.Hidden = true; return result; }
        if (value.Length > 2048) value = value.Substring(0, 2048); // a stranger's host could send anything
        var parts = value.Split('|');
        if (parts[0] != Version) return null;
        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i];
            if (i == parts.Length - 1 && p.Length > 1 && p[0] == '+' && int.TryParse(p.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int more))
            {
                result.More += Math.Min(more, 999);
                continue;
            }
            if (result.List.Count >= MaxNames) { result.More++; continue; }
            bool host = p.StartsWith("*", StringComparison.Ordinal);
            if (host && result.HostIndex < 0) result.HostIndex = result.List.Count;
            result.List.Add(Clean(host ? p.Substring(1) : p));
        }
        return result;
    }

    // ---------------------------------------------------------------- reading sessions (browser side)

    private sealed class CacheEntry
    {
        public bool Present;
        public Names Parsed;
        public float At;
    }

    private static readonly Dictionary<string, CacheEntry> Cache = new();
    private static readonly Dictionary<string, Names> ParsedByValue = new();
    private static bool _loggedReadError;

    /// <summary>
    /// The names a session publishes. <paramref name="present"/> is false when the host doesn't publish them (not
    /// modded, or an older mod version). Cached per session for a second: the browser asks every frame.
    /// </summary>
    public static Names For(Fusion.SessionInfo session, float now, out bool present)
    {
        present = false;
        if (session == null) return null;
        string id;
        try { id = session.Name ?? ""; } catch (Exception ex) { DebugLog.Error("core", ex, "PlayerNamesProperty.For"); return null; }
        if (Cache.TryGetValue(id, out var c) && now - c.At < 1f && now >= c.At)
        {
            present = c.Present;
            return c.Parsed;
        }
        if (Cache.Count > 256) Cache.Clear();
        c ??= new CacheEntry();
        c.At = now;
        c.Present = false;
        c.Parsed = null;
        try
        {
            string raw = ReadString(session, Key, out bool found);
            c.Present = found;
            if (found)
            {
                // Parsing cleans and filters every name: do it once per distinct value, not once a second per server.
                if (!ParsedByValue.TryGetValue(raw ?? "", out var parsed))
                {
                    parsed = Decode(raw);
                    if (ParsedByValue.Count > 256) ParsedByValue.Clear();
                    ParsedByValue[raw ?? ""] = parsed;
                }
                c.Parsed = parsed;
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "PlayerNamesProperty.For");
            if (!_loggedReadError)
            {
                _loggedReadError = true;
                Core.CorePlugin.Logger?.LogWarning("[ServerPlayers] reading a session's player names failed (further errors suppressed): " + e.Message);
            }
        }
        Cache[id] = c;
        present = c.Present;
        return c.Parsed;
    }

    /// <summary>A string session property, or null. <paramref name="found"/> tells a missing key from an empty value.</summary>
    public static string ReadString(Fusion.SessionInfo session, string key, out bool found) => GameState.SessionString(session, key, out found);

    /// <summary>The game's RoomProperties flags: bit 0 = mods allowed, bit 1 = private server (decompiled GetSessionProperties).</summary>
    public static bool IsPrivateFlags(Fusion.SessionProperty roomProperties)
    {
        try { return roomProperties != null && roomProperties.IsInt && (((int)roomProperties) & 2) != 0; }
        catch (Exception ex) { DebugLog.Error("core", ex, "PlayerNamesProperty.IsPrivateFlags"); return false; }
    }
}
