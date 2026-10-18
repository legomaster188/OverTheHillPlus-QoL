using System;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.Ghosts;

/// <summary>Ghost Replays' MODS page: record / stop, playback settings, racing your best ghost, and the saved ghosts.</summary>
internal static class GhostsSimple
{
    private static readonly float[] SpeedSteps = { 0.25f, 0.5f, 1f, 1.5f, 2f, 4f };
    private static readonly float[] OpacitySteps = { 0.15f, 0.25f, 0.35f, 0.5f, 0.65f, 0.8f };

    private static string _renaming, _renameText, _confirmDelete;

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (GhostsConfig.PlaybackSpeed == null) return;

        SimpleMenu.Heading("Record");
        bool hasCar = GameState.LocalCar != null;
        if (GhostRecorder.Recording)
            SimpleMenu.Button("Stop and save", "REC " + GhostTime.Clock(GhostRecorder.Seconds),
                "Saves this drive to the list below.", GhostsFrame.ToggleRecording);
        else
            SimpleMenu.Button("Start recording", hasCar ? "Ready" : "No car",
                "Records your car (position, rotation and wheels, 20 times a second) until you stop it." + KeyHint(),
                GhostsFrame.ToggleRecording, hasCar);

        SimpleMenu.Heading("Playback");
        if (GhostPlayer.Playing != null)
            SimpleMenu.Button("Stop the ghost", $"{GhostTime.Clock(GhostPlayer.Seconds)} / {GhostTime.Clock(GhostPlayer.Duration)}",
                $"Playing \"{GhostPlayer.Playing.Name}\".", GhostPlayer.Stop);
        SimpleMenu.FloatChoice("Playback speed", "How fast a ghost from the list plays (races always play at 1x).",
            GhostsConfig.PlaybackSpeed.Value, SpeedSteps, v => v.ToString("0.##") + "x", v => GhostsConfig.PlaybackSpeed.Value = v);
        SimpleMenu.FloatChoice("Ghost opacity", "How solid the ghost car looks.",
            GhostsConfig.Opacity.Value, OpacitySteps, v => (v * 100f).ToString("0") + "%", v => GhostsConfig.Opacity.Value = v);
        SimpleMenu.Toggle(GhostsConfig.Loop, "Loop", "Start a ghost from the list again when it reaches its end.");

        DrawRace();
        DrawList();
    }

    private static string KeyHint()
    {
        var k = GhostsConfig.RecordKey.Value;
        return k == UnityEngine.InputSystem.Key.None ? "" : $" Hotkey: {k}.";
    }

    private static void DrawRace()
    {
        SimpleMenu.Heading("Race Mode");
        SimpleMenu.Toggle(GhostsConfig.RaceBestGhost, "Race your best ghost", "Your fastest ghost on a Race Mode track starts with the race.");
        SimpleMenu.Toggle(GhostsConfig.RecordRaces, "Record races", "Every race you drive is recorded; finished ones are saved with their time.");
        string track = GhostRace.ActiveTrackId;
        if (string.IsNullOrEmpty(track))
        {
            SimpleMenu.Note("No Race Mode track right now. Racing ghosts needs Race Mode: start a race on a saved track and your best ghost drives with you.");
            return;
        }
        string name = GhostRace.ActiveTrackName;
        var best = GhostLibrary.BestFor(track);
        SimpleMenu.Button("Play best ghost now", best == null ? "None yet" : GhostTime.Race(best.Header.RaceTimeMs),
            $"Your best on {(string.IsNullOrEmpty(name) ? track : name)}. It also starts by itself with the race.",
            () => GhostsFrame.Notice(GhostPlayer.Play(best, race: true) ?? $"Ghost \"{best.Name}\" is driving."), best != null);
    }

    private static void DrawList()
    {
        var entries = GhostLibrary.Entries;
        SimpleMenu.Heading($"Saved ghosts ({entries.Count})");
        if (entries.Count == 0)
        {
            SimpleMenu.Note("Nothing saved yet. Ghosts are kept in BepInEx/config/oth.ghosts/.");
            SimpleMenu.Button("Reload the list", null, "Reads the ghost folder again.", GhostLibrary.Refresh);
            return;
        }
        // Copied: a delete or rename refreshes the list while it is drawn.
        foreach (var e in new System.Collections.Generic.List<GhostEntry>(entries))
        {
            var r = e.Header;
            string info = GhostTime.Clock(r.Duration) + (string.IsNullOrEmpty(r.Car) ? "" : " · " + r.Car)
                          + (r.RaceTimeMs >= 0 ? " · race " + GhostTime.Race(r.RaceTimeMs) : "");
            if (_renaming == e.Name)
            {
                _renameText = SimpleMenu.Text("New name", _renameText, GhostNames.MaxLength, "Letters, numbers, spaces, '-' and '_'.");
                SimpleMenu.Actions("", ("Save", () => FinishRename(e.Name), true), ("Cancel", () => _renaming = null, true));
                continue;
            }
            bool playing = GhostPlayer.Playing != null && GhostPlayer.Playing.Name == e.Name;
            bool confirm = _confirmDelete == e.Name;
            string name = e.Name;
            SimpleMenu.Actions(name + "   " + info,
                (playing ? "Stop" : "Play", () => { if (playing) GhostPlayer.Stop(); else Play(name); }, true),
                ("Rename", () => { _renaming = name; _renameText = name; _confirmDelete = null; }, true),
                (confirm ? "Sure?" : "Delete", () => Delete(name, confirm), true));
        }
        SimpleMenu.Button("Reload the list", null, "Reads the ghost folder again (after copying ghosts in).", GhostLibrary.Refresh);
    }

    private static void Play(string name)
    {
        _confirmDelete = null;
        var e = GhostLibrary.Find(name);
        GhostsFrame.Notice(GhostPlayer.Play(e) ?? $"Playing \"{name}\".");
    }

    private static void Delete(string name, bool confirmed)
    {
        if (!confirmed) { _confirmDelete = name; return; }
        _confirmDelete = null;
        if (GhostPlayer.Playing != null && GhostPlayer.Playing.Name == name) GhostPlayer.Stop();
        GhostsFrame.Notice(GhostLibrary.Delete(name) ? $"Deleted \"{name}\"." : "Couldn't delete it.");
    }

    private static void FinishRename(string name)
    {
        string to = GhostLibrary.Rename(name, _renameText, out string error);
        _renaming = null;
        if (to == null) { GhostsFrame.Notice(error); return; }
        if (GhostPlayer.Playing != null && GhostPlayer.Playing.Name == name) GhostPlayer.Stop();
        if (!string.Equals(to, name, StringComparison.Ordinal)) GhostsFrame.Notice($"Renamed to \"{to}\".");
    }
}
