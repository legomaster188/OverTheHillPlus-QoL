using System;

namespace OverTheHillPlus.Flares;

/// <summary>One flare as the host broadcasts it: who fired it, where, in what colour, and whether it was automatic.</summary>
internal readonly struct FlareInfo
{
    public readonly int Owner;
    public readonly float X, Y, Z;
    public readonly byte R, G, B;
    public readonly bool Auto;

    public FlareInfo(int owner, float x, float y, float z, byte r, byte g, byte b, bool auto)
    {
        Owner = owner; X = x; Y = y; Z = z; R = r; G = g; B = b; Auto = auto;
    }
}

/// <summary>
/// Flares' two messages (ModNet channel 33): a player's request to the host (where, and manual or automatic) and the
/// host's broadcast of an accepted flare (owner, where, colour). Floats are little endian. Decoding never throws and
/// rejects anything that is not a finite position; extra bytes (a newer version's fields) are ignored.
/// </summary>
internal static class FlareWire
{
    public const byte MsgRequest = 1, MsgFlare = 2;
    public const int RequestSize = 13, FlareSize = 20;

    private static void PutFloat(byte[] b, int at, float v) => BitConverter.GetBytes(v).CopyTo(b, at);

    private static float GetFloat(byte[] b, int at) => BitConverter.ToSingle(b, at);

    private static bool Finite(float a, float b, float c) => float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(c);

    public static byte[] EncodeRequest(float x, float y, float z, bool auto)
    {
        var b = new byte[RequestSize];
        PutFloat(b, 0, x); PutFloat(b, 4, y); PutFloat(b, 8, z);
        b[12] = (byte)(auto ? 1 : 0);
        return b;
    }

    public static bool DecodeRequest(byte[] b, out float x, out float y, out float z, out bool auto)
    {
        x = y = z = 0f;
        auto = false;
        if (b == null || b.Length < RequestSize) return false;
        x = GetFloat(b, 0); y = GetFloat(b, 4); z = GetFloat(b, 8);
        auto = b[12] == 1;
        return Finite(x, y, z);
    }

    public static byte[] EncodeFlare(in FlareInfo f)
    {
        var b = new byte[FlareSize];
        BitConverter.GetBytes(f.Owner).CopyTo(b, 0);
        PutFloat(b, 4, f.X); PutFloat(b, 8, f.Y); PutFloat(b, 12, f.Z);
        b[16] = f.R; b[17] = f.G; b[18] = f.B;
        b[19] = (byte)(f.Auto ? 1 : 0);
        return b;
    }

    public static bool DecodeFlare(byte[] b, out FlareInfo f)
    {
        f = default;
        if (b == null || b.Length < FlareSize) return false;
        float x = GetFloat(b, 4), y = GetFloat(b, 8), z = GetFloat(b, 12);
        if (!Finite(x, y, z)) return false;
        f = new FlareInfo(BitConverter.ToInt32(b, 0), x, y, z, b[16], b[17], b[18], b[19] == 1);
        return true;
    }

    public static byte ToByte(float unit) => float.IsNaN(unit) ? (byte)0 : (byte)Math.Round(Math.Clamp(unit, 0f, 1f) * 255f);
}
