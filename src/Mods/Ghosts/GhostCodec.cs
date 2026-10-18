using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OverTheHillPlus.Ghosts;

/// <summary>One wheel's look at a sample: steering (degrees), spin (degrees, not wrapped) and suspension (the wheel's
/// height in the car's own space, metres).</summary>
internal struct GhostWheel
{
    public float Steer, Spin, Suspension;
}

/// <summary>One 20 Hz sample of a drive: the car's root position and rotation (world space) and its wheels.</summary>
internal sealed class GhostFrame
{
    public float Px, Py, Pz;
    public float Qx, Qy, Qz, Qw;
    public GhostWheel[] Wheels;

    public GhostFrame(int wheels) => Wheels = new GhostWheel[wheels];
}

/// <summary>A recorded drive. <see cref="RaceTimeMs"/> is set for a finished race on <see cref="TrackId"/>.</summary>
internal sealed class GhostRecording
{
    public int Hz = GhostCodec.Hz;
    public string Car = "";
    public string TrackId = "";
    public int RaceTimeMs = -1;
    public long CreatedUnix;
    public int WheelCount;
    public List<GhostFrame> Frames = new();

    /// <summary>The frame count in the file (also set by a header-only read, which leaves <see cref="Frames"/> empty).</summary>
    public int FrameCount;

    private int Count => Frames.Count > 0 ? Frames.Count : FrameCount;

    public float Duration => Count < 2 ? 0f : (Count - 1) / (float)Hz;

    /// <summary>The same recording without its frames (what the list keeps).</summary>
    public GhostRecording HeaderCopy() => new()
    {
        Hz = Hz, Car = Car, TrackId = TrackId, RaceTimeMs = RaceTimeMs, CreatedUnix = CreatedUnix, WheelCount = WheelCount,
        FrameCount = Count,
    };
}

/// <summary>A ghost file's content could not be read.</summary>
internal sealed class GhostFormatException : Exception
{
    public GhostFormatException(string message) : base(message) { }
}

/// <summary>
/// The ghost file format (pure, no Unity). Little endian:
/// <c>"OTHG"</c>, version (1), Hz, wheel count, flags (0), car name, track id (varint length + UTF-8), created (int64 unix
/// seconds), race time (zig-zag varint ms, -1 = none), frame count (varint), the frames, then a CRC-32 of everything
/// before it. Every value in a frame is quantised to a whole number (position mm, rotation 1/32767, steer 0.1°, spin
/// 1°, suspension 0.5 mm) and written as the zig-zag varint difference from the frame before, so a slow-changing drive
/// costs a few bytes a value. A file that is cut short, has a bad checksum, an unknown version or counts out of range is
/// refused with a <see cref="GhostFormatException"/> (<see cref="TryDecode"/> turns that into false and a reason).
/// </summary>
internal static class GhostCodec
{
    public const int Hz = 20;
    public const int Version = 1;
    public const int MaxWheels = 8;
    /// <summary>Two hours at 20 Hz.</summary>
    public const int MaxFrames = Hz * 60 * 60 * 2;
    public const int MaxText = 64;

    private static readonly byte[] Magic = { (byte)'O', (byte)'T', (byte)'H', (byte)'G' };

    private const float PosScale = 1000f, RotScale = 32767f, SteerScale = 10f, SpinScale = 1f, SuspScale = 2000f;

    // ---------------------------------------------------------------- encode

    public static byte[] Encode(GhostRecording rec)
    {
        if (rec == null) throw new ArgumentNullException(nameof(rec));
        if (rec.WheelCount < 0 || rec.WheelCount > MaxWheels) throw new ArgumentException($"wheel count {rec.WheelCount} is out of range");
        if (rec.Frames.Count > MaxFrames) throw new ArgumentException($"{rec.Frames.Count} frames is more than {MaxFrames}");
        if (rec.Hz < 1 || rec.Hz > 120) throw new ArgumentException($"rate {rec.Hz} Hz is out of range");
        if (rec.Frames.Count == 0 && rec.FrameCount > 0) throw new ArgumentException("the frames of this recording weren't read");

        var w = new Writer();
        w.Bytes(Magic);
        w.Byte(Version);
        w.Byte(rec.Hz);
        w.Byte(rec.WheelCount);
        w.Byte(0);
        w.Text(rec.Car);
        w.Text(rec.TrackId);
        w.Int64(rec.CreatedUnix);
        w.Signed(rec.RaceTimeMs < 0 ? -1 : rec.RaceTimeMs);
        w.Unsigned((uint)rec.Frames.Count);

        int n = 7 + rec.WheelCount * 3;
        var prev = new long[n];
        var cur = new long[n];
        foreach (var f in rec.Frames)
        {
            if (f.Wheels == null || f.Wheels.Length < rec.WheelCount) throw new ArgumentException("a frame has fewer wheels than the recording");
            cur[0] = Q(f.Px, PosScale);
            cur[1] = Q(f.Py, PosScale);
            cur[2] = Q(f.Pz, PosScale);
            Normalise(f.Qx, f.Qy, f.Qz, f.Qw, out float x, out float y, out float z, out float qw);
            long qx = Q(x, RotScale), qy = Q(y, RotScale), qz = Q(z, RotScale), qq = Q(qw, RotScale);
            // q and -q are the same rotation: keep the sign closest to the previous sample so the deltas stay small.
            if (qx * prev[3] + qy * prev[4] + qz * prev[5] + qq * prev[6] < 0) { qx = -qx; qy = -qy; qz = -qz; qq = -qq; }
            cur[3] = qx; cur[4] = qy; cur[5] = qz; cur[6] = qq;
            for (int i = 0; i < rec.WheelCount; i++)
            {
                var wh = f.Wheels[i];
                cur[7 + i * 3] = Q(wh.Steer, SteerScale);
                cur[8 + i * 3] = Q(wh.Spin, SpinScale);
                cur[9 + i * 3] = Q(wh.Suspension, SuspScale);
            }
            for (int i = 0; i < n; i++)
            {
                w.Signed64(cur[i] - prev[i]);
                prev[i] = cur[i];
            }
        }
        w.UInt32(Crc32(w.Buffer, 0, w.Length));
        return w.ToArray();
    }

