using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// Which custom parts a player owns and has mounted, per car: the mod's own record, kept in a sidecar file next to the
/// game's saves, because the game's save never gets a custom item id (a save with one would carry an item the game does
/// not know once the mod is gone). An entry is (car item id, part item id); car id 0 means "on every car". Free parts
/// (price 0) count as owned without an entry. Text format, one entry per line (<c>own|use</c>, car id and part id in
/// hex), so a damaged file loses a line, not everything. Pure (unit-tested).
/// </summary>
public sealed class Ownership
{
    public const string Header = "oth-customcosmetics 1";

    private readonly HashSet<(uint car, uint part)> _owned = new();
    private readonly HashSet<(uint car, uint part)> _inUse = new();

    public int OwnedCount => _owned.Count;
    public int InUseCount => _inUse.Count;

    public bool Owns(uint car, uint part) => _owned.Contains((car, part)) || _owned.Contains((0u, part));

    public bool InUse(uint car, uint part) => _inUse.Contains((car, part));

    /// <summary>True when the set changed.</summary>
    public bool Own(uint car, uint part) => _owned.Add((car, part));

    /// <summary>Sets the in-use flag (a mounted part is owned too). True when the set changed.</summary>
    public bool SetInUse(uint car, uint part, bool inUse)
    {
        if (inUse) { bool a = _owned.Add((car, part)); bool b = _inUse.Add((car, part)); return a || b; }
        return _inUse.Remove((car, part));
    }

    /// <summary>The parts mounted on a car.</summary>
    public List<uint> MountedOn(uint car)
    {
        var l = new List<uint>();
        foreach (var e in _inUse) if (e.car == car) l.Add(e.part);
        l.Sort();
        return l;
    }

    public string Encode()
    {
        var sb = new StringBuilder(Header).Append('\n');
        var own = new List<(uint, uint)>(_owned);
        own.Sort();
        foreach (var (c, p) in own) sb.Append("own ").Append(c.ToString("X8")).Append(' ').Append(p.ToString("X8")).Append('\n');
        var use = new List<(uint, uint)>(_inUse);
        use.Sort();
        foreach (var (c, p) in use) sb.Append("use ").Append(c.ToString("X8")).Append(' ').Append(p.ToString("X8")).Append('\n');
        return sb.ToString();
    }

    /// <summary>Reads a file's text. Null when it isn't ours (wrong header); bad lines are skipped and counted.</summary>
    public static Ownership Decode(string text, out int badLines)
    {
        badLines = 0;
        if (text == null) return null;
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Header) return null;
        var o = new Ownership();
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            var f = line.Split(' ');
            if (f.Length != 3 || !uint.TryParse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint car) ||
                !uint.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint part))
            { badLines++; continue; }
            if (f[0] == "own") o._owned.Add((car, part));
            else if (f[0] == "use") { o._owned.Add((car, part)); o._inUse.Add((car, part)); }
            else badLines++;
        }
        return o;
    }

    /// <summary>The profile's part of the sidecar's file name: its ASCII letters and digits, lower case, never empty.</summary>
    public static string FileKey(string profile)
    {
        var sb = new StringBuilder();
        foreach (char c in profile ?? "")
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(char.ToLowerInvariant(c));
        return sb.Length == 0 ? "default" : sb.ToString();
    }
}
