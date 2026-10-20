using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OverTheHillPlus.Unreleased;

/// <summary>What kind of inventory entry a stashed line is.</summary>
internal enum StashKind
{
    /// <summary>One of our cars (the game's VehicleItem).</summary>
    Vehicle,
    /// <summary>A plain item whose id only exists with this mod (an unreleased car's livery, say).</summary>
    Plain,
    /// <summary>One car's state of a vehicle part (VehicleSpecificItem.VehicleStates) that belongs to one of our cars.</summary>
    PartState,
    /// <summary>A vehicle part entry of ours with no car states at all (kept so it isn't lost).</summary>
    Part,
}

/// <summary>One inventory entry kept out of the game's save file.</summary>
internal sealed class StashItem
{
    public StashKind Kind;
    /// <summary>Vehicle / plain item id, or the part id for a part state.</summary>
    public uint Id;
    public int Count;
    public bool InUse;
    public bool IsNew;
    /// <summary>Vehicle: distance driven (VehicleItem.MetersTravelled).</summary>
    public int Meters;
    /// <summary>Vehicle: mounted tools (VehicleItem.ToolsetIDs).</summary>
    public List<uint> Tools = new();
    /// <summary>Part state: the car the state belongs to.</summary>
    public uint VehicleId;
}

/// <summary>Everything kept out of one profile's save, and the stock car written in its place.</summary>
internal sealed class ProfileStash
{
    /// <summary>The stock car the saved file has in use instead of ours (0 = none of ours was in use).</summary>
    public uint FallbackId;
    public readonly List<StashItem> Items = new();

    public IEnumerable<StashItem> Vehicles() => Items.Where(i => i.Kind == StashKind.Vehicle);
}

/// <summary>
/// The stash file (BepInEx/config/oth.unreleased.stash.txt), one section per profile:
/// <code>
/// [profile name, escaped]
/// fallback=&lt;id&gt;
/// v|id|count|inUse|isNew|meters|tool,tool
/// i|id|count|inUse|isNew
/// s|partId|isNew|vehicleId|count|inUse
/// p|partId|isNew
/// </code>
/// Unknown or broken lines are skipped, so a damaged file loses at most those lines; after a damaged section header
/// the lines up to the next good header are skipped too (they never land in another profile).
/// </summary>
internal static class StashFile
{
    public static string Format(IDictionary<string, ProfileStash> profiles)
    {
        var sb = new StringBuilder("# Unreleased Cars: cars and parts kept out of the game's save so it loads without the mod\n");
        foreach (var kv in profiles.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append('[').Append(Escape(kv.Key)).Append("]\n");
            var s = kv.Value ?? new ProfileStash();
            sb.Append("fallback=").Append(U(s.FallbackId)).Append('\n');
            foreach (var i in s.Items)
            {
                switch (i.Kind)
                {
                    case StashKind.Vehicle:
                        sb.Append("v|").Append(U(i.Id)).Append('|').Append(I(i.Count)).Append('|').Append(B(i.InUse)).Append('|')
                          .Append(B(i.IsNew)).Append('|').Append(I(i.Meters)).Append('|')
                          .Append(string.Join(",", (i.Tools ?? new List<uint>()).Select(U))).Append('\n');
                        break;
                    case StashKind.Plain:
                        sb.Append("i|").Append(U(i.Id)).Append('|').Append(I(i.Count)).Append('|').Append(B(i.InUse)).Append('|')
                          .Append(B(i.IsNew)).Append('\n');
                        break;
                    case StashKind.PartState:
                        sb.Append("s|").Append(U(i.Id)).Append('|').Append(B(i.IsNew)).Append('|').Append(U(i.VehicleId)).Append('|')
                          .Append(I(i.Count)).Append('|').Append(B(i.InUse)).Append('\n');
                        break;
                    case StashKind.Part:
                        sb.Append("p|").Append(U(i.Id)).Append('|').Append(B(i.IsNew)).Append('\n');
                        break;
                }
            }
        }
        return sb.ToString();
    }

