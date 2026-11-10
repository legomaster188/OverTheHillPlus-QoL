namespace OverTheHillPlus.Grayling;

/// <summary>Where a boat a player just changed into goes in.</summary>
public enum Launch
{
    /// <summary>The nearest open water off the shore (no boat trailer of theirs to come from).</summary>
    Shore,
    /// <summary>Where their boat floats, moored (it floated off their trailer at a ramp, or they left it there).</summary>
    Moored,
    /// <summary>Off their trailer's bunks, which are in the water deep enough (they backed it in and took the boat).</summary>
    Bunks,
}

/// <summary>Where the car a player changes into from their boat goes, and what happens to the boat.</summary>
public enum Landing
{
    /// <summary>The nearest dry ground, facing the land (no boat trailer of theirs out with this boat).</summary>
    Shore,
    /// <summary>In front of their parked trailer, hitched to it; the boat stays moored where they left it (afloat), for
    /// them to winch on.</summary>
    ToTrailerMoored,
    /// <summary>In front of their trailer, hitched; the boat goes straight onto its bunks (it was left ashore, not afloat).</summary>
    ToTrailerLoaded,
    /// <summary>The nearest dry ground; the boat goes back on its trailer, which is too far away to walk to.</summary>
    ShoreBoatHome,
}

/// <summary>
/// The Grayling and the player's boat trailer (Trailers+, through core's WorldSites), decided on the host:
/// <list type="bullet">
/// <item>Changing into the boat near their moored boat takes them to it; near their loaded trailer backed into the water,
///   the boat comes off its bunks; near the loaded trailer on dry ground the boat goes in off the shore and leaves the
///   trailer empty (it can't be in two places).</item>
/// <item>Changing back into a car near their parked trailer puts the car at its hitch and hitches it, and the boat stays
///   moored where it floats, to be winched on at the ramp; far from it, the car goes ashore and the boat goes home on the
///   trailer.</item>
/// </list>
/// Pure (unit-tested).
/// </summary>
public static class TrailerLaunch
{
    /// <summary>How near the moored boat a player must change into it to be taken to it, m.</summary>
    public const float MooredReach = 80f;
    /// <summary>How near their trailer a player must change into the boat for it to come from the trailer, m.</summary>
    public const float TrailerReach = 40f;
    /// <summary>How near their trailer the boat must be for the car to go to the trailer when they come ashore, m.</summary>
    public const float RetrieveReach = 70f;
    /// <summary>Water over the keel on the bunks deep enough for the boat to float off them, m.</summary>
    public const float SeatDepth = 0.25f;

    /// <param name="trailer">They have a boat trailer carrying a boat they can drive.</param>
    /// <param name="moored">Its boat floats moored (else: on the trailer, or out).</param>
    /// <param name="onTrailer">Its boat is on the bunks.</param>
    /// <param name="toBoat">From where they changed to the moored boat, m.</param>
    /// <param name="toSeat">From where they changed to the trailer's bunks, m.</param>
    /// <param name="seatDepth">The water over the keel on the bunks, m (NaN: none).</param>
    public static Launch PickLaunch(bool trailer, bool moored, bool onTrailer, float toBoat, float toSeat, float seatDepth)
    {
        if (!trailer) return Launch.Shore;
        if (moored && toBoat <= MooredReach) return Launch.Moored;
        if (onTrailer && toSeat <= TrailerReach && float.IsFinite(seatDepth) && seatDepth >= SeatDepth) return Launch.Bunks;
        return Launch.Shore;
    }

    /// <summary>After a shore launch: does the trailer give up its boat? Yes when it was moored (it's the same boat, taken out
    /// somewhere else) or on the bunks of a trailer nearby (it came off that trailer).</summary>
    public static bool TrailerEmptiesOnShoreLaunch(bool trailer, bool moored, bool onTrailer, float toSeat) =>
        trailer && (moored || (onTrailer && toSeat <= TrailerReach));

    /// <param name="trailer">They have a boat trailer carrying a boat they can drive.</param>
    /// <param name="boatOut">Its boat is out (they were in it).</param>
    /// <param name="afloat">The boat they left floats.</param>
    /// <param name="toTrailer">From the boat to the trailer's bunks, m.</param>
    public static Landing PickLanding(bool trailer, bool boatOut, bool afloat, float toTrailer)
    {
        if (!trailer || !boatOut) return Landing.Shore;
        if (toTrailer <= RetrieveReach) return afloat ? Landing.ToTrailerMoored : Landing.ToTrailerLoaded;
        return Landing.ShoreBoatHome;
    }
}
