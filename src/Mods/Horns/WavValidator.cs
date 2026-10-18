using System;
using System.IO;

namespace OverTheHillPlus.Horns;

/// <summary>What <see cref="WavValidator"/> found in a good file.</summary>
internal sealed class WavInfo
{
    /// <summary>1 = PCM, 3 = IEEE float (an extensible file reports its sub-format).</summary>
    public int Format;
    public int Channels;
    public int SampleRate;
    public int BitsPerSample;
    public int BlockAlign;
    /// <summary>Where the samples start in the file, and how many bytes of them there are.</summary>
    public int DataOffset;
    public int DataLength;
    public double Seconds;
}

/// <summary>
/// Checks a horn sound before it is played, sent or kept: at most <see cref="MaxBytes"/> (256 KB), a RIFF/WAVE file
/// with a PCM (8/16/24/32-bit) or 32-bit float fmt chunk, 1 or 2 channels at 8-96 kHz, a data chunk inside the file,
/// and at most <see cref="MaxSeconds"/> long. Pure (no game code) and never throws on hostile input: every chunk size
/// is checked against the bytes that are really there.
/// </summary>
internal static class WavValidator
{
    public const int MaxBytes = 256 * 1024;
    public const double MaxSeconds = 10.0;
    private const int MaxChunks = 64;
    private const int FormatPcm = 1, FormatFloat = 3, FormatExtensible = 0xFFFE;

    /// <summary>Validates <paramref name="data"/>. On failure <paramref name="error"/> says why in a few words.</summary>
    public static bool TryValidate(byte[] data, out WavInfo info, out string error)
    {
        info = null;
        if (data == null || data.Length < 44) { error = "too short to be a wav file"; return false; }
        if (data.Length > MaxBytes) { error = "larger than 256 KB"; return false; }
        if (!Tag(data, 0, "RIFF") || !Tag(data, 8, "WAVE")) { error = "not a RIFF/WAVE file"; return false; }

        long riffEnd = 8L + U32(data, 4);
        if (riffEnd > data.Length) { error = "truncated (the file is shorter than its header says)"; return false; }
        int end = (int)riffEnd;

        WavInfo fmt = null;
        int dataOffset = -1, dataLength = 0;
        int pos = 12;
        for (int n = 0; pos + 8 <= end && n < MaxChunks; n++)
        {
            uint size = U32(data, pos + 4);
            long body = pos + 8L;
            if (body + size > end) { error = "truncated chunk"; return false; }
            if (Tag(data, pos, "fmt "))
            {
                if (fmt != null) { error = "two fmt chunks"; return false; }
                if (!ReadFmt(data, (int)body, (int)size, out fmt, out error)) return false;
            }
            else if (Tag(data, pos, "data"))
            {
                if (dataOffset >= 0) { error = "two data chunks"; return false; }
                dataOffset = (int)body;
                dataLength = (int)size;
            }
            long next = body + size + (size & 1); // chunks are padded to an even size
            if (next > int.MaxValue) break;
            pos = (int)next;
        }

        if (pos + 8 <= end && (fmt == null || dataOffset < 0)) { error = $"too many chunks (over {MaxChunks}) before the sound"; return false; }
        if (fmt == null) { error = "no fmt chunk"; return false; }
        if (dataOffset < 0) { error = "no data chunk (no sound in it)"; return false; }
        if (dataLength < fmt.BlockAlign) { error = "the data chunk is empty"; return false; }

        double seconds = (double)dataLength / fmt.BlockAlign / fmt.SampleRate;
        if (seconds > MaxSeconds + 1e-9) { error = $"longer than {MaxSeconds:0} s ({seconds:0.0} s)"; return false; }

        fmt.DataOffset = dataOffset;
        fmt.DataLength = dataLength;
        fmt.Seconds = seconds;
        info = fmt;
        error = null;
        return true;
    }

    /// <summary>
    /// A clean copy of a validated file: a plain 16-byte fmt chunk (PCM or float tag, never extensible) and the data
    /// chunk, cut to whole frames, nothing else. What FMOD opens is only ever this, so it never parses a chunk the
    /// validator didn't check.
    /// </summary>
    public static byte[] Canonical(byte[] data, WavInfo info)
    {
        int len = info.DataLength / info.BlockAlign * info.BlockAlign;
        var b = new byte[44 + len];
        void Ascii(int at, string t) { for (int i = 0; i < 4; i++) b[at + i] = (byte)t[i]; }
        void I32(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
        void I16(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
        Ascii(0, "RIFF"); I32(4, 36 + len); Ascii(8, "WAVE");
        Ascii(12, "fmt "); I32(16, 16); I16(20, info.Format); I16(22, info.Channels); I32(24, info.SampleRate);
        I32(28, info.SampleRate * info.BlockAlign); I16(32, info.BlockAlign); I16(34, info.BitsPerSample);
        Ascii(36, "data"); I32(40, len);
        Buffer.BlockCopy(data, info.DataOffset, b, 44, len);
        return b;
    }

    /// <summary>
    /// Reads and validates a wav file. A file over the cap is refused from its size alone, before any of it is read.
    /// </summary>
    public static bool TryReadFile(string path, out byte[] bytes, out WavInfo info, out string error)
    {
        bytes = null;
        info = null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { error = "file not found"; return false; }
            if (fi.Length > MaxBytes) { error = "larger than 256 KB"; return false; }
            var data = File.ReadAllBytes(path);
            if (!TryValidate(data, out info, out error)) return false;
            bytes = data;
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("horns", e, "WavValidator.TryReadFile");
            error = "could not read it (" + e.GetType().Name + ")";
            return false;
        }
    }

    private static bool ReadFmt(byte[] d, int at, int size, out WavInfo fmt, out string error)
    {
        fmt = null;
        if (size < 16) { error = "fmt chunk too small"; return false; }
        int format = U16(d, at);
        int channels = U16(d, at + 2);
        int rate = (int)Math.Min(U32(d, at + 4), int.MaxValue);
        int blockAlign = U16(d, at + 12);
        int bits = U16(d, at + 14);
        if (format == FormatExtensible)
        {
            if (size < 40) { error = "extensible fmt chunk too small"; return false; }
            format = U16(d, at + 24); // the first two bytes of the sub-format GUID
        }

        bool pcm = format == FormatPcm && (bits == 8 || bits == 16 || bits == 24 || bits == 32);
        bool flt = format == FormatFloat && bits == 32;
        if (!pcm && !flt) { error = $"unsupported sample format ({format}, {bits}-bit): use 16-bit PCM"; return false; }
        if (channels < 1 || channels > 2) { error = "only mono or stereo files"; return false; }
        if (rate < 8000 || rate > 96000) { error = "sample rate must be 8-96 kHz"; return false; }
        if (blockAlign != channels * bits / 8) { error = "inconsistent fmt chunk"; return false; }

        fmt = new WavInfo { Format = format, Channels = channels, SampleRate = rate, BitsPerSample = bits, BlockAlign = blockAlign };
        error = null;
        return true;
    }

    private static bool Tag(byte[] d, int at, string tag) =>
        at + 4 <= d.Length && d[at] == tag[0] && d[at + 1] == tag[1] && d[at + 2] == tag[2] && d[at + 3] == tag[3];

    private static int U16(byte[] d, int at) => d[at] | (d[at + 1] << 8);

    private static uint U32(byte[] d, int at) => (uint)(d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (d[at + 3] << 24));
}
