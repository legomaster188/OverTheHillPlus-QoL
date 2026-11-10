using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>Which of the car's lamps a lens glows with: lights.png's R (tail and brake), G (head lamps) or B (reversing).</summary>
public enum LampChannel : byte { Tail = 0, Head = 1, Reverse = 2 }

/// <summary>
/// A part.json's <c>light</c> (Liveries/cosmetics/FORMAT.md): what a part adds to the glow of its lenses. <c>flares</c>: a
/// lens flare per lamp; <c>pool</c> (0-4): that many real point lights spread over the lenses (<c>range</c> metres,
/// <c>intensity</c>, optionally only over the lenses of one <c>channel</c>: "head", "tail" or "reverse"); <c>twinkle</c>:
/// the head-lamp bulbs fade in three sets in turn. Pure (unit-tested).
/// </summary>
public sealed class PartLight
{
    public const int MaxPool = 4;

    public bool Flares;
    public int Pool;
    public float Range = 2.5f;
    public float Intensity = 1f;
    public bool Twinkle;
    /// <summary>The lenses the pool lights sit on: null for any.</summary>
    public LampChannel? PoolChannel;

    public bool Any => Flares || Pool > 0 || Twinkle;

    /// <summary>Reads the <c>light</c> object; null (and why) when it can't be used.</summary>
    public static PartLight Read(JsonElement e, out string why)
    {
        why = null;
        if (e.ValueKind != JsonValueKind.Object) { why = "not an object"; return null; }
        var l = new PartLight();
        if (e.TryGetProperty("flares", out var f)) l.Flares = f.ValueKind == JsonValueKind.True;
        if (e.TryGetProperty("twinkle", out var t)) l.Twinkle = t.ValueKind == JsonValueKind.True;
        if (e.TryGetProperty("pool", out var p))
        {
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out l.Pool) || l.Pool < 0 || l.Pool > MaxPool)
            { why = $"pool is not a whole number from 0 to {MaxPool}"; return null; }
        }
        if (!Num(e, "range", 0.3f, 12f, ref l.Range, out why)) return null;
        if (!Num(e, "intensity", 0f, 8f, ref l.Intensity, out why)) return null;
        string ch = PartManifest.Str(e, "channel");
        if (ch != null)
        {
            if (!TryChannel(ch, out var c)) { why = $"channel \"{ch}\" is not head, tail or reverse"; return null; }
            l.PoolChannel = c;
        }
        return l;
    }

    public static bool TryChannel(string s, out LampChannel c)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "head": c = LampChannel.Head; return true;
            case "tail": c = LampChannel.Tail; return true;
            case "reverse": c = LampChannel.Reverse; return true;
            default: c = LampChannel.Head; return false;
        }
    }

    private static bool Num(JsonElement e, string name, float min, float max, ref float into, out string why)
    {
        why = null;
        if (!e.TryGetProperty(name, out var v)) return true;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetSingle(out float f) || float.IsNaN(f) || f < min || f > max)
        { why = $"{name} is not a number from {min} to {max}"; return false; }
        into = f;
        return true;
    }
}

/// <summary>One glowing lens of a part (a bulb's glass, an LED cell, a lamp's lens): its middle and facing in the mesh's
/// own space, its size, which lamps it glows with and its colour (the albedo under it, 0-1).</summary>
public sealed class LampSpot
{
    public float X, Y, Z;
    public float NX, NY, NZ;
    /// <summary>The lens's largest extent (metres).</summary>
    public float Size;
    public LampChannel Channel;
    public float R = 1f, G = 1f, B = 1f;
    /// <summary>Its triangles (indices into the mesh's triangle list), in mesh order.</summary>
    public readonly List<int> Triangles = new();
    /// <summary>The twinkle set (0-2): neighbours along the mesh's order take turns.</summary>
    public int Set;
}

