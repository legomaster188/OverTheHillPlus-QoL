using System;
using System.Collections.Generic;
using System.Text;
using OverTheHillPlus.Shared;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A stock engine sound a modded car can use. The game picks a car's engine (and its other vehicle sounds) with the FMOD
/// label parameter "VehicleType", set from CarAudioManager.vehicleType (one label per stock car).
/// </summary>
public sealed class EngineSound
{
    /// <summary>A CarAudioManager.VehicleType name (the FMOD label), e.g. "Roamer".</summary>
    public string Label;
    /// <summary>The menu name, e.g. "Roamer (smooth V6)".</summary>
    public string Name;

    public EngineSound() { }
    public EngineSound(string label, string name) { Label = label; Name = name; }
}

/// <summary>Index lookups on a car's engine sound list. Pure (unit-tested).</summary>
public static class EngineSoundList
{
    /// <summary>The index of a label (any case); <paramref name="fallback"/> when unknown.</summary>
    public static byte IndexOf(IReadOnlyList<EngineSound> sounds, string label, byte fallback)
    {
        for (int i = 0; i < sounds.Count; i++)
            if (string.Equals(sounds[i].Label, label, StringComparison.OrdinalIgnoreCase)) return (byte)i;
        return fallback;
    }

    /// <summary>The label of an index; the default's when out of range (a newer peer's choice this build lacks).</summary>
    public static string Label(IReadOnlyList<EngineSound> sounds, byte index, byte fallback) =>
        index < sounds.Count ? sounds[index].Label : sounds[fallback < sounds.Count ? fallback : 0].Label;
}

/// <summary>How one player's modded car looks and sounds: the colour pair, the skin it wears (null: none) with the
/// first 8 bytes of the skin file's SHA-256, and the engine sound (an index into the car's own list).</summary>
public sealed class CarLook : IEquatable<CarLook>
{
    public CarRgb Main, Trim;
    public string SkinKey;
    public ulong SkinHash;
    public byte Engine;

    public bool HasSkin => !string.IsNullOrEmpty(SkinKey);

    /// <summary>The same look without its skin (what a player without that skin sees).</summary>
    public CarLook WithoutSkin() => new() { Main = Main, Trim = Trim, Engine = Engine };

    public bool Equals(CarLook o) =>
        o != null && Main == o.Main && Trim == o.Trim && Engine == o.Engine &&
        string.Equals(SkinKey ?? "", o.SkinKey ?? "", StringComparison.Ordinal) && (!HasSkin || SkinHash == o.SkinHash);

    public override bool Equals(object obj) => obj is CarLook o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Main, Trim, Engine, SkinKey ?? "", HasSkin ? SkinHash : 0UL);
}

/// <summary>
/// The appearance message on the shared modded-car look channel (ModNet channel 15).
/// <list type="bullet">
/// <item>Version 3 (every car but the Vera): u8 version, u8 car id length (2..40), the car id (ASCII, a mod id), main RGB
///   (3 bytes), trim RGB (3), u8 engine sound, u8 skin key length (0..96), the key (UTF-8), the skin's 8-byte hash
///   (big-endian; zeros without a skin).</item>
/// <item>Version 2 (the Vera, <see cref="LegacyCar"/>, exactly as builds before the car framework sent it): the same
///   without the car id. Version 1 (no engine byte) is still read too, as the Vera's default engine (index 0).</item>
/// </list>
/// The Vera keeps the old layout so a build from before the framework (which drops version 3) still sees its colours.
/// Receivers drop anything malformed: another version, a wrong length, an invalid car id or skin key
/// (see <see cref="CarSkinKey"/>). Pure (unit-tested).
/// </summary>
public static class CarLookWire
{
    /// <summary>The car whose looks travel without a car id (versions 1 and 2).</summary>
    public const string LegacyCar = "vera";

    public const byte Version = 3, LegacyVersion = 2;
    public const int MaxCarId = 40;
    /// <summary>A version-2 payload without a skin.</summary>
    public const int MinLegacyPayload = 1 + 3 + 3 + 1 + 1 + 8;
    private const int MinPayloadV1 = MinLegacyPayload - 1;
    /// <summary>The largest payload of any version (version 3 with the longest car id and skin key).</summary>
    public const int MaxPayload = MinLegacyPayload + 1 + MaxCarId + CarSkinKey.MaxBytes;

