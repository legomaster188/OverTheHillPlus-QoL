using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Horns;

/// <summary>
/// The built-in horns, made from sine waves at runtime (no recorded audio ships with the mod). Each has a stable id
/// that goes over the wire, so ids are never reused or renumbered; add new ones at the end.
///
/// <para>"Hold" horns are exactly one second of 16-bit mono at <see cref="SampleRate"/>, and every frequency in them
/// is a whole number of hertz, so the buffer ends where it began and loops without a click for as long as the horn
/// is held. "Whole clip" horns play once per press.</para>
/// Pure: no game code, so the tests cover it.
/// </summary>
internal static class HornSynth
{
    public const int SampleRate = 22050;

    internal sealed class Preset
    {
        public byte Id;
        public string Name;
        public HornMode Mode;
        internal Func<float[]> Make;
    }

    public static readonly Preset[] All =
    {
        new() { Id = 1, Name = "Two-tone", Mode = HornMode.Hold, Make = () => Chord(new[] { 415, 523 }, harmonics: 9, drive: 2.2f, vibratoHz: 0) },
        new() { Id = 2, Name = "Air horn", Mode = HornMode.Hold, Make = () => Chord(new[] { 147, 185, 220 }, harmonics: 14, drive: 3.0f, vibratoHz: 0) },
        new() { Id = 3, Name = "Compact beep", Mode = HornMode.Hold, Make = () => Chord(new[] { 620, 740 }, harmonics: 5, drive: 1.6f, vibratoHz: 0) },
        new() { Id = 4, Name = "Train chime", Mode = HornMode.Hold, Make = () => Chord(new[] { 311, 370, 440, 494, 622 }, harmonics: 6, drive: 1.4f, vibratoHz: 0) },
        new() { Id = 5, Name = "Warble", Mode = HornMode.Hold, Make = Warble },
        new() { Id = 6, Name = "Bulb honk", Mode = HornMode.WholeClip, Make = BulbHonk },
        new() { Id = 7, Name = "Fanfare", Mode = HornMode.WholeClip, Make = Fanfare },
        new() { Id = 8, Name = "Low rumble", Mode = HornMode.Hold, Make = () => Chord(new[] { 98, 131 }, harmonics: 18, drive: 3.5f, vibratoHz: 3) },
    };

    private static readonly Dictionary<byte, byte[]> Cache = new();

    public static Preset Find(byte id)
    {
        foreach (var p in All)
            if (p.Id == id) return p;
        return null;
    }

    /// <summary>The preset as a wav file (made once, then cached), or null for an unknown id.</summary>
    public static byte[] Wav(byte id)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var wav)) return (byte[])wav.Clone();
            var p = Find(id);
            if (p == null) return null;
            wav = WavWriter.Pcm16Mono(Normalize(p.Make(), 0.8f), SampleRate);
            Cache[id] = wav;
            return (byte[])wav.Clone();
        }
    }

    // ---------------------------------------------------------------- voices

    /// <summary>
    /// One second of a chord of horn reeds: each note a band-limited sawtooth (harmonics at 1/k), soft-clipped for
    /// the brassy edge. <paramref name="vibratoHz"/> (whole hertz) adds a slow amplitude wobble.
    /// </summary>
    private static float[] Chord(int[] notes, int harmonics, float drive, int vibratoHz)
    {
        var s = new float[SampleRate];
        for (int n = 0; n < s.Length; n++)
        {
            double t = (double)n / SampleRate;
            double v = 0;
            foreach (int f in notes)
                for (int k = 1; k <= harmonics && f * k < SampleRate / 2; k++)
                    v += Math.Sin(2 * Math.PI * f * k * t) / k;
            v /= notes.Length;
            if (vibratoHz > 0) v *= 0.85 + 0.15 * Math.Sin(2 * Math.PI * vibratoHz * t);
            s[n] = (float)Math.Tanh(drive * v);
        }
        return s;
    }

    /// <summary>A siren-like warble: 700 Hz swept +-150 Hz six times a second (whole hertz, so it loops cleanly).</summary>
    private static float[] Warble()
    {
        var s = new float[SampleRate];
        const double carrier = 700, depth = 150, rate = 6;
        for (int n = 0; n < s.Length; n++)
        {
            double t = (double)n / SampleRate;
            double phase = 2 * Math.PI * carrier * t - depth / rate * Math.Cos(2 * Math.PI * rate * t);
            s[n] = (float)Math.Tanh(1.8 * (Math.Sin(phase) + 0.3 * Math.Sin(2 * phase) + 0.15 * Math.Sin(3 * phase)));
        }
        return s;
    }

    /// <summary>A rubber bulb horn: a nasal "honk" that slides down in pitch, played once.</summary>
    private static float[] BulbHonk()
    {
        int len = (int)(0.55 * SampleRate);
        var s = new float[len];
        double phase = 0;
        for (int n = 0; n < len; n++)
        {
            double t = (double)n / SampleRate;
            double f = 360 - 110 * Math.Min(1.0, t / 0.45);
            phase += 2 * Math.PI * f / SampleRate;
            double v = 0;
            for (int k = 1; k <= 12; k += 2) v += Math.Sin(k * phase) / k; // odd harmonics: square-ish, nasal
            double env = Math.Min(1.0, t / 0.015) * Math.Min(1.0, (0.55 - t) / 0.12);
            s[n] = (float)(Math.Tanh(2.5 * v) * Math.Max(0, env));
        }
        return s;
    }

    /// <summary>Four rising brass notes (C, E, G, high C), the last one held a little.</summary>
    private static float[] Fanfare()
    {
        int[] notes = { 523, 659, 784, 1047 };
        double[] lengths = { 0.16, 0.16, 0.16, 0.5 };
        var list = new List<float>();
        for (int i = 0; i < notes.Length; i++)
        {
            int len = (int)(lengths[i] * SampleRate);
            for (int n = 0; n < len; n++)
            {
                double t = (double)n / SampleRate;
                double v = 0;
                for (int k = 1; k <= 8 && notes[i] * k < SampleRate / 2; k++) v += Math.Sin(2 * Math.PI * notes[i] * k * t) / (k * k * 0.6 + 0.4);
                double env = Math.Min(1.0, t / 0.01) * Math.Min(1.0, (lengths[i] - t) / 0.03);
                list.Add((float)(Math.Tanh(1.5 * v) * Math.Max(0, env)));
            }
        }
        return list.ToArray();
    }

    private static float[] Normalize(float[] s, float peak)
    {
        float max = 0f;
        foreach (float v in s) max = Math.Max(max, Math.Abs(v));
        if (max <= 0f) return s;
        float g = peak / max;
        for (int i = 0; i < s.Length; i++) s[i] *= g;
        return s;
    }
}

/// <summary>Writes a 16-bit mono PCM wav file.</summary>
internal static class WavWriter
{
    public static byte[] Pcm16Mono(float[] samples, int rate)
    {
        int data = samples.Length * 2;
        var b = new byte[44 + data];
        void Ascii(int at, string s) { for (int i = 0; i < 4; i++) b[at + i] = (byte)s[i]; }
        void I32(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
        void I16(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
        Ascii(0, "RIFF"); I32(4, 36 + data); Ascii(8, "WAVE");
        Ascii(12, "fmt "); I32(16, 16); I16(20, 1); I16(22, 1); I32(24, rate); I32(28, rate * 2); I16(32, 2); I16(34, 16);
        Ascii(36, "data"); I32(40, data);
        for (int i = 0; i < samples.Length; i++)
        {
            int v = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            I16(44 + i * 2, v);
        }
        return b;
    }
}
