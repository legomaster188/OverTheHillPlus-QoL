using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverTheHillPlus.Cars;

/// <summary>An sRGB colour, 0..255 a channel.</summary>
public readonly struct CarRgb : IEquatable<CarRgb>
{
    public readonly byte R, G, B;

    public CarRgb(byte r, byte g, byte b) { R = r; G = g; B = b; }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>"#RRGGBB" or "RRGGBB" (any case, spaces around ignored).</summary>
    public static bool TryParse(string text, out CarRgb rgb)
    {
        rgb = default;
        if (text == null) return false;
        string t = text.Trim();
        if (t.StartsWith("#", StringComparison.Ordinal)) t = t.Substring(1);
        if (t.Length != 6) return false;
        foreach (char c in t)
            if (!Uri.IsHexDigit(c)) return false;
        int v = int.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        rgb = new CarRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public bool Equals(CarRgb o) => R == o.R && G == o.G && B == o.B;
    public override bool Equals(object obj) => obj is CarRgb o && Equals(o);
    public override int GetHashCode() => R << 16 | G << 8 | B;
    public static bool operator ==(CarRgb a, CarRgb b) => a.Equals(b);
    public static bool operator !=(CarRgb a, CarRgb b) => !a.Equals(b);
    public override string ToString() => ToHex();
}

/// <summary>A main paint preset of a modded car.</summary>
public sealed class PaintPreset
{
    public string Name;
    public CarRgb Rgb;

    public PaintPreset() { }
    public PaintPreset(string name, CarRgb rgb) { Name = name; Rgb = rgb; }
}

/// <summary>A trim (second colour) preset: a colour, or the body colour itself.</summary>
public sealed class TrimPreset
{
    public string Name;
    public CarRgb Rgb;
    /// <summary>The trim takes the body colour (<see cref="Rgb"/> is not used).</summary>
    public bool BodyColour;

    public TrimPreset() { }
    public TrimPreset(string name, CarRgb rgb) { Name = name; Rgb = rgb; }
    public static TrimPreset Body(string name = CarPaint.TrimBody) => new() { Name = name, BodyColour = true };
}

/// <summary>
/// A modded car's paint: preset and custom colours and the recolouring of the albedo. <c>&lt;name&gt;_zones.png</c>
/// (same UV atlas as the albedo, Non-Color) says where the colours go: R = main paint, G = the second colour (trim),
/// 0..255 with anti-aliased edges. The albedo for a colour pair is <c>base * (1 - r - g) + main * r + trim * g</c> in
/// sRGB bytes, with r + g clamped to 1 (both scaled down together). Pure (unit-tested); the runtime builds one texture
/// per pair in use.
/// </summary>
public static class CarPaint
{
    public const string Custom = "Custom";
    public const string TrimBody = "Body colour";

    /// <summary>Menu labels: the presets, then Custom.</summary>
    public static string[] PaintLabels(IReadOnlyList<PaintPreset> paints)
    {
        var l = new string[paints.Count + 1];
        for (int i = 0; i < paints.Count; i++) l[i] = paints[i].Name;
        l[paints.Count] = Custom;
        return l;
    }

    /// <summary>Menu labels: the trims, then Custom.</summary>
    public static string[] TrimLabels(IReadOnlyList<TrimPreset> trims)
    {
        var l = new string[trims.Count + 1];
        for (int i = 0; i < trims.Count; i++) l[i] = trims[i].Name;
        l[trims.Count] = Custom;
        return l;
    }

    /// <summary>The main colour for a paint setting: a preset by name, Custom's colour, or the first (default) preset.</summary>
    public static CarRgb MainColour(IReadOnlyList<PaintPreset> paints, string paint, string customHex)
    {
        foreach (var p in paints)
            if (string.Equals(p.Name, paint, StringComparison.OrdinalIgnoreCase)) return p.Rgb;
        if (string.Equals(paint, Custom, StringComparison.OrdinalIgnoreCase) && CarRgb.TryParse(customHex, out var c)) return c;
        return paints[0].Rgb;
    }

    /// <summary>The trim colour for a trim setting (a body-colour trim = <paramref name="main"/>); the first (default)
    /// trim when unknown.</summary>
    public static CarRgb TrimColour(IReadOnlyList<TrimPreset> trims, string trim, string customHex, CarRgb main)
    {
        foreach (var t in trims)
            if (string.Equals(t.Name, trim, StringComparison.OrdinalIgnoreCase)) return t.BodyColour ? main : t.Rgb;
        if (string.Equals(trim, Custom, StringComparison.OrdinalIgnoreCase) && CarRgb.TryParse(customHex, out var c)) return c;
        return trims[0].BodyColour ? main : trims[0].Rgb;
    }

    /// <summary>
    /// Recolours RGBA32 pixels: <paramref name="baseRgba"/> and <paramref name="zonesRgba"/> (R = main, G = trim) of the
    /// same size into <paramref name="outRgba"/>; alpha is the base's. Throws ArgumentException on mismatched sizes.
    /// </summary>
    public static void Recolour(ReadOnlySpan<byte> baseRgba, ReadOnlySpan<byte> zonesRgba, CarRgb main, CarRgb trim, Span<byte> outRgba)
    {
        if (baseRgba.Length % 4 != 0 || zonesRgba.Length != baseRgba.Length || outRgba.Length != baseRgba.Length)
            throw new ArgumentException("the base, zones and output must be RGBA32 of the same size");
        for (int i = 0; i < baseRgba.Length; i += 4)
        {
            Weights(zonesRgba[i], zonesRgba[i + 1], out int r, out int g);
            int keep = 255 - r - g;
            outRgba[i] = Mix(baseRgba[i], main.R, trim.R, keep, r, g);
            outRgba[i + 1] = Mix(baseRgba[i + 1], main.G, trim.G, keep, r, g);
            outRgba[i + 2] = Mix(baseRgba[i + 2], main.B, trim.B, keep, r, g);
            outRgba[i + 3] = baseRgba[i + 3];
        }
    }

    /// <summary>The zone weights out of 255, scaled down together so they add up to at most 255.</summary>
    public static void Weights(byte zoneR, byte zoneG, out int r, out int g)
    {
        r = zoneR;
        g = zoneG;
        int sum = r + g;
        if (sum <= 255) return;
        r = (r * 255 + sum / 2) / sum;
        g = 255 - r;
    }

    /// <summary>One channel: (base * keep + main * r + trim * g) / 255, rounded; keep + r + g = 255.</summary>
    public static byte Mix(byte b, byte main, byte trim, int keep, int r, int g) =>
        (byte)((b * keep + main * r + trim * g + 127) / 255);
}