    // ---------------------------------------------------------------- decode

    /// <summary>Reads a ghost file; false (with a short reason) for anything that isn't a whole, valid file.</summary>
    public static bool TryDecode(byte[] data, out GhostRecording rec, out string error) => TryDecode(data, true, out rec, out error);

    /// <summary>
    /// The header only (car, track, race time, created, frame count): the whole file is still checked against its
    /// checksum, but the frames are left unread (<see cref="GhostRecording.Frames"/> stays empty). For the list.
    /// </summary>
    public static bool TryDecodeHeader(byte[] data, out GhostRecording rec, out string error) => TryDecode(data, false, out rec, out error);

    private static bool TryDecode(byte[] data, bool frames, out GhostRecording rec, out string error)
    {
        try
        {
            rec = Decode(data, frames);
            error = null;
            return true;
        }
        catch (GhostFormatException e)
        {
            rec = null;
            error = e.Message;
            return false;
        }
    }

    public static GhostRecording Decode(byte[] data, bool readFrames = true)
    {
        if (data == null || data.Length < Magic.Length + 4 + 4) throw new GhostFormatException("file is too short");
        for (int i = 0; i < Magic.Length; i++)
            if (data[i] != Magic[i]) throw new GhostFormatException("not a ghost file");
        int body = data.Length - 4;
        uint stored = (uint)(data[body] | data[body + 1] << 8 | data[body + 2] << 16 | data[body + 3] << 24);
        if (stored != Crc32(data, 0, body)) throw new GhostFormatException("checksum mismatch (file damaged or cut short)");

        var r = new Reader(data, Magic.Length, body);
        int version = r.Byte();
        if (version != Version) throw new GhostFormatException($"unknown version {version}");
        var rec = new GhostRecording { Hz = r.Byte() };
        if (rec.Hz < 1 || rec.Hz > 120) throw new GhostFormatException($"rate {rec.Hz} Hz is out of range");
        rec.WheelCount = r.Byte();
        if (rec.WheelCount > MaxWheels) throw new GhostFormatException($"wheel count {rec.WheelCount} is out of range");
        r.Byte(); // flags
        rec.Car = r.Text();
        rec.TrackId = r.Text();
        rec.CreatedUnix = r.Int64();
        long race = r.Signed64();
        if (race < -1 || race > int.MaxValue) throw new GhostFormatException("race time is out of range");
        rec.RaceTimeMs = (int)race;
        ulong frames = r.Unsigned64();
        int n = 7 + rec.WheelCount * 3;
        // Each value is at least one byte: a count the rest of the file can't hold is refused before allocating.
        if (frames > MaxFrames || frames * (ulong)n > (ulong)r.Left) throw new GhostFormatException($"frame count {frames} is out of range");
        rec.FrameCount = (int)frames;
        if (!readFrames) return rec;

        var acc = new long[n];
        rec.Frames = new List<GhostFrame>((int)frames);
        for (int k = 0; k < (int)frames; k++)
        {
            for (int i = 0; i < n; i++)
            {
                acc[i] += r.Signed64();
                if (acc[i] > int.MaxValue || acc[i] < int.MinValue) throw new GhostFormatException("a value is out of range");
            }
            var f = new GhostFrame(rec.WheelCount)
            {
                Px = acc[0] / PosScale, Py = acc[1] / PosScale, Pz = acc[2] / PosScale,
            };
            Normalise(acc[3] / RotScale, acc[4] / RotScale, acc[5] / RotScale, acc[6] / RotScale, out f.Qx, out f.Qy, out f.Qz, out f.Qw);
            for (int i = 0; i < rec.WheelCount; i++)
                f.Wheels[i] = new GhostWheel
                {
                    Steer = acc[7 + i * 3] / SteerScale, Spin = acc[8 + i * 3] / SpinScale, Suspension = acc[9 + i * 3] / SuspScale,
                };
            rec.Frames.Add(f);
        }
        if (r.Left != 0) throw new GhostFormatException("unexpected bytes after the last frame");
        return rec;
    }

