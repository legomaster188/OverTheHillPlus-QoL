using System;
using System.IO;
using OverTheHillPlus.Audio;
using OverTheHillPlus.Shared;
using UnityEngine;

namespace OverTheHillPlus.Radio;

/// <summary>
/// Plays the radio folder through the game's FMOD (<see cref="FmodCore"/>): one streamed track at a time, opened
/// without blocking the frame, the next one when it ends. Pauses (keeping its place) while it may not play, e.g. out
/// of the car or on a loading screen, and turns itself down while the game's own music plays.
/// </summary>
internal static class RadioPlayer
{
    private const float FadeSpeed = 2f; // duck / unduck over about half a second

    public static string Root { get; private set; }

    private static RadioPlaylist _playlist;
    private static IntPtr _sound, _channel;
    private static bool _opening;
    private static RadioTrack _track;
    private static int _failedInRow;
    private static float _duck = 1f, _masterVolume = 1f, _nextSlowCheck, _nextRescan;
    private static bool _gameMusic;

    /// <summary>Last thing to tell the player (now playing, volume, a problem), and when.</summary>
    public static string Notice { get; private set; }
    public static float NoticeAt { get; private set; } = -100f;
    /// <summary>One line for the MODS page.</summary>
    public static string Status { get; private set; } = "starting";

    public static RadioPlaylist Playlist => _playlist;
    public static RadioTrack Track => _track;
    public static int TrackCount
    {
        get
        {
            int n = 0;
            if (_playlist != null) foreach (var s in _playlist.Stations) n += s.Tracks.Count;
            return n;
        }
    }

    public static void Init()
    {
        Root = Path.Combine(BepInEx.Paths.ConfigPath, "oth.radio");
        try { Directory.CreateDirectory(Root); }
        catch (Exception e) { DebugLog.Error("radio", e, "RadioPlayer.Init"); LogOnce("mkdir", "Could not create the radio folder: " + e.Message); }
        _playlist = new RadioPlaylist(new System.Collections.Generic.List<RadioStation>(), Environment.TickCount);
        _playlist.Shuffle = RadioConfig.Shuffle.Value;
        Rescan(announce: false);
        if (!string.IsNullOrEmpty(RadioConfig.Station.Value)) _playlist.SelectStation(RadioConfig.Station.Value);
    }

    /// <summary>Reads the folder again; keeps playing the same track when it is still there.</summary>
    public static void Rescan(bool announce)
    {
        var stations = new System.Collections.Generic.List<RadioStation>();
        if (RootIsLink()) Status = "the radio folder is a link: put the files in the folder itself";
        else stations = RadioLibrary.Scan(Root, new DiskFolder());
        string before = _track?.Path;
        _playlist.Replace(stations);
        DebugLog.Info(RadioPlugin.ModId, $"radio folder scanned: {stations.Count} station(s), {TrackCount} track(s) in {Root}" + (Status != null ? $" (status: {Status})" : ""));
        if (announce) Say(stations.Count == 0 ? "Radio: no music found" : $"Radio: {stations.Count} station(s), {TrackCount} track(s)");
        // The track that was playing is gone: move on.
        if (before != null && _playlist.Current?.Path != before) StopTrack();
    }

    private static bool RootIsLink() => Directory.Exists(Root) && ConfinedFolder.IsLink(Root);

    // ---------------------------------------------------------------- controls

    public static void TogglePower()
    {
        RadioConfig.Power.Value = !RadioConfig.Power.Value;
        DebugLog.Info(RadioPlugin.ModId, RadioConfig.Power.Value ? "radio switched on" : "radio switched off");
        if (RadioConfig.Power.Value)
        {
            Rescan(announce: false);
            Say(_playlist.Current == null ? "Radio on: no music in the radio folder" : "Radio on");
        }
        else
        {
            StopTrack();
            Say("Radio off");
        }
    }

    public static void NextTrack() => Switch(_playlist.Next());

