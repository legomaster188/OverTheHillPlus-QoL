using System;
using System.Collections.Generic;

namespace OverTheHillPlus.GearboxPlus;

/// <summary>What a differential does. Stock leaves the game's own setting alone (every stock car has locked differentials: the
/// axles, and the link between them, are joined solid).</summary>
public enum DiffMode : byte { Stock = 0, Open = 1, LimitedSlip = 2, Locked = 3 }

/// <summary>Where the differential modes come from: the game's own, the real car's, or the player's choice.</summary>
public enum DiffSource : byte { Stock = 0, RealCar = 1, Custom = 2 }

/// <summary>What is forced on one axle's differential: nothing (it follows its settings), locked, or open. These are the vehicle
/// physics' own runtime overrides.</summary>
public enum AxleForce : byte { None = 0, Locked = 1, Open = 2 }

/// <summary>The three differentials of a car (front axle, rear axle, the centre between the axles), and whether the lock key is
/// on, which locks every one of them.</summary>
public readonly struct DiffSetup : IEquatable<DiffSetup>
{
    public readonly DiffMode Front, Rear, Centre;
    public readonly bool LockAll;

    public DiffSetup(DiffMode front, DiffMode rear, DiffMode centre, bool lockAll = false)
    {
        Front = front; Rear = rear; Centre = centre; LockAll = lockAll;
    }

    public static readonly DiffSetup Stock = new(DiffMode.Stock, DiffMode.Stock, DiffMode.Stock);

    /// <summary>Nothing is changed from the game: every mode is Stock and the lock key is off.</summary>
    public bool IsStock => Front == DiffMode.Stock && Rear == DiffMode.Stock && Centre == DiffMode.Stock && !LockAll;

    public DiffSetup WithLock(bool on) => new(Front, Rear, Centre, on);

    public bool Equals(DiffSetup o) => Front == o.Front && Rear == o.Rear && Centre == o.Centre && LockAll == o.LockAll;
    public override bool Equals(object obj) => obj is DiffSetup o && Equals(o);
    public override int GetHashCode() => (int)Front | (int)Rear << 2 | (int)Centre << 4 | (LockAll ? 1 : 0) << 6;
}

/// <summary>What to write on a car's axle differentials for a setup.</summary>
public readonly struct AxleDrive
{
    public readonly AxleForce Front, Rear;
    /// <summary>The axle differentials' shared type must be the limited-slip one (one of the axles wants it).</summary>
    public readonly bool LimitedSlip;
    /// <summary>The setup changes something on the axles.</summary>
    public readonly bool Touches;

    public AxleDrive(AxleForce front, AxleForce rear, bool limitedSlip, bool touches)
    {
        Front = front; Rear = rear; LimitedSlip = limitedSlip; Touches = touches;
    }
}

/// <summary>Gearbox+'s differential logic and wording (no game types, unit tested).</summary>
public static class DiffMath
{
    /// <summary>The mode an axle or the centre really has: locked while the lock key is on, else the setting.</summary>
    public static DiffMode Effective(DiffMode configured, bool lockAll) => lockAll ? DiffMode.Locked : configured;

    /// <summary>
    /// How to write the two axle differentials. The vehicle physics has one type for both axles (the game's cars share one
    /// settings object) and a per-axle override that can force a differential locked or open. A limited-slip axle therefore
    /// sets the shared type to limited-slip and follows it; the other axle, whatever it wants, is forced (locked for Stock, which is
    /// what the game's cars have). With no limited-slip axle the type is left alone and Open / Locked are forced.
    /// </summary>
    public static AxleDrive Resolve(DiffSetup s)
    {
        var f = Effective(s.Front, s.LockAll);
        var r = Effective(s.Rear, s.LockAll);
        bool lsd = f == DiffMode.LimitedSlip || r == DiffMode.LimitedSlip;
        return new AxleDrive(Force(f, lsd), Force(r, lsd), lsd, f != DiffMode.Stock || r != DiffMode.Stock);
    }

    private static AxleForce Force(DiffMode m, bool sharedTypeIsLsd) => m switch
    {
        DiffMode.Open => AxleForce.Open,
        DiffMode.Locked => AxleForce.Locked,
        DiffMode.LimitedSlip => AxleForce.None,
        _ => sharedTypeIsLsd ? AxleForce.Locked : AxleForce.None,
    };

    /// <summary>The centre differential's mode (a stock one stays as the game has it).</summary>
    public static DiffMode CentreMode(DiffSetup s) => Effective(s.Centre, s.LockAll);

    // ---------------------------------------------------------------- the setup for a car