    // ---------------------------------------------------------------- helpers

    private static long Q(float v, float scale)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return 0;
        double d = Math.Round((double)v * scale);
        return (long)Math.Max(int.MinValue, Math.Min(int.MaxValue, d));
    }

    /// <summary>Unit quaternion (identity for a zero or invalid one).</summary>
    internal static void Normalise(float x, float y, float z, float w, out float ox, out float oy, out float oz, out float ow)
    {
        double len = Math.Sqrt((double)x * x + (double)y * y + (double)z * z + (double)w * w);
        if (len < 1e-6 || double.IsNaN(len) || double.IsInfinity(len)) { ox = oy = oz = 0f; ow = 1f; return; }
        ox = (float)(x / len); oy = (float)(y / len); oz = (float)(z / len); ow = (float)(w / len);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    internal static uint Crc32(byte[] data, int offset, int count)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = offset; i < offset + count; i++) c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private sealed class Writer
    {
        public byte[] Buffer = new byte[256];
        public int Length;

        private void Ensure(int more)
        {
            if (Length + more <= Buffer.Length) return;
            Array.Resize(ref Buffer, Math.Max(Buffer.Length * 2, Length + more));
        }

        public void Byte(int b) { Ensure(1); Buffer[Length++] = (byte)b; }

        public void Bytes(byte[] b) { Ensure(b.Length); System.Buffer.BlockCopy(b, 0, Buffer, Length, b.Length); Length += b.Length; }

        public void UInt32(uint v) { for (int i = 0; i < 4; i++) Byte((int)(v >> (8 * i)) & 0xFF); }

        public void Int64(long v) { for (int i = 0; i < 8; i++) Byte((int)((ulong)v >> (8 * i)) & 0xFF); }

        public void Unsigned(ulong v)
        {
            while (v >= 0x80) { Byte((int)(v & 0x7F) | 0x80); v >>= 7; }
            Byte((int)v);
        }

        public void Signed(int v) => Signed64(v);

        public void Signed64(long v) => Unsigned((ulong)((v << 1) ^ (v >> 63)));

        public void Text(string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            if (b.Length > MaxText) b = Encoding.UTF8.GetBytes(Trim(s, MaxText));
            Unsigned((ulong)b.Length);
            Bytes(b);
        }

        public byte[] ToArray()
        {
            var r = new byte[Length];
            System.Buffer.BlockCopy(Buffer, 0, r, 0, Length);
            return r;
        }
    }

    /// <summary>Cuts <paramref name="s"/> so its UTF-8 form fits <paramref name="maxBytes"/>, never mid-character.</summary>
    private static string Trim(string s, int maxBytes)
    {
        var sb = new StringBuilder();
        int bytes = 0;
        foreach (var ch in s)
        {
            int n = Encoding.UTF8.GetByteCount(new[] { ch });
            if (char.IsHighSurrogate(ch)) n = 4; // with its pair; the low half then adds 0
            else if (char.IsLowSurrogate(ch)) n = 0;
            if (bytes + n > maxBytes) break;
            bytes += n;
            sb.Append(ch);
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[sb.Length - 1])) sb.Length--;
        return sb.ToString();
    }

    private sealed class Reader
    {
        private readonly byte[] _d;
        private int _p;
        private readonly int _end;

        public Reader(byte[] d, int start, int end) { _d = d; _p = start; _end = end; }

        public int Left => _end - _p;

        public int Byte()
        {
            if (_p >= _end) throw new GhostFormatException("file is cut short");
            return _d[_p++];
        }

        public long Int64()
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)Byte() << (8 * i);
            return (long)v;
        }

        public ulong Unsigned64()
        {
            ulong v = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                int b = Byte();
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
            }
            throw new GhostFormatException("a number is too long");
        }

        public long Signed64()
        {
            ulong u = Unsigned64();
            return (long)(u >> 1) ^ -(long)(u & 1);
        }

        public string Text()
        {
            ulong len = Unsigned64();
            if (len > MaxText || (long)len > Left) throw new GhostFormatException("a name is too long");
            try
            {
                string s = new UTF8Encoding(false, true).GetString(_d, _p, (int)len);
                _p += (int)len;
                return s;
            }
            catch (ArgumentException) { throw new GhostFormatException("a name is not valid text"); }
        }
    }
}
