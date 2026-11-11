using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>One shape of a part's mesh file as a walk over the file found it: its name, kind and sizes, none of its data.</summary>
public readonly struct ShapeHead
{
    public readonly string Name;
    public readonly MeshPartKind Kind;
    public readonly int Vertices;
    public readonly int Indices;

    public ShapeHead(string name, MeshPartKind kind, int vertices, int indices)
    {
        Name = name;
        Kind = kind;
        Vertices = vertices;
        Indices = indices;
    }

    /// <summary>It can be drawn: not a collider, and at least one triangle.</summary>
    public bool Drawable => Kind != MeshPartKind.Collider && Indices >= 3;
}

/// <summary>What a part's mesh file holds: its shapes in file order (the shared one, each car's own, their lower detail).</summary>
public sealed class PartMeshIndex
{
    public readonly List<ShapeHead> Shapes = new();
    /// <summary>The file's size unpacked (bytes).</summary>
    public long Bytes;

    public bool IsPlaceholder
    {
        get
        {
            foreach (var s in Shapes)
                if (s.Name == MeshBlob.PlaceholderPartName) return true;
            return false;
        }
    }
}

/// <summary>
/// A part's mesh file read in a stream, without ever holding the whole of it. The file is core's car mesh format
/// (<see cref="MeshBlob"/>: "VERA" v1), gzip-packed, and carries a shape for every car the part fits: 20 to 50 MB unpacked
/// for the big parts, of which one car draws two shapes of a few thousand vertices. Reading it whole (unpack to one array,
/// then a float array for every shape of every car) took 3 to 4 seconds for the parts of one car and left hundreds of MB
/// behind; here the unpacked bytes pass through a small buffer, <see cref="Scan"/> checks every shape and keeps only its
/// name and sizes, and <see cref="Read"/> keeps only the shapes asked for and stops after the last of them.
/// Checks what <see cref="MeshBlob.Read"/> checks, with the same errors (<see cref="MeshBlobException"/>).
/// Pure code (unit-tested): a background thread may run it. Little-endian machines only, like the file.
/// </summary>
public static class PartMeshFile
{
    /// <summary>MeshBlob's own limit.</summary>
    private const int MaxVertices = 1 << 20;
    private const int Chunk = 1 << 16;

    /// <summary>
    /// The file's bytes unpacked, as a stream, and how many there are. A packed file (gzip) says its unpacked size in its
    /// last four bytes; <paramref name="file"/> must be seekable for that (an embedded resource and a MemoryStream are).
    /// </summary>
    public static Stream Open(Stream file, bool packed, out long length)
    {
        if (file == null) throw new MeshBlobException("no data", 0);
        if (!packed) { length = file.Length - file.Position; return file; }
        long start = file.Position;
        if (file.Length - start < 18) throw new InvalidDataException("the packed mesh is too short to be a gzip file");
        var tail = new byte[4];
        file.Position = file.Length - 4;
        int got = 0;
        while (got < 4)
        {
            int n = file.Read(tail, got, 4 - got);
            if (n <= 0) throw new InvalidDataException("the packed mesh could not be read to its end");
            got += n;
        }
        file.Position = start;
        length = (uint)(tail[0] | tail[1] << 8 | tail[2] << 16 | tail[3] << 24);
        return new GZipStream(file, CompressionMode.Decompress);
    }

    /// <summary>Walks the whole file and checks all of it (every float finite, every index inside its shape, nothing after
    /// the anchors), keeping only what each shape is.</summary>
    public static PartMeshIndex Scan(Stream file, bool packed)
    {
        using var raw = Open(file, packed, out long length);
        return Scan(raw, length);
    }

