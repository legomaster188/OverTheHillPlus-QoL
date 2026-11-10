using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Api;

/// <summary>
/// The place kinds world mods publish. Career jobs read them by these names (<c>OfferContext.Places(kind)</c>), so the
/// common ones match the career's own: "depot", "fuel", "camp", "shop", "poi".
/// </summary>
public static class WorldKinds
{
    public const string Depot = "depot";            // a yard where loads are picked up and dropped
    public const string Bay = "bay";                // one trailer bay of a depot (Tags: the trailer kind)
    public const string Shop = "shop";              // any shop (a shop's own kind is below; it lists "shop" in Also)
    public const string Dealer = "dealer";          // sells cars
    public const string Parts = "parts";            // sells parts and upgrades
    public const string Store = "store";            // general store
    public const string Fuel = "fuel";              // a fuel pump (Radius: how close counts)
    public const string Garage = "garage";          // repairs
    public const string Camp = "camp";
    public const string Lodge = "lodge";
    public const string Town = "town";
    public const string Poi = "poi";
    public const string Scenic = "scenic";
    public const string TowerPeak = "tower_peak";   // the radio tower's summit pad
    public const string Lake = "lake";              // a lake's centre (the Water entry has its outline)
    public const string BoatRamp = "boat_ramp";     // Yaw: down the ramp, into the water
    public const string Dock = "dock";
    public const string Fishing = "fishing";        // Tags: "ice" for a hole in the ice, "boat" reached by boat
    public const string Mine = "mine";
    public const string Quarry = "quarry";
    public const string Logging = "logging";        // log landings and yards
    public const string Ranch = "ranch";
    public const string Pickup = "pickup";          // job pick-up points (Tags: what loads there)
    public const string Dropoff = "dropoff";        // job drop-off points (Tags: what is wanted there)
    public const string Spawn = "spawn";            // where a player may be put on the map (Yaw: facing)
    public const string Arrival = "arrival";        // where a trip onto the map lands
    public const string Trailhead = "trailhead";    // where the trip onto a map starts (it stands on the land outside the map)
    public const string Hard = "hard";              // a hard piece of track (Tags: rock_steps, off_camber, ford, chute, descent...)
    public const string Expansion = "expansion";    // kept for later: a reserved plot, or a road that stops at the map's edge

    // route kinds
    public const string Road = "road";
    public const string Race = "race";
    public const string Plow = "plow";
    public const string Trail = "trail";

    // water kinds
    public const string LakeWater = "lake";
    public const string RiverWater = "river";
}

/// <summary>A named spot of a world: world position (Unity axes, metres), a radius on the ground, a heading.</summary>
public sealed class WorldPlace
{
    /// <summary>Unique: "&lt;area&gt;.&lt;key&gt;" ("skadi.hollowmere_fuel").</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    /// <summary>Other kinds it also counts as (a parts shop is "parts" and also "shop"). <see cref="WorldPlaces.OfKind"/>
    /// lists it under each.</summary>
    public string[] Also { get; init; } = Array.Empty<string>();
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    /// <summary>How close counts as there, metres (on the ground plane).</summary>
    public float Radius { get; init; } = 25f;
    /// <summary>Degrees, Unity's yaw (0 = +z, 90 = +x).</summary>
    public float Yaw { get; init; }
    /// <summary>The map or area it belongs to ("skadi", or a stock area name).</summary>
    public string Area { get; init; } = "";
    /// <summary>Set by <see cref="WorldPlaces.Publish"/>: the mod that published it.</summary>
    public string Owner { get; internal set; } = "";
    /// <summary>Free words ("utility", "ice", "logs"...).</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
    /// <summary>Extra data as text ("pay" = "hard", "load" = "steel"...).</summary>
    public IReadOnlyDictionary<string, string> Props { get; init; } = EmptyProps;

    internal static readonly IReadOnlyDictionary<string, string> EmptyProps = new Dictionary<string, string>();