    /// <summary>The payload for car <paramref name="carId"/>'s look; a skin key that isn't valid is sent as no skin.
    /// Throws ArgumentException for a car id that isn't a valid mod id.</summary>
    public static byte[] Encode(string carId, CarLook look)
    {
        look ??= new CarLook();
        bool legacy = string.Equals(carId, LegacyCar, StringComparison.Ordinal);
        if (!legacy && !IsCarId(carId)) throw new ArgumentException("not a car id: " + carId, nameof(carId));
        byte[] id = legacy ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(carId);
        byte[] key = look.HasSkin && CarSkinKey.IsValid(look.SkinKey) ? Encoding.UTF8.GetBytes(look.SkinKey) : Array.Empty<byte>();
        var b = new byte[MinLegacyPayload + (legacy ? 0 : 1 + id.Length) + key.Length];
        int i = 0;
        b[i++] = legacy ? LegacyVersion : Version;
        if (!legacy)
        {
            b[i++] = (byte)id.Length;
            Array.Copy(id, 0, b, i, id.Length);
            i += id.Length;
        }
        b[i++] = look.Main.R; b[i++] = look.Main.G; b[i++] = look.Main.B;
        b[i++] = look.Trim.R; b[i++] = look.Trim.G; b[i++] = look.Trim.B;
        b[i++] = look.Engine;
        b[i++] = (byte)key.Length;
        Array.Copy(key, 0, b, i, key.Length);
        i += key.Length;
        ulong h = key.Length > 0 ? look.SkinHash : 0;
        for (int k = 7; k >= 0; k--) b[i++] = (byte)(h >> (k * 8));
        return b;
    }

    /// <summary>The channel's budget for one car (what the Vera's own channel had): each car announces, asks back and
    /// answers on its own, so the budget grows with the cars.</summary>
    public const float RatePerCar = 2f;
    public const int BurstPerCar = 6;

    /// <summary>The channel's rate (messages a second, per sender) and burst for <paramref name="cars"/> registered
    /// modded cars (at least one car's).</summary>
    public static (float rate, int burst) Budget(int cars)
    {
        int n = Math.Max(1, cars);
        return (RatePerCar * n, BurstPerCar * n);
    }

    /// <summary>Reads a payload of any version: versions 1 and 2 are the Vera's (<see cref="LegacyCar"/>).</summary>
    public static bool TryDecode(byte[] p, out string carId, out CarLook look)
    {
        carId = null;
        look = null;
        if (p == null || p.Length < MinPayloadV1 || p.Length > MaxPayload) return false;
        int at;
        byte engine;
        string id;
        switch (p[0])
        {
            case 1:
                id = LegacyCar; at = 1;
                engine = 0; // the Vera's default
                break;
            case 2:
                id = LegacyCar; at = 1;
                if (p.Length < MinLegacyPayload) return false;
                engine = p[7];
                break;
            case 3:
            {
                int n = p[1];
                if (n < 2 || n > MaxCarId || p.Length < 2 + n + MinLegacyPayload - 1) return false;
                for (int k = 0; k < n; k++)
                    if (p[2 + k] >= 0x80) return false;
                id = Encoding.ASCII.GetString(p, 2, n);
                if (!IsCarId(id) || id == LegacyCar) return false;
                at = 2 + n;
                engine = p[at + 6];
                break;
            }
            default:
                return false;
        }
        int colours = at;
        int keyAt = at + 6 + (p[0] == 1 ? 0 : 1); // the key length byte
        if (keyAt >= p.Length) return false;
        int len = p[keyAt];
        if (len > CarSkinKey.MaxBytes || p.Length != keyAt + 1 + len + 8) return false;
        string key = null;
        if (len > 0)
        {
            try { key = new UTF8Encoding(false, true).GetString(p, keyAt + 1, len); }
            catch (ArgumentException ex) { DebugLog.Error("core", ex, "CarLookWire.TryDecode"); return false; }
            if (!CarSkinKey.IsValid(key)) return false;
        }
        ulong h = 0;
        for (int k = 0; k < 8; k++) h = (h << 8) | p[keyAt + 1 + len + k];
        carId = id;
        look = new CarLook
        {
            Main = new CarRgb(p[colours], p[colours + 1], p[colours + 2]),
            Trim = new CarRgb(p[colours + 3], p[colours + 4], p[colours + 5]),
            Engine = engine,
            SkinKey = key,
            SkinHash = key != null ? h : 0,
        };
        return true;
    }

    /// <summary>A car id as it travels: a mod id (2 to 40 characters from a-z, 0-9, '_', '.' and '-').</summary>
    public static bool IsCarId(string id)
    {
        if (id == null || id.Length < 2 || id.Length > MaxCarId) return false;
        foreach (char c in id)
            if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '.' || c == '-')) return false;
        return true;
    }
}
