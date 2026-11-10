using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// Working places mods add to the world and other mods use, through core, so no mod references another: a map mod
/// (Skadi's Hollow) publishes its trailer depot bays and fuel pumps here; Trailers parks a trailer asked for at a depot
/// in a free bay, ready to hitch, and Fuel refuels at a pump. Trailers also says what this game's player is towing and
/// offers a way to ask for a trailer, so a map's jobs and depot can use them. Plain data, main thread only.
/// </summary>
internal static class WorldSites
{
    /// <summary>A bay where a depot's trailers wait: world position, the heading a trailer stands at (degrees, its
    /// hitch pointing that way) and the trailer kind the bay is for ("utility", "camper", "hauler", "boat"; "" any).</summary>
    public readonly struct Bay
    {
        public readonly float X, Y, Z, Yaw;
        public readonly string Kind;
        public readonly string Name;

        public Bay(string name, float x, float y, float z, float yaw, string kind)
        {
            Name = name ?? "";
            X = x; Y = y; Z = z; Yaw = yaw;
            Kind = kind ?? "";
        }
    }

    /// <summary>A place that refuels (a pump): world position and how close counts, metres.</summary>
    public readonly struct Station
    {
        public readonly float X, Y, Z, Radius;
        public readonly string Name;

        public Station(string name, float x, float y, float z, float radius)
        {
            Name = name ?? "";
            X = x; Y = y; Z = z; Radius = radius;
        }
    }

    private static readonly Dictionary<string, Bay[]> BaysByOwner = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Station[]> StationsByOwner = new(StringComparer.Ordinal);
    private static List<Bay> _bays;
    private static List<Station> _stations;

    /// <summary>How near a depot's bays a car must stand for its trailer to go into a bay, metres.</summary>
    public const float DepotReach = 45f;

    /// <summary>Replaces <paramref name="owner"/>'s bays (null or empty: none).</summary>
    public static void SetBays(string owner, IReadOnlyCollection<Bay> bays)
    {
        if (string.IsNullOrEmpty(owner)) return;
        if (bays == null || bays.Count == 0) BaysByOwner.Remove(owner);
        else
        {
            var a = new Bay[bays.Count];
            int i = 0;
            foreach (var b in bays) a[i++] = b;
            BaysByOwner[owner] = a;
        }
        _bays = null;
    }

    /// <summary>Replaces <paramref name="owner"/>'s fuel stations (null or empty: none).</summary>
    public static void SetStations(string owner, IReadOnlyCollection<Station> stations)
    {
        if (string.IsNullOrEmpty(owner)) return;
        if (stations == null || stations.Count == 0) StationsByOwner.Remove(owner);
        else
        {
            var a = new Station[stations.Count];
            int i = 0;
            foreach (var s in stations) a[i++] = s;
            StationsByOwner[owner] = a;
        }
        _stations = null;
    }

    public static IReadOnlyList<Bay> Bays
    {
        get
        {
            if (_bays != null) return _bays;
            var keys = new List<string>(BaysByOwner.Keys);
            keys.Sort(StringComparer.Ordinal);
            var l = new List<Bay>();
            foreach (var k in keys) l.AddRange(BaysByOwner[k]);
            return _bays = l;
        }
    }

    public static IReadOnlyList<Station> Stations
    {
        get
        {
            if (_stations != null) return _stations;
            var keys = new List<string>(StationsByOwner.Keys);
            keys.Sort(StringComparer.Ordinal);
            var l = new List<Station>();
            foreach (var k in keys) l.AddRange(StationsByOwner[k]);
            return _stations = l;
        }
    }

    /// <summary>True when (x, z) is within a station's radius (height ignored: a pump stands on the ground).</summary>
    public static bool AtStation(float x, float z)
    {
        foreach (var s in Stations)
        {
            float dx = x - s.X, dz = z - s.Z;
            if (dx * dx + dz * dz <= s.Radius * s.Radius) return true;
        }
        return false;
    }

    /// <summary>True when (x, z) is within <see cref="DepotReach"/> of any bay.</summary>
    public static bool AtDepot(float x, float z)
    {
        foreach (var b in Bays)
        {
            float dx = x - b.X, dz = z - b.Z;
            if (dx * dx + dz * dz <= DepotReach * DepotReach) return true;
        }
        return false;
    }

    /// <summary>
    /// The bay a <paramref name="kind"/> trailer asked for at (x, z) goes to: among the bays within
    /// <see cref="DepotReach"/>, not taken (<paramref name="taken"/> says whether a trailer already stands in one), the
    /// one meant for that kind first, else any free one, nearest first. False when none is free (or no depot is near).
    /// </summary>
    public static bool PickBay(float x, float z, string kind, Func<Bay, bool> taken, out Bay bay)
    {
        bay = default;
        bool found = false;
        int bestRank = int.MaxValue;
        float bestD = float.MaxValue;
        foreach (var b in Bays)
        {
            float dx = x - b.X, dz = z - b.Z;
            float d = dx * dx + dz * dz;
            if (d > DepotReach * DepotReach) continue;
            if (taken != null && taken(b)) continue;
            int rank = string.Equals(b.Kind, kind ?? "", StringComparison.OrdinalIgnoreCase) ? 0 : b.Kind.Length == 0 ? 1 : 2;
            if (rank < bestRank || (rank == bestRank && d < bestD))
            {
                bestRank = rank;
                bestD = d;
                bay = b;
                found = true;
            }
        }
        return found;
    }

