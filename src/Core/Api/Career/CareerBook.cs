using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Api;

/// <summary>What a career knows about one car it owns: the clock, how worn it is and the dents on it now.</summary>
internal sealed class CarRecord
{
    /// <summary>Kilometres on the clock.</summary>
    public double Km;
    /// <summary>What is left of the car, 0 to 1: it goes with the miles and with wrecks, and comes back a little with a full
    /// service. It stays with the car and decides what the car sells for.</summary>
    public float Condition = 1f;
    /// <summary>The damage on it now, 0 to 1 of what the car can take (a repair takes it off).</summary>
    public float Damage;
    /// <summary>What the career paid for it (0: a gift).</summary>
    public long Paid;
    /// <summary>It came second hand.</summary>
    public bool Used;
    /// <summary>The seller's note, when it had one.</summary>
    public string Note = "";

    public CarRecord Copy() => (CarRecord)MemberwiseClone();
}

/// <summary>A place on the map a career bought: where it is, kept with the career, so the place is found again when a map
/// changes its ids.</summary>
internal sealed class PlaceDeed
{
    /// <summary>The world place's id, or the name when it had none.</summary>
    public string Id = "";
    public string Name = "";
    /// <summary>What it is: "depot" (a yard) or "garage".</summary>
    public string Kind = "";
    public float X, Y, Z, Yaw;
    public long Paid;

    public PlaceDeed Copy() => (PlaceDeed)MemberwiseClone();
}

/// <summary>
/// Everything one career has: its money and the last changes of it, the cars it owns and their upgrades, its unlocks,
/// what it carries to sell, the progress mods keep in it, and a few counts. <see cref="Career"/> works on the book of
/// the career that is loaded; the career mod saves and loads it (its slot files). Plain data, every change checked by
/// <see cref="Career"/> (the book itself only keeps its sizes in bounds). Pure code (unit-tested).
/// </summary>
internal sealed class CareerBook
{
    /// <summary>The most money a career can hold (and the most any single change can be).</summary>
    public const long MaxMoney = 1_000_000_000L;
    public const int MaxLedger = 60, MaxCars = 128, MaxUnlocks = 512, MaxGoods = 128, MaxProgress = 512, MaxDoneOnce = 512;
    public const int MaxUpgradesPerCar = 64;
    public const int MaxText = 256;
    public const int MaxMedals = 256, MaxMilestones = 128, MaxVisited = 512, MaxDeeds = 32;

