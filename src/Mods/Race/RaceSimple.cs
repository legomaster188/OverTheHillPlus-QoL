using System.Collections.Generic;
using OverTheHillPlus.Shared;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Race;

/// <summary>Race Mode on its MODS page: the race, results, leaderboard, track editor (host) and saved tracks.</summary>
internal static class RaceSimple
{
    private static readonly int[] Countdowns = { 3, 5, 10, 15, 20, 30 };
    private static readonly int[] LapSteps = { 1, 2, 3, 4, 5, 6, 8, 10, 15, 20 };
    private static readonly float[] Radii = { 6f, 8f, 10f, 12f, 15f, 20f, 25f, 30f, 40f, 50f };
    private static readonly string[] Kinds = { "Sprint", "Circuit" };

    private static string _confirm;
    private static float _confirmAt;
    private static string _nameEdit;

    internal static void Draw()
    {
        if (RaceConfig.ShowHud == null) return; // failed to start
        if (Time.unscaledTime - _confirmAt > 4f) _confirm = null;
        bool host = RaceNet.IsHostSide;
        var me = RaceView.Me;
        var track = RaceView.Track;

        SimpleMenu.Heading("Race");
        SimpleMenu.Note(StatusLine(host));
        if (RaceView.Live && me != null && me.State == RacerState.Racing)
            SimpleMenu.Button("Give up", "", "Leave this race (you're listed as out).", () => Say(host ? RaceHost.GiveUpSelf() : RaceNet.SendGiveUp()));
        if (host)
        {
            if (RaceHost.Racing) SimpleMenu.Button("Stop the race", "", "Ends the race now; everyone still racing is out.", () => Say(RaceHost.Stop()));
            else
            {
                SimpleMenu.IntChoice("Countdown", "Seconds from Start to GO.", RaceConfig.Countdown.Value, Countdowns, v => v + " s", v => RaceConfig.Countdown.Value = v);
                string problem = RaceHost.TrackProblem();
                SimpleMenu.Button("Start the race", problem == null ? Summary(RaceHost.Draft) : "Track not ready",
                    problem == null ? "Everyone with Race Mode races (guests can opt out)." : "Not yet: " + problem + ".",
                    () => Say(RaceHost.Start()), problem == null);
            }
        }
        else SimpleMenu.Toggle(RaceConfig.JoinRaces, "Join the host's races", "Off: you watch instead.");

        if (RaceView.Results != null && RaceView.Results.Count > 0)
        {
            SimpleMenu.Heading("Last results  ·  " + (RaceView.ResultsTrack ?? ""));
            foreach (var s in RaceView.Results)
                SimpleMenu.Note((s.Place > 0 ? s.Place + ".  " : "–   ") + s.Name + "   " +
                                (s.State == RacerState.Finished ? SplitCompare.FormatTime(s.Ms) : SplitCompare.DnfText(s.Reason)));
        }

        if (track != null && RaceView.Board != null)
        {
            SimpleMenu.Heading("Leaderboard  ·  " + track.Name);
            var entries = RaceView.Board.Entries;
            if (entries.Count == 0) SimpleMenu.Note("No times on this track yet.");
            for (int i = 0; i < entries.Count && i < 10; i++)
                SimpleMenu.Note($"{i + 1}.  {entries[i].Name}   {SplitCompare.FormatTime(entries[i].Ms)}   {entries[i].When}");
        }

        if (host) DrawEditor();
        else if (track != null)
        {
            SimpleMenu.Heading("The host's track");
            SimpleMenu.Note($"{track.Name}: {Summary(track)}.");
            SimpleMenu.Button("Save a copy", "", "Keeps this track in your own tracks folder.", () => Say(RaceFiles.SaveTrack(track)));
        }
        DrawSaved(host);
    }

