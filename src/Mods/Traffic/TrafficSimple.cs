using System;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.Traffic;

/// <summary>AI Traffic's MODS page: on/off, how many cars, how far, how fast, and which roads.</summary>
internal static class TrafficSimple
{
    private static readonly int[] PerPlayerSteps = { 0, 1, 2, 3, 4, 5, 6, 8, 10 };
    private static readonly int[] MaxSteps = { 0, 2, 4, 6, 8, 10, 12, 16 };
    private static readonly float[] SpawnSteps = { 60f, 100f, 150f, 200f, 300f, 400f, 600f };
    private static readonly float[] DespawnSteps = { 200f, 300f, 500f, 750f, 1000f, 1500f, 2000f };
    private static readonly float[] SpeedSteps = { 15f, 25f, 35f, 45f, 55f, 65f, 80f, 90f };
    private static readonly string[] RoadLabels = { "Auto", "Map roads", "Driven paths", "Between places" };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (TrafficConfig.Enabled == null) return;
        string blocked = TrafficAi.BlockReason();

        SimpleMenu.Heading("Traffic");
        SimpleMenu.Toggle(TrafficConfig.Enabled, "AI traffic", "Driverless cars wander the roads around every player (host or solo).");
        if (blocked != null) SimpleMenu.Note(blocked);
        SimpleMenu.IntChoice("Cars per player", "AI cars around each player.", TrafficConfig.CarsPerPlayer.Value, PerPlayerSteps,
            v => v.ToString(), v => TrafficConfig.CarsPerPlayer.Value = v);
        SimpleMenu.IntChoice("Most cars at once", "Each AI car is a networked car for everyone: keep it modest.", TrafficConfig.MaxCars.Value,
            MaxSteps, v => v.ToString(), v => TrafficConfig.MaxCars.Value = v);
        SimpleMenu.FloatChoice("Top speed", "How fast the AI cars drive on a straight road.", TrafficConfig.MaxSpeed.Value, SpeedSteps,
            v => v.ToString("0") + " km/h", v => TrafficConfig.MaxSpeed.Value = v);

        SimpleMenu.Heading("Distances");
        SimpleMenu.FloatChoice("Appear from", "New AI cars appear at least this far from every player.", TrafficConfig.SpawnDistance.Value,
            SpawnSteps, v => v.ToString("0") + " m", v => TrafficConfig.SpawnDistance.Value = v);
        SimpleMenu.FloatChoice("Disappear beyond", "AI cars farther than this from every player are taken away.", TrafficConfig.DespawnDistance.Value,
            DespawnSteps, v => v.ToString("0") + " m", v => TrafficConfig.DespawnDistance.Value = v);

        SimpleMenu.Heading("Now");
        SimpleMenu.Choice("Roads", "Auto: the map's roads, else paths players drove this session, else straight lines between places.",
            (int)TrafficConfig.Roads.Value, RoadLabels, i => TrafficConfig.Roads.Value = (TrafficConfig.RoadChoice)i, wrap: false);
        SimpleMenu.Note($"Roads: {RoadSource.Describe()}.");
        int waiting = TrafficAi.PendingCount;
        SimpleMenu.Note($"AI cars: {TrafficAi.Agents.Count}" + (waiting > 0 ? $" (+{waiting} on the way)" : "") +
                        (string.IsNullOrEmpty(TrafficAi.LastEvent) ? "" : $". {TrafficAi.LastEvent}"));
        SimpleMenu.Button("Take all AI cars away", "", "Despawns every AI car now (new ones come while traffic is on).",
            () => Menu.SetStatus(TrafficAi.Dismiss("taken away")), TrafficAi.Agents.Count > 0);
        SimpleMenu.Note("AI cars never push into players: they stop, and one standing in someone's way is replaced. " +
                        "Achievements stay locked for the session once traffic spawned.");
    }

    /// <summary>The Advanced fold: lane offset and a list of the cars.</summary>
    internal static void DrawAdvanced()
    {
        if (TrafficConfig.Enabled == null) return;
        Menu.Card("Driving", "Where on the road the AI cars drive", () =>
        {
            Menu.SliderRow(TrafficConfig.LaneOffset, "Keep right by", 0f, 4f, "0.0", " m", true);
        });
        Menu.Card("AI cars", "What each one is doing", () =>
        {
            if (TrafficAi.Agents.Count == 0) { Menu.Small("None right now."); return; }
            foreach (var a in TrafficAi.Agents)
                Menu.KeyValue($"{a.Model} #{a.Id}", $"{a.Activity}, {Math.Abs(a.Speed) * 3.6f:0} km/h");
        });
    }
}