    public bool HasTag(string tag)
    {
        foreach (var t in Tags) if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when it is of this kind, as its own or one of <see cref="Also"/>.</summary>
    public bool IsKind(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return false;
        if (string.Equals(Kind, kind, StringComparison.Ordinal)) return true;
        foreach (var k in Also) if (string.Equals(k, kind, StringComparison.Ordinal)) return true;
        return false;
    }

    public string Prop(string key, string fallback = null) =>
        key != null && Props.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>Distance on the ground plane.</summary>
    public float GroundDistance(float x, float z)
    {
        float dx = x - X, dz = z - Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public bool Contains(float x, float z) => GroundDistance(x, z) <= Radius;
}

/// <summary>A line through the world: a road, a race route, a plow route. Points are x, y, z triplets.</summary>
public sealed class WorldRoute
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    /// <summary>x0, y0, z0, x1, y1, z1, ... (world metres).</summary>
    public float[] Points { get; init; } = Array.Empty<float>();
    /// <summary>Driven width, metres.</summary>
    public float Width { get; init; }
    /// <summary>"paved", "gravel", "dirt", "snow".</summary>
    public string Surface { get; init; } = "";
    /// <summary>A loop (a race that ends where it starts).</summary>
    public bool Loop { get; init; }
    /// <summary>Plow routes: how many segments it is cut into for clearing (0 for other kinds).</summary>
    public int Segments { get; init; }
    public string Area { get; init; } = "";
    public string Owner { get; internal set; } = "";
    public string[] Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Props { get; init; } = WorldPlace.EmptyProps;

    public int Count => Points.Length / 3;

    /// <summary>The route's length along its points, metres.</summary>
    public float Length
    {
        get
        {
            float l = 0f;
            for (int i = 1; i < Count; i++) l += Seg(i - 1);
            return l;
        }
    }

    private float Seg(int i)
    {
        float dx = Points[3 * i + 3] - Points[3 * i], dy = Points[3 * i + 4] - Points[3 * i + 1], dz = Points[3 * i + 5] - Points[3 * i + 2];
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The nearest point of the route to (x, z) on the ground plane: its distance and how far along the route
    /// it is (metres from the first point).</summary>
    public float Nearest(float x, float z, out float along)
    {
        along = 0f;
        float best = float.MaxValue, run = 0f;
        for (int i = 0; i + 1 < Count; i++)
        {
            float ax = Points[3 * i], az = Points[3 * i + 2], bx = Points[3 * i + 3], bz = Points[3 * i + 5];
            float vx = bx - ax, vz = bz - az, l2 = vx * vx + vz * vz;
            float t = l2 > 1e-6f ? Math.Clamp(((x - ax) * vx + (z - az) * vz) / l2, 0f, 1f) : 0f;
            float px = ax + vx * t - x, pz = az + vz * t - z;
            float d = MathF.Sqrt(px * px + pz * pz);
            float seg = Seg(i);
            if (d < best) { best = d; along = run + seg * t; }
            run += seg;
        }
        return best;
    }

    /// <summary>The point at a distance along the route (clamped to its ends). False for a route without points.</summary>
    public bool PointAt(float along, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        int n = Count;
        if (n == 0) return false;
        if (n == 1 || !(along > 0f)) { x = Points[0]; y = Points[1]; z = Points[2]; return true; }
        for (int i = 0; i + 1 < n; i++)
        {
            float seg = Seg(i);
            if (along <= seg || i + 2 == n)
            {
                float t = seg > 1e-6f ? Math.Clamp(along / seg, 0f, 1f) : 0f;
                x = Points[3 * i] + (Points[3 * i + 3] - Points[3 * i]) * t;
                y = Points[3 * i + 1] + (Points[3 * i + 4] - Points[3 * i + 1]) * t;
                z = Points[3 * i + 2] + (Points[3 * i + 5] - Points[3 * i + 2]) * t;
                return true;
            }
            along -= seg;
        }
        return true;
    }

    /// <summary>Evenly spaced points along the route, about <paramref name="spacing"/> metres apart, both ends included
    /// (race checkpoints, plow segment ends): x, y, z triplets.</summary>
    public float[] Spaced(float spacing)
    {
        float len = Length;
        if (Count == 0) return Array.Empty<float>();
        int k = Math.Max(1, (int)MathF.Round(len / Math.Max(1f, spacing)));
        var a = new float[3 * (k + 1)];
        for (int i = 0; i <= k; i++)
        {
            PointAt(len * i / k, out a[3 * i], out a[3 * i + 1], out a[3 * i + 2]);
        }
        return a;
    }

    /// <summary>The plow segment at a distance along the route (0..Segments-1), or -1.</summary>
    public int SegmentAt(float along)
    {
        if (Segments <= 0) return -1;
        float len = Length;
        if (!(len > 0f)) return -1;
        return Math.Clamp((int)(along / len * Segments), 0, Segments - 1);
    }
}

/// <summary>A body of water: a lake (flat, an outline) or a river (a line with a width and a falling surface).</summary>
public sealed class WorldWater
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>"lake" or "river".</summary>
    public string Kind { get; init; } = "";
    /// <summary>Lakes: the surface height (world y).</summary>
    public float Level { get; init; }
    /// <summary>Frozen: the surface is ice you can drive on (no boating, no flooding).</summary>
    public bool Frozen { get; init; }
    /// <summary>Lakes: the shore as x, z pairs (world metres, closed). Rivers: the centre line as x, z pairs.</summary>
    public float[] Outline { get; init; } = Array.Empty<float>();
    /// <summary>Rivers: the water surface height at each centre line point.</summary>
    public float[] Surface { get; init; } = Array.Empty<float>();
    /// <summary>Rivers: the width at each centre line point, metres.</summary>
    public float[] Widths { get; init; } = Array.Empty<float>();
    public string Area { get; init; } = "";
    public string Owner { get; internal set; } = "";

    /// <summary>True when (x, z) is on this water; <paramref name="level"/> is the surface height there.</summary>
    public bool Contains(float x, float z, out float level)
    {
        level = Level;
        if (Kind == WorldKinds.LakeWater) return Inside(Outline, x, z);
        int n = Outline.Length / 2;
        float best = float.MaxValue;
        for (int i = 0; i + 1 < n; i++)
        {
            float ax = Outline[2 * i], az = Outline[2 * i + 1], bx = Outline[2 * i + 2], bz = Outline[2 * i + 3];
            float vx = bx - ax, vz = bz - az, l2 = vx * vx + vz * vz;
            float t = l2 > 1e-6f ? Math.Clamp(((x - ax) * vx + (z - az) * vz) / l2, 0f, 1f) : 0f;
            float px = ax + vx * t - x, pz = az + vz * t - z;
            float d = MathF.Sqrt(px * px + pz * pz);
            float half = 0.5f * (At(Widths, i, t));
            if (d <= half && d < best)
            {
                best = d;
                level = At(Surface, i, t);
            }
        }
        return best < float.MaxValue;
    }

    private static float At(float[] a, int i, float t)
    {
        if (a == null || a.Length == 0) return 0f;
        if (i + 1 >= a.Length) return a[a.Length - 1];
        return a[i] + (a[i + 1] - a[i]) * t;
    }

    /// <summary>Even-odd point-in-polygon over x, z pairs.</summary>
    public static bool Inside(float[] poly, float x, float z)
    {
        int n = poly.Length / 2;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = poly[2 * i], zi = poly[2 * i + 1], xj = poly[2 * j], zj = poly[2 * j + 1];
            if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// The world's named places, routes and waters, published by world mods (Skadi's Hollow's map, the stock-map parts) and
/// read by everyone else (the career's jobs, haulage, boats, fishing, the minimap) without referencing each other.
/// Main thread only. Lists come sorted by owner, then id, so every peer sees the same order.
/// </summary>
public static class WorldPlaces
{
    private sealed class Book
    {
        public WorldPlace[] Places = Array.Empty<WorldPlace>();
        public WorldRoute[] Routes = Array.Empty<WorldRoute>();
        public WorldWater[] Waters = Array.Empty<WorldWater>();
        public Func<string, int, bool, bool> Surface;
        public Land Land;
    }

    /// <summary>A map's ground plane rectangle (see <see cref="SetLand"/>).</summary>
    private sealed class Land
    {
        public float MinX, MinZ, MaxX, MaxZ;

        public bool Holds(float x, float z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    }

    private static readonly SortedDictionary<string, Book> Books = new(StringComparer.Ordinal);
    private static List<WorldPlace> _all;
    private static Dictionary<string, List<WorldPlace>> _byKind;
    private static List<WorldRoute> _routes;
    private static List<WorldWater> _waters;
    private static readonly Dictionary<string, bool[]> Cleared = new(StringComparer.Ordinal);

    /// <summary>Goes up by one at every change (cheap "did anything change" test).</summary>
    public static int Version { get; private set; }

    /// <summary>
    /// The game's area (its AreaInfo name) where a career's work is, set by a world mod at load: a new career's world opens it
    /// from the start, so the job map isn't behind the game's own portals. Empty: no such area.
    /// </summary>
    public static string CareerArea { get; set; } = "";

    /// <summary>The map a career plays on, set by a world mod at load (null: careers play on the game's own areas).</summary>
    public static ICareerWorld CareerWorld { get; set; }

    /// <summary>Something was published or cleared. Handlers that throw are skipped (and reported once).</summary>
    public static event Action Changed;

    /// <summary>Where a handler's exception goes (set by core to the log).</summary>
    internal static Action<string> Warn { get; set; }

    // ---------------------------------------------------------------- publishing

    /// <summary>Replaces everything <paramref name="owner"/> (your mod id) published with these. Places, routes and
    /// waters with an empty id are skipped; a later duplicate id of the same owner replaces the earlier one.</summary>
    public static void Publish(string owner, IEnumerable<WorldPlace> places, IEnumerable<WorldRoute> routes = null,
                               IEnumerable<WorldWater> waters = null)
    {
        if (string.IsNullOrEmpty(owner)) return;
        Books.TryGetValue(owner, out var old);
        var b = new Book { Surface = old?.Surface, Land = old?.Land };
        b.Places = Dedup(places, p => p.Id, p => p.Owner = owner);
        b.Routes = Dedup(routes, r => r.Id, r => r.Owner = owner);
        b.Waters = Dedup(waters, w => w.Id, w => w.Owner = owner);
        Books[owner] = b;
        Touch();
    }

    /// <summary>Removes everything <paramref name="owner"/> published (and its surface handler).</summary>
    public static void Clear(string owner)
    {
        if (string.IsNullOrEmpty(owner) || !Books.Remove(owner)) return;
        Touch();
    }

    /// <summary>Forgets everything (tests, session end).</summary>
    public static void Reset()
    {
        Books.Clear();
        Cleared.Clear();
        Touch();
    }

    private static T[] Dedup<T>(IEnumerable<T> src, Func<T, string> id, Action<T> own) where T : class
    {
        if (src == null) return Array.Empty<T>();
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var x in src)
        {
            if (x == null) continue;
            string k = id(x);
            if (string.IsNullOrEmpty(k)) continue;
            own(x);
            if (!map.ContainsKey(k)) order.Add(k);
            map[k] = x;
        }
        order.Sort(StringComparer.Ordinal);
        var a = new T[order.Count];
        for (int i = 0; i < a.Length; i++) a[i] = map[order[i]];
        return a;
    }

    private static void Touch()
    {
        _all = null;
        _byKind = null;
        _routes = null;
        _waters = null;
        Version++;
        var h = Changed;
        if (h == null) return;
        foreach (Action d in h.GetInvocationList())
        {
            try { d(); }
            catch (Exception e) { Warn?.Invoke("WorldPlaces.Changed handler threw: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- land

    /// <summary>
    /// Says that a rectangle of the world is <paramref name="owner"/>'s own land: a map you only reach by a trip, with no
    /// road to it from the land outside (a trailer can't come along). Jobs stay on one land, so a job never starts on a map
    /// and ends on the stock area next to it. <see cref="Clear"/> takes it away with the rest.
    /// </summary>
    public static void SetLand(string owner, float minX, float minZ, float maxX, float maxZ)
    {
        if (string.IsNullOrEmpty(owner) || !(maxX > minX) || !(maxZ > minZ)) return;
        if (!Books.TryGetValue(owner, out var b)) Books[owner] = b = new Book();
        b.Land = new Land { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ };
        Touch();
    }

    /// <summary>Whose land (x, z) is on: the owner that set it, or "" for the stock area around it.</summary>
    public static string LandAt(float x, float z)
    {
        foreach (var kv in Books)
            if (kv.Value.Land != null && kv.Value.Land.Holds(x, z)) return kv.Key;
        return "";
    }

    /// <summary>True when you can drive from one point to the other: both on the same land.</summary>
    public static bool SameLand(float x0, float z0, float x1, float z1) =>
        string.Equals(LandAt(x0, z0), LandAt(x1, z1), StringComparison.Ordinal);

    // ---------------------------------------------------------------- places

    public static IReadOnlyList<WorldPlace> All
    {
        get
        {
            if (_all != null) return _all;
            var l = new List<WorldPlace>();
            foreach (var b in Books.Values) l.AddRange(b.Places);
            return _all = l;
        }
    }

    /// <summary>Places of one kind ("depot", "fuel", "tower_peak"...). Never null.</summary>
    public static IReadOnlyList<WorldPlace> OfKind(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return Array.Empty<WorldPlace>();
        if (_byKind == null)
        {
            _byKind = new Dictionary<string, List<WorldPlace>>(StringComparer.Ordinal);
            foreach (var p in All)
            {
                Add(p.Kind, p);
                foreach (var k in p.Also) if (!string.IsNullOrEmpty(k) && k != p.Kind) Add(k, p);
            }
        }
        return _byKind.TryGetValue(kind, out var r) ? r : Array.Empty<WorldPlace>();

        void Add(string k, WorldPlace p)
        {
            if (!_byKind.TryGetValue(k, out var l)) _byKind[k] = l = new List<WorldPlace>();
            if (l.Count == 0 || !ReferenceEquals(l[l.Count - 1], p)) l.Add(p);
        }
    }

    /// <summary>
    /// The career's view of a kind: <paramref name="own"/> (what the career found itself: depots from trailer bays, fuel
    /// pumps, points of interest) plus every published place of that kind that isn't already one of them (a published
    /// place whose centre is inside one of <paramref name="own"/>'s radius, or theirs inside its, is the same spot).
    /// </summary>
    public static List<Place> ForCareer(string kind, IReadOnlyList<Place> own)
    {
        var l = new List<Place>();
        if (own != null) l.AddRange(own);
        int mine = l.Count;
        foreach (var w in OfKind(kind))
        {
            var p = new Place(string.IsNullOrWhiteSpace(w.Name) ? w.Id : w.Name, w.X, w.Y, w.Z, w.Radius);
            if (!p.IsValid) continue;
            bool same = false;
            for (int i = 0; i < mine && !same; i++)
            {
                float d = l[i].GroundDistance(p.X, p.Z);
                same = d <= l[i].Radius || d <= p.Radius;
            }
            if (!same) l.Add(p);
        }
        return l;
    }

    /// <summary>The place with this id, or null.</summary>
    public static WorldPlace Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var p in All) if (p.Id == id) return p;
        return null;
    }

    /// <summary>The nearest place of a kind to (x, z) within <paramref name="maxDistance"/>, or null.</summary>
    public static WorldPlace Nearest(string kind, float x, float z, float maxDistance = float.MaxValue)
    {
        WorldPlace best = null;
        float bd = maxDistance;
        foreach (var p in OfKind(kind))
        {
            float d = p.GroundDistance(x, z);
            if (d <= bd) { bd = d; best = p; }
        }
        return best;
    }

    /// <summary>The place of a kind whose radius (x, z) is inside, nearest first, or null.</summary>
    public static WorldPlace At(string kind, float x, float z)
    {
        WorldPlace best = null;
        float bd = float.MaxValue;
        foreach (var p in OfKind(kind))
        {
            float d = p.GroundDistance(x, z);
            if (d <= p.Radius && d < bd) { bd = d; best = p; }
        }
        return best;
    }

    /// <summary>Places of one area (a map's id), any kind.</summary>
    public static List<WorldPlace> InArea(string area)
    {
        var l = new List<WorldPlace>();
        foreach (var p in All) if (string.Equals(p.Area, area, StringComparison.Ordinal)) l.Add(p);
        return l;
    }

    // ---------------------------------------------------------------- routes

    /// <summary>Routes, all or of one kind ("race", "plow", "road", "trail").</summary>
    public static IReadOnlyList<WorldRoute> Routes(string kind = null)
    {
        if (_routes == null)
        {
            _routes = new List<WorldRoute>();
            foreach (var b in Books.Values) _routes.AddRange(b.Routes);
        }
        if (kind == null) return _routes;
        var l = new List<WorldRoute>();
        foreach (var r in _routes) if (r.Kind == kind) l.Add(r);
        return l;
    }

    public static WorldRoute FindRoute(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var r in Routes()) if (r.Id == id) return r;
        return null;
    }

    // ---------------------------------------------------------------- waters

    public static IReadOnlyList<WorldWater> Waters
    {
        get
        {
            if (_waters != null) return _waters;
            _waters = new List<WorldWater>();
            foreach (var b in Books.Values) _waters.AddRange(b.Waters);
            return _waters;
        }
    }

    /// <summary>The water (x, z) is on, if any, and its surface height there.</summary>
    public static bool WaterAt(float x, float z, out WorldWater water, out float level)
    {
        foreach (var w in Waters)
            if (w.Contains(x, z, out level)) { water = w; return true; }
        water = null;
        level = 0f;
        return false;
    }

    // ---------------------------------------------------------------- plow routes (surface changes)

    /// <summary>A world mod that can change a route's ground (snow ploughed off to gravel and back) registers how. The
    /// handler gets (route id, segment, cleared) and returns true when it changed the ground.</summary>
    public static void SetSurfaceHandler(string owner, Func<string, int, bool, bool> handler)
    {
        if (string.IsNullOrEmpty(owner)) return;
        if (!Books.TryGetValue(owner, out var b)) Books[owner] = b = new Book();
        b.Surface = handler;
    }

    /// <summary>
    /// Clears (or snows in again) one segment of a plow route on THIS game. The ground's grip changes with it, so every
    /// player in a session must make the same call: the host decides (its plow job) and tells the others over its own
    /// mod network channel; each game then calls this. Returns false when no world mod owns the route.
    /// </summary>
    public static bool SetCleared(string routeId, int segment, bool cleared)
    {
        var r = FindRoute(routeId);
        if (r == null || segment < 0 || segment >= Math.Max(1, r.Segments)) return false;
        if (!Cleared.TryGetValue(routeId, out var st)) Cleared[routeId] = st = new bool[Math.Max(1, r.Segments)];
        if (!Books.TryGetValue(r.Owner, out var b) || b.Surface == null) return false;
        bool ok;
        try { ok = b.Surface(routeId, segment, cleared); }
        catch (Exception e) { Warn?.Invoke("WorldPlaces surface handler threw: " + e.Message); return false; }
        if (ok) st[segment] = cleared;
        return ok;
    }

    /// <summary>True when the segment was cleared on this game (by <see cref="SetCleared"/>).</summary>
    public static bool IsCleared(string routeId, int segment) =>
        routeId != null && Cleared.TryGetValue(routeId, out var st) && segment >= 0 && segment < st.Length && st[segment];

    /// <summary>Forgets the cleared segments (a new session starts snowed in).</summary>
    public static void ResetCleared() => Cleared.Clear();
}
