using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Shared;

namespace OverTheHillPlus.Race;

internal sealed class LeaderEntry
{
    public string Name { get; set; } = "";
    public long Ms { get; set; }
    public string When { get; set; } = "";
    /// <summary>Time since GO at every gate of this run (for split deltas), or null.</summary>
    public long[] Splits { get; set; }
}

/// <summary>
/// The best times on one track (pure): one entry per player (their best), fastest first, at most
/// <see cref="MaxEntries"/>. Saved as a file per track and sent to the session (top entries, without splits) after
/// every race; boards received from a host are merged into the local file.
/// </summary>
internal sealed class Leaderboard
{
    public const byte WireVersion = 1;
    public const int MaxEntries = 25;
    public const int MaxName = 24;
    /// <summary>Faster than this is not a real run.</summary>
    public const long MinMs = 500;

    public string TrackId { get; set; } = "";
    public string TrackName { get; set; } = "";
    public List<LeaderEntry> Entries { get; set; } = new();

    [JsonIgnore] public LeaderEntry Best => Entries.Count > 0 ? Entries[0] : null;
    [JsonIgnore] public long[] BestSplits => Best?.Splits;

    public readonly struct AddResult
    {
        /// <summary>1-based place on the board after the add, 0 = not on the board.</summary>
        public readonly int Rank;
        public readonly bool PersonalBest, Record;
        public AddResult(int rank, bool pb, bool record) { Rank = rank; PersonalBest = pb; Record = record; }
    }

    public AddResult Add(string name, long ms, long[] splits, string when)
    {
        name = PinBoard.CleanText(name, MaxName);
        if (name.Length == 0 || ms < MinMs || ms > 24L * 3600000) return default;
        long? recordBefore = Best?.Ms;
        var existing = FindName(name);
        bool pb = existing == null || ms < existing.Ms;
        if (pb)
        {
            if (existing != null) Entries.Remove(existing);
            Entries.Add(new LeaderEntry { Name = name, Ms = ms, When = when ?? "", Splits = splits });
            Sort();
        }
        int rank = RankOf(name);
        bool record = pb && rank == 1 && (!recordBefore.HasValue || ms < recordBefore.Value);
        return new AddResult(rank, pb && rank > 0, record);
    }

    /// <summary>Adds faster times from another board (best per player wins).</summary>
    public void Merge(IEnumerable<LeaderEntry> entries)
    {
        if (entries == null) return;
        foreach (var e in entries)
        {
            if (e == null) continue;
            string name = PinBoard.CleanText(e.Name, MaxName);
            if (name.Length == 0 || e.Ms < MinMs) continue;
            var mine = FindName(name);
            if (mine != null && mine.Ms <= e.Ms) continue;
            if (mine != null) Entries.Remove(mine);
            Entries.Add(new LeaderEntry { Name = name, Ms = e.Ms, When = e.When ?? "", Splits = e.Splits ?? mine?.Splits });
        }
        Sort();
    }

    private LeaderEntry FindName(string name)
    {
        foreach (var e in Entries) if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    private int RankOf(string name)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (string.Equals(Entries[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    private void Sort()
    {
        Entries.Sort((a, b) => a.Ms != b.Ms ? a.Ms.CompareTo(b.Ms) : string.CompareOrdinal(a.When, b.When));
        if (Entries.Count > MaxEntries) Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
    }

    // ---------------------------------------------------------------- wire (no splits)

    public byte[] Encode(int top)
    {
        int n = Math.Min(Math.Min(top, Entries.Count), MaxEntries);
        var w = new NetWriter(32 + n * 40).Byte(WireVersion).String(TrackId, 80).String(TrackName, RaceTrack.MaxName).Byte((byte)n);
        for (int i = 0; i < n; i++) w.String(Entries[i].Name, MaxName).Int((int)Math.Min(int.MaxValue, Entries[i].Ms)).String(Entries[i].When, 20);
        return w.ToArray();
    }

    public static Leaderboard Decode(byte[] bytes)
    {
        var r = new NetReader(bytes);
        if (r.Byte() != WireVersion) return null;
        var b = new Leaderboard { TrackId = r.String(80), TrackName = PinBoard.CleanText(r.String(RaceTrack.MaxName), RaceTrack.MaxName) };
        int n = r.Byte();
        if (!r.Ok || n > MaxEntries) return null;
        var list = new List<LeaderEntry>();
        for (int i = 0; i < n; i++) list.Add(new LeaderEntry { Name = r.String(MaxName), Ms = r.Int(), When = PinBoard.CleanText(r.String(20), 20) });
        if (!r.Ok) return null;
        b.Merge(list);
        return b;
    }

    // ---------------------------------------------------------------- files

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static Leaderboard FromJson(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<Leaderboard>(json ?? "");
            if (raw == null) return null;
            var b = new Leaderboard { TrackId = raw.TrackId ?? "", TrackName = PinBoard.CleanText(raw.TrackName, RaceTrack.MaxName) };
            b.Merge(raw.Entries);
            return b;
        }
        catch (Exception ex) { DebugLog.Error("race", ex, "Leaderboard.FromJson"); return null; }
    }
}
