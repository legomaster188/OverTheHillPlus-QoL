namespace OverTheHillPlus.Api;

/// <summary>
/// What a world mod offers a career: a map that takes a moment to come up in the loaded area, and a way onto it. Set on
/// <see cref="WorldPlaces.CareerWorld"/> at load.
/// </summary>
public interface ICareerWorld
{
    /// <summary>The map's name ("Skadi's Hollow").</summary>
    string Name { get; }

    /// <summary>How far the map is built in this session, 0 to 1.</summary>
    float Progress { get; }

    /// <summary>The map is up: <see cref="Arrive"/> can run.</summary>
    bool Ready { get; }

    /// <summary>Why the map can't come up in this session, or null.</summary>
    string Problem { get; }

    /// <summary>The local car is on the map.</summary>
    bool CarOnMap { get; }

    /// <summary>
    /// A car is about to be put on the map at <paramref name="spot"/> (null: the map's own arrival spot) behind a loading
    /// screen: the map comes up there first, as fast as it can. Call it before waiting for <see cref="Ready"/>.
    /// </summary>
    void Prepare(CareerSpot spot);

    /// <summary>
    /// Puts the local car on the map, at a spot the map's own place list picks when <paramref name="spot"/> is null. Solo and
    /// host only. Null when it worked, else why.
    /// </summary>
    string Arrive(CareerSpot spot);

    /// <summary>Where the local car stands now on the map, or null when it isn't on it.</summary>
    CareerSpot Here();
}

/// <summary>A place on a career's map, kept with the career so it picks up where it stopped.</summary>
public sealed class CareerSpot
{
    public float X, Y, Z, Yaw;
}