    private static void DrawEditor()
    {
        var d = RaceHost.Draft;
        bool can = RaceHost.CanEdit;
        SimpleMenu.Heading($"Track  ·  {d.Gates.Count} checkpoint(s)");
        if (!can) SimpleMenu.Note("The track can't change during a race.");
        _nameEdit ??= d.Name;
        string typed = SimpleMenu.Text("Name", _nameEdit, RaceTrack.MaxName, "Shown to everyone in the session.");
        if (typed != _nameEdit) { _nameEdit = typed; RaceHost.SetName(typed); }
        if (!can) _nameEdit = d.Name;
        SimpleMenu.Choice("Kind", "Sprint: start to finish. Circuit: laps back through the start.", (int)d.Kind, Kinds, i => RaceHost.SetKind((RaceKind)i), can);
        if (d.Kind == RaceKind.Circuit)
            SimpleMenu.IntChoice("Laps", null, d.Laps, LapSteps, v => v.ToString(), RaceHost.SetLaps, can);
        SimpleMenu.FloatChoice("Checkpoint size", "Radius of every checkpoint on this track (and of new ones).",
            RaceConfig.GateRadius.Value, Radii, v => v.ToString("0") + " m", RaceHost.SetRadius, can);
        bool hasCar = GameMap.TryCarPosition(out var car);
        SimpleMenu.Button(d.Gates.Count == 0 ? "Place the start at my car" : "Add a checkpoint at my car", hasCar ? "" : "No car",
            "Drive there, then press. Hotkey under Keybinds.", () => Say(RaceHost.AddGateAtCar()), can && hasCar);
        SimpleMenu.Button("Remove the last checkpoint", "", null, () => Say(RaceHost.RemoveLast()), can && d.Gates.Count > 0);
        bool sure = _confirm == "clear";
        SimpleMenu.Button(sure ? "Clear: press again" : "Clear the checkpoints", "", null, () =>
        {
            if (sure) { Say(RaceHost.ClearTrack()); _confirm = null; }
            else { _confirm = "clear"; _confirmAt = Time.unscaledTime; }
        }, can && d.Gates.Count > 0);
        SimpleMenu.Button("Save the track", SaveFile(d) + ".json", "Saved in BepInEx/config/oth.race/tracks.",
            () => Say(RaceFiles.SaveTrack(d)), d.Validate() == null);
        SimpleMenu.Button("New track", "", "Starts an empty track.", () => { Say(RaceHost.New()); _nameEdit = null; }, can);

        // Pins as checkpoints: the game's map pin, and pins other mods offer through core (Waypoints+).
        var pins = new List<NamedPoint>();
        if (GameMap.TryGetGamePinCached(out var gp)) pins.Add(new NamedPoint("My map pin", gp.x, gp.y, gp.z, "game"));
        pins.AddRange(PinSources.All());
        if (pins.Count == 0) return;
        SimpleMenu.Heading("Add a pin as a checkpoint");
        int shown = 0;
        foreach (var p in pins)
        {
            if (shown++ >= 16) break;
            var pos = new Vector3(p.X, p.Y, p.Z);
            string dist = hasCar ? "  ·  " + GameMap.DistanceText(GameMap.FlatDistance(car, pos)) : "";
            SimpleMenu.ActionsFor("gatepin:" + p.Name + "|" + p.Source, $"{p.Name}  ·  {p.Source}{dist}", ("Add", () => Say(RaceHost.AddGate(Ground(pos), "\"" + p.Name + "\"")), can));
        }
    }

    private static string _saveFile, _saveFileName;
    private static float _saveFileAt = -100f;

    /// <summary>The file "Save the track" writes (looked up at most once a second: it reads the tracks folder).</summary>
    private static string SaveFile(RaceTrack d)
    {
        if (_saveFile == null || _saveFileName != d.Name || Time.unscaledTime - _saveFileAt > 1f || Time.unscaledTime < _saveFileAt)
        {
            _saveFileAt = Time.unscaledTime;
            _saveFileName = d.Name;
            _saveFile = RaceFiles.FileFor(d);
        }
        return _saveFile;
    }

    /// <summary>Pins sit on the map's surface; a checkpoint from one goes on the ground there.</summary>
    private static Vector3 Ground(Vector3 p) => GameMap.TryTerrainHeight(p.x, p.z, out float y) ? new Vector3(p.x, y, p.z) : p;

    private static void DrawSaved(bool host)
    {
        var files = RaceFiles.List();
        SimpleMenu.Heading($"Saved tracks ({files.Count})");
        if (files.Count == 0) { SimpleMenu.Note("None yet. Save one above; they live in BepInEx/config/oth.race/tracks."); return; }
        string area = GameMap.AreaKey;
        foreach (var f in files)
        {
            var t = f.Track;
            string where = !string.IsNullOrEmpty(t.Area) && t.Area != area ? "  ·  other map" : "";
            bool sure = _confirm == "del:" + f.File;
            var items = new List<(string, System.Action, bool)>();
            if (host) items.Add(("Load", () => { Say(RaceHost.Load(t)); _nameEdit = null; }, RaceHost.CanEdit));
            items.Add((sure ? "Sure?" : "Delete", () =>
            {
                if (sure) { RaceFiles.DeleteTrack(f); _confirm = null; Say($"Deleted \"{t.Name}\"."); }
                else { _confirm = "del:" + f.File; _confirmAt = Time.unscaledTime; }
            }, true));
            SimpleMenu.ActionsFor("track:" + t.Name, $"{t.Name}  ·  {Summary(t)}{where}", items.ToArray());
        }
    }

    private static string Summary(RaceTrack t) =>
        t.Kind == RaceKind.Circuit ? $"circuit, {t.Laps} lap(s), {t.Gates.Count} checkpoints" : $"sprint, {t.Gates.Count} checkpoints";

    private static string StatusLine(bool host)
    {
        string s = host ? RaceHost.Status() : null;
        if (s != null) return s;
        if (!RaceNet.GuestActive)
            return Features.Public.ModNet.ModNet.IsGuest ? "Off in this session: the host doesn't run Race Mode (or the mod network is off)." : "Load into a world to race.";
        if (RaceView.Live) return RaceView.Me != null ? "You're in this race." : "A race is on (you're watching).";
        return RaceView.Track != null ? "Waiting for the host to start a race." : "The host has no track yet.";
    }

    private static void Say(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        Menu.SetStatus(s);
        RaceView.Tell(s);
    }
}