    /// <summary><see cref="Scan(Stream, bool)"/> over bytes that are unpacked already (<paramref name="length"/> of them).</summary>
    public static PartMeshIndex Scan(Stream raw, long length)
    {
        var w = new Walker(raw, length);
        int parts = w.Header();
        var index = new PartMeshIndex { Bytes = length };
        for (int i = 0; i < parts; i++)
        {
            string name = w.Name($"part {i} name");
            var kind = w.KindAndTransform(name);
            uint n = w.VertexCount(name);
            w.CheckFloats((long)n * 3, $"part {name} positions");
            w.CheckFloats((long)n * 3, $"part {name} normals");
            w.CheckFloats((long)n * 2, $"part {name} uv0");
            uint ic = w.IndexCount(name);
            w.CheckIndices(ic, n, name);
            index.Shapes.Add(new ShapeHead(name, kind, (int)n, (int)ic));
        }
        int anchors = w.U16("anchor count");
        for (int i = 0; i < anchors; i++)
        {
            string name = w.Name($"anchor {i} name");
            // one by one, so a file cut short inside an anchor names the float it was cut at, as the whole read does
            for (int k = 0; k < 3; k++) w.CheckFloats(1, $"anchor {name} position");
            for (int k = 0; k < 4; k++) w.CheckFloats(1, $"anchor {name} rotation");
        }
        w.End();
        return index;
    }

    /// <summary>
    /// The shapes named <paramref name="names"/> (exact names, as <see cref="PartMeshIndex"/> lists them), in that order;
    /// null where the file has no such shape. Every other shape passes by unread, and the walk ends at the last one found.
    /// </summary>
    public static MeshPart[] Read(Stream file, bool packed, IReadOnlyList<string> names)
    {
        using var raw = Open(file, packed, out long length);
        return Read(raw, length, names);
    }

    /// <summary><see cref="Read(Stream, bool, IReadOnlyList{string})"/> over bytes that are unpacked already.</summary>
    public static MeshPart[] Read(Stream raw, long length, IReadOnlyList<string> names)
    {
        var found = new MeshPart[names?.Count ?? 0];
        int left = 0;
        for (int k = 0; k < found.Length; k++) if (!string.IsNullOrEmpty(names[k])) left++;
        if (left == 0) return found;
        var w = new Walker(raw, length);
        int parts = w.Header();
        for (int i = 0; i < parts && left > 0; i++)
        {
            string name = w.Name($"part {i} name");
            var kind = w.KindAndTransform(name, out var position, out var rotation, out var scale);
            uint n = w.VertexCount(name);
            int at = -1;
            for (int k = 0; k < found.Length && at < 0; k++)
                if (found[k] == null && string.Equals(names[k], name, StringComparison.Ordinal)) at = k;
            if (at < 0)
            {
                w.Skip((long)n * 32, $"part {name} vertices");
                uint skipped = w.IndexCount(name);
                w.Skip((long)skipped * 4, $"part {name} indices");
                continue;
            }
            var p = new MeshPart { Name = name, Kind = kind, Position = position, Rotation = rotation, Scale = scale };
            p.Positions = w.Floats((int)n * 3, $"part {name} positions");
            p.Normals = w.Floats((int)n * 3, $"part {name} normals");
            p.Uv0 = w.Floats((int)n * 2, $"part {name} uv0");
            uint ic = w.IndexCount(name);
            p.Indices = w.Indices(ic, n, name);
            // the same shape asked for twice is the same object twice
            for (int k = 0; k < found.Length; k++)
                if (found[k] == null && string.Equals(names[k], name, StringComparison.Ordinal)) { found[k] = p; left--; }
        }
        return found;
    }

    /// <summary>Bounds-checked little-endian reads off a forward-only stream; every failure names the offset and what was
    /// being read, as MeshBlob's reader does.</summary>
    private sealed class Walker
    {
        private readonly Stream _s;
        private readonly long _length;
        private readonly byte[] _buf = new byte[Chunk];
        private long _pos;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public Walker(Stream s, long length)
        {
            _s = s ?? throw new MeshBlobException("no data", 0);
            _length = length;
        }

        private long Left => _length - _pos;

