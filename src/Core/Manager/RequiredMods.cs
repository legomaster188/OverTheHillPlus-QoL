using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Core.Manager;

/// <summary>What is wrong with one mod a server needs, seen from this game.</summary>
internal enum RequiredIssueKind
{
    /// <summary>Not installed here.</summary>
    Missing,
    /// <summary>Installed but switched off (or switched on since the game started: it runs after a restart).</summary>
    Off,
    /// <summary>Same major version, another minor one: a warning, joining works.</summary>
    VersionMinor,
    /// <summary>Another major version: the two can't talk, joining is blocked.</summary>
    VersionMajor,
    /// <summary>A mod with network prefabs (cars, trailers) runs here but not on the host, which runs others: the host
    /// refuses the join until it is switched off (and the game restarted).</summary>
    PrefabExtra,
    /// <summary>The host runs this prefab mod and it is on here, but its prefabs did not start here (see its MODS
    /// page): the host refuses the join.</summary>
    PrefabNotReady,
    /// <summary>The same prefab mods on both sides, but another build of them (or of the game): the host refuses the
    /// join until both have the same version.</summary>
    PrefabBuild,
}

/// <summary>
/// Where this game is: no session (no runner at all), single-player, hosting, a guest in someone's session, or
/// <see cref="Connecting"/>: a runner exists but isn't running a session yet (joining, or the browser's lobby
/// connection), so nothing is known about the session.
/// </summary>
internal enum SessionRole { None, Solo, Host, Guest, Connecting }

/// <summary>One mod a session advertises: its id and major version (and minor, when the host sent one).</summary>
internal sealed class RequiredMod
{
    public string Id;
    public int Major;
    public int Minor;
    /// <summary>The host sent the minor version (prefab mods, and hosts before the major-only format).</summary>
    public bool MinorKnown = true;
    public string Version => Major.ToString(CultureInfo.InvariantCulture) +
                             (MinorKnown ? "." + Minor.ToString(CultureInfo.InvariantCulture) : "");
}

/// <summary>One mod for the advert (see <see cref="RequiredMods.AdvertisedMods"/>).</summary>
internal readonly struct AdvertMod
{
    public readonly string Id;
    public readonly string Version;
    /// <summary>It owns network prefabs: listed first, with its minor version (the join gate's hash changes with it).</summary>
    public readonly bool Prefab;

    public AdvertMod(string id, string version, bool prefab = false)
    {
        Id = id;
        Version = version;
        Prefab = prefab;
    }
}

/// <summary>The decoded othp.req value: the mods listed, plus how many more the host could not fit.</summary>
internal sealed class RequiredList
{
    public readonly List<RequiredMod> Mods = new();
    /// <summary>Server mods the host runs that did not fit into the property.</summary>
    public int More;
    /// <summary>The prefab mods the host runs, as a mask over NetPrefabPlan.OwnerMods; -1 when the host didn't say
    /// (a host without prefab mods, or one older than the field).</summary>
    public int PrefabMask = -1;
    /// <summary>The host's join marker (16 hex digits), or null.</summary>
    public string PrefabHash;
    public bool HasPrefabInfo => PrefabMask >= 0;

    public RequiredMod Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var m in Mods)
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }
}

internal sealed class RequiredModIssue
{
    public string Id;
    public RequiredIssueKind Kind;
    /// <summary>The server's version (major.minor).</summary>
    public string Required;
    /// <summary>The version installed here (major.minor, or the raw text when unreadable); null when missing.</summary>
    public string Installed;
    /// <summary>The mod owns network prefabs and the host's join gate checks it: joining without it isn't possible.</summary>
    public bool Prefab;
    /// <summary>Joining as things are is refused (another major version, or the host's prefab check).</summary>
    public bool Blocks => Kind == RequiredIssueKind.VersionMajor || (Prefab && Kind != RequiredIssueKind.VersionMinor);
}

/// <summary>What the join prompt can do about a server's issues (see <see cref="RequiredMods.PlanJoin"/>).</summary>
internal sealed class JoinPlan
{
    /// <summary>Missing, or another major version, and listed for download at the version the server needs.</summary>
    public readonly List<string> Install = new();
    /// <summary>Installed at a usable version but switched off.</summary>
    public readonly List<string> SwitchOn = new();
    /// <summary>Missing and not listed at the needed major version (the player can still join without it).</summary>
    public readonly List<string> MissingUnlisted = new();
    /// <summary>Another major version, and no download fixes it: joining stays blocked.</summary>
    public readonly List<string> Blocking = new();
    /// <summary>Prefab mods running here that the host doesn't run: switching them off (and restarting) lets you join.</summary>
    public readonly List<string> SwitchOff = new();
    /// <summary>Prefab mods the host's join gate would refuse and nothing here fixes (not listed for download, didn't
    /// start here, another build): joining stays blocked.</summary>
    public readonly List<string> PrefabBlocking = new();
    /// <summary>Some issue is a prefab mod's: the host refuses the join until it is fixed, so "join without them" is
    /// never offered.</summary>
    public bool PrefabIssues;
    /// <summary>Only minor version differences.</summary>
    public bool WarningsOnly;

    /// <summary>Joining as things are is allowed (nothing blocks, and no prefab mod differs from the host's).</summary>
    public bool CanJoinNow => Blocking.Count == 0 && PrefabBlocking.Count == 0 && !PrefabIssues;
    /// <summary>"Install &amp; join" / "Switch on &amp; restart" can fix something, and the rejoin after it isn't blocked.</summary>
    public bool CanFix => Blocking.Count == 0 && PrefabBlocking.Count == 0 && Install.Count + SwitchOn.Count + SwitchOff.Count > 0;
}

