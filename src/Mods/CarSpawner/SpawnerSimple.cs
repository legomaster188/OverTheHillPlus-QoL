using System.Collections.Generic;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.CarSpawner;

/// <summary>Car Spawner's MODS page: a search box, a group switch and one row per car with Spawn and Drive.</summary>
internal static class SpawnerSimple
{
    private static readonly string[] GroupLabels = { "All cars", "Game cars", "Modded cars" };

    private static string _query = "";
    private static CarGroup _group = CarGroup.All;

    // The filtered list is rebuilt only when the search, the group or the car list changed.
    private static List<CarChoice> _shown = new();
    private static string _shownQuery;
    private static CarGroup _shownGroup;
    private static int _shownVersion = -1;

    internal static void Draw()
    {
        string blocked = SpawnerCars.BlockReason();

        SimpleMenu.Heading("Car Spawner");
        if (blocked != null) SimpleMenu.Note(blocked);
        _query = SimpleMenu.Text("Search", _query, 40, "Part of a car's name, e.g. \"vera\" or \"wag\".");
        SimpleMenu.Choice("Show", "Every car, only the game's own, or only the ones mods add.", (int)_group, GroupLabels,
            i => _group = (CarGroup)i);

        var all = SpawnerCars.All;
        if (_shownVersion != SpawnerCars.Version || _shownQuery != _query || _shownGroup != _group)
        {
            _shown = SpawnerLogic.Filter(all, _query, _group);
            _shownVersion = SpawnerCars.Version;
            _shownQuery = _query;
            _shownGroup = _group;
        }

        SimpleMenu.Heading($"Cars ({_shown.Count} of {all.Count})");
        if (all.Count == 0) SimpleMenu.Note("No cars yet: the game's item catalog isn't loaded.");
        else if (_shown.Count == 0) SimpleMenu.Note("No car matches.");
        bool canDrive = SpawnerCars.DriveBlockReason() == null;
        foreach (var car in _shown)
        {
            var c = car;
            SimpleMenu.ActionsFor(c.Prefab, c.Modded ? c.Display + " (mod)" : c.Display,
                ("Place", () => SpawnerPlace.Start(c), blocked == null),
                ("Spawn", () => Menu.SetStatus(SpawnerCars.Spawn(c)), blocked == null),
                ("Drive", () => Menu.SetStatus(SpawnerCars.Drive(c)), canDrive));
        }

        SimpleMenu.Heading("Spawned");
        int spawnedCount = SpawnerCars.Count;
        SimpleMenu.Note($"Cars out: {spawnedCount}");
        SimpleMenu.Button("Remove spawned cars", "", "Despawns every car this page spawned.",
            () => Menu.SetStatus(SpawnerCars.RemoveAll()), spawnedCount > 0);
        SimpleMenu.Note("Place closes this page and shows a box where you aim, mouse or screen centre (wheel or Q/E turns it, left click spawns, right click or Esc cancels; with no ground under the aim the box sits ahead of your car and W/S/A/D move it). Spawn drops the car on open ground in front of you, facing your way; Drive puts you in it where you stand " +
                        "(stopped, as in the garage). Cars locked behind a DLC you don't own are not listed. " +
                        "Achievements stay locked for the session once you used this.");
    }
}
