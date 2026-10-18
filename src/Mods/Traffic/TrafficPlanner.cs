using System;
using System.Collections.Generic;
using System.Numerics;

namespace OverTheHillPlus.Traffic;

/// <summary>
/// AI Traffic's density and despawn rules, about once a second: how many cars there should be, which ones to take
/// away (far from every player, stuck, flipped, or standing in a player's way), and where new ones may appear (on a
/// road, out of sight range, not next to another car). Pure (System.Numerics), so the tests compile it on its own.
/// </summary>
internal static class TrafficPlanner
{
    internal struct Settings
    {
        public int CarsPerPlayer;
        public int MaxCars;
        /// <summary>New cars appear between these distances from the nearest player (m).</summary>
        public float SpawnMin, SpawnMax;
        /// <summary>A car farther than this from every player is taken away (m). Kept above SpawnMax.</summary>
        public float DespawnDistance;
        /// <summary>No two AI cars closer than this when spawning (m).</summary>
        public float MinGap;
        /// <summary>Taken away after this long without progress or upside down (s).</summary>
        public float StuckSeconds;
        /// <summary>Taken away after standing this long next to a player (s), so it never blocks anyone.</summary>
        public float BlockingSeconds;

        public static Settings Default => new()
        {
            CarsPerPlayer = 3, MaxCars = 8, SpawnMin = 120f, SpawnMax = 320f, DespawnDistance = 450f,
            MinGap = 40f, StuckSeconds = 20f, BlockingSeconds = 6f,
        };

        /// <summary>Sane values whatever the config says (a despawn ring inside the spawn ring would churn cars).</summary>
        public Settings Normalized()
        {
            var s = this;
            s.CarsPerPlayer = Math.Clamp(s.CarsPerPlayer, 0, 10);
            s.MaxCars = Math.Clamp(s.MaxCars, 0, 16);
            s.SpawnMin = Math.Clamp(s.SpawnMin, 40f, 1000f);
            s.SpawnMax = Math.Clamp(s.SpawnMax, s.SpawnMin + 20f, 1500f);
            s.DespawnDistance = Math.Clamp(s.DespawnDistance, s.SpawnMax + 50f, 3000f);
            s.MinGap = Math.Clamp(s.MinGap, 10f, 300f);
            s.StuckSeconds = Math.Clamp(s.StuckSeconds, 5f, 300f);
            s.BlockingSeconds = Math.Clamp(s.BlockingSeconds, 2f, 120f);
            return s;
        }
    }

    internal struct Car
    {
        public int Id;
        public Vector3 Pos;
        /// <summary>Seconds without progress, or upside down.</summary>
        public float StuckFor;
        /// <summary>Seconds standing still right next to a player.</summary>
        public float BlockingFor;
    }

    internal enum Why { TooFar, Stuck, Blocking, TooMany }

    internal sealed class Plan
    {
        public readonly List<(int id, Why why)> Despawn = new();
        public readonly List<int> SpawnAt = new(); // indexes into the candidate list
        public int Wanted;
    }

    /// <summary>How many AI cars the session should have for <paramref name="players"/> players.</summary>
    public static int Target(int players, Settings s)
    {
        s = s.Normalized();
        if (players <= 0) return 0;
        return Math.Min(s.MaxCars, players * s.CarsPerPlayer);
    }

    public static float NearestPlayer(Vector3 p, IReadOnlyList<Vector3> players)
    {
        float best = float.MaxValue;
        for (int i = 0; i < players.Count; i++) best = Math.Min(best, RoadNet.Flat(p, players[i]));
        return best;
    }

    /// <summary>
    /// The plan for this round. <paramref name="candidates"/> are places on the roads (shuffled by the caller); at
    /// most <paramref name="maxSpawns"/> of them are picked.
    /// </summary>
    public static Plan Make(IReadOnlyList<Vector3> players, IReadOnlyList<Car> cars, IReadOnlyList<Vector3> candidates,
        Settings s, int maxSpawns)
    {
        s = s.Normalized();
        var plan = new Plan { Wanted = Target(players.Count, s) };
        var gone = new HashSet<int>();

        // Cars that have to go whatever the density.
        foreach (var c in cars)
        {
            Why? why = null;
            if (players.Count == 0 || NearestPlayer(c.Pos, players) > s.DespawnDistance) why = Why.TooFar;
            else if (c.BlockingFor >= s.BlockingSeconds) why = Why.Blocking;
            else if (c.StuckFor >= s.StuckSeconds) why = Why.Stuck;
            if (why == null) continue;
            plan.Despawn.Add((c.Id, why.Value));
            gone.Add(c.Id);
        }

        // Too many (density lowered, a player left): the ones farthest from everyone go first.
        var kept = new List<Car>();
        foreach (var c in cars) if (!gone.Contains(c.Id)) kept.Add(c);
        int extra = kept.Count - plan.Wanted;
        if (extra > 0)
        {
            kept.Sort((a, b) => NearestPlayer(b.Pos, players).CompareTo(NearestPlayer(a.Pos, players)));
            for (int i = 0; i < extra; i++) plan.Despawn.Add((kept[i].Id, Why.TooMany));
            kept.RemoveRange(0, extra);
        }

        int need = Math.Min(plan.Wanted - kept.Count, Math.Max(0, maxSpawns));
        if (need <= 0 || candidates == null) return plan;
        var taken = new List<Vector3>();
        foreach (var c in kept) taken.Add(c.Pos);
        for (int i = 0; i < candidates.Count && plan.SpawnAt.Count < need; i++)
        {
            var p = candidates[i];
            float near = NearestPlayer(p, players);
            if (near < s.SpawnMin || near > s.SpawnMax) continue;
            bool crowded = false;
            foreach (var t in taken)
                if (RoadNet.Flat(t, p) < s.MinGap) { crowded = true; break; }
            if (crowded) continue;
            plan.SpawnAt.Add(i);
            taken.Add(p);
        }
        return plan;
    }
}
