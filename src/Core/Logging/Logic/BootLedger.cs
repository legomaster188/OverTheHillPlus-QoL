using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OverTheHillPlus.Logging;

/// <summary>
/// Where a game start went, in numbers: each mod's load and how much of it was Harmony patches, the first frame, the moment the
/// menu settled, Fusion's config and the cars. Filled in as the start goes, written as one block once every car stands.
/// Pure code (unit-tested).
/// </summary>
internal sealed class BootLedger
{
    private sealed class Mod
    {
        public string Name;
        public double LoadMs, PatchMs;
    }

    private readonly List<Mod> _mods = new();
    private readonly Dictionary<string, Mod> _byPatchId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _marks = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    /// <summary>A mod's Load took this long.</summary>
    public void Loaded(string name, double ms) => _mods.Add(new Mod { Name = name ?? "?", LoadMs = ms });

    /// <summary>Patching took this long, inside the Load of the mod running now.</summary>
    public void Patched(string modId, double ms)
    {
        if (!_byPatchId.TryGetValue(modId ?? "?", out var m)) _byPatchId[modId ?? "?"] = m = new Mod { Name = modId ?? "?" };
        m.PatchMs += ms;
    }

    /// <summary>Seconds since the process started when something happened (the first time counts).</summary>
    public void Mark(string what, double seconds)
    {
        if (_marks.ContainsKey(what)) return;
        _marks[what] = seconds;
        _order.Add(what);
    }

    public bool Has(string what) => _marks.ContainsKey(what);

    public double TotalLoadMs => _mods.Sum(m => m.LoadMs);
    public double TotalPatchMs => _byPatchId.Values.Sum(m => m.PatchMs);

    /// <summary>The report: a line of milestones, the totals, and the mods that took longest.</summary>
    public string Text(int top = 12)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("start-up: ");
        sb.Append(string.Join(", ", _order.Select(k => string.Format(c, "{0} at {1:0.0} s", k, _marks[k]))));
        sb.Append(string.Format(c, ". {0} mods loaded in {1:0.0} s, of it {2:0.0} s Harmony patches.", _mods.Count, TotalLoadMs / 1000.0, TotalPatchMs / 1000.0));
        var slow = _mods.OrderByDescending(m => m.LoadMs).Take(top).Select(m => string.Format(c, "{0} {1:0} ms", m.Name, m.LoadMs));
        if (_mods.Count > 0) sb.Append(" Slowest loads: ").Append(string.Join(", ", slow)).Append('.');
        var patches = _byPatchId.Values.OrderByDescending(m => m.PatchMs).Take(top).Select(m => string.Format(c, "{0} {1:0} ms", m.Name, m.PatchMs));
        if (_byPatchId.Count > 0) sb.Append(" Slowest patches: ").Append(string.Join(", ", patches)).Append('.');
        return sb.ToString();
    }
}