    public static Dictionary<string, ProfileStash> Parse(string text)
    {
        var result = new Dictionary<string, ProfileStash>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;
        ProfileStash current = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                if (line.Length >= 2 && line[line.Length - 1] == ']')
                {
                    current = new ProfileStash();
                    result[Unescape(line.Substring(1, line.Length - 2))] = current;
                }
                else current = null; // a damaged header: skip its lines rather than give them to the previous profile
                continue;
            }
            if (current == null) continue;
            if (line.StartsWith("fallback=", StringComparison.Ordinal))
            {
                if (TryU(line.Substring(9), out uint f)) current.FallbackId = f;
                continue;
            }
            var item = ParseItem(line.Split('|'));
            if (item != null) current.Items.Add(item);
        }
        return result;
    }

    private static StashItem ParseItem(string[] p)
    {
        try
        {
            switch (p[0])
            {
                case "v" when p.Length >= 7:
                {
                    var i = new StashItem { Kind = StashKind.Vehicle };
                    if (!TryU(p[1], out i.Id) || !TryI(p[2], out i.Count) || !TryB(p[3], out i.InUse) || !TryB(p[4], out i.IsNew) ||
                        !TryI(p[5], out i.Meters)) return null;
                    foreach (var t in p[6].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        if (TryU(t, out uint tool)) i.Tools.Add(tool);
                    return i;
                }
                case "i" when p.Length >= 5:
                {
                    var i = new StashItem { Kind = StashKind.Plain };
                    if (!TryU(p[1], out i.Id) || !TryI(p[2], out i.Count) || !TryB(p[3], out i.InUse) || !TryB(p[4], out i.IsNew)) return null;
                    return i;
                }
                case "s" when p.Length >= 6:
                {
                    var i = new StashItem { Kind = StashKind.PartState };
                    if (!TryU(p[1], out i.Id) || !TryB(p[2], out i.IsNew) || !TryU(p[3], out i.VehicleId) || !TryI(p[4], out i.Count) ||
                        !TryB(p[5], out i.InUse)) return null;
                    return i;
                }
                case "p" when p.Length >= 3:
                {
                    var i = new StashItem { Kind = StashKind.Part };
                    if (!TryU(p[1], out i.Id) || !TryB(p[2], out i.IsNew)) return null;
                    return i;
                }
            }
        }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "StashFile.ParseItem"); }
        return null;
    }

    // Profile names are the game's file names: escaped so they can't break the line format.
    private static string Escape(string s) =>
        (s ?? "").Replace("%", "%25").Replace("\n", "%0A").Replace("\r", "%0D").Replace("[", "%5B").Replace("]", "%5D");

    private static string Unescape(string s) =>
        s.Replace("%5D", "]").Replace("%5B", "[").Replace("%0D", "\r").Replace("%0A", "\n").Replace("%25", "%");

    private static string U(uint v) => v.ToString(CultureInfo.InvariantCulture);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string B(bool v) => v ? "1" : "0";
    private static bool TryU(string s, out uint v) => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
    private static bool TryI(string s, out int v) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);

    private static bool TryB(string s, out bool v)
    {
        v = s == "1";
        return s == "1" || s == "0";
    }
}

/// <summary>
/// Reading and updating the stash file through the given file access (pure: the tests pass their own). A file that
/// can't be read is never written over, so a locked or broken file can't wipe the other profiles' stashes.
/// </summary>
internal static class StashStore
{
    /// <summary>All profiles' stashes; empty when there is no file; null when it exists but can't be read.</summary>
    public static Dictionary<string, ProfileStash> TryRead(Func<bool> exists, Func<string> read)
    {
        try { return exists() ? StashFile.Parse(read()) : new Dictionary<string, ProfileStash>(StringComparer.Ordinal); }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "StashStore.TryRead"); return null; }
    }

    /// <summary>
    /// Stores <paramref name="now"/> for <paramref name="profile"/>, merged with what the file had for it
    /// (<see cref="UnreleasedRules.Merge"/> with <paramref name="couldRestore"/>); a profile left with nothing is
    /// removed. False (and nothing written) when the file can't be read. Nothing is written when the content would
    /// stay the same.
    /// </summary>
    public static bool Update(string profile, ProfileStash now, Func<bool> exists, Func<string> read, Action<string> write,
        Func<StashItem, bool> couldRestore)
    {
        var all = TryRead(exists, read);
        if (all == null || profile == null) return false;
        string before = StashFile.Format(all);
        all.TryGetValue(profile, out var previous);
        var stash = UnreleasedRules.Merge(previous, now, couldRestore);
        if (stash.Items.Count == 0) all.Remove(profile);
        else all[profile] = stash;
        string after = StashFile.Format(all);
        if (after != before) write(after);
        return true;
    }
}

/// <summary>
/// Whether the stash was read back into a loaded profile (per inventory object and profile). Only a finished
/// restore (the file read, every row applied) marks it: until then nothing may write the stash, so a failed read or
/// a restore that threw never makes the next save drop rows as "removed by the player".
/// </summary>
internal sealed class RestoreGate
{
    private long _inventory;
    private string _profile;
    private bool _done;

    public bool MayWrite(long inventory, string profile) => _done && inventory == _inventory && profile == _profile;

    public bool NeedsRestore(long inventory, string profile) => !MayWrite(inventory, profile);

    /// <summary>
    /// Reads all stashes (null = unreadable) and applies this profile's (null when it has none). True, and marked
    /// done, only when both worked; otherwise the next call tries again.
    /// </summary>
    public bool Attempt(long inventory, string profile, Func<Dictionary<string, ProfileStash>> read, Action<ProfileStash> apply)
    {
        if (profile == null) return false;
        if (_inventory != inventory || _profile != profile)
        {
            _inventory = inventory;
            _profile = profile;
            _done = false;
        }
        var all = read();
        if (all == null) return false;
        all.TryGetValue(profile, out var stash);
        try { apply(stash); }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "RestoreGate.Attempt"); return false; }
        _done = true;
        return true;
    }
}
