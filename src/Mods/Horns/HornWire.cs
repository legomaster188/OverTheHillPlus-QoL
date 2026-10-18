using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OverTheHillPlus.Horns;

/// <summary>Which kind of horn a player picked.</summary>
internal enum HornKind : byte
{
    /// <summary>The game's own horn: nothing is replaced.</summary>
    Stock = 0,
    /// <summary>One of the horns this mod makes at runtime (<see cref="HornSynth"/>), by id.</summary>
    BuiltIn = 1,
    /// <summary>The player's own wav file, sent to the others in chunks.</summary>
    Custom = 2,
}

/// <summary>How a horn plays: while the horn button is held (looping), or the whole clip once per press.</summary>
internal enum HornMode : byte { Hold = 0, WholeClip = 1 }

/// <summary>A player's horn choice as it goes over the wire. For a custom horn: the file's hash and size.</summary>
internal readonly struct HornChoice : IEquatable<HornChoice>
{
    public readonly HornKind Kind;
    public readonly byte BuiltInId;
    public readonly HornMode Mode;
    public readonly ulong Hash;
    public readonly int Length;

    public HornChoice(HornKind kind, byte builtInId, HornMode mode, ulong hash, int length)
    {
        Kind = kind;
        BuiltInId = builtInId;
        Mode = mode;
        Hash = hash;
        Length = length;
    }

    public static readonly HornChoice Stock = new(HornKind.Stock, 0, HornMode.Hold, 0, 0);

    public static HornChoice BuiltIn(byte id) => new(HornKind.BuiltIn, id, HornSynth.Find(id)?.Mode ?? HornMode.Hold, 0, 0);

    public bool IsStock => Kind == HornKind.Stock;

    public bool Equals(HornChoice o) =>
        Kind == o.Kind && BuiltInId == o.BuiltInId && Mode == o.Mode && Hash == o.Hash && Length == o.Length;

    public override bool Equals(object obj) => obj is HornChoice o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Kind, BuiltInId, Mode, Hash, Length);
    public static bool operator ==(HornChoice a, HornChoice b) => a.Equals(b);
    public static bool operator !=(HornChoice a, HornChoice b) => !a.Equals(b);
    public override string ToString() => $"{Kind}/{BuiltInId}/{Mode}/{Hash:x16}/{Length}";
}

/// <summary>
/// Custom Horns' wire format on its mod-network channel (little-endian; fields are only ever appended):
/// <list type="bullet">
/// <item><see cref="MsgSelect"/>: [u8 kind][u8 built-in id][u8 mode][u64 hash][i32 length][u8 flags] - "this is my
///   horn". <see cref="FlagAskBack"/> asks every receiver to answer with its own (a player who just arrived).</item>
/// <item><see cref="MsgRequest"/>: [u64 hash][u64 have-mask] - "send me the chunks of that file I don't have".</item>
/// <item><see cref="MsgChunk"/>: [u64 hash][i32 total][u8 index][bytes] - one <see cref="ChunkSize"/> piece.</item>
/// </list>
/// Pure: no game code, so the tests cover it.
/// </summary>
internal static class HornWire
{
    public const byte MsgSelect = 1, MsgRequest = 2, MsgChunk = 3;
    public const byte FlagAskBack = 1;

    /// <summary>6 KB a chunk: the 256 KB cap is 43 chunks, under the 64 a have-mask can track.</summary>
    public const int ChunkSize = 6144;
    private const int ChunkHeader = 8 + 4 + 1;
    /// <summary>The channel's payload cap.</summary>
    public const int MaxPayload = ChunkHeader + ChunkSize + 32;

    public static int ChunkCount(int total) => total <= 0 ? 0 : (total + ChunkSize - 1) / ChunkSize;

    /// <summary>Where chunk <paramref name="index"/> sits in a file of <paramref name="total"/> bytes.</summary>
    public static (int offset, int length) Slice(int total, int index)
    {
        int off = index * ChunkSize;
        return (off, Math.Max(0, Math.Min(ChunkSize, total - off)));
    }

    /// <summary>The first 8 bytes of the file's SHA-256 (never 0, which means "no file").</summary>
    public static ulong Hash(byte[] data)
    {
        Span<byte> sha = stackalloc byte[32];
        SHA256.HashData(data ?? Array.Empty<byte>(), sha);
        ulong h = BinaryPrimitives.ReadUInt64LittleEndian(sha);
        return h == 0 ? 1UL : h;
    }

    /// <summary>The sender's check before announcing a custom horn: the same validator every receiver runs.</summary>
    public static bool CanUpload(byte[] wav, out string why)
    {
        if (!WavValidator.TryValidate(wav, out _, out why)) return false;
        if (ChunkCount(wav.Length) > 64) { why = "too many chunks"; return false; }
        return true;
    }

    // ---------------------------------------------------------------- select

