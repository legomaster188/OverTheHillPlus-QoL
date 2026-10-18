using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Cargo;

/// <summary>Cargo Jobs' MODS page: your job, the session's job board, and the display switches.</summary>
internal static class CargoSimple
{
    private static readonly int[] Caps = { 0, 1000, 2000, 3000, 5000, 10000, 20000 };
    private static readonly int[] Sizes = { 3, 4, 6, 8, 10, 12 };
    private static readonly int[] PerKm = { 60, 90, 120, 160, 200, 300 };
    private static readonly float[] Radii = { 15f, 20f, 30f, 40f, 60f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (CargoConfig.HourlyPayCap == null) return;
        SimpleMenu.Heading("Your job");
        SimpleMenu.Note(CargoGame.Status());
        var mine = CargoGame.MyJob;
        if (mine != null)
        {
            bool loaded = mine.State == JobState.Loaded;
            SimpleMenu.Note($"{mine.FromName} → {mine.ToName} · {Km(mine.Distance)} · " +
                            (loaded ? "loaded: drive to " + mine.ToName : "drive to " + mine.FromName + " to load"));
            SimpleMenu.Button("Drop this job", null, "Puts it back on the board for anyone.", () => Menu.SetStatus(CargoGame.Abandon()), !CargoGame.Waiting);
        }

        var board = CargoGame.Fresh ? CargoGame.View : null;
        if (board != null)
        {
            SimpleMenu.Heading("Job board");
            if (board.Count == 0) SimpleMenu.Note("No jobs right now (the map has too few places far enough apart, or it is still loading).");
            int me = CargoCars.Me;
            foreach (var j in board)
            {
                string label = $"{j.FromName} → {j.ToName}";
                string value;
                bool canTake = j.State == JobState.Open && mine == null && !CargoGame.Waiting;
                if (j.State == JobState.Open) value = $"{Km(j.Distance)} · {j.Pay:N0}–{j.MaxPay:N0}";
                else value = j.Carrier == me ? "your job" : "taken: " + CargoCars.ShortName(j.Carrier);
                int id = j.Id;
                SimpleMenu.Button(label, value,
                    $"Pick up at {j.FromName}, deliver to {j.ToName}. Faster than {CargoHud.Clock(j.TimeLimit)} after loading earns a bonus.",
                    () => Menu.SetStatus(CargoGame.Take(id)), canTake);
            }
        }

        SimpleMenu.Heading("Display");
        SimpleMenu.Toggle(CargoConfig.ShowHud, "Job card on screen", "Where to go, how far, and the time left for the bonus.");
        SimpleMenu.Toggle(CargoConfig.ShowMarker, "Marker over the place", "Over the pick-up, then over the destination.");
    }

    internal static void DrawAdvanced()
    {
        if (CargoConfig.HourlyPayCap == null) return;
        SimpleMenu.Heading("Pay");
        SimpleMenu.IntChoice("Hourly pay cap", "Most pebbles one player earns per hour. Hosting: what you pay each player; as a guest: the most your game accepts.",
            CargoConfig.HourlyPayCap.Value, Caps, v => v <= 0 ? "No cap" : v.ToString("N0"), v => CargoConfig.HourlyPayCap.Value = v);
        SimpleMenu.Heading("Jobs (host)");
        SimpleMenu.IntChoice("Pay per km", "Straight-line distance, before the on-time bonus.", CargoConfig.PayPerKm.Value, PerKm,
            v => v.ToString(), v => CargoConfig.PayPerKm.Value = v);
        SimpleMenu.IntChoice("Jobs on the board", null, CargoConfig.BoardSize.Value, Sizes, v => v.ToString(), v => CargoConfig.BoardSize.Value = v);
        SimpleMenu.FloatChoice("Pick-up radius", "How close counts as at the pick-up.", CargoConfig.PickupRadius.Value, Radii,
            v => v.ToString("0") + " m", v => CargoConfig.PickupRadius.Value = v);
        SimpleMenu.FloatChoice("Delivery radius", "How close counts as delivered.", CargoConfig.DeliveryRadius.Value, Radii,
            v => v.ToString("0") + " m", v => CargoConfig.DeliveryRadius.Value = v);
        SimpleMenu.Toggle(CargoConfig.Announce, "Announce deliveries", "In the game's pop-up, for everyone in the session.");
    }

    private static string Km(float metres) => metres >= 1000f ? $"{metres / 1000f:0.0} km" : $"{Mathf.RoundToInt(metres)} m";
}