/// <summary>
/// Mod types and the mods a server needs (pure logic: no game types, covered by RequiredModsTests).
///
/// A host running <see cref="ModSide.Server"/> mods advertises them in one session property, <see cref="Key"/>.
/// Photon's limits are the ones ServerPlayers documents (PlayerNamesProperty): at most 10 custom properties and 500
/// bytes for all of them together, and only keys present at StartGame can be updated later. So the value is short,
/// written once at StartGame (a mod's switch only changes at a restart, so it never needs updating), and the player
/// names publisher gives up the bytes it takes.
///
/// Format: "r1", then "|np&lt;mask&gt;:&lt;hash&gt;" when the host runs network prefab mods (the mask over
/// NetPrefabPlan.OwnerMods in hex, and its join marker), then "|id@major" per mod ("|id@major.minor" for prefab mods,
/// whose join hash changes with the minor version), and "|+N" at the end when N more did not fit into
/// <see cref="MaxValueBytes"/>. Example: "r1|np1:0123456789abcdef|vera@1.1|chat@1|biglobbies@1". Order: prefab mods
/// first, then ours, then other creators' (<see cref="AdvertisedMods"/>), and a mod that doesn't fit doesn't stop
/// smaller ones after it. Readers of the first format ("id@major.minor" only) read this one too: they skip the np
/// token (no '@') and read "id@1" as 1.0. Vanilla clients ignore the key.
/// <para>Compact token: "|k1&lt;entries&gt;", four characters per mod of ours: a two-character code (the mod's place
/// in <see cref="CodeTable"/> in base 36; the table only ever grows at its end, so a code never changes meaning), the major
/// version and the minor version, each one base-36 character (the minor is '_' when only the major is sent, as for mods
/// without network prefabs). Example: "r1|np1:0123456789abcdef|k100110041_|someone.weather@0". A mod not in the table, or
/// with a version part above 35, is sent as "id@version" as before. Older clients read only "id@version" tokens and the np
/// token: they skip the k token (no '@') and so see the other creators' mods only, which degrades to "no requirements
/// known" for ours instead of failing; the np token still tells them the prefab mods. A later token version ("k3...") is
/// skipped by this reader, and codes it doesn't know count as "more mods".</para>
/// <para>Mask token: "|k2&lt;mask&gt;[.&lt;entries&gt;]" replaces "k1" once the "k1" token is longer than
/// <c>MaskMinK1Chars</c> and "k2" is shorter: bit i of the mask (base64url, six bits per character, lowest bit first) is
/// <see cref="CodeTable"/> mod i at major 1 (and minor 0 for a prefab mod), and mods at another version follow the dot as
/// "k1" entries. Clients that only know "k1" skip it (no '@') and see the other creators' mods only, as they did before
/// "k1" existed.</para>
/// <para>Budget: othp.req and the player names (othp.pl) share the 300 value bytes the names property was given
/// (PlayerNamesProperty.MaxValueBytes; the names publisher gives up what othp.req takes, see RequiredModsSession.BytesIn).
/// Every Server mod should be listed (a guest runs only the listed ones), so othp.req may take up to
/// <see cref="MaxValueBytes"/> of them; the names keep the rest. Our own mods go into one compact token (below), so all
/// our public Server mods take about 80 bytes and there is room for several mods of other creators. One property, not two: a second key would use a property slot and
/// its own overhead from the same 500 bytes, and older guests read only this one.</para>
/// </summary>
internal static class RequiredMods
{
    /// <summary>Session property key (short: keys count toward Photon's 500-byte budget).</summary>
    public const string Key = "othp.req";
    public const string Format = "r1";
    /// <summary>The most the value may take (see the budget above): the names keep at least
    /// <see cref="NamesKeepBytes"/> of the shared 300.</summary>
    public const int MaxValueBytes = 240;
    /// <summary>The shared value bytes of othp.req and othp.pl (PlayerNamesProperty.MaxValueBytes).</summary>
    public const int SharedBytes = 300;
    /// <summary>What the player names keep at the least (key, overhead and <see cref="MaxValueBytes"/> taken off).</summary>
    public const int NamesKeepBytes = SharedBytes - MaxValueBytes - 12;
    private const string PrefabPrefix = "np";
    private const string CompactPrefix = "k1";
    private const string MaskPrefix = "k2";
    private const int CompactEntryChars = 4;
    /// <summary>The "k1" token switches to "k2" once it is longer than this (about 20 mods): short lists stay readable by
    /// clients that only know "k1".</summary>
    private const int MaskMinK1Chars = 80;
    private const string Base64Url = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private const int CompactMaxPart = 35;
    private const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>Our Server mods' ids, by code (the index in base 36, two characters). Append only: never reorder or remove
    /// an entry, a released client reads the codes by position. A test pins the start of it and that every public Server
    /// mod is in it.</summary>
    public static readonly IReadOnlyList<string> CodeTable = new[]
    {
        "vera", "-", "-", "-", "chat", "horns", "-", "biglobbies", "tercel", "trailers",
        "aro", "hilo", "bastion", "hilo22", "gearbox", "drifter", "nomad", "mule", "pixie", "bento", "longhorn", "-", "ibex", "marmot", "drover",
        "limber", "badger", "gemse",
    };

