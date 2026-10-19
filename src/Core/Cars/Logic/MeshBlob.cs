using System;
using System.Collections.Generic;
using System.Text;

namespace OverTheHillPlus.Cars;

/// <summary>What a part of a car (or trailer) model is for (the u8 kind in the mesh blob).</summary>
public enum MeshPartKind : byte
{
    Body = 0,
    Glass = 1,
    Wheel = 2,
    Tyre = 3,
    Collider = 4,
    Other = 5,
}

/// <summary>A float triple in Unity axes (x right, y up, z forward).</summary>
public readonly struct MeshVec3 : IEquatable<MeshVec3>
{
    public readonly float X, Y, Z;
    public MeshVec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public bool Equals(MeshVec3 o) => X == o.X && Y == o.Y && Z == o.Z;
    public override bool Equals(object obj) => obj is MeshVec3 o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>A rotation quaternion (x, y, z, w), Unity's order.</summary>
public readonly struct MeshQuat
{
    public readonly float X, Y, Z, W;
    public MeshQuat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
}

/// <summary>One mesh of the model, with its local transform (relative to the car's root).</summary>
public sealed class MeshPart
{
    public string Name;
    public MeshPartKind Kind;
    public MeshVec3 Position;
    public MeshQuat Rotation;
    public MeshVec3 Scale;
    /// <summary>x, y, z per vertex.</summary>
    public float[] Positions;
    /// <summary>x, y, z per vertex.</summary>
    public float[] Normals;
    /// <summary>u, v per vertex.</summary>
    public float[] Uv0;
    /// <summary>Three per triangle, each below <see cref="VertexCount"/>.</summary>
    public int[] Indices;

    public int VertexCount => Positions.Length / 3;

    /// <summary>Axis-aligned bounds of the vertices (in the part's own space; zero when it has none).</summary>
    public void Bounds(out MeshVec3 min, out MeshVec3 max)
    {
        if (Positions.Length < 3) { min = max = default; return; }
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
        float x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        for (int i = 0; i + 2 < Positions.Length; i += 3)
        {
            float x = Positions[i], y = Positions[i + 1], z = Positions[i + 2];
            if (x < x0) x0 = x; if (x > x1) x1 = x;
            if (y < y0) y0 = y; if (y > y1) y1 = y;
            if (z < z0) z0 = z; if (z > z1) z1 = z;
        }
        min = new MeshVec3(x0, y0, z0);
        max = new MeshVec3(x1, y1, z1);
    }
}

/// <summary>A named point on the model (light sources, exhausts, wheel centres).</summary>
public sealed class MeshAnchor
{
    public string Name;
    public MeshVec3 Position;
    public MeshQuat Rotation;
}

/// <summary>A mesh blob could not be read: the file is truncated, from another format version, or corrupt.</summary>
public sealed class MeshBlobException : Exception
{
    /// <summary>Byte offset where reading failed.</summary>
    public int Offset { get; }

    public MeshBlobException(string message, int offset) : base($"mesh blob: {message} (at offset {offset})")
    {
        Offset = offset;
    }
}

/// <summary>
/// Reads a modded car's (or trailer's) binary model, the format the Vera introduced (Liveries/vera/vera_format.md; every
/// car's <c>&lt;name&gt;.mesh</c> and the trailers' models use it). Pure code: no Unity types, so it is unit-tested.
/// Layout, little-endian: magic "VERA", u16 version (1), u16 part count; per part a u8-length UTF-8 name, u8 kind,
/// local position f32x3, rotation f32x4 (x, y, z, w), scale f32x3, u32 vertex count, positions f32x3, normals f32x3,
/// uv0 f32x2, u32 index count, u32 indices; then u16 anchor count, per anchor a name, position f32x3, rotation f32x4.
/// Every problem is a <see cref="MeshBlobException"/>; nothing past the end of the data is ever read or allocated.
/// </summary>
public sealed class MeshBlob
{
    public const ushort FormatVersion = 1;
    /// <summary>A part with this name marks the stand-in file the repo ships until the real model is merged.</summary>
    public const string PlaceholderPartName = "__placeholder__";
    /// <summary>The wheel centre anchors the car needs, front-left first.</summary>
    public static readonly string[] WheelAnchors = { "wheel_FL", "wheel_FR", "wheel_RL", "wheel_RR" };

    private const int MaxVertices = 1 << 20;

    public ushort Version { get; private set; }
    public List<MeshPart> Parts { get; } = new();
    public List<MeshAnchor> Anchors { get; } = new();

    public bool IsPlaceholder
    {
        get
        {
            foreach (var p in Parts)
                if (p.Name == PlaceholderPartName) return true;
            return false;
        }
    }

    public MeshAnchor Anchor(string name)
    {
        foreach (var a in Anchors)
            if (string.Equals(a.Name, name, StringComparison.Ordinal)) return a;
        return null;
    }

    public List<MeshPart> PartsOf(MeshPartKind kind)
    {
        var l = new List<MeshPart>();
        foreach (var p in Parts)
            if (p.Kind == kind) l.Add(p);
        return l;
    }

    public MeshPart First(MeshPartKind kind)
    {
        foreach (var p in Parts)
            if (p.Kind == kind) return p;
        return null;
    }

    /// <summary>The part with this exact name, or null.</summary>
    public MeshPart Part(string name)
    {
        foreach (var p in Parts)
            if (string.Equals(p.Name, name, StringComparison.Ordinal)) return p;
        return null;
    }

    /// <summary>The part named <paramref name="name"/> when it has <paramref name="kind"/>; otherwise the first part
    /// of that kind that isn't a body LOD (blobs from before the names were fixed).</summary>
    public MeshPart Pick(MeshPartKind kind, string name)
    {
        var p = Part(name);
        if (p != null && p.Kind == kind) return p;
        foreach (var q in Parts)
            if (q.Kind == kind && !IsBodyLod(q.Name) && q.Name != PlaceholderPartName) return q;
        return null;
    }

    public const string BodyName = "body", WheelName = "wheel", TyreName = "tyre", ColliderName = "collider";
    /// <summary>Optional: a rim and a tyre of their own for the rear wheels (a dual wheel), see <see cref="CarTandem"/>. The
    /// steer wheels keep <see cref="WheelName"/> and <see cref="TyreName"/>.</summary>
    public const string RearWheelName = "wheel_rear", RearTyreName = "tyre_rear";

    /// <summary>The name of the body part for the donor's body LOD <paramref name="lod"/> (1..3 in the real model).</summary>
    public static string BodyLodName(int lod) => "body_lod" + lod;

    /// <summary>"body_lod" followed by digits: a lower-detail copy of <see cref="BodyName"/>, never drawn on LOD 0.</summary>
    public static bool IsBodyLod(string name)
    {
        const string prefix = "body_lod";
        if (name == null || name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        for (int i = prefix.Length; i < name.Length; i++)
            if (name[i] < '0' || name[i] > '9') return false;
        return true;
    }

    /// <summary>
    /// The shell parts (body, glass and other parts: everything but rims, tyres, the collider, the placeholder marker
    /// and the body LOD copies) drawn on the donor's body LOD <paramref name="lod"/>. On LOD 1 and up, the part named
    /// <see cref="BodyName"/> is swapped for <see cref="BodyLodName"/>(lod) when the model has that part; a model
    /// without LOD parts draws the full body on every LOD.
    /// </summary>
    public List<MeshPart> ShellParts(int lod)
    {
        var lodPart = lod >= 1 ? Part(BodyLodName(lod)) : null;
        if (lodPart != null && lodPart.Kind != MeshPartKind.Body) lodPart = null;
        bool hasBody = Part(BodyName) is { Kind: MeshPartKind.Body };
        var list = new List<MeshPart>();
        foreach (var p in Parts)
        {
            if (p.Kind is not (MeshPartKind.Body or MeshPartKind.Glass or MeshPartKind.Other)) continue;
            if (p.Name == PlaceholderPartName || IsBodyLod(p.Name)) continue;
            if (lodPart != null && hasBody && p.Name == BodyName) { list.Add(lodPart); continue; }
            list.Add(p);
        }
        return list;
    }

    /// <summary>Null when the model has what the car needs (a body, a rim, a tyre and the four wheel anchors);
    /// otherwise what is missing.</summary>
    public string CheckDrivable()
    {
        var missing = new List<string>();
        if (!ShellParts(0).Exists(p => p.Kind == MeshPartKind.Body)) missing.Add("body");
        if (Pick(MeshPartKind.Wheel, WheelName) == null) missing.Add("wheel");
        if (Pick(MeshPartKind.Tyre, TyreName) == null) missing.Add("tyre");
        foreach (var a in WheelAnchors)
            if (Anchor(a) == null) missing.Add(a);
        return missing.Count == 0 ? null : "missing " + string.Join(", ", missing);
    }

    public static MeshBlob Read(byte[] data)
    {
        if (data == null) throw new MeshBlobException("no data", 0);
        var r = new Reader(data);
        if (data.Length < 4 || data[0] != 'V' || data[1] != 'E' || data[2] != 'R' || data[3] != 'A')
            throw new MeshBlobException("wrong magic (not a mesh blob)", 0);
        r.Pos = 4;
        var blob = new MeshBlob { Version = r.U16("version") };
        if (blob.Version != FormatVersion)
            throw new MeshBlobException($"format version {blob.Version}, this mod reads version {FormatVersion}", 4);
        int parts = r.U16("part count");
        for (int i = 0; i < parts; i++) blob.Parts.Add(ReadPart(r, i));
        int anchors = r.U16("anchor count");
        for (int i = 0; i < anchors; i++)
        {
            var a = new MeshAnchor { Name = r.Name($"anchor {i} name") };
            a.Position = r.Vec3($"anchor {a.Name} position");
            a.Rotation = r.Quat($"anchor {a.Name} rotation");
            blob.Anchors.Add(a);
        }
        if (r.Pos != data.Length) throw new MeshBlobException($"{data.Length - r.Pos} trailing bytes after the anchors", r.Pos);
        return blob;
    }

    private static MeshPart ReadPart(Reader r, int index)
    {
        var p = new MeshPart { Name = r.Name($"part {index} name") };
        int kindAt = r.Pos;
        byte kind = r.U8($"part {p.Name} kind");
        if (kind > (byte)MeshPartKind.Other) throw new MeshBlobException($"part {p.Name}: unknown kind {kind}", kindAt);
        p.Kind = (MeshPartKind)kind;
        p.Position = r.Vec3($"part {p.Name} position");
        p.Rotation = r.Quat($"part {p.Name} rotation");
        p.Scale = r.Vec3($"part {p.Name} scale");

        int countAt = r.Pos;
        uint n = r.U32($"part {p.Name} vertex count");
        // 8 floats (32 bytes) per vertex follow: refuse counts the remaining data can't hold before allocating.
        if (n > MaxVertices || (long)n * 32 > r.Left)
            throw new MeshBlobException($"part {p.Name}: vertex count {n} does not fit the data", countAt);
        int verts = (int)n;
        p.Positions = r.Floats(verts * 3, $"part {p.Name} positions");
        p.Normals = r.Floats(verts * 3, $"part {p.Name} normals");
        p.Uv0 = r.Floats(verts * 2, $"part {p.Name} uv0");

        int icAt = r.Pos;
        uint ic = r.U32($"part {p.Name} index count");
        if ((long)ic * 4 > r.Left) throw new MeshBlobException($"part {p.Name}: index count {ic} does not fit the data", icAt);
        if (ic % 3 != 0) throw new MeshBlobException($"part {p.Name}: {ic} indices is not whole triangles", icAt);
        p.Indices = new int[ic];
        for (int i = 0; i < ic; i++)
        {
            int at = r.Pos;
            uint v = r.U32($"part {p.Name} indices");
            if (v >= n) throw new MeshBlobException($"part {p.Name}: index {v} past the {n} vertices", at);
            p.Indices[i] = (int)v;
        }
        return p;
    }

    /// <summary>Bounds-checked little-endian reads; every failure names the offset and what was being read.</summary>
    private sealed class Reader
    {
        private readonly byte[] _d;
        public int Pos;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public Reader(byte[] d) { _d = d; }

        public int Left => _d.Length - Pos;

        private void Need(int bytes, string what)
        {
            if (bytes < 0 || Left < bytes) throw new MeshBlobException($"truncated while reading {what}", Pos);
        }

        public byte U8(string what)
        {
            Need(1, what);
            return _d[Pos++];
        }

        public ushort U16(string what)
        {
            Need(2, what);
            ushort v = (ushort)(_d[Pos] | _d[Pos + 1] << 8);
            Pos += 2;
            return v;
        }

        public uint U32(string what)
        {
            Need(4, what);
            uint v = (uint)(_d[Pos] | _d[Pos + 1] << 8 | _d[Pos + 2] << 16 | _d[Pos + 3] << 24);
            Pos += 4;
            return v;
        }

        public float F32(string what)
        {
            int at = Pos;
            float f = BitConverter.Int32BitsToSingle((int)U32(what));
            if (float.IsNaN(f) || float.IsInfinity(f)) throw new MeshBlobException($"{what}: not a finite number", at);
            return f;
        }

        public float[] Floats(int count, string what)
        {
            Need(count * 4, what);
            var a = new float[count];
            for (int i = 0; i < count; i++) a[i] = F32(what);
            return a;
        }

        public MeshVec3 Vec3(string what) => new(F32(what), F32(what), F32(what));

        public MeshQuat Quat(string what) => new(F32(what), F32(what), F32(what), F32(what));

        public string Name(string what)
        {
            int at = Pos;
            int len = U8(what);
            if (len == 0) throw new MeshBlobException($"{what}: empty name", at);
            Need(len, what);
            string s;
            try { s = StrictUtf8.GetString(_d, Pos, len); }
            catch (ArgumentException) { throw new MeshBlobException($"{what}: not valid UTF-8", Pos); }
            Pos += len;
            return s;
        }
    }
}