    public static void PrevTrack()
    {
        // Like a car stereo: back to the start of the track first, unless it only just started.
        if (_channel != IntPtr.Zero && !_opening && FmodCore.PositionMs(_channel) > 4000) Switch(_track);
        else Switch(_playlist.Prev());
    }

    public static void NextStation() => SwitchStation(_playlist.NextStation());

    public static void PrevStation() => SwitchStation(_playlist.PrevStation());

    public static void SelectStation(string name) => SwitchStation(_playlist.SelectStation(name) ? _playlist.CurrentStation : null);

    private static void SwitchStation(RadioStation s)
    {
        if (s == null) return;
        RadioConfig.Station.Value = s.Name;
        Switch(_playlist.Current);
    }

    public static void SetShuffle(bool on)
    {
        RadioConfig.Shuffle.Value = on;
        _playlist.Shuffle = on;
    }

    public static void StepVolume(float step)
    {
        RadioConfig.Volume.Value = Mathf.Clamp01(Mathf.Round((RadioConfig.Volume.Value + step) * 20f) / 20f);
        Say($"Radio volume {Mathf.RoundToInt(RadioConfig.Volume.Value * 100f)}%");
    }

    private static void Switch(RadioTrack t)
    {
        StopTrack();
        _failedInRow = 0;
        if (t == null) return;
        if (!RadioConfig.Power.Value) RadioConfig.Power.Value = true;
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        if (_playlist == null) return;
        if (!FmodCore.Ready)
        {
            Status = "waiting for the game's audio" + (FmodCore.Problem != null ? ": " + FmodCore.Problem : "");
            return;
        }

        float now = Time.unscaledTime;
        if (now >= _nextSlowCheck)
        {
            _nextSlowCheck = now + 0.5f;
            _gameMusic = GameMusicPlaying();
            _masterVolume = RadioConfig.FollowMasterVolume.Value ? FmodCore.GameVolume("bus:/") : 1f;
        }

        if (!RadioConfig.Power.Value)
        {
            // Switched off from anywhere (key, MODS page, config file): the music stops now, not on the next track.
            if (_sound != IntPtr.Zero) StopTrack();
            Status = "off";
            return;
        }
        if (_playlist.Current == null)
        {
            Status = "no music: put mp3, ogg or wav files in BepInEx/config/oth.radio";
            // Look again now and then, so files added while the game runs show up.
            if (now >= _nextRescan) { _nextRescan = now + 10f; Rescan(announce: false); }
            return;
        }

        bool may = MayPlay(out string why);
        if (_channel != IntPtr.Zero) FmodCore.SetPaused(_channel, !may);
        if (!may) { Status = "paused: " + why; return; }

        if (_sound == IntPtr.Zero) Open(_playlist.Current);
        else if (_opening) PollOpen();
        else if (!FmodCore.IsPlaying(_channel))
        {
            // Finished (or the channel went away): next track.
            StopTrack();
            Open(_playlist.Next());
        }
        if (_sound == IntPtr.Zero) return;

        float target = RadioConfig.DuckForGameMusic.Value && _gameMusic ? RadioConfig.DuckLevel.Value : 1f;
        _duck = Mathf.MoveTowards(_duck, target, FadeSpeed * Time.unscaledDeltaTime);
        if (_channel != IntPtr.Zero)
            FmodCore.SetVolume(_channel, FmodCore.GameMuted ? 0f : RadioConfig.Volume.Value * _duck * _masterVolume);
        if (!_opening) Status = $"playing {_playlist.CurrentStation?.Name}: {_track?.Name}" + (_gameMusic && RadioConfig.DuckForGameMusic.Value ? " (turned down for the game's music)" : "");
    }

    private static bool MayPlay(out string why)
    {
        why = null;
        try { if (InGameMusicManager.IsLoadingScreenUp()) { why = "loading"; return false; } }
        catch (Exception ex) { DebugLog.Error("radio", ex, "RadioPlayer.MayPlay"); /* not in this build: ignore */ }
        if (RadioConfig.PlayWhen.Value == RadioPlayWhen.Always) return true;
        if (GameState.LocalCar == null) { why = "not in your car (set it to play always on the MODS page)"; return false; }
        return true;
    }

