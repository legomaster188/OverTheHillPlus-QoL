namespace OverTheHillPlus.Cars;

/// <summary>
/// What a modded vehicle is, for the mods that treat vehicles differently (core's VehicleTraits answers it for a live car).
/// The numbers never change (append only).
/// </summary>
public enum VehicleKind : byte
{
    /// <summary>A car, truck or side-by-side on four tyres: everything works as for the stock cars.</summary>
    Car = 0,
    /// <summary>Skis at the front and a rubber track at the back: its four wheels are hidden physics wheels under the
    /// skis and the track, so it has no tyres to pump, change or chain.</summary>
    Snowmobile = 1,
    /// <summary>A boat: it floats (its own mod's hull model) and its four small wheels are hidden, only there so it can
    /// roll up a ramp or sit on the shore.</summary>
    Boat = 2,
}

/// <summary>What the kinds mean for other features. Pure (unit-tested).</summary>
public static class VehicleKinds
{
    /// <summary>The vehicle stands on tyres you can see (pressures, treads, sizes and snow chains apply).</summary>
    public static bool HasTyres(VehicleKind k) => k == VehicleKind.Car;

    /// <summary>The vehicle drives on roads, so it can be road traffic or join a road convoy (boats and snowmobiles can't).</summary>
    public static bool OnRoad(VehicleKind k) => k == VehicleKind.Car;

    /// <summary>A short lower-case word for logs and pages.</summary>
    public static string Word(VehicleKind k) => k switch
    {
        VehicleKind.Snowmobile => "snowmobile",
        VehicleKind.Boat => "boat",
        _ => "car",
    };
}