    private static int CodeOf(string id)
    {
        for (int i = 0; i < CodeTable.Count; i++)
            if (string.Equals(CodeTable[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static int FromBase36(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : -1;
    public const int MaxIdChars = 40;
    public const int MaxMods = 32;
    private const int MaxVersionPart = 9999;
    private const int MaxReadChars = 1024;

    // ---------------------------------------------------------------- ids and versions

    /// <summary>A mod id that can go into the property: [a-z0-9_.-], 1 to <see cref="MaxIdChars"/> characters.</summary>
    public static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdChars) return false;
        foreach (char c in id)
            if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '.' || c == '-')) return false;
        return true;
    }

    /// <summary>"1.2.3" → 1, 2 ("v1.2", "7", "2.5.0-beta" work too). False when unreadable.</summary>
    public static bool TryParseVersion(string version, out int major, out int minor)
    {
        major = minor = 0;
        if (string.IsNullOrWhiteSpace(version)) return false;
        string s = version.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s.Substring(0, cut);
        var parts = s.Split('.');
        if (parts.Length == 0 || !ReadPart(parts[0], out major)) return false;
        if (parts.Length > 1 && !ReadPart(parts[1], out minor)) return false;
        return true;
    }

    private static bool ReadPart(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= MaxVersionPart;

    /// <summary>"1.2.3" → "1.2"; null when unreadable.</summary>
    public static string ShortVersion(string version) =>
        TryParseVersion(version, out int major, out int minor)
            ? major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture)
            : null;

    // ---------------------------------------------------------------- the property

    /// <summary>"1.2.3" → "1"; null when unreadable.</summary>
    public static string MajorVersion(string version) =>
        TryParseVersion(version, out int major, out _) ? major.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>The prefab token ("np&lt;mask hex&gt;[:&lt;hash&gt;]"), null for an empty mask.</summary>
    public static string PrefabToken(int mask, string hash)
    {
        if (mask <= 0) return null;
        string t = PrefabPrefix + mask.ToString("x", CultureInfo.InvariantCulture);
        return IsHash(hash) ? t + ":" + hash.ToLowerInvariant() : t;
    }

    private static bool IsHash(string h)
    {
        if (h == null || h.Length != 16) return false;
        foreach (char c in h)
            if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F')) return false;
        return true;
    }

    /// <summary>
    /// The property value for these (id, version) pairs, in their order (versions as major only), keeping to
    /// <paramref name="maxBytes"/>. See the other overload.
    /// </summary>
    public static string Encode(IEnumerable<(string id, string version)> mods, int maxBytes = MaxValueBytes)
    {
        if (mods == null) return null;
        var list = new List<AdvertMod>();
        foreach (var (id, version) in mods) list.Add(new AdvertMod(id, version));
        return Encode(list, null, maxBytes, out _);
    }

    /// <summary>
    /// The property value: the prefab token first (<see cref="PrefabToken"/>, may be null), then the mods in their order
    /// (prefab mods as id@major.minor, the rest as id@major), keeping to <paramref name="maxBytes"/>. A mod that doesn't
    /// fit is left out (in <paramref name="leftOut"/>) and smaller ones after it are still tried; "|+N" says how many
    /// were left out. Invalid ids and repeated ids are skipped; an unreadable version is sent as 0. Null when there is
    /// nothing to advertise (then the key is not added at all).
    /// </summary>
    public static string Encode(IEnumerable<AdvertMod> mods, string prefabToken, int maxBytes, out List<string> leftOut)
    {
        leftOut = new List<string>();
        if (mods == null) return null;
        maxBytes = Math.Max(16, Math.Min(maxBytes, MaxValueBytes));
        const int reserve = 4; // room for "|+NN"
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(Format, 128);
        int bytes = Format.Length, written = 0;
        if (!string.IsNullOrEmpty(prefabToken) && prefabToken.StartsWith(PrefabPrefix, StringComparison.Ordinal) &&
            bytes + 1 + prefabToken.Length <= maxBytes - reserve)
        {
            sb.Append('|').Append(prefabToken);
            bytes += 1 + prefabToken.Length;
        }
        else prefabToken = null;
        var entries = new List<CompactEntry>();
        var texts = new StringBuilder();
        foreach (var m in mods)
        {
            string id = m.Id;
            if (!IsValidId(id) || !seen.Add(id)) continue;
            if (written >= MaxMods) { leftOut.Add(id); continue; }
            TryParseVersion(m.Version, out int major, out int minor);
            int code = CodeOf(id);
            bool packed = code >= 0 && code < 36 * 36 && major <= CompactMaxPart && (!m.Prefab || minor <= CompactMaxPart);
            var entry = new CompactEntry { Code = code, Major = major, Minor = m.Prefab ? minor : 0, MinorKnown = m.Prefab };
            string token = null;
            int tb;
            if (packed)
            {
                int before = CompactToken(entries).Length;
                entries.Add(entry);
                tb = CompactToken(entries).Length - before;
                entries.RemoveAt(entries.Count - 1);
            }
            else
            {
                string ver = m.Prefab ? ShortVersion(m.Version) ?? "0.0" : MajorVersion(m.Version) ?? "0";
                token = "|" + id + "@" + ver;
                tb = Encoding.UTF8.GetByteCount(token);
            }
            if (bytes + tb > maxBytes - reserve) { leftOut.Add(id); continue; }
            if (packed) entries.Add(entry);
            else texts.Append(token);
            bytes += tb;
            written++;
        }
        sb.Append(CompactToken(entries));
        sb.Append(texts);
        if (written == 0 && leftOut.Count == 0) return prefabToken == null ? null : sb.ToString();
        if (leftOut.Count > 0) sb.Append("|+").Append(Math.Min(leftOut.Count, 99).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private struct CompactEntry
    {
        public int Code, Major, Minor;
        public bool MinorKnown;
    }

    /// <summary>What a set bit in a "k2" mask stands for: major 1, and minor 0 for a prefab mod (no minor otherwise).
    /// Fixed forever: a client reads old values by it.</summary>
    private static bool IsMaskDefault(CompactEntry e) =>
        e.Major == 1 && e.MinorKnown == OverTheHillPlus.Core.Shared.NetPrefabPlan.IsOwnerMod(CodeTable[e.Code]) && (!e.MinorKnown || e.Minor == 0);

    private static string EntryText(CompactEntry e) =>
        new string(new[] { Base36[e.Code / 36], Base36[e.Code % 36], Base36[e.Major], e.MinorKnown ? Base36[e.Minor] : '_' });

    /// <summary>The compact token ("|k1..." or "|k2...") for these entries, "" when there are none. "k2" once "k1" is long
    /// and "k2" is the shorter one.</summary>
    private static string CompactToken(List<CompactEntry> entries)
    {
        if (entries.Count == 0) return "";
        var k1 = new StringBuilder("|" + CompactPrefix);
        foreach (var e in entries) k1.Append(EntryText(e));
        if (k1.Length <= MaskMinK1Chars) return k1.ToString();
        int high = -1;
        foreach (var e in entries) if (IsMaskDefault(e)) high = Math.Max(high, e.Code);
        var chars = new int[high < 0 ? 0 : high / 6 + 1];
        var extra = new StringBuilder();
        foreach (var e in entries)
        {
            if (IsMaskDefault(e)) chars[e.Code / 6] |= 1 << (e.Code % 6);
            else extra.Append(EntryText(e));
        }
        var k2 = new StringBuilder("|" + MaskPrefix);
        foreach (int c in chars) k2.Append(Base64Url[c]);
        if (extra.Length > 0) k2.Append('.').Append(extra);
        return k2.Length < k1.Length ? k2.ToString() : k1.ToString();
    }

    /// <summary>Parses a received value (another host's mod may send anything). Null when it isn't this format.</summary>
    public static RequiredList Decode(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > MaxReadChars) value = value.Substring(0, MaxReadChars);
        var parts = value.Split('|');
        if (parts[0] != Format) return null;
        var list = new RequiredList();
        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Length > 1 && p[0] == '+')
            {
                if (i == parts.Length - 1 && int.TryParse(p.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int more))
                    list.More += Math.Min(more, 99);
                continue;
            }
            int at = p.IndexOf('@');
            if (at < 0)
            {
                if (p.StartsWith(CompactPrefix, StringComparison.Ordinal)) ReadCompact(p, CompactPrefix.Length, list);
                else if (p.StartsWith(MaskPrefix, StringComparison.Ordinal)) ReadMask(p, list);
                else if (!list.HasPrefabInfo) ReadPrefabToken(p, list);
                continue;
            }
            if (at == 0) continue;
            string id = p.Substring(0, at);
            string ver = p.Substring(at + 1);
            if (!IsValidId(id) || ver.Length == 0 || ver.IndexOfAny(new[] { 'v', 'V', '-', '+', ' ' }) >= 0) continue;
            if (!TryParseVersion(ver, out int major, out int minor)) continue;
            if (list.Find(id) != null) continue;
            if (list.Mods.Count >= MaxMods) { list.More++; continue; }
            list.Mods.Add(new RequiredMod { Id = id, Major = major, Minor = minor, MinorKnown = ver.IndexOf('.') >= 0 });
        }
        return list;
    }

