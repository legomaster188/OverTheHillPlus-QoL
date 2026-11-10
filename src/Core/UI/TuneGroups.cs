using System;
using System.Collections.Generic;

namespace OverTheHillPlus.UI;

/// <summary>
/// Where each kind of car setting lives. The car mods' pages use the same group names (Suspension, Tyres, Gears and
/// diffs, Lights, Looks); a setting that two mods both touch is on one page and the other page links to it
/// (<see cref="SimpleMenu.Link"/>) instead of showing it twice. Pure data, unit tested.
/// </summary>
internal static class TuneGroups
{
    /// <summary>One group: its heading, the mod whose page holds it and the mod's id.</summary>
    internal readonly struct Group
    {
        public readonly string Title;
        public readonly string ModId;
        public Group(string title, string modId) { Title = title; ModId = modId; }
    }

    internal const string Suspension = "Suspension", Tyres = "Tyres", Gears = "Gears and diffs", Lights = "Lights", Looks = "Looks";

    // The ids of the mods (their plugins' ModId); core never references a mod's assembly.
    internal const string AirRideId = "airride", TyrePressureId = "tyrepressure", GearboxId = "gearbox", BeamsId = "beams",
        DrivingId = "drivingqol", PartsId = "customcosmetics", StackingId = "stacking", LiveryPacksId = "liverypacks";

    /// <summary>The tuning groups of a car, in the order the car's page lists them.</summary>
    internal static readonly IReadOnlyList<Group> Tuning = new[]
    {
        new Group(Suspension, AirRideId),
        new Group(Tyres, TyrePressureId),
        new Group(Gears, GearboxId),
        new Group(Lights, BeamsId),
    };

    /// <summary>The pages that change how a car looks, besides the car's own paint and skin.</summary>
    internal static readonly IReadOnlyList<Group> LookPages = new[]
    {
        new Group("Custom parts", PartsId),
        new Group("Garage stacking", StackingId),
        new Group("Livery packs", LiveryPacksId),
    };

    /// <summary>The groups of <paramref name="all"/> that are another mod's (never a link to the page it is on).</summary>
    internal static List<Group> Others(IReadOnlyList<Group> all, string selfModId)
    {
        var list = new List<Group>(all.Count);
        foreach (var g in all)
            if (!string.Equals(g.ModId, selfModId, StringComparison.OrdinalIgnoreCase)) list.Add(g);
        return list;
    }
}
