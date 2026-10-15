using System;
using System.Collections.Generic;
using System.Text;

namespace OverTheHillPlus.Features.Public.ModNet;

/// <summary>
/// Builds a message payload: little-endian numbers and length-prefixed UTF-8 strings. Cheap, allocation-light, and
/// the matching <see cref="NetReader"/> never throws on short or hostile input.
/// </summary>
internal sealed class NetWriter
{
    private readonly List<byte> _bytes;

    public NetWriter(int capacity = 64) => _bytes = new List<byte>(capacity);

    public int Length => _bytes.Count;

    public NetWriter Byte(byte v) { _bytes.Add(v); return this; }

    public NetWriter Bool(bool v) => Byte(v ? (byte)1 : (byte)0);

    public NetWriter UShort(ushort v) { _bytes.Add((byte)v); _bytes.Add((byte)(v >> 8)); return this; }

    public NetWriter Int(int v)
    {
        _bytes.Add((byte)v); _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)(v >> 16)); _bytes.Add((byte)(v >> 24));
        return this;
    }

    public NetWriter Float(float v) => Int(BitConverter.SingleToInt32Bits(v));

    /// <summary>A string of at most <paramref name="maxChars"/> characters (longer ones are cut), u16 byte length first.</summary>
    public NetWriter String(string s, int maxChars = 64)
    {
        s ??= "";
        if (s.Length > maxChars) s = s.Substring(0, maxChars);
        var b = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(b.Length, ushort.MaxValue);
        UShort((ushort)n);
        for (int i = 0; i < n; i++) _bytes.Add(b[i]);
        return this;
    }

    public NetWriter Bytes(byte[] b)
    {
        if (b != null) _bytes.AddRange(b);
        return this;
    }

    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>Reads what <see cref="NetWriter"/> wrote. Reading past the end returns defaults and clears <see cref="Ok"/>.</summary>
internal sealed class NetReader
{
    private readonly byte[] _b;
    private int _pos;

    public NetReader(byte[] bytes) { _b = bytes ?? Array.Empty<byte>(); }

    /// <summary>False once any read ran past the end of the payload.</summary>
    public bool Ok { get; private set; } = true;

    public int Remaining => _b.Length - _pos;

    private bool Need(int n)
    {
        if (Ok && _pos + n <= _b.Length) return true;
        Ok = false;
        return false;
    }

    public byte Byte() => Need(1) ? _b[_pos++] : (byte)0;

    public bool Bool() => Byte() != 0;

    public ushort UShort()
    {
        if (!Need(2)) return 0;
        ushort v = (ushort)(_b[_pos] | (_b[_pos + 1] << 8));
        _pos += 2;
        return v;
    }

    public int Int()
    {
        if (!Need(4)) return 0;
        int v = _b[_pos] | (_b[_pos + 1] << 8) | (_b[_pos + 2] << 16) | (_b[_pos + 3] << 24);
        _pos += 4;
        return v;
    }

    /// <summary>A float; NaN and infinities read as 0.</summary>
    public float Float()
    {
        float f = BitConverter.Int32BitsToSingle(Int());
        return float.IsFinite(f) ? f : 0f;
    }

    /// <summary>A string, cut to <paramref name="maxChars"/>. Control characters are dropped.</summary>
    public string String(int maxChars = 64)
    {
        int n = UShort();
        if (!Need(n)) return "";
        string s;
        try { s = Encoding.UTF8.GetString(_b, _pos, n); }
        catch { s = ""; }
        _pos += n;
        var sb = new StringBuilder(Math.Min(s.Length, maxChars));
        foreach (char c in s)
        {
            if (sb.Length >= maxChars) break;
            if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}