    /// <summary>"k1" and four characters per mod (see the class remarks); a damaged token is skipped as a whole.</summary>
    private static void ReadCompact(string p, int start, RequiredList list)
    {
        int n = p.Length - start;
        if (n <= 0 || n % CompactEntryChars != 0) return;
        for (int i = start; i < p.Length; i += CompactEntryChars)
        {
            int code = FromBase36(p[i]) * 36 + FromBase36(p[i + 1]);
            int major = FromBase36(p[i + 2]);
            int minor = p[i + 3] == '_' ? 0 : FromBase36(p[i + 3]);
            if (FromBase36(p[i]) < 0 || FromBase36(p[i + 1]) < 0 || major < 0 || minor < 0) continue;
            if (code >= CodeTable.Count) { list.More++; continue; } // a mod newer than this client's table
            string id = CodeTable[code];
            if (list.Find(id) != null) continue;
            if (list.Mods.Count >= MaxMods) { list.More++; continue; }
            list.Mods.Add(new RequiredMod { Id = id, Major = major, Minor = minor, MinorKnown = p[i + 3] != '_' });
        }
    }

    /// <summary>"k2", a base64url bit mask over <see cref="CodeTable"/> (bit i: mod i at its default version, see
    /// <see cref="IsMaskDefault"/>), then optionally "." and four-character entries as in "k1" for the other versions. The
    /// mods come out prefab mods first (NetPrefabPlan order), then the rest by code. A damaged token is skipped as a
    /// whole; bits past this client's table count as "more mods".</summary>
    private static void ReadMask(string p, RequiredList list)
    {
        string rest = p.Substring(MaskPrefix.Length);
        string maskText = rest, extra = "";
        int dot = rest.IndexOf('.');
        if (dot >= 0) { maskText = rest.Substring(0, dot); extra = rest.Substring(dot + 1); }
        if (maskText.Length == 0 && extra.Length == 0) return;
        if (extra.Length % CompactEntryChars != 0) return;
        var bits = new List<int>();
        for (int c = 0; c < maskText.Length; c++)
        {
            int v = Base64Url.IndexOf(maskText[c]);
            if (v < 0) return;
            for (int b = 0; b < 6; b++)
                if ((v >> b & 1) != 0) bits.Add(c * 6 + b);
        }
        var found = new RequiredList();
        foreach (int code in bits)
        {
            if (code >= CodeTable.Count) { found.More++; continue; }
            string id = CodeTable[code];
            found.Mods.Add(new RequiredMod { Id = id, Major = 1, Minor = 0, MinorKnown = OverTheHillPlus.Core.Shared.NetPrefabPlan.IsOwnerMod(id) });
        }
        if (extra.Length > 0) ReadCompact(CompactPrefix + extra, CompactPrefix.Length, found);
        found.Mods.Sort((a, b) =>
        {
            int oa = OverTheHillPlus.Core.Shared.NetPrefabPlan.OwnerIndex(a.Id), ob = OverTheHillPlus.Core.Shared.NetPrefabPlan.OwnerIndex(b.Id);
            if ((oa >= 0) != (ob >= 0)) return oa >= 0 ? -1 : 1;
            return oa >= 0 ? oa.CompareTo(ob) : CodeOf(a.Id).CompareTo(CodeOf(b.Id));
        });
        list.More += found.More;
        foreach (var m in found.Mods)
        {
            if (list.Find(m.Id) != null) continue;
            if (list.Mods.Count >= MaxMods) { list.More++; continue; }
            list.Mods.Add(m);
        }
    }

