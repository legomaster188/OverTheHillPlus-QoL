using System;
using System.Collections.Generic;
using System.Threading;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>A shape as a Unity mesh takes it: plain arrays in the space it is drawn in (the shape's own transform applied).</summary>
public sealed class ShapeArrays
{
    /// <summary>x, y, z per vertex.</summary>
    public float[] Positions;
    /// <summary>x, y, z per vertex, unit length.</summary>
    public float[] Normals;
    /// <summary>u, v per vertex.</summary>
    public float[] Uvs;
    public int[] Indices;

    public int VertexCount => Positions.Length / 3;
}

/// <summary>A decoded texture: RGBA32 with its full mip chain, bottom row first (what LoadRawTextureData takes).</summary>
public sealed class TexturePixelData
{
    public byte[] Chain;
    public int Width, Height;
}

/// <summary>
/// Everything of a part that can be worked out without the game, so a background thread does it: a shape's arrays as the
/// mesh takes them, its glowing lenses, the split of a twinkling part into its bulb sets, and a texture's pixels. The game
/// thread is left with handing the results to Unity. Each step does what the main-thread code it replaces did (core's
/// CarAssets.BuildMesh for the arrays, the part lights' lens search and bulb split), so a part looks the same.
/// Pure code (unit-tested).
/// </summary>
public static class PartDecode
{
    /// <summary>The shape's own matrix (position, rotation, scale) as the three rows <see cref="MeshMath"/> takes.</summary>
    public static float[] Rows(MeshPart p)
    {
        var f = MeshFrames.PartMatrix(p);   // column-major: index = column * 4 + row
        return new[] { f[0], f[4], f[8], f[12], f[1], f[5], f[9], f[13], f[2], f[6], f[10], f[14] };
    }

    /// <summary>The arrays of a Unity mesh for one shape.</summary>
    public static ShapeArrays Arrays(MeshPart shape)
    {
        int nv = shape.VertexCount;
        var a = new ShapeArrays { Positions = new float[nv * 3], Normals = new float[nv * 3], Uvs = new float[nv * 2], Indices = shape.Indices };
        MeshMath.Transform(Rows(shape), shape.Positions, shape.Normals, nv, a.Positions, a.Normals);
        Array.Copy(shape.Uv0, a.Uvs, nv * 2);
        return a;
    }

    /// <summary>
    /// The lenses of a shape (<see cref="LampSpots.Find"/>), in the drawn mesh's space. <paramref name="lights"/> and
    /// <paramref name="albedo"/> are RGBA32 pixels, bottom row first (albedo may be null: white).
    /// </summary>
    public static List<LampSpot> Spots(MeshPart shape, byte[] lights, int lw, int lh, byte[] albedo, int aw, int ah)
    {
        if (shape == null || lights == null || lw <= 0 || lh <= 0) return new List<LampSpot>();
        LampSpots.Sampler l = (float u, float v, out float r, out float g, out float b) => Sample(lights, lw, lh, u, v, out r, out g, out b);
        LampSpots.Sampler a = albedo == null || aw <= 0 || ah <= 0 ? null
            : (float u, float v, out float r, out float g, out float b) => Sample(albedo, aw, ah, u, v, out r, out g, out b);
        var list = LampSpots.Find(shape.Positions, shape.Uv0, shape.Indices, l, a);
        var m = Rows(shape);
        foreach (var s in list)
        {
            float x = s.X, y = s.Y, z = s.Z;
            s.X = m[0] * x + m[1] * y + m[2] * z + m[3];
            s.Y = m[4] * x + m[5] * y + m[6] * z + m[7];
            s.Z = m[8] * x + m[9] * y + m[10] * z + m[11];
            float nx = m[0] * s.NX + m[1] * s.NY + m[2] * s.NZ, ny = m[4] * s.NX + m[5] * s.NY + m[6] * s.NZ, nz = m[8] * s.NX + m[9] * s.NY + m[10] * s.NZ;
            float sq = nx * nx + ny * ny + nz * nz;
            if (sq > 1e-8f)
            {
                float mag = MathF.Sqrt(sq);
                s.NX = nx / mag; s.NY = ny / mag; s.NZ = nz / mag;
            }
        }
        return list;
    }

    /// <summary>One texel (nearest), 0-1 per channel; (u, v) with v up, as Unity keeps the rows.</summary>
    public static void Sample(byte[] px, int w, int h, float u, float v, out float r, out float g, out float b)
    {
        int x = Math.Clamp((int)(u * w), 0, w - 1), y = Math.Clamp((int)(v * h), 0, h - 1);
        int i = (y * w + x) * 4;
        r = px[i] / 255f;
        g = px[i + 1] / 255f;
        b = px[i + 2] / 255f;
    }

    /// <summary>
    /// A twinkling shape cut in four: the three sets of head-lamp bulbs ([0..2], null for an empty set) and the shape
    /// without them ([3]). Null when it has no head-lamp lens, or nothing but bulbs.
    /// </summary>
    public static ShapeArrays[] TwinkleSets(MeshPart shape, IReadOnlyList<LampSpot> spots)
    {
        if (shape == null || spots == null) return null;
        int nt = shape.Indices.Length / 3;
        var bySet = new List<int>[] { new(), new(), new() };
        var bulb = new bool[nt];
        int bulbs = 0;
        foreach (var s in spots)
        {
            if (s.Channel != LampChannel.Head) continue;
            bySet[s.Set % 3].AddRange(s.Triangles);
            foreach (int t in s.Triangles)
                if (!bulb[t]) { bulb[t] = true; bulbs++; }
        }
        if (bulbs == 0 || bulbs == nt) return null;
        var rest = new List<int>(nt - bulbs);
        for (int t = 0; t < nt; t++) if (!bulb[t]) rest.Add(t);
        var sets = new ShapeArrays[4];
        for (int k = 0; k < 3; k++) sets[k] = bySet[k].Count == 0 ? null : Arrays(Subset(shape, bySet[k]));
        sets[3] = Arrays(Subset(shape, rest));
        return sets;
    }

    /// <summary>A shape of some of another's triangles, its vertices compacted in the order the triangles use them.</summary>
    public static MeshPart Subset(MeshPart shape, IReadOnlyList<int> triangles)
    {
        var map = new int[shape.VertexCount];
        Array.Fill(map, -1);
        var idx = new int[triangles.Count * 3];
        var order = new List<int>();
        int k = 0;
        foreach (int t in triangles)
            for (int c = 0; c < 3; c++)
            {
                int v = shape.Indices[t * 3 + c];
                if (map[v] < 0) { map[v] = order.Count; order.Add(v); }
                idx[k++] = map[v];
            }
        var sub = new MeshPart
        {
            Name = shape.Name, Kind = shape.Kind, Position = shape.Position, Rotation = shape.Rotation, Scale = shape.Scale,
            Positions = new float[order.Count * 3], Normals = new float[order.Count * 3], Uv0 = new float[order.Count * 2], Indices = idx,
        };
        for (int i = 0; i < order.Count; i++)
        {
            int v = order[i];
            Array.Copy(shape.Positions, v * 3, sub.Positions, i * 3, 3);
            Array.Copy(shape.Normals, v * 3, sub.Normals, i * 3, 3);
            Array.Copy(shape.Uv0, v * 2, sub.Uv0, i * 2, 2);
        }
        return sub;
    }

    /// <summary>A PNG's pixels with their mip chain; null for a kind of PNG the plain decoder doesn't read (the game's
    /// own decoder takes it then).</summary>
    public static TexturePixelData Texture(byte[] png)
    {
        var rgba = TexturePixels.DecodePng(png, out int w, out int h);
        return rgba == null ? null : new TexturePixelData { Chain = TexturePixels.MipChain(rgba, w, h), Width = w, Height = h };
    }
}

/// <summary>
/// A few background threads that run the parts' decoding, first come first served. They start with the first job and then
/// wait for the next; below the game's own threads in priority, so a busy machine draws its frames first. A job reports
/// its own outcome (it must not throw: anything it lets out is kept in <see cref="LastError"/> for the game thread to log).
/// Pure code (unit-tested).
/// </summary>
public sealed class PartWorker
{
    private readonly Queue<Action> _jobs = new();
    private readonly int _max;
    private readonly string _name;
    private int _threads, _idle;

    public PartWorker(int threads, string name)
    {
        _max = Math.Max(1, threads);
        _name = name ?? "parts";
    }

    /// <summary>Jobs waiting for a thread.</summary>
    public int Waiting { get { lock (_jobs) return _jobs.Count; } }

    /// <summary>What the last job that threw said (null when none did).</summary>
    public volatile string LastError;

    public void Run(Action job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        lock (_jobs)
        {
            _jobs.Enqueue(job);
            // another thread only when every one that runs is busy
            if (_idle == 0 && _threads < _max)
            {
                _threads++;
                var t = new Thread(Loop) { IsBackground = true, Name = _name + " " + _threads, Priority = ThreadPriority.BelowNormal };
                t.Start();
            }
            Monitor.Pulse(_jobs);
        }
    }

    private void Loop()
    {
        while (true)
        {
            Action job;
            lock (_jobs)
            {
                while (_jobs.Count == 0)
                {
                    _idle++;
                    Monitor.Wait(_jobs);
                    _idle--;
                }
                job = _jobs.Dequeue();
            }
            try { job(); }
            catch (Exception e) { LastError = e.ToString(); }
        }
    }
}