    public static byte[] EncodeSelect(HornChoice c, byte flags)
    {
        var b = new byte[3 + 8 + 4 + 1];
        b[0] = (byte)c.Kind;
        b[1] = c.BuiltInId;
        b[2] = (byte)c.Mode;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(3), c.Hash);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(11), c.Length);
        b[15] = flags;
        return b;
    }

    /// <summary>
    /// False for a short payload or an impossible custom horn (no hash, or a size outside the cap). An unknown kind
    /// or mode from a newer version decodes as it is; the player treats what it doesn't know as the stock horn.
    /// </summary>
    public static bool TryDecodeSelect(byte[] p, out HornChoice c, out byte flags)
    {
        c = HornChoice.Stock;
        flags = 0;
        if (p == null || p.Length < 16) return false;
        var kind = (HornKind)p[0];
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(3));
        int length = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(11));
        if (kind == HornKind.Custom && (hash == 0 || length < 44 || length > WavValidator.MaxBytes)) return false;
        c = new HornChoice(kind, p[1], (HornMode)p[2], hash, length);
        flags = p[15];
        return true;
    }

    // ---------------------------------------------------------------- request

    public static byte[] EncodeRequest(ulong hash, ulong haveMask)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(b, hash);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), haveMask);
        return b;
    }

    public static bool TryDecodeRequest(byte[] p, out ulong hash, out ulong haveMask)
    {
        hash = haveMask = 0;
        if (p == null || p.Length < 16) return false;
        hash = BinaryPrimitives.ReadUInt64LittleEndian(p);
        haveMask = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(8));
        return hash != 0;
    }

    // ---------------------------------------------------------------- chunk

    public static byte[] EncodeChunk(ulong hash, int total, int index, byte[] src, int offset, int count)
    {
        var b = new byte[ChunkHeader + count];
        BinaryPrimitives.WriteUInt64LittleEndian(b, hash);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), total);
        b[12] = (byte)index;
        Buffer.BlockCopy(src, offset, b, ChunkHeader, count);
        return b;
    }

    public static bool TryDecodeChunk(byte[] p, out ulong hash, out int total, out int index, out ArraySegment<byte> data)
    {
        hash = 0;
        total = index = 0;
        data = default;
        if (p == null || p.Length <= ChunkHeader || p.Length > MaxPayload) return false;
        hash = BinaryPrimitives.ReadUInt64LittleEndian(p);
        total = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(8));
        index = p[12];
        data = new ArraySegment<byte>(p, ChunkHeader, p.Length - ChunkHeader);
        return hash != 0;
    }
}

/// <summary>
/// Puts one custom horn file back together from its chunks, in any order. Each chunk must have exactly the size its
/// index calls for; duplicates and strays are ignored. The finished file must match its hash and pass
/// <see cref="WavValidator"/> before anything plays it.
/// </summary>
internal sealed class ChunkAssembler
{
    public readonly ulong Hash;
    public readonly int Total;
    public readonly int Count;
    private readonly byte[] _buffer;

    public ulong HaveMask { get; private set; }
    public int Received { get; private set; }
    public bool Complete => Received == Count;

    private ChunkAssembler(ulong hash, int total)
    {
        Hash = hash;
        Total = total;
        Count = HornWire.ChunkCount(total);
        _buffer = new byte[total];
    }

    /// <summary>Refuses a total outside 44 bytes .. 256 KB (or more than 64 chunks) and a zero hash.</summary>
    public static bool TryCreate(ulong hash, int total, out ChunkAssembler assembler, out string error)
    {
        assembler = null;
        if (hash == 0) { error = "no hash"; return false; }
        if (total < 44) { error = "too small to be a wav file"; return false; }
        if (total > WavValidator.MaxBytes) { error = "larger than 256 KB"; return false; }
        if (HornWire.ChunkCount(total) > 64) { error = "too many chunks"; return false; }
        assembler = new ChunkAssembler(hash, total);
        error = null;
        return true;
    }

    /// <summary>True when the chunk was new and fit; false for a duplicate, a bad index or a wrong size.</summary>
    public bool Add(int index, byte[] src, int offset, int count)
    {
        if (index < 0 || index >= Count || src == null || offset < 0 || count < 0 || offset + count > src.Length) return false;
        ulong bit = 1UL << index;
        if ((HaveMask & bit) != 0) return false;
        var (at, len) = HornWire.Slice(Total, index);
        if (count != len) return false;
        Buffer.BlockCopy(src, offset, _buffer, at, len);
        HaveMask |= bit;
        Received++;
        return true;
    }

    public bool Add(int index, ArraySegment<byte> data) => Add(index, data.Array, data.Offset, data.Count);

    /// <summary>The finished file, once every chunk is in, its hash matches and it is a valid wav.</summary>
    public bool TryFinish(out byte[] bytes, out string error)
    {
        bytes = null;
        if (!Complete) { error = "not complete"; return false; }
        if (HornWire.Hash(_buffer) != Hash) { error = "hash mismatch (corrupt transfer)"; return false; }
        if (!WavValidator.TryValidate(_buffer, out _, out error)) return false;
        bytes = _buffer;
        return true;
    }
}