    /// <summary>"np&lt;hex mask&gt;" or "np&lt;hex mask&gt;:&lt;16 hex&gt;"; anything else is ignored.</summary>
    private static void ReadPrefabToken(string p, RequiredList list)
    {
        if (!p.StartsWith(PrefabPrefix, StringComparison.Ordinal)) return;
        string rest = p.Substring(PrefabPrefix.Length);
        string hash = null;
        int colon = rest.IndexOf(':');
        if (colon >= 0)
        {
            hash = rest.Substring(colon + 1);
            rest = rest.Substring(0, colon);
            if (!IsHash(hash)) return;
        }
        if (rest.Length == 0 || rest.Length > 7) return;
        if (!int.TryParse(rest, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int mask) || mask < 0) return;
        list.PrefabMask = mask;
        list.PrefabHash = hash?.ToLowerInvariant();
    }

    // ---------------------------------------------------------------- comparing with what is installed here

    /// <summary>
    /// What stands between this game and the mods a server needs, one issue per mod, in the server's order.
    /// <paramref name="installed"/>: id → version of every mod installed here (switched off ones too);
    /// <paramref name="enabled"/>: whether that mod runs this session. The most serious problem of a mod is reported:
    /// missing, then another major version (switching it on would not help), then off, then a minor difference.
    /// </summary>
    public static List<RequiredModIssue> Compare(RequiredList required, IReadOnlyDictionary<string, string> installed, Func<string, bool> enabled)
    {
        var issues = new List<RequiredModIssue>();
        if (required == null) return issues;
        var here = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (installed != null)
            foreach (var kv in installed)
                if (kv.Key != null) here[kv.Key] = kv.Value;
        foreach (var r in required.Mods)
        {
            var issue = new RequiredModIssue { Id = r.Id, Required = r.Version };
            if (!here.TryGetValue(r.Id, out string version))
            {
                issue.Kind = RequiredIssueKind.Missing;
                issues.Add(issue);
                continue;
            }
            bool readable = TryParseVersion(version, out int major, out int minor);
            issue.Installed = readable ? ShortVersion(version) : version;
            bool on = enabled != null && enabled(r.Id);
            if (readable && major != r.Major) issue.Kind = RequiredIssueKind.VersionMajor;
            else if (!on) issue.Kind = RequiredIssueKind.Off;
            else if (!readable || (r.MinorKnown && minor != r.Minor)) issue.Kind = RequiredIssueKind.VersionMinor;
            else continue;
            issues.Add(issue);
        }
        return issues;
    }

    /// <summary>
    /// The join check: <see cref="Compare"/>, plus what the host's network prefab gate will say (it refuses a player
    /// whose registered prefab mods, their builds or the game build differ from its own).
    /// <para>The host's prefab mods are <paramref name="required"/>'s prefab token (or, from a host older than the
    /// token, every listed mod that owns prefab keys). This game's are <paramref name="localPrefabMods"/> (the mods with
    /// registered entries) and <paramref name="localMarker"/> its join marker; while <paramref name="localPending"/>
    /// (entries still registering) only what is certain is reported.</para>
    /// Issues of prefab mods get <see cref="RequiredModIssue.Prefab"/>: "join without them" is never offered for them.
    /// </summary>
    public static List<RequiredModIssue> CompareForJoin(RequiredList required, IReadOnlyDictionary<string, string> installed,
        Func<string, bool> enabled, IReadOnlyCollection<string> localPrefabMods, string localMarker, bool localPending)
    {
        var issues = Compare(required, installed, enabled);
        if (required == null) return issues;
        List<string> hostSet = required.HasPrefabInfo ? OverTheHillPlus.Core.Shared.NetPrefabPlan.ModsOf(required.PrefabMask) : null;
        bool HostRuns(string id) => hostSet != null
            ? hostSet.Exists(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase))
            : OverTheHillPlus.Core.Shared.NetPrefabPlan.IsOwnerMod(id) && required.Find(id) != null;
        foreach (var i in issues)
            if (HostRuns(i.Id)) i.Prefab = true;
        if (hostSet == null || hostSet.Count == 0) return issues; // unknown, or a host that admits everyone