/// <summary>
/// Finds a part's lenses from its mesh and lights.png: every triangle whose middle samples a lit texel is part of a lens,
/// and triangles that share a corner (welded by position) are one lens. Then: the few spots point lights go on, the lamps a
/// flare goes on (lenses close together are one lamp) and the twinkle sets. Pure (unit-tested).
/// </summary>
public static class LampSpots
{
    /// <summary>Samples a texture at (u, v), Unity's UV convention (v up), giving r, g, b in 0-1.</summary>
    public delegate void Sampler(float u, float v, out float r, out float g, out float b);

    /// <summary>A texel counts as lit from this value on.</summary>
    public const float Lit = 0.5f;
    /// <summary>Corners closer than this (metres) are the same corner: lens shells split at UV seams still join.</summary>
    public const float Weld = 0.0005f;
    /// <summary>Most lenses a part is searched for (a string of 110 bulbs fits).</summary>
    public const int MaxSpots = 512;

    /// <summary>The lenses of one mesh (positions xyz, UVs uv, triangle indices), in the order of their first triangle.
    /// <paramref name="albedo"/> may be null (white).</summary>
    public static List<LampSpot> Find(float[] positions, float[] uvs, int[] indices, Sampler lights, Sampler albedo = null)
    {
        var spots = new List<LampSpot>();
        if (positions == null || uvs == null || indices == null || lights == null) return spots;
        int nv = positions.Length / 3, nt = indices.Length / 3;
        if (uvs.Length < nv * 2) return spots;
        // the lit triangles and their channel
        var lit = new int[nt];
        int any = 0;
        for (int t = 0; t < nt; t++)
        {
            lit[t] = -1;
            int a = indices[t * 3], b = indices[t * 3 + 1], c = indices[t * 3 + 2];
            if ((uint)a >= nv || (uint)b >= nv || (uint)c >= nv) continue;
            float u = (uvs[a * 2] + uvs[b * 2] + uvs[c * 2]) / 3f, v = (uvs[a * 2 + 1] + uvs[b * 2 + 1] + uvs[c * 2 + 1]) / 3f;
            lights(u, v, out float r, out float g, out float bl);
            float m = Math.Max(r, Math.Max(g, bl));
            if (m < Lit) continue;
            lit[t] = g >= r && g >= bl ? (int)LampChannel.Head : r >= bl ? (int)LampChannel.Tail : (int)LampChannel.Reverse;
            any++;
        }
        if (any == 0) return spots;
        // welded corners: one id per position (quantised), so a lens split at a UV seam is still one piece
        var weld = new Dictionary<(long, long, long), int>();
        var corner = new int[nv];
        for (int i = 0; i < nv; i++)
        {
            var k = ((long)Math.Round(positions[i * 3] / Weld), (long)Math.Round(positions[i * 3 + 1] / Weld), (long)Math.Round(positions[i * 3 + 2] / Weld));
            if (!weld.TryGetValue(k, out int id)) weld[k] = id = weld.Count;
            corner[i] = id;
        }
        // union the lit triangles of one channel that share a corner
        var parent = new int[nt];
        for (int t = 0; t < nt; t++) parent[t] = t;
        int Root(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var firstAt = new Dictionary<(int, int), int>();
        for (int t = 0; t < nt; t++)
        {
            if (lit[t] < 0) continue;
            for (int k = 0; k < 3; k++)
            {
                var key = (corner[indices[t * 3 + k]], lit[t]);
                if (firstAt.TryGetValue(key, out int o)) { int ra = Root(o), rb = Root(t); if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb); }
                else firstAt[key] = t;
            }
        }
        var byRoot = new Dictionary<int, LampSpot>();
        for (int t = 0; t < nt; t++)
        {
            if (lit[t] < 0) continue;
            int r = Root(t);
            if (!byRoot.TryGetValue(r, out var s))
            {
                if (byRoot.Count >= MaxSpots) continue;
                byRoot[r] = s = new LampSpot { Channel = (LampChannel)lit[t] };
                spots.Add(s);
            }
            s.Triangles.Add(t);
        }
        foreach (var s in spots) Measure(s, positions, uvs, indices, albedo);
        for (int i = 0; i < spots.Count; i++) spots[i].Set = i % 3;
        return spots;
    }

