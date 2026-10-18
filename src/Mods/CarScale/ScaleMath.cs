using System;
using System.Collections.Generic;

namespace OverTheHillPlus.CarScale;

/// <summary>
/// Car Scale's pure rules: the safe size range, how each physics value follows the size, who gets which size, and
/// the size table the host sends to modded players. Nothing here touches the game (the tests compile this file on
/// its own).
/// </summary>
internal static class ScaleMath
{
    /// <summary>
    /// Smallest and largest size (1 = the car's own). Outside this the vehicle physics stops behaving: tiny wheels
    /// fall into terrain gaps and the raycast suspension of a much bigger car reaches through the ground.
    /// </summary>
    public const float Min = 0.5f, Max = 2f;

    public static readonly float[] Steps = { 0.5f, 0.6f, 0.75f, 0.9f, 1f, 1.25f, 1.5f, 1.75f, 2f };

    public static float Clamp(float s)
    {
        if (!float.IsFinite(s)) return 1f;
        return s < Min ? Min : s > Max ? Max : s;
    }

    /// <summary>Two sizes that look the same (sizes are sent with 1/1000 precision).</summary>
    public static bool Same(float a, float b) => Math.Abs(a - b) < 0.0015f;

    public static bool IsNormal(float s) => Same(Clamp(s), 1f);

    public static string Label(float s)
    {
        s = Clamp(s);
        if (IsNormal(s)) return "Normal";
        string x = s.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
        return s < 1f ? $"Mini ({x})" : $"Giant ({x})";
    }

    // ---------------------------------------------------------------- physics

    /// <summary>Multipliers for one car's physics values at a size.</summary>
    internal readonly struct PhysicsScale
    {
        public readonly float Radius, Travel, Mass, Spring, Damper, Brake;

        public PhysicsScale(float radius, float travel, float mass, float spring, float damper, float brake)
        {
            Radius = radius; Travel = travel; Mass = mass; Spring = spring; Damper = damper; Brake = brake;
        }
    }

    /// <summary>
    /// How the physics follows a size s. Lengths (tyre radius, suspension travel) scale with s. The weight scales
    /// with s too, not with the volume (s³): a giant car keeps the stock engine, so with s³ it could barely move.
    /// With the weight and the travel both ×s the spring rate stays the same (the car sinks the same share of its
    /// travel), the damper rate ×√s keeps the same damping ratio (c ~ √(k·m)), and the brake torque ×s² keeps the
    /// same stopping distance (torque = mass × deceleration × radius).
    /// </summary>
    public static PhysicsScale PhysicsFor(float s)
    {
        s = Clamp(s);
        return new PhysicsScale(radius: s, travel: s, mass: s, spring: 1f, damper: (float)Math.Sqrt(s), brake: s * s);
    }

    // ---------------------------------------------------------------- who gets which size

    internal enum Mode
    {
        /// <summary>Every car normal size.</summary>
        Off,
        /// <summary>Only the host's car (players may still ask for their own size when allowed).</summary>
        JustMe,
        /// <summary>Every player's car.</summary>
        Everyone,
        /// <summary>Sizes the host picks per player.</summary>
        PerPlayer,
    }

    public static readonly string[] ModeLabels = { "Off", "Just me", "Everyone", "Per player" };

    /// <summary>
    /// The size one player's car gets. <paramref name="size"/> is the host's size setting (for itself in JustMe, for
    /// everyone in Everyone); <paramref name="perPlayer"/> the host's pick for this player (PerPlayer);
    /// <paramref name="request"/> what the player asked for, used only when <paramref name="allowRequests"/> and the
    /// host made no pick of its own for them (in Everyone the host's size wins).
    /// </summary>
    public static float SizeFor(Mode mode, bool isHost, float size, float? perPlayer, float? request, bool allowRequests)
    {
        float? asked = allowRequests && !isHost ? request : null;
        switch (mode)
        {
            case Mode.JustMe:
                return Clamp(isHost ? size : asked ?? 1f);
            case Mode.Everyone:
                return Clamp(size);
            case Mode.PerPlayer:
                return Clamp(perPlayer ?? asked ?? 1f);
            default:
                return 1f;
        }
    }

    // ---------------------------------------------------------------- the table sent to modded players

    public const int MaxEntries = 64;

    /// <summary>Wire size of a table with <paramref name="entries"/> rows.</summary>
    public static int EncodedSize(int entries) => 1 + Math.Min(entries, MaxEntries) * 6;

    /// <summary>
    /// Encodes (player id, size) rows: a count byte, then per row the player id (int32, little-endian) and the size in
    /// thousandths (uint16). Normal-size rows are left out (absent = normal); at most <see cref="MaxEntries"/> rows.
    /// </summary>
    public static byte[] EncodeTable(IEnumerable<KeyValuePair<int, float>> rows)
    {
        var list = new List<KeyValuePair<int, float>>();
        foreach (var r in rows)
        {
            if (list.Count >= MaxEntries) break;
            if (!IsNormal(r.Value)) list.Add(new KeyValuePair<int, float>(r.Key, Clamp(r.Value)));
        }
        list.Sort((a, b) => a.Key.CompareTo(b.Key));
        var bytes = new byte[EncodedSize(list.Count)];
        bytes[0] = (byte)list.Count;
        int p = 1;
        foreach (var r in list)
        {
            int id = r.Key;
            bytes[p++] = (byte)id;
            bytes[p++] = (byte)(id >> 8);
            bytes[p++] = (byte)(id >> 16);
            bytes[p++] = (byte)(id >> 24);
            ushort milli = (ushort)Math.Round(r.Value * 1000f);
            bytes[p++] = (byte)milli;
            bytes[p++] = (byte)(milli >> 8);
        }
        return bytes;
    }

    /// <summary>Reads a table; never throws. Short or hostile input gives the rows that are whole; sizes are clamped.</summary>
    public static Dictionary<int, float> DecodeTable(byte[] bytes)
    {
        var result = new Dictionary<int, float>();
        if (bytes == null || bytes.Length < 1) return result;
        int count = Math.Min((int)bytes[0], MaxEntries);
        int p = 1;
        for (int i = 0; i < count && p + 6 <= bytes.Length; i++)
        {
            int id = bytes[p] | bytes[p + 1] << 8 | bytes[p + 2] << 16 | bytes[p + 3] << 24;
            int milli = bytes[p + 4] | bytes[p + 5] << 8;
            p += 6;
            float s = Clamp(milli / 1000f);
            if (!IsNormal(s)) result[id] = s;
        }
        return result;
    }

    /// <summary>A player's own size request: the size in thousandths (uint16, little-endian).</summary>
    public static byte[] EncodeRequest(float size)
    {
        ushort milli = (ushort)Math.Round(Clamp(size) * 1000f);
        return new[] { (byte)milli, (byte)(milli >> 8) };
    }

    /// <summary>Reads a size request; null when too short.</summary>
    public static float? DecodeRequest(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 2) return null;
        return Clamp((bytes[0] | bytes[1] << 8) / 1000f);
    }
}
