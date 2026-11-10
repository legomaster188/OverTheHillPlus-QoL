using System.Collections.Generic;

namespace OverTheHillPlus.Grayling;

/// <summary>What a player's car change was, as the launch rules see it.</summary>
public enum CarSwap
{
    None = 0,
    /// <summary>They changed into a boat (from a car, or the first time they are seen, in a boat).</summary>
    IntoBoat,
    /// <summary>They changed from a boat into a car.</summary>
    OutOfBoat,
    /// <summary>Car to car, boat to boat (a respawn), or the first time they are seen, in a car.</summary>
    Other,
}

/// <summary>
/// One player's car changes, noticed from the network id of the car object they drive (the game's FusionPlayer sits on the
/// car: changing car spawns a new object with a new id). The host only sees the players' cars through a scan that is up to
/// a second old, so across a change a player can be missing for a moment (the old car destroyed, the new one not in the
/// scan yet), or be seen in both cars at once (the old one not destroyed yet). Neither is a change by itself: a player
/// missing for a while keeps what they last drove (the first version forgot them, and so never saw anyone come out of a
/// boat), and of two cars the one that isn't the known one is the new one. Pure (unit-tested).
/// </summary>
public sealed class CarSwapWatch
{
    /// <summary>A player not seen for this long has left (their record goes), s.</summary>
    public const float ForgetAfter = 10f;

    private bool _known;
    public uint Car { get; private set; }
    /// <summary>The car they changed out of last (it can still be in the scan for a moment: it is not a change back).</summary>
    public uint Left { get; private set; }
    public bool InBoat { get; private set; }
    public float LastSeen { get; private set; }

    /// <summary>The player was seen driving <paramref name="car"/> (a boat or not) at <paramref name="now"/>.</summary>
    public CarSwap Saw(uint car, bool boat, float now)
    {
        LastSeen = now;
        if (!_known)
        {
            _known = true;
            Car = car;
            InBoat = boat;
            return boat ? CarSwap.IntoBoat : CarSwap.Other;
        }
        if (car == Car) { InBoat = boat; return CarSwap.None; }
        bool was = InBoat;
        Left = Car;
        Car = car;
        InBoat = boat;
        if (boat && !was) return CarSwap.IntoBoat;
        if (!boat && was) return CarSwap.OutOfBoat;
        return CarSwap.Other;
    }

    /// <summary>Not seen for <see cref="ForgetAfter"/>: they left.</summary>
    public bool Gone(float now) => _known && now - LastSeen > ForgetAfter;

    /// <summary>Of the cars one player was seen in this scan, the one to go by (an index, -1: none): a car that is neither
    /// the known one nor the one they just left (a new car, while the old one isn't destroyed yet), else the known one.</summary>
    public int Pick(IReadOnlyList<uint> cars)
    {
        if (cars == null || cars.Count == 0) return -1;
        if (!_known) return 0;
        for (int i = 0; i < cars.Count; i++)
            if (cars[i] != Car && cars[i] != Left) return i;
        for (int i = 0; i < cars.Count; i++)
            if (cars[i] == Car) return i;
        return -1;      // only the car they left (on its way out): nothing to go by in this scan
    }
}