        var local = new List<string>();
        if (localPrefabMods != null) foreach (var m in localPrefabMods) if (m != null) local.Add(m.ToLowerInvariant());
        var here = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (installed != null) foreach (var kv in installed) if (kv.Key != null) here[kv.Key] = kv.Value;
        bool prefabTrouble = false;
        foreach (var i in issues) if (i.Prefab && i.Kind != RequiredIssueKind.VersionMinor) prefabTrouble = true;

        // Ours, not the host's: the host's hash covers exactly its own set.
        foreach (string m in local)
        {
            if (HostRuns(m)) continue;
            here.TryGetValue(m, out string v);
            issues.Add(new RequiredModIssue { Id = m, Kind = RequiredIssueKind.PrefabExtra, Prefab = true, Installed = ShortVersion(v) ?? v });
            prefabTrouble = true;
        }
        // The host's, on here and at its major version, but not registered here.
        if (!localPending)
            foreach (string m in hostSet)
            {
                if (local.Contains(m) || issues.Exists(i => string.Equals(i.Id, m, StringComparison.OrdinalIgnoreCase))) continue;
                here.TryGetValue(m, out string v);
                issues.Add(new RequiredModIssue { Id = m, Kind = RequiredIssueKind.PrefabNotReady, Prefab = true,
                    Required = required.Find(m)?.Version, Installed = ShortVersion(v) ?? v });
                prefabTrouble = true;
            }
        // Same set: the hashes still have to match.
        if (!prefabTrouble && !localPending && required.PrefabHash != null && localMarker != null &&
            !string.Equals(required.PrefabHash, localMarker, StringComparison.OrdinalIgnoreCase))
        {
            bool named = false;
            foreach (var i in issues)
                if (i.Prefab && i.Kind == RequiredIssueKind.VersionMinor) { i.Kind = RequiredIssueKind.PrefabBuild; named = true; }
            if (!named)
            {
                string first = hostSet[0];
                issues.Add(new RequiredModIssue { Id = first, Kind = RequiredIssueKind.PrefabBuild, Prefab = true,
                    Required = required.Find(first)?.Version, Installed = null });
            }
        }
        return issues;
    }

    /// <summary>True when any issue blocks joining (another major version).</summary>
    public static bool Blocks(IEnumerable<RequiredModIssue> issues)
    {
        if (issues == null) return false;
        foreach (var i in issues)
            if (i != null && i.Blocks) return true;
        return false;
    }

    /// <summary>
    /// Sorts a server's issues into what the join prompt offers. <paramref name="listedVersion"/>: the version the
    /// AVAILABLE tab's mod list has for an id (null when unlisted or not fetched yet); a download only helps when its
    /// major version is the server's.
    /// </summary>
    public static JoinPlan PlanJoin(IReadOnlyList<RequiredModIssue> issues, Func<string, string> listedVersion)
    {
        var plan = new JoinPlan();
        if (issues == null) return plan;
        bool onlyMinor = true;
        foreach (var i in issues)
        {
            if (i == null) continue;
            if (i.Kind != RequiredIssueKind.VersionMinor) onlyMinor = false;
            if (i.Prefab && i.Kind != RequiredIssueKind.VersionMinor) plan.PrefabIssues = true;
            string listed = null;
            switch (i.Kind)
            {
                case RequiredIssueKind.Off:
                    plan.SwitchOn.Add(i.Id);
                    break;
                case RequiredIssueKind.Missing:
                case RequiredIssueKind.VersionMajor:
                    try { listed = listedVersion?.Invoke(i.Id); } catch (Exception ex) { DebugLog.Error("core", ex, "RequiredMods.PlanJoin"); }
                    bool fits = TryParseVersion(listed, out int lm, out _) && TryParseVersion(i.Required, out int rm, out _) && lm == rm;
                    if (fits) plan.Install.Add(i.Id);
                    else if (i.Kind == RequiredIssueKind.VersionMajor) plan.Blocking.Add(i.Id);
                    else if (i.Prefab) plan.PrefabBlocking.Add(i.Id); // the host refuses players without it
                    else plan.MissingUnlisted.Add(i.Id);
                    break;
                case RequiredIssueKind.PrefabExtra:
                    plan.SwitchOff.Add(i.Id);
                    break;
                case RequiredIssueKind.PrefabNotReady:
                    plan.PrefabBlocking.Add(i.Id);
                    break;
                case RequiredIssueKind.PrefabBuild:
                    // Only a download of exactly the host's major.minor can make the builds match.
                    try { listed = listedVersion?.Invoke(i.Id); } catch (Exception ex) { DebugLog.Error("core", ex, "RequiredMods.PlanJoin"); }
                    bool same = i.Installed != null && i.Required != null && i.Required.IndexOf('.') > 0 &&
                                string.Equals(ShortVersion(listed), i.Required, StringComparison.Ordinal);
                    if (same) plan.Install.Add(i.Id);
                    else plan.PrefabBlocking.Add(i.Id);
                    break;
            }
        }
        plan.WarningsOnly = onlyMinor && issues.Count > 0;
        return plan;
    }

    /// <summary>
    /// The mods a host lists in <see cref="Key"/>: running Server mods, prefab mods first (in NetPrefabPlan.OwnerMods
    /// order, with their minor version), then ours, then other creators' (<paramref name="thirdParty"/>), each group by
    /// id. Private mods are never listed (a session property is public: anyone browsing would see the private mod's
    /// id). A private Server mod finds its peers over the mod network instead (ModNet.HostHas / PeerHas on its own
    /// channel).
    /// </summary>
    public static List<AdvertMod> AdvertisedMods(IEnumerable<ModInfo> mods, Func<string, bool> running, Func<string, bool> thirdParty = null)
    {
        var list = new List<(int group, int order, AdvertMod mod)>();
        if (mods == null) return new List<AdvertMod>();
        foreach (var m in mods)
        {
            if (m == null || m.Side != ModSide.Server || m.Private || string.IsNullOrEmpty(m.Id)) continue;
            if (running != null && !running(m.Id)) continue;
            string id = m.Id.ToLowerInvariant();
            int owner = OverTheHillPlus.Core.Shared.NetPrefabPlan.OwnerIndex(id);
            bool other = false;
            try { other = thirdParty != null && thirdParty(m.Id); } catch (Exception ex) { DebugLog.Error("core", ex, "RequiredMods.AdvertisedMods"); }
            int group = owner >= 0 && !other ? 0 : other ? 2 : 1;
            list.Add((group, owner, new AdvertMod(id, m.Version, group == 0)));
        }
        list.Sort((a, b) =>
        {
            if (a.group != b.group) return a.group.CompareTo(b.group);
            if (a.group == 0) return a.order.CompareTo(b.order);
            return string.CompareOrdinal(a.mod.Id, b.mod.Id);
        });
        var result = new List<AdvertMod>(list.Count);
        foreach (var e in list) result.Add(e.mod);
        return result;
    }

    /// <summary>
    /// Whether a private Server mod's networked part may run: it is never advertised, so a guest can't check the
    /// host's list. It runs whenever it is loaded and the session role is known; the mod itself talks only to
    /// players who run its channel (ModNet.HostHas / PeerHas), so a vanilla or other host gets nothing.
    /// </summary>
    public static bool PrivateServerModActive(SessionRole role, bool loaded) => loaded && role != SessionRole.Connecting;

    /// <summary>
    /// Whether a Server mod's networked part may run: offline (no runner at all), single-player and hosting follow the
    /// mod's own switch (<paramref name="loaded"/>); a guest also needs the session to advertise the mod at the same
    /// major version. Without an advert (a vanilla host, one whose mods predate it, or a session whose properties
    /// haven't been read yet) a guest's Server mods stay off.
    /// <para>Also false while <see cref="SessionRole.Connecting"/>: in menus while only the server browser's lobby
    /// runner exists, and while a runner is starting (joining or hosting). It answers "may the networked part run in
    /// this session", so don't call it from menu code expecting true; use ModManager.IsLoaded there.</para>
    /// </summary>
    public static bool ServerModActive(SessionRole role, bool loaded, string localVersion, RequiredList advertised, string id)
    {
        if (!loaded || string.IsNullOrEmpty(id)) return false;
        if (role is SessionRole.None or SessionRole.Solo or SessionRole.Host) return true;
        if (role != SessionRole.Guest) return false; // connecting: the session isn't known yet
        var m = advertised?.Find(id);
        return m != null && TryParseVersion(localVersion, out int major, out _) && major == m.Major;
    }

    /// <summary>
    /// The runner that decides the role, as PlayerListRoster.Runner picks it: the overland runner while it runs a
    /// session, else the running one GameState found, else the idle overland runner (joining, or the browser's lobby,
    /// which reads as <see cref="SessionRole.Connecting"/>). So a lobby runner never makes solo or hosting look like
    /// connecting.
    /// </summary>
    public static T PickRunner<T>(T overland, bool overlandRunning, T gameState) where T : class =>
        overland != null && overlandRunning ? overland : gameState ?? overland;

    // ---------------------------------------------------------------- texts

    public static string SideBadge(ModSide side) => side switch
    {
        ModSide.Host => "HOST",
        ModSide.Server => "SERVER",
        _ => "CLIENT",
    };

    /// <summary>The MODS row's badge: the mod's type, and PRIVATE for private mods.</summary>
    public static string Badge(ModInfo m) =>
        m == null ? SideBadge(ModSide.Client) : SideBadge(m.Side) + (m.Private ? " · PRIVATE" : "");

    /// <summary>One line for the mod's page: who needs it.</summary>
    public static string SideText(ModSide side) => side switch
    {
        ModSide.Host => "Host mod: only the host needs it. Guests can play without it.",
        ModSide.Server => "Server mod: everyone in the session needs it. Servers that run it list it, and you are asked before joining one without it.",
        _ => "Client mod: only you need it.",
    };

    /// <summary>"Needs: Vera Crossover, Race Mode +2 more" for a server browser row (at most <paramref name="maxChars"/>), or null.</summary>
    public static string NeedsText(RequiredList list, Func<string, string> nameOf, int maxChars = 60)
    {
        if (list == null || list.Mods.Count + list.More == 0) return null;
        maxChars = Math.Max(24, maxChars);
        const string head = "Needs: ";
        var sb = new StringBuilder(head);
        int shown = 0, total = list.Mods.Count + list.More;
        foreach (var m in list.Mods)
        {
            string name = NameOrId(nameOf, m.Id);
            string part = (shown > 0 ? ", " : "") + name;
            int left = total - shown - 1;
            string tail = left > 0 ? $" +{left} more" : "";
            if (sb.Length + part.Length + tail.Length > maxChars && shown > 0) break;
            if (sb.Length + part.Length + tail.Length > maxChars)
            {
                // Even the first name is too long: shorten it.
                int room = Math.Max(4, maxChars - sb.Length - tail.Length - 1);
                part = name.Length > room ? name.Substring(0, room) + "…" : name;
            }
            sb.Append(part);
            shown++;
        }
        int rest = total - shown;
        if (rest > 0) sb.Append(" +").Append(rest.ToString(CultureInfo.InvariantCulture)).Append(" more");
        return sb.ToString();
    }

    /// <summary>One issue as a short line for the join prompt.</summary>
    public static string IssueText(RequiredModIssue i) => i == null ? "" : i.Kind switch
    {
        RequiredIssueKind.Missing => i.Prefab
            ? $"not installed (the server has v{i.Required}): it refuses players without it"
            : $"not installed (the server has v{i.Required})",
        RequiredIssueKind.Off => i.Prefab ? "installed but switched off: the server refuses players without it" : "installed but switched off",
        RequiredIssueKind.VersionMinor => $"you have v{i.Installed}, the server has v{i.Required} (should still work)",
        RequiredIssueKind.PrefabExtra => "you run it, the server doesn't: it refuses players with other added cars or trailers. Switch it off to join",
        RequiredIssueKind.PrefabNotReady => "switched on, but it didn't start here (see its page): the server refuses players without it",
        RequiredIssueKind.PrefabBuild => i.Installed != null
            ? $"you have v{i.Installed}, the server has v{i.Required}: the server refuses another build of it"
            : "another build than the server's (a different version of an added car or trailer, or of the game): update to the same versions",
        _ => $"you have v{i.Installed}, the server needs v{i.Required}: you can't join until they match",
    };

    /// <summary>
    /// The specific reason a server's prefab check would refuse this game ("The server runs Trailers, you don't; you
    /// run the Terrier 4WD Wagon, the server doesn't."), or null when no prefab issue is in the list.
    /// </summary>
    public static string PrefabSummary(IReadOnlyList<RequiredModIssue> issues, Func<string, string> nameOf)
    {
        if (issues == null) return null;
        var hostOnly = new List<string>();
        var mineOnly = new List<string>();
        var build = new List<string>();
        foreach (var i in issues)
        {
            if (i == null || !i.Prefab) continue;
            string name = NameOrId(nameOf, i.Id);
            switch (i.Kind)
            {
                case RequiredIssueKind.Missing:
                case RequiredIssueKind.Off:
                case RequiredIssueKind.PrefabNotReady:
                    hostOnly.Add(name); break;
                case RequiredIssueKind.PrefabExtra: mineOnly.Add(name); break;
                case RequiredIssueKind.VersionMajor:
                case RequiredIssueKind.PrefabBuild:
                    build.Add(name); break;
            }
        }
        var parts = new List<string>();
        if (hostOnly.Count > 0) parts.Add("The server runs " + JoinAnd(hostOnly) + ", you don't");
        if (mineOnly.Count > 0) parts.Add((parts.Count > 0 ? "you run " : "You run ") + JoinAnd(mineOnly) + ", the server doesn't");
        if (build.Count > 0) parts.Add((parts.Count > 0 ? "your " : "Your ") + JoinAnd(build) + (build.Count == 1 ? " is" : " are") + " another build than the server's");
        if (parts.Count == 0) return null;
        return string.Join("; ", parts) + ". Servers with added cars or trailers only let in players with exactly the same ones.";
    }

    /// <summary>The host's notice when its listing left mods out (they stay off for guests), or null.</summary>
    public static string LeftOutNotice(IReadOnlyList<string> leftOut, Func<string, string> nameOf)
    {
        if (leftOut == null || leftOut.Count == 0) return null;
        var names = new List<string>();
        foreach (var id in leftOut) names.Add(NameOrId(nameOf, id));
        return $"This server's mod list is full: {JoinAnd(names)} {(names.Count == 1 ? "isn't" : "aren't")} listed, so " +
               $"guests keep {(names.Count == 1 ? "it" : "them")} off. Switch off a server mod you don't need and start a new server.";
    }

    /// <summary>A guest's notice when the host's listing says it left mods out, or null.</summary>
    public static string MoreNotice(RequiredList list)
    {
        if (list == null || list.More <= 0) return null;
        return $"The host runs {list.More} more server mod{(list.More == 1 ? "" : "s")} than its list could hold: " +
               "any of yours it doesn't list stay off this session.";
    }

    /// <summary>The one-time notice for a guest whose host runs Server mods this game can't run with it, or null.</summary>
    public static string SessionNotice(IReadOnlyList<RequiredModIssue> issues, Func<string, string> nameOf)
    {
        if (issues == null) return null;
        var missing = new List<string>();
        var off = new List<string>();
        var major = new List<string>();
        foreach (var i in issues)
        {
            if (i == null) continue;
            string name = NameOrId(nameOf, i.Id);
            switch (i.Kind)
            {
                case RequiredIssueKind.Missing: missing.Add(name); break;
                case RequiredIssueKind.Off: off.Add(name); break;
                case RequiredIssueKind.VersionMajor: major.Add($"{name} (host v{i.Required}, you v{i.Installed})"); break;
            }
        }
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add(JoinAnd(missing) + (missing.Count == 1 ? " is" : " are") + " not installed");
        if (off.Count > 0) parts.Add(JoinAnd(off) + (off.Count == 1 ? " is" : " are") + " switched off");
        if (major.Count > 0) parts.Add(JoinAnd(major) + " can't work with the host's version");
        if (parts.Count == 0) return null;
        return "This server runs mods you can't use with it: " + string.Join("; ", parts) + ". Their features are off for you this session.";
    }

    private static string NameOrId(Func<string, string> nameOf, string id)
    {
        string n = null;
        try { n = nameOf?.Invoke(id); } catch (Exception ex) { DebugLog.Error("core", ex, "RequiredMods.NameOrId"); }
        return string.IsNullOrWhiteSpace(n) ? id : n;
    }

    internal static string JoinAnd(List<string> parts) =>
        parts.Count <= 1 ? (parts.Count == 1 ? parts[0] : "") : string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[parts.Count - 1];
}