    private static bool GameMusicPlaying()
    {
        try
        {
            var m = InGameMusicManager.Instance;
            if (m == null || !m.IsMusicOn) return false;
            var e = m.musicEmitter;
            return e != null && e.IsPlaying();
        }
        catch (Exception ex) { DebugLog.Error("radio", ex, "RadioPlayer.GameMusicPlaying"); return false; }
    }

    private static void Open(RadioTrack t)
    {
        _track = t;
        if (t == null) return;
        // Checked again right before opening: still a plain file inside the radio folder, and neither it, its
        // station folder nor the radio folder became a link.
        if (!ConfinedFolder.IsConfinedFile(Root, t.Path, out string why))
        {
            Failed(t, why);
            return;
        }
        _sound = FmodCore.CreateSound(t.Path, FmodCore.Mode2D | FmodCore.CreateStream | FmodCore.NonBlocking | FmodCore.LoopOff, out int r);
        if (_sound == IntPtr.Zero) { Failed(t, "FMOD error " + r); return; }
        DebugLog.Verbose(RadioPlugin.ModId, () => $"opening track '{t.Name}' ({System.IO.Path.GetFileName(t.Path)}) of '{_playlist.CurrentStation?.Name}'");
        _opening = true;
        Status = "opening " + t.Name;
    }

    private static void PollOpen()
    {
        int state = FmodCore.OpenState(_sound);
        if (state == FmodCore.OpenLoading || state == FmodCore.OpenConnecting || state == FmodCore.OpenBuffering) return;
        if (state == FmodCore.OpenError) { Failed(_track, "can't be played (unsupported or damaged file)"); return; }
        _channel = FmodCore.Play(_sound, paused: true);
        _opening = false;
        if (_channel == IntPtr.Zero) { Failed(_track, "no free channel"); return; }
        FmodCore.SetVolume(_channel, RadioConfig.Volume.Value * _duck * _masterVolume);
        FmodCore.SetPaused(_channel, false);
        DebugLog.Verbose(RadioPlugin.ModId, () => $"playing '{_track.Name}' at volume {RadioConfig.Volume.Value:0.00} (duck {_duck:0.00})");
        _failedInRow = 0;
        if (RadioConfig.ShowNowPlaying.Value) Say($"{_playlist.CurrentStation?.Name}  ·  {_track.Name}");
    }

    private static void Failed(RadioTrack t, string why)
    {
        StopTrack();
        DebugLog.Error(RadioPlugin.ModId, $"track '{t?.Name}' skipped: {why}");
        LogOnce("fail:" + t?.Path, $"Skipping \"{t?.Name}\": {why}");
        _failedInRow++;
        // Every track of the station failed in a row: stop instead of going round forever.
        int tracks = _playlist.CurrentStation?.Tracks.Count ?? 0;
        if (_failedInRow >= Math.Max(1, tracks))
        {
            RadioConfig.Power.Value = false;
            Status = "off: none of the files in this station could be played";
            Say("Radio: none of these files could be played");
            _failedInRow = 0;
            return;
        }
        _playlist.Next(); // the next frame opens it
    }

    /// <summary>Stops and frees the current track.</summary>
    public static void StopTrack()
    {
        try
        {
            if (_channel != IntPtr.Zero) FmodCore.Stop(_channel);
            if (_sound != IntPtr.Zero) FmodCore.Release(_sound);
        }
        catch (Exception e) { DebugLog.Error("radio", e, "RadioPlayer.StopTrack"); LogOnce("stop", "Stopping the radio: " + e.Message); }
        _channel = _sound = IntPtr.Zero;
        _opening = false;
    }

    private static void Say(string text)
    {
        Notice = text;
        NoticeAt = Time.unscaledTime;
    }

    private static void LogOnce(string key, string message) => RadioUtil.LogOnce(key, message);
}
