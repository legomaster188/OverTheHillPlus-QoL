using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Stats;

/// <summary>Trip Stats' numbers and everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class StatsSimple
{
    private static readonly string[] ShowsLabels = { "This session", "All time", "Both" };
    private static float _confirmUntil = -1f;

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (StatsConfig.ShowCard == null) return;
        var rec = StatsTracker.Recorder;
        bool imp = StatsConfig.Imperial.Value;

        SimpleMenu.Heading("This session");
        Numbers(rec.Session, imp);
        SimpleMenu.Heading("All time");
        Numbers(rec.Lifetime, imp);

        SimpleMenu.Heading("Card");
        SimpleMenu.Toggle(StatsConfig.ShowCard, "Show the trip card", "A small card with the numbers while you drive.");
        SimpleMenu.Choice("Card shows", "This session's numbers, all-time numbers, or both side by side.",
            (int)StatsConfig.Shows.Value, ShowsLabels, i => StatsConfig.Shows.Value = (StatsConfig.CardShows)i);
        SimpleMenu.Choice("Card corner", "Where on screen the card sits.", (int)StatsConfig.Corner.Value, HudLayout.CornerNames,
            i => StatsConfig.Corner.Value = HudLayout.Corner(i));
        SimpleMenu.Toggle(StatsConfig.Imperial, "Miles and feet", "Imperial units on the card and here.");

        SimpleMenu.Heading("Reset");
        SimpleMenu.Button("Start this session again", null, "Sets this session's numbers back to zero.", StatsTracker.ResetSession);
        bool confirming = Time.unscaledTime < _confirmUntil;
        SimpleMenu.Button("Clear all-time numbers", confirming ? "Press again" : null,
            "Sets every all-time number back to zero. Press twice.", () =>
            {
                if (Time.unscaledTime < _confirmUntil)
                {
                    _confirmUntil = -1f;
                    StatsTracker.ResetAllTime();
                    Menu.SetStatus("All-time trip numbers cleared");
                }
                else _confirmUntil = Time.unscaledTime + 4f;
            });
    }

    private static void Numbers(TripTotals t, bool imp)
    {
        SimpleMenu.Note($"Distance {TripFormat.Distance(t.DistanceM, imp)}  ·  top speed {TripFormat.Speed(t.TopSpeedKmh, imp)}  ·  " +
                        $"driven {TripFormat.Duration(t.DrivenS)}");
        SimpleMenu.Note($"{t.Jumps} jumps, {TripFormat.Duration(t.AirTimeS)} in the air  ·  longest {TripFormat.Length(t.LongestJumpM, imp)} " +
                        $"({TripFormat.Duration(t.LongestAirS)})  ·  {t.Flips} flips (best {t.MostFlips} in one jump)");
    }
}