        private static int At(long offset) => offset > int.MaxValue ? int.MaxValue : (int)offset;

        private void Need(long bytes, string what)
        {
            if (bytes < 0 || Left < bytes) throw new MeshBlobException($"truncated while reading {what}", At(_pos));
        }

        /// <summary>Exactly <paramref name="into"/>.Length bytes, or the file ended early.</summary>
        private void Fill(Span<byte> into, string what)
        {
            int got = 0;
            while (got < into.Length)
            {
                int n = _s.Read(into.Slice(got));
                if (n <= 0) throw new MeshBlobException($"truncated while reading {what}", At(_pos + got));
                got += n;
            }
            _pos += into.Length;
        }

        public int Header()
        {
            if (_length < 4) throw new MeshBlobException("wrong magic (not a mesh blob)", 0);
            Fill(_buf.AsSpan(0, 4), "magic");
            if (_buf[0] != 'V' || _buf[1] != 'E' || _buf[2] != 'R' || _buf[3] != 'A') throw new MeshBlobException("wrong magic (not a mesh blob)", 0);
            int version = U16("version");
            if (version != MeshBlob.FormatVersion)
                throw new MeshBlobException($"format version {version}, this mod reads version {MeshBlob.FormatVersion}", 4);
            return U16("part count");
        }

        public int U16(string what)
        {
            Need(2, what);
            Fill(_buf.AsSpan(0, 2), what);
            return _buf[0] | _buf[1] << 8;
        }

        private uint U32(string what)
        {
            Need(4, what);
            Fill(_buf.AsSpan(0, 4), what);
            return (uint)(_buf[0] | _buf[1] << 8 | _buf[2] << 16 | _buf[3] << 24);
        }

        public string Name(string what)
        {
            long at = _pos;
            Need(1, what);
            Fill(_buf.AsSpan(0, 1), what);
            int len = _buf[0];
            if (len == 0) throw new MeshBlobException($"{what}: empty name", At(at));
            Need(len, what);
            long textAt = _pos;
            Fill(_buf.AsSpan(0, len), what);
            try { return StrictUtf8.GetString(_buf, 0, len); }
            catch (ArgumentException) { throw new MeshBlobException($"{what}: not valid UTF-8", At(textAt)); }
        }

        public MeshPartKind KindAndTransform(string name) => KindAndTransform(name, out _, out _, out _);

        /// <summary>The shape's kind and its own transform (position, rotation, scale: ten floats).</summary>
        public MeshPartKind KindAndTransform(string name, out MeshVec3 position, out MeshQuat rotation, out MeshVec3 scale)
        {
            long kindAt = _pos;
            Need(1, $"part {name} kind");
            Fill(_buf.AsSpan(0, 1), $"part {name} kind");
            byte kind = _buf[0];
            if (kind > (byte)MeshPartKind.Other) throw new MeshBlobException($"part {name}: unknown kind {kind}", At(kindAt));
            Span<float> f = stackalloc float[10];
            for (int i = 0; i < 10; i++)
            {
                string what = i < 3 ? $"part {name} position" : i < 7 ? $"part {name} rotation" : $"part {name} scale";
                long at = _pos;
                uint bits = U32(what);
                if ((bits & 0x7F800000u) == 0x7F800000u) throw new MeshBlobException($"{what}: not a finite number", At(at));
                f[i] = BitConverter.Int32BitsToSingle((int)bits);
            }
            position = new MeshVec3(f[0], f[1], f[2]);
            rotation = new MeshQuat(f[3], f[4], f[5], f[6]);
            scale = new MeshVec3(f[7], f[8], f[9]);
            return (MeshPartKind)kind;
        }

        public uint VertexCount(string name)
        {
            long at = _pos;
            uint n = U32($"part {name} vertex count");
            // 8 floats (32 bytes) per vertex follow: refuse counts the remaining data can't hold before anything is read
            if (n > MaxVertices || (long)n * 32 > Left)
                throw new MeshBlobException($"part {name}: vertex count {n} does not fit the data", At(at));
            return n;
        }