    /// <summary>The kind of trailer this game's player tows right now ("utility", ...; null: none), set by Trailers.</summary>
    public static string LocalTowing { get; set; }

    /// <summary>True when the player (PlayerRef.RawEncoded) has a trailer hitched, as this game knows it. Set by Trailers
    /// while it runs (null otherwise).</summary>
    public static Func<int, bool> PlayerTowing { get; set; }

    /// <summary>The kind of trailer the player (PlayerRef.RawEncoded) has hitched as this game knows it ("utility", "camper",
    /// "hauler", "boat", "semi"; null: none). Set by Trailers while it runs (null otherwise). On the host it knows every
    /// player's trailer, so a job can check the kind a guest tows without taking the guest's word for it.</summary>
    public static Func<int, string> PlayerTowingKind { get; set; }

    /// <summary>Asks for a trailer of a kind ("utility", "camper", "hauler", "boat"); returns a status line. Set by
    /// Trailers while it runs (null otherwise).</summary>
    public static Func<string, string> RequestTrailer { get; set; }

    /// <summary>Where a boat trailer's boat is (the numbers are on the trailers' wire: append only).</summary>
    public enum BoatAt : byte
    {
        /// <summary>Strapped on the bunks.</summary>
        OnTrailer = 0,
        /// <summary>Out with its driver: the player is in the boat (nothing of it is drawn by the trailer).</summary>
        Out = 1,
        /// <summary>Afloat by itself, moored where it was left (floated off the trailer, or its driver went ashore): drawn
        /// there by the trailer's mod until someone takes it or it is winched back on.</summary>
        Afloat = 2,
    }

    /// <summary>A boat trailer as Trailers+ knows it on this game (the host's is the one that counts): its network id, owner
    /// (PlayerRef.RawEncoded), whether it is hitched, where its boat is and whether that is a boat a player can drive (a
    /// boat mod's; otherwise the trailer's own runabout, for show), where the boat's root stands on the bunks (world x, z)
    /// and how high its keel is there, where the boat floats when <see cref="BoatAt.Afloat"/> (its root's x, z and heading),
    /// the coupler, and the trailer's heading (degrees, the way the hitch points; the boat on it faces the same way).</summary>
    public readonly struct BoatTrailer
    {
        public readonly uint NetId;
        public readonly int Owner;
        public readonly bool Hitched, Drivable;
        public readonly BoatAt At;
        public readonly float SeatX, SeatZ, KeelY, BoatX, BoatZ, BoatYaw, HitchX, HitchY, HitchZ, Yaw;

        public BoatTrailer(uint netId, int owner, bool hitched, bool drivable, BoatAt at, float seatX, float seatZ, float keelY,
            float boatX, float boatZ, float boatYaw, float hitchX, float hitchY, float hitchZ, float yaw)
        {
            NetId = netId; Owner = owner; Hitched = hitched; Drivable = drivable; At = at;
            SeatX = seatX; SeatZ = seatZ; KeelY = keelY; BoatX = boatX; BoatZ = boatZ; BoatYaw = boatYaw;
            HitchX = hitchX; HitchY = hitchY; HitchZ = hitchZ; Yaw = yaw;
        }

        /// <summary>The boat is on the bunks.</summary>
        public bool Loaded => At == BoatAt.OnTrailer;
    }

    /// <summary>The player's boat trailer (the one they tow, else their nearest), or null. Set by Trailers+ while it runs;
    /// a boat mod launches from it and lands back on it.</summary>
    public static Func<int, BoatTrailer?> BoatTrailerOf { get; set; }

    /// <summary>Host: where a boat trailer's boat is now, for everyone (for <see cref="BoatAt.Afloat"/>: the root's x, z and
    /// heading where it floats); a trailer whose boat is out or afloat stays when its owner changes car (they are out in
    /// the boat, or went back for the car). Returns null or why not. Set by Trailers+.</summary>
    public static Func<uint, BoatAt, float, float, float, string> SetTrailerBoat { get; set; }

    /// <summary>Host: hitches the boat trailer to the player's car as soon as that car stands near its coupler (the boat mod
    /// just put the car in front of it, or is about to). Null or why not. Set by Trailers+.</summary>
    public static Func<uint, int, string> HitchBoatTrailer { get; set; }

    /// <summary>The item id of the boat a boat trailer carries while a boat mod runs (the Grayling sets it; 0: none, and the
    /// trailer carries its own runabout, which can't be launched). Every peer of a session runs the same server mods.</summary>
    public static uint TrailerBoatCar { get; set; }

    /// <summary>How deep that boat's keel sits afloat at rest, metres (the trailer draws it moored at this draft).</summary>
    public static float TrailerBoatDraft { get; set; } = 0.13f;

    /// <summary>Forgets everything (tests).</summary>
    public static void Clear()
    {
        BoatTrailerOf = null;
        SetTrailerBoat = null;
        HitchBoatTrailer = null;
        TrailerBoatCar = 0;
        TrailerBoatDraft = 0.13f;
        BaysByOwner.Clear();
        StationsByOwner.Clear();
        _bays = null;
        _stations = null;
        LocalTowing = null;
        RequestTrailer = null;
        PlayerTowing = null;
        PlayerTowingKind = null;
    }
}