    public long Money;
    public readonly List<WalletChange> Ledger = new();
    public readonly SortedSet<string> Cars = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The car the career drives now (a key in <see cref="Cars"/>), or "".</summary>
    public string CarInUse = "";
    /// <summary>Car → upgrade id → level owned (1 and up; a missing entry is stock).</summary>
    public readonly Dictionary<string, Dictionary<string, int>> Levels = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Car → upgrades owned but not fitted (another of their group is).</summary>
    public readonly Dictionary<string, SortedSet<string>> Unfitted = new(StringComparer.OrdinalIgnoreCase);
    public readonly SortedSet<string> Unlocks = new(StringComparer.Ordinal);
    public readonly Dictionary<string, double> Goods = new(StringComparer.Ordinal);
    public readonly Dictionary<string, long> Counters = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal);
    /// <summary>Jobs that are done for good (templates that aren't repeatable).</summary>
    public readonly SortedSet<string> DoneOnce = new(StringComparer.Ordinal);
    /// <summary>Loose things the career owns that aren't cars: "&lt;kind&gt;:&lt;id&gt;" → count (cosmetics, liveries,
    /// upgrades not on a car).</summary>
    public readonly Dictionary<string, int> Items = new(StringComparer.Ordinal);
    public const int MaxItems = 512, MaxItemCount = 9999;

    /// <summary>The job this career was on when it last saved (the career mod's text; null: none).</summary>
    public string ActiveJob;
    public long Earned, Spent;
    public int JobsDone, JobsFailed;

    /// <summary>0 Normal, 1 Hardcore: picked when the career starts and never changed.</summary>
    public int Difficulty;
    /// <summary>Car → its clock, wear and dents.</summary>
    public readonly Dictionary<string, CarRecord> Records = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Job template → the best medal it was done with (1 bronze, 2 silver, 3 gold).</summary>
    public readonly Dictionary<string, int> Medals = new(StringComparer.Ordinal);
    /// <summary>Milestones paid out, for good.</summary>
    public readonly SortedSet<string> Milestones = new(StringComparer.Ordinal);
    /// <summary>Places this career has been to (exploring).</summary>
    public readonly SortedSet<string> Visited = new(StringComparer.Ordinal);
    /// <summary>The places it bought.</summary>
    public readonly List<PlaceDeed> Deeds = new();
    /// <summary>The deed it starts from (its <see cref="PlaceDeed.Id"/>; "": where it last stopped).</summary>
    public string Home = "";
    /// <summary>The cover it pays for (0: none).</summary>
    public int Insurance;

    /// <summary>A car's record, made when it has none yet (null for no car).</summary>
    public CarRecord Record(string car)
    {
        if (string.IsNullOrEmpty(car)) return null;
        if (!Records.TryGetValue(car, out var r)) Records[car] = r = new CarRecord();
        return r;
    }

    public int Level(string car, string upgrade)
    {
        if (car == null || upgrade == null || !Levels.TryGetValue(car, out var ups)) return 0;
        return ups.TryGetValue(upgrade, out int l) ? l : 0;
    }

    public bool Fitted(string car, string upgrade) =>
        Level(car, upgrade) > 0 && !(Unfitted.TryGetValue(car, out var off) && off.Contains(upgrade));

    /// <summary>Sets a level (0 takes the upgrade off the car); false when the car would get too many upgrades.</summary>
    public bool SetLevel(string car, string upgrade, int level)
    {
        if (string.IsNullOrEmpty(car) || string.IsNullOrEmpty(upgrade)) return false;
        if (level <= 0)
        {
            if (Levels.TryGetValue(car, out var have))
            {
                have.Remove(upgrade);
                if (have.Count == 0) Levels.Remove(car);
            }
            SetFitted(car, upgrade, true);
            return true;
        }
        if (!Levels.TryGetValue(car, out var ups))
        {
            if (Levels.Count >= MaxCars) return false;
            Levels[car] = ups = new Dictionary<string, int>(StringComparer.Ordinal);
        }
        if (!ups.ContainsKey(upgrade) && ups.Count >= MaxUpgradesPerCar) return false;
        ups[upgrade] = level;
        return true;
    }

    public void SetFitted(string car, string upgrade, bool fitted)
    {
        if (string.IsNullOrEmpty(car) || string.IsNullOrEmpty(upgrade)) return;
        if (fitted)
        {
            if (Unfitted.TryGetValue(car, out var off))
            {
                off.Remove(upgrade);
                if (off.Count == 0) Unfitted.Remove(car);
            }
            return;
        }
        if (!Unfitted.TryGetValue(car, out var set)) Unfitted[car] = set = new SortedSet<string>(StringComparer.Ordinal);
        if (set.Count < MaxUpgradesPerCar) set.Add(upgrade);
    }

    /// <summary>The upgrades of a car with their levels (empty when it has none).</summary>
    public IReadOnlyDictionary<string, int> LevelsOf(string car) =>
        car != null && Levels.TryGetValue(car, out var ups) ? ups : (IReadOnlyDictionary<string, int>)EmptyLevels;

    private static readonly Dictionary<string, int> EmptyLevels = new();

    public void AddLedger(WalletChange c)
    {
        Ledger.Add(c);
        if (Ledger.Count > MaxLedger) Ledger.RemoveRange(0, Ledger.Count - MaxLedger);
    }

    /// <summary>A copy of everything (for a save written off the main thread).</summary>
    public CareerBook Copy()
    {
        var b = new CareerBook
        {
            Money = Money, CarInUse = CarInUse, Earned = Earned, Spent = Spent, JobsDone = JobsDone, JobsFailed = JobsFailed,
            ActiveJob = ActiveJob, Difficulty = Difficulty, Home = Home, Insurance = Insurance,
        };
        b.Ledger.AddRange(Ledger);
        foreach (var c in Cars) b.Cars.Add(c);
        foreach (var kv in Levels) b.Levels[kv.Key] = new Dictionary<string, int>(kv.Value, StringComparer.Ordinal);
        foreach (var kv in Unfitted) b.Unfitted[kv.Key] = new SortedSet<string>(kv.Value, StringComparer.Ordinal);
        foreach (var u in Unlocks) b.Unlocks.Add(u);
        foreach (var kv in Goods) b.Goods[kv.Key] = kv.Value;
        foreach (var kv in Counters) b.Counters[kv.Key] = kv.Value;
        foreach (var kv in Texts) b.Texts[kv.Key] = kv.Value;
        foreach (var d in DoneOnce) b.DoneOnce.Add(d);
        foreach (var kv in Items) b.Items[kv.Key] = kv.Value;
        foreach (var kv in Records) b.Records[kv.Key] = kv.Value.Copy();
        foreach (var kv in Medals) b.Medals[kv.Key] = kv.Value;
        foreach (var m in Milestones) b.Milestones.Add(m);
        foreach (var v in Visited) b.Visited.Add(v);
        foreach (var d in Deeds) b.Deeds.Add(d.Copy());
        return b;
    }
}