        public uint IndexCount(string name)
        {
            long at = _pos;
            uint ic = U32($"part {name} index count");
            if ((long)ic * 4 > Left) throw new MeshBlobException($"part {name}: index count {ic} does not fit the data", At(at));
            if (ic % 3 != 0) throw new MeshBlobException($"part {name}: {ic} indices is not whole triangles", At(at));
            return ic;
        }

        /// <summary><paramref name="count"/> floats pass by; each must be a finite number.</summary>
        public void CheckFloats(long count, string what)
        {
            Need(count * 4, what);
            long left = count * 4;
            while (left > 0)
            {
                int n = (int)Math.Min(left, Chunk);
                long at = _pos;
                Fill(_buf.AsSpan(0, n), what);
                int bad = NotFinite(MemoryMarshal.Cast<byte, uint>(_buf.AsSpan(0, n)));
                if (bad >= 0) throw new MeshBlobException($"{what}: not a finite number", At(at + bad * 4L));
                left -= n;
            }
        }

        /// <summary><paramref name="count"/> indices pass by; each must be below <paramref name="vertices"/>.</summary>
        public void CheckIndices(uint count, uint vertices, string name)
        {
            long left = (long)count * 4;
            string what = $"part {name} indices";
            Need(left, what);
            while (left > 0)
            {
                int n = (int)Math.Min(left, Chunk);
                long at = _pos;
                Fill(_buf.AsSpan(0, n), what);
                var u = MemoryMarshal.Cast<byte, uint>(_buf.AsSpan(0, n));
                for (int i = 0; i < u.Length; i++)
                    if (u[i] >= vertices) throw new MeshBlobException($"part {name}: index {u[i]} past the {vertices} vertices", At(at + i * 4L));
                left -= n;
            }
        }

        public float[] Floats(int count, string what)
        {
            Need((long)count * 4, what);
            var a = new float[count];
            long at = _pos;
            Fill(MemoryMarshal.AsBytes(a.AsSpan()), what);
            int bad = NotFinite(MemoryMarshal.Cast<float, uint>(a.AsSpan()));
            if (bad >= 0) throw new MeshBlobException($"{what}: not a finite number", At(at + bad * 4L));
            return a;
        }

        public int[] Indices(uint count, uint vertices, string name)
        {
            string what = $"part {name} indices";
            Need((long)count * 4, what);
            var a = new int[count];
            long at = _pos;
            Fill(MemoryMarshal.AsBytes(a.AsSpan()), what);
            var u = MemoryMarshal.Cast<int, uint>(a.AsSpan());
            for (int i = 0; i < u.Length; i++)
                if (u[i] >= vertices) throw new MeshBlobException($"part {name}: index {u[i]} past the {vertices} vertices", At(at + i * 4L));
            return a;
        }

        public void Skip(long bytes, string what)
        {
            Need(bytes, what);
            while (bytes > 0)
            {
                int n = (int)Math.Min(bytes, Chunk);
                Fill(_buf.AsSpan(0, n), what);
                bytes -= n;
            }
        }

        /// <summary>Nothing may follow the anchors.</summary>
        public void End()
        {
            if (_pos != _length) throw new MeshBlobException($"{_length - _pos} trailing bytes after the anchors", At(_pos));
            // a packed file whose stated size is short of what it unpacks to
            if (_s.Read(_buf, 0, 1) > 0) throw new MeshBlobException("trailing bytes after the anchors", At(_pos));
        }

        /// <summary>The first value that is not a finite float (NaN or an infinity: all exponent bits set), -1 when all are.</summary>
        private static int NotFinite(ReadOnlySpan<uint> bits)
        {
            for (int i = 0; i < bits.Length; i++)
                if ((bits[i] & 0x7F800000u) == 0x7F800000u) return i;
            return -1;
        }
    }
}