    /// <summary>
    /// The real car's differentials, from a modded car's own definition (<c>CarDefinition.Diffs</c>: front, rear, centre as 0 the
    /// game's own, 1 open, 2 limited slip, 3 locked; their real counterparts are 4x4s with open differentials, a part-time or
    /// clutch-coupled centre, and a lock the driver engages, which the lock key does). <paramref name="modded"/> gives them by the
    /// car's name (null for a car that is not modded); a stock car, another creator's car, or anything unreadable keeps the game's
    /// own: <see cref="DiffSetup.Stock"/>.
    /// </summary>
    public static DiffSetup RealCarSetup(string gameName, Func<string, byte[]> modded = null)
    {
        string n = GearSets.Normalize(gameName);
        if (n.Length == 0 || modded == null) return DiffSetup.Stock;
        byte[] d;
        try { d = modded(n); }
        catch (Exception) { return DiffSetup.Stock; }
        if (d == null || d.Length != 3) return DiffSetup.Stock;
        return new DiffSetup(ClampMode(d[0]), ClampMode(d[1]), ClampMode(d[2]));
    }

    /// <summary>The setup for a car under the player's settings (the lock key's state goes on top).</summary>
    public static DiffSetup For(DiffSource source, string gameName, DiffMode front, DiffMode rear, DiffMode centre, bool lockAll,
        Func<string, byte[]> modded = null)
    {
        DiffSetup s = source switch
        {
            DiffSource.RealCar => RealCarSetup(gameName, modded),
            DiffSource.Custom => new DiffSetup(front, rear, centre),
            _ => DiffSetup.Stock,
        };
        return s.WithLock(lockAll && source != DiffSource.Stock);
    }

    // ---------------------------------------------------------------- words

    /// <summary>"OPEN", "LSD" or "LOCK" (Stock reads as LOCK: the game's differentials are locked).</summary>
    public static string Label(DiffMode m) => m switch
    {
        DiffMode.Open => "OPEN",
        DiffMode.LimitedSlip => "LSD",
        _ => "LOCK",
    };

    /// <summary>"Stock", "Open", "Limited slip" or "Locked" for the MODS page.</summary>
    public static string Name(DiffMode m) => m switch
    {
        DiffMode.Open => "Open",
        DiffMode.LimitedSlip => "Limited slip",
        DiffMode.Locked => "Locked",
        _ => "Stock (locked)",
    };

    public static string SourceName(DiffSource s) => s switch
    {
        DiffSource.RealCar => "The real car's",
        DiffSource.Custom => "My choice",
        _ => "Stock (the game's locked)",
    };

    /// <summary>The indicator's line: "F LOCK  R LOCK" and, when the car has a centre differential, "C LSD".</summary>
    public static string Line(DiffSetup s, bool hasCentre)
    {
        string f = Label(Effective(s.Front, s.LockAll)), r = Label(Effective(s.Rear, s.LockAll));
        string line = "F " + f + "   R " + r;
        return hasCentre ? line + "   C " + Label(CentreMode(s)) : line;
    }

    /// <summary>Whether every differential reads locked (the lit state of the indicator).</summary>
    public static bool AllLocked(DiffSetup s, bool hasCentre) =>
        Effective(s.Front, s.LockAll) is DiffMode.Locked or DiffMode.Stock &&
        Effective(s.Rear, s.LockAll) is DiffMode.Locked or DiffMode.Stock &&
        (!hasCentre || CentreMode(s) is DiffMode.Locked or DiffMode.Stock);

    public static DiffMode ClampMode(int v) => v < 0 || v > (int)DiffMode.Locked ? DiffMode.Stock : (DiffMode)v;
    public static DiffSource ClampSource(int v) => v < 0 || v > (int)DiffSource.Custom ? DiffSource.Stock : (DiffSource)v;
}

/// <summary>A player's differential setup as it travels to the host (a guest tells the host how its car's differentials are set, so
/// the host simulates the same car). Decoding clamps everything and never throws.</summary>
public static class DiffWire
{
    public const byte MsgSetup = 1;
    public const byte Format = 1;
    public const int Size = 5;

    public static byte[] Encode(DiffSetup s) => new[] { Format, (byte)s.Front, (byte)s.Rear, (byte)s.Centre, (byte)(s.LockAll ? 1 : 0) };

    public static bool Decode(byte[] payload, out DiffSetup s)
    {
        s = DiffSetup.Stock;
        if (payload == null || payload.Length < Size || payload[0] != Format) return false;
        s = new DiffSetup(DiffMath.ClampMode(payload[1]), DiffMath.ClampMode(payload[2]), DiffMath.ClampMode(payload[3]), (payload[4] & 1) != 0);
        return true;
    }
}
