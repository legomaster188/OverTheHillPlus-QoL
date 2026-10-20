using System;
using System.IO;
using OverTheHillPlus.Features;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Horns;

/// <summary>
/// Custom Horns' per-frame work (your horn choice, the network, the player) and its Advanced card. Your choice is
/// read from the settings once a second; your own file is re-read when it changes on disk.
/// </summary>
internal sealed class HornsFeature : IFeature
{
    private static string _seenValue;
    private static HornMode _seenMode;
    private static DateTime _seenWrite;
    private static long _seenLength = -1;
    private static float _nextCheck;
    private static byte[] _ownBytes;
    private static bool _loggedChoiceError, _loggedNetError, _loggedPlayerError;

    /// <summary>Why your own file can't be used (null when it can, or when you picked another horn).</summary>
    public static string OwnProblem { get; private set; }

    /// <summary>Your own file's sound (zero until FMOD is up, or when you picked another horn).</summary>
    public static IntPtr OwnSound => _ownBytes == null ? IntPtr.Zero : HornSounds.Custom(_ownKey, _ownBytes);

    private static string _ownKey;

    public void Register()
    {
        HornNet.Register();
        Registry.OnTick(Tick);
        Registry.Section(Registry.PlusMode, "Horns", DrawCard);
        CoreBehaviour.OnQuit += HornPlayer.StopAll;
    }

    // Three parts, each in its own try: one failing must never leave the player's state stale (a car whose
    // stock horn stays muted). A player that throws stops every horn it plays and unmutes every car.
    private static void Tick()
    {
        try
        {
            if (Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 1f;
                RefreshMine(force: false);
            }
        }
        catch (Exception e) { DebugLog.Error("horns", e, "HornsFeature.Tick"); LogError(ref _loggedChoiceError, "reading your horn", e); }
        try { HornNet.Tick(); }
        catch (Exception e) { DebugLog.Error("horns", e, "HornsFeature.Tick"); LogError(ref _loggedNetError, "network", e); }
        try { HornPlayer.Tick(); }
        catch (Exception e)
        {
            DebugLog.Error("horns", e, "HornsFeature.Tick");
            try { HornPlayer.StopAll(); } catch (Exception ex) { DebugLog.Error("horns", ex, "HornsFeature.Tick"); /* nothing more to undo */ }
            LogError(ref _loggedPlayerError, "player", e);
        }
    }

    private static void LogError(ref bool logged, string part, Exception e)
    {
        if (logged) return;
        logged = true;
        HornsPlugin.Logger.LogError($"Custom Horns {part} failed (further errors suppressed): {e}");
    }

    /// <summary>Works out your horn from the settings (and your file), and tells the network when it changed.</summary>
    internal static void RefreshMine(bool force)
    {
        string value = HornsConfig.Horn.Value ?? "";
        var kind = HornsConfig.Parse(value, out byte id, out string file);
        var mode = HornsConfig.CustomMode.Value;
        DateTime write = default;
        long length = -1;
        if (kind == HornKind.Custom)
        {
            try
            {
                var fi = new FileInfo(Path.Combine(HornSounds.CustomDir, file));
                if (fi.Exists) { write = fi.LastWriteTimeUtc; length = fi.Length; }
            }
            catch (Exception ex) { DebugLog.Error("horns", ex, "HornsFeature.RefreshMine"); }
        }
        if (!force && value == _seenValue && mode == _seenMode && write == _seenWrite && length == _seenLength) return;
        _seenValue = value;
        _seenMode = mode;
        _seenWrite = write;
        _seenLength = length;

        OwnProblem = null;
        string oldOwn = _ownBytes != null ? _ownKey : null;
        _ownBytes = null;
        HornChoice choice;
        switch (kind)
        {
            case HornKind.BuiltIn:
                choice = HornChoice.BuiltIn(id);
                break;
            case HornKind.Custom:
                if (HornSounds.ReadOwn(file, out var wav, out string why) && HornWire.CanUpload(wav, out why))
                {
                    _ownBytes = wav;
                    choice = new HornChoice(HornKind.Custom, 0, mode, HornWire.Hash(wav), wav.Length);
                    _ownKey = $"own_{choice.Hash:x16}";
                }
                else
                {
                    OwnProblem = $"{file}: {why}";
                    choice = HornChoice.Stock;
                }
                break;
            default:
                choice = HornChoice.Stock;
                break;
        }
        if (oldOwn != null && (_ownBytes == null || oldOwn != _ownKey)) HornSounds.Release(oldOwn);
        HornNet.SetMine(choice, _ownBytes);
    }

    /// <summary>Plays your current horn to you only.</summary>
    internal static string PreviewMine()
    {
        var c = HornNet.Mine;
        switch (c.Kind)
        {
            case HornKind.BuiltIn: return HornPlayer.Preview(HornSounds.BuiltIn(c.BuiltInId), c.Mode);
            case HornKind.Custom: return HornPlayer.Preview(OwnSound, c.Mode);
            default: return "Blocked: that's the game's horn; honk in a car to hear it";
        }
    }

    private static void DrawCard()
    {
        Menu.Card("Custom Horns", "Your horn: a built-in one or your own wav file. Players with this mod hear it when you honk; everyone else hears the game's horn.", () =>
        {
            Menu.KeyValue("Your horn", HornsSimple.Label(HornsConfig.Horn.Value));
            if (OwnProblem != null) Menu.KeyValue("Problem", OwnProblem);
            Menu.KeyValue("Here", HornNet.Describe());
            Menu.KeyValue("Folder", HornSounds.CustomDir ?? "?");
            foreach (var f in HornSounds.Files())
                if (!f.Ok) Menu.KeyValue(HornsUtil.Short(f.Name, 26), "can't use: " + f.Problem);
            Menu.Switch(HornsConfig.HearOthers, "Hear other players' horns", null, true);
            Menu.Switch(HornsConfig.OthersCustomFiles, "Play other players' own files", "Downloads their wav (up to 256 KB) once per session", HornsConfig.HearOthers.Value);
            Menu.SliderRow(HornsConfig.Volume, "Horn volume", 0f, 1f, "0", "%", true, percent: true);
            Menu.SliderRow(HornsConfig.Range, "Heard up to", 30f, 400f, "0", " m", true);
            Menu.Switch(HornsConfig.FollowGameVolume, "Follow the game's sound-effects volume", null, true);
            Menu.KeyValue("Files", $"{HornNet.FilesReceived} received, {HornNet.FilesSent} sent, {HornNet.Rejected} refused this session");
            Menu.Chips(2, ("Try my horn", () => Menu.SetStatus(PreviewMine() ?? "Playing your horn"), true),
                ("Read the folder again", () => { HornSounds.Files(fresh: true); RefreshMine(force: true); }, true));
        });
    }
}
