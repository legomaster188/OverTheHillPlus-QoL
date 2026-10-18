using System.Linq;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.Tag;

/// <summary>Tag & Hide and Seek's MODS page: the round (host controls), the scoreboard and the display switches.</summary>
internal static class TagSimple
{
    private static readonly string[] Modes = { "Tag", "Hide and seek" };
    private static readonly int[] Minutes = { 1, 2, 3, 5, 8, 10, 15, 20, 30 };
    private static readonly int[] HeadStarts = { 0, 15, 30, 45, 60, 90, 120, 180 };
    private static readonly int[] SeekerCounts = { 1, 2, 3, 4 };
    private static readonly float[] TagBacks = { 1f, 3f, 5f, 10f, 15f };
    private static readonly float[] Distances = { 0f, 2.5f, 3f, 3.5f, 4f, 5f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (TagConfig.Mode == null) return;
        SimpleMenu.Heading("Round");
        SimpleMenu.Note(TagGame.Status());
        bool host = TagGame.IsHost && TagGame.Active;
        bool running = TagGame.Hosting && TagGame.Running;
        SimpleMenu.Choice("Game", "Tag: touch a car to pass on \"it\". Hide and seek: seekers find hiders by touching them.",
            (int)TagConfig.Mode.Value, Modes, i => TagConfig.Mode.Value = (TagMode)i, !running);
        SimpleMenu.IntChoice("Round length", "Hide and seek: counted after the head start.", TagConfig.RoundMinutes.Value, Minutes,
            v => v + " min", v => TagConfig.RoundMinutes.Value = v, !running);
        if (TagConfig.Mode.Value == TagMode.HideAndSeek)
        {
            SimpleMenu.IntChoice("Head start", "Seconds the hiders get. Seekers' screens go dark and their cars are held.",
                TagConfig.HeadStartSeconds.Value, HeadStarts, v => v + " s", v => TagConfig.HeadStartSeconds.Value = v, !running);
            SimpleMenu.IntChoice("Seekers", "How many players start as seekers (picked at random).", TagConfig.Seekers.Value,
                SeekerCounts, v => v.ToString(), v => TagConfig.Seekers.Value = v, !running);
        }
        if (running) SimpleMenu.Button("Stop the round", null, "Ends the round now, without a winner.", () => Menu.SetStatus(TagGame.Stop()));
        else
            SimpleMenu.Button("Start a round", host ? null : "host only",
                "Starts a round for everyone with a car. Players without the mod follow it from the host's pop-ups.",
                () => Menu.SetStatus(TagGame.Start()), host);

        Scoreboard();

        SimpleMenu.Heading("Display");
        SimpleMenu.Toggle(TagConfig.ShowScoreboard, "Scoreboard on screen", "During a round and for a while after it.");
        SimpleMenu.Toggle(TagConfig.ShowItMarker, "IT marker", "Tag: a red marker above the car that is it, and on the map.");
        SimpleMenu.Toggle(TagConfig.Announce, "Announce to everyone (host)", "Tags, finds and results in the game's pop-up, so players without the mod can play too.");
    }

    private static void Scoreboard()
    {
        var snap = TagGame.View;
        if (snap == null || snap.Players.Count == 0 || snap.Phase == TagPhase.Idle) return;
        SimpleMenu.Heading(snap.Phase == TagPhase.Over ? "Last round" : "Scoreboard");
        var rows = snap.Mode == TagMode.Tag
            ? snap.Players.OrderBy(p => p.ItSeconds).ToList()
            : snap.Players.OrderByDescending(p => p.Winner).ThenBy(p => p.Seeker).ThenByDescending(p => p.Finds).ToList();
        foreach (var p in rows.Take(16))
        {
            string name = TagCars.ShortName(p.Id) + (p.Winner ? " (winner)" : "");
            string value = snap.Mode == TagMode.Tag
                ? $"{TagGame.Clock(p.ItSeconds)} as it · {p.Tags} {(p.Tags == 1 ? "tag" : "tags")}" + (snap.Running && p.Id == snap.It ? " · IT" : "")
                : $"{(p.Found ? "found" : p.Seeker ? "seeker" : "hiding")} · {p.Finds} {(p.Finds == 1 ? "find" : "finds")}";
            SimpleMenu.Note(name + ": " + value);
        }
    }

    internal static void DrawAdvanced()
    {
        if (TagConfig.Mode == null) return;
        SimpleMenu.Heading("Rules (host)");
        SimpleMenu.FloatChoice("No tag-backs for", "Tag: the newly tagged player can't tag the one who tagged them for this long.",
            TagConfig.TagBackSeconds.Value, TagBacks, v => v.ToString("0") + " s", v => TagConfig.TagBackSeconds.Value = v);
        SimpleMenu.FloatChoice("Touch distance", "Cars this close (centre to centre) count as touching, besides real collisions.",
            TagConfig.ContactDistance.Value, Distances, v => v <= 0f ? "Collisions only" : v.ToString("0.0") + " m", v => TagConfig.ContactDistance.Value = v);
        SimpleMenu.Toggle(TagConfig.HoldVanillaSeekers, "Hold seekers without the mod",
            "Head start: also hold the cars of seekers who don't run the mod (they only get the pop-up, no blackout).");
    }
}