    /// <summary>A lens's area-weighted middle, facing, size and colour.</summary>
    private static void Measure(LampSpot s, float[] p, float[] uv, int[] idx, Sampler albedo)
    {
        double ax = 0, ay = 0, az = 0, aw = 0, nx = 0, ny = 0, nz = 0, cr = 0, cg = 0, cb = 0;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (int t in s.Triangles)
        {
            int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
            float x0 = p[a * 3], y0 = p[a * 3 + 1], z0 = p[a * 3 + 2];
            float x1 = p[b * 3], y1 = p[b * 3 + 1], z1 = p[b * 3 + 2];
            float x2 = p[c * 3], y2 = p[c * 3 + 1], z2 = p[c * 3 + 2];
            // the face normal (cross product) is twice the area times the facing
            double ex = x1 - x0, ey = y1 - y0, ez = z1 - z0, fx = x2 - x0, fy = y2 - y0, fz = z2 - z0;
            double cx = ey * fz - ez * fy, cy = ez * fx - ex * fz, cz = ex * fy - ey * fx;
            double w = Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2 + 1e-12;
            ax += w * (x0 + x1 + x2) / 3; ay += w * (y0 + y1 + y2) / 3; az += w * (z0 + z1 + z2) / 3; aw += w;
            nx += cx / 2; ny += cy / 2; nz += cz / 2;
            minX = Math.Min(minX, Math.Min(x0, Math.Min(x1, x2))); maxX = Math.Max(maxX, Math.Max(x0, Math.Max(x1, x2)));
            minY = Math.Min(minY, Math.Min(y0, Math.Min(y1, y2))); maxY = Math.Max(maxY, Math.Max(y0, Math.Max(y1, y2)));
            minZ = Math.Min(minZ, Math.Min(z0, Math.Min(z1, z2))); maxZ = Math.Max(maxZ, Math.Max(z0, Math.Max(z1, z2)));
            if (albedo != null)
            {
                albedo((uv[a * 2] + uv[b * 2] + uv[c * 2]) / 3f, (uv[a * 2 + 1] + uv[b * 2 + 1] + uv[c * 2 + 1]) / 3f, out float r, out float g, out float bl);
                cr += w * r; cg += w * g; cb += w * bl;
            }
        }
        s.X = (float)(ax / aw); s.Y = (float)(ay / aw); s.Z = (float)(az / aw);
        double n = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        // a closed bulb's normals cancel out: it shines all round (no facing)
        if (n > 1e-9 && n / aw > 0.2) { s.NX = (float)(nx / n); s.NY = (float)(ny / n); s.NZ = (float)(nz / n); }
        s.Size = (float)Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
        if (albedo != null) { s.R = (float)(cr / aw); s.G = (float)(cg / aw); s.B = (float)(cb / aw); }
    }

