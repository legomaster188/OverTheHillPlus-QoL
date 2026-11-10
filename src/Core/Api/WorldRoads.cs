using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Api;

/// <summary>
/// How far it really is to drive between two points: the shortest way over the world's published roads (a route of kind "road",
/// not a track tagged "hard"), with the straight stretches from each point to the nearest road. A job across a lake pays and
/// reads for the drive round it, not for the width of the water. Where a world has no roads, or the two points aren't
/// joined by any, it is the distance on the ground. Main thread only, like <see cref="WorldPlaces"/>; pure code.
/// </summary>
public static class WorldRoads
{
    private const float NodeEvery = 20f, JoinWithin = 14f;

    private sealed class Net
    {
        public int Version;
        public float[] X, Z;
        public List<(int To, float Len)>[] Edges;
        public readonly Dictionary<int, float[]> Paths = new();
    }

    private static Net _net;

    /// <summary>Road metres from (ax, az) to (bx, bz), never less than the distance on the ground.</summary>
    public static float Metres(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        float straight = MathF.Sqrt(dx * dx + dz * dz);
        var net = Get();
        if (net == null) return straight;
        int a = Nearest(net, ax, az, out float da), b = Nearest(net, bx, bz, out float db);
        if (a < 0 || b < 0) return straight;
        if (!net.Paths.TryGetValue(a, out var path))
        {
            if (net.Paths.Count >= 16) net.Paths.Clear();
            net.Paths[a] = path = Dijkstra(net, a);
        }
        float road = path[b];
        if (float.IsInfinity(road)) return straight;
        return MathF.Max(straight, road + da + db);
    }

    private static int Nearest(Net n, float x, float z, out float d)
    {
        int best = -1;
        float bd = float.MaxValue;
        for (int i = 0; i < n.X.Length; i++)
        {
            float dx = n.X[i] - x, dz = n.Z[i] - z, q = dx * dx + dz * dz;
            if (q < bd) { bd = q; best = i; }
        }
        d = best >= 0 ? MathF.Sqrt(bd) : 0f;
        return best;
    }

    private static Net Get()
    {
        if (_net != null && _net.Version == WorldPlaces.Version) return _net.X.Length > 0 ? _net : null;
        var xs = new List<float>();
        var zs = new List<float>();
        var edges = new List<(int A, int B, float Len)>();
        var owner = new List<int>();
        var routes = WorldPlaces.Routes(WorldKinds.Road);
        for (int r = 0; r < routes.Count; r++)
        {
            var w = routes[r];
            if (w == null || w.Count < 2 || Hard(w)) continue;
            int last = -1;
            float run = 0f;
            for (int i = 0; i < w.Count; i++)
            {
                if (i > 0)
                {
                    float dx = w.Points[3 * i] - w.Points[3 * i - 3], dz = w.Points[3 * i + 2] - w.Points[3 * i - 1];
                    run += MathF.Sqrt(dx * dx + dz * dz);
                }
                if (last >= 0 && run < NodeEvery && i != w.Count - 1) continue;
                xs.Add(w.Points[3 * i]);
                zs.Add(w.Points[3 * i + 2]);
                owner.Add(r);
                int n = xs.Count - 1;
                if (last >= 0) edges.Add((last, n, run));
                last = n;
                run = 0f;
            }
        }
        var net = new Net { Version = WorldPlaces.Version, X = xs.ToArray(), Z = zs.ToArray() };
        net.Edges = new List<(int, float)>[net.X.Length];
        for (int i = 0; i < net.Edges.Length; i++) net.Edges[i] = new List<(int, float)>(3);
        foreach (var (a, b, len) in edges) { net.Edges[a].Add((b, len)); net.Edges[b].Add((a, len)); }
        // where roads meet: nodes of two roads close together (a grid, so it isn't every pair)
        var cells = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < net.X.Length; i++)
        {
            var key = ((int)MathF.Floor(net.X[i] / JoinWithin), (int)MathF.Floor(net.Z[i] / JoinWithin));
            if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<int>(2);
            l.Add(i);
        }
        foreach (var kv in cells)
            for (int di = -1; di <= 1; di++)
                for (int dj = -1; dj <= 1; dj++)
                {
                    if (!cells.TryGetValue((kv.Key.Item1 + di, kv.Key.Item2 + dj), out var other)) continue;
                    foreach (int a in kv.Value)
                        foreach (int b in other)
                        {
                            if (a >= b || owner[a] == owner[b]) continue;
                            float dx = net.X[a] - net.X[b], dz = net.Z[a] - net.Z[b], d = MathF.Sqrt(dx * dx + dz * dz);
                            if (d > JoinWithin) continue;
                            net.Edges[a].Add((b, d + 1f));
                            net.Edges[b].Add((a, d + 1f));
                        }
                }
        _net = net;
        return net.X.Length > 0 ? net : null;
    }

    private static bool Hard(WorldRoute r)
    {
        foreach (var t in r.Tags) if (string.Equals(t, "hard", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static float[] Dijkstra(Net n, int from)
    {
        var dist = new float[n.X.Length];
        Array.Fill(dist, float.PositiveInfinity);
        dist[from] = 0f;
        var heap = new PriorityQueue<int, float>();
        heap.Enqueue(from, 0f);
        while (heap.TryDequeue(out int u, out float du))
        {
            if (du > dist[u]) continue;
            foreach (var (v, len) in n.Edges[u])
            {
                float d = du + len;
                if (d < dist[v]) { dist[v] = d; heap.Enqueue(v, d); }
            }
        }
        return dist;
    }
}