    /// <summary>Where <paramref name="count"/> point lights go: the spots (of <paramref name="channel"/> only, when given)
    /// split into that many runs along their longest spread, and in each run the spot nearest the run's middle (so a light
    /// takes a real bulb's place and colour). Fewer when there are fewer spots.</summary>
    public static List<LampSpot> PoolSpots(IReadOnlyList<LampSpot> spots, int count, LampChannel? channel = null)
    {
        var list = new List<LampSpot>();
        if (spots == null || count <= 0) return list;
        foreach (var s in spots) if (channel == null || s.Channel == channel) list.Add(s);
        if (list.Count <= count) return list;
        // the axis of the widest spread
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var s in list)
        {
            minX = Math.Min(minX, s.X); maxX = Math.Max(maxX, s.X); minY = Math.Min(minY, s.Y); maxY = Math.Max(maxY, s.Y); minZ = Math.Min(minZ, s.Z); maxZ = Math.Max(maxZ, s.Z);
        }
        float dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
        Func<LampSpot, float> key = dx >= dy && dx >= dz ? s => s.X : dy >= dz ? s => s.Y : s => s.Z;
        list.Sort((a, b) => key(a).CompareTo(key(b)));
        var picks = new List<LampSpot>(count);
        for (int g = 0; g < count; g++)
        {
            int from = g * list.Count / count, to = (g + 1) * list.Count / count;
            float mx = 0, my = 0, mz = 0;
            for (int i = from; i < to; i++) { mx += list[i].X; my += list[i].Y; mz += list[i].Z; }
            int n = to - from;
            mx /= n; my /= n; mz /= n;
            LampSpot best = null;
            float bd = float.MaxValue;
            for (int i = from; i < to; i++)
            {
                float d = (list[i].X - mx) * (list[i].X - mx) + (list[i].Y - my) * (list[i].Y - my) + (list[i].Z - mz) * (list[i].Z - mz);
                if (d < bd) { bd = d; best = list[i]; }
            }
            picks.Add(best);
        }
        return picks;
    }

    /// <summary>The lamps of a part for flares: lenses of one channel closer than <paramref name="merge"/> metres are one
    /// lamp (the cells of one pod, the LEDs of one bar section), at their middle; at most <paramref name="max"/>, the
    /// biggest first.</summary>
    public static List<LampSpot> Lamps(IReadOnlyList<LampSpot> spots, float merge, int max)
    {
        var lamps = new List<(LampSpot lamp, float area, int n)>();
        if (spots == null || max <= 0) return new List<LampSpot>();
        float m2 = merge * merge;
        foreach (var s in spots)
        {
            int at = -1;
            for (int i = 0; i < lamps.Count; i++)
            {
                var l = lamps[i].lamp;
                if (l.Channel != s.Channel) continue;
                float d = (l.X - s.X) * (l.X - s.X) + (l.Y - s.Y) * (l.Y - s.Y) + (l.Z - s.Z) * (l.Z - s.Z);
                if (d <= m2) { at = i; break; }
            }
            if (at < 0)
            {
                lamps.Add((new LampSpot { X = s.X, Y = s.Y, Z = s.Z, NX = s.NX, NY = s.NY, NZ = s.NZ, Size = s.Size, Channel = s.Channel, R = s.R, G = s.G, B = s.B }, s.Size, 1));
                continue;
            }
            var (lamp, area, n) = lamps[at];
            // running mean of the middles; the facing and size of the whole group
            lamp.X = (lamp.X * n + s.X) / (n + 1); lamp.Y = (lamp.Y * n + s.Y) / (n + 1); lamp.Z = (lamp.Z * n + s.Z) / (n + 1);
            lamp.NX += s.NX; lamp.NY += s.NY; lamp.NZ += s.NZ;
            lamp.Size = Math.Max(lamp.Size, s.Size);
            lamps[at] = (lamp, area + s.Size, n + 1);
        }
        foreach (var (lamp, _, _) in lamps)
        {
            float n = (float)Math.Sqrt(lamp.NX * lamp.NX + lamp.NY * lamp.NY + lamp.NZ * lamp.NZ);
            if (n > 1e-6f) { lamp.NX /= n; lamp.NY /= n; lamp.NZ /= n; }
        }
        lamps.Sort((a, b) => b.area.CompareTo(a.area));
        var result = new List<LampSpot>();
        for (int i = 0; i < lamps.Count && i < max; i++) result.Add(lamps[i].lamp);
        return result;
    }

    /// <summary>How bright twinkle set <paramref name="set"/> is at <paramref name="time"/> seconds: each set fades down
    /// and back up in turn over <paramref name="period"/> seconds, never quite out (0.25 to 1).</summary>
    public static float Twinkle(int set, float time, float period = 2.4f)
    {
        if (period <= 0f) return 1f;
        double phase = time / period - (set % 3) / 3.0;
        double f = phase - Math.Floor(phase);                 // 0-1 through this set's turn
        double dip = f < 1.0 / 3 ? Math.Sin(f * 3 * Math.PI) : 0;   // down and up in its third of the period
        return (float)(1.0 - 0.75 * dip);
    }
}
