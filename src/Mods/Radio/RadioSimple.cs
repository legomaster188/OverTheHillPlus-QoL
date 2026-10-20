using OverTheHillPlus.Features.Public;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Radio;

/// <summary>Radio's everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class RadioSimple
{
    private static readonly float[] VolumeSteps = { 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f };
    private static readonly string[] PlayWhenLabels = { "In your car", "Always" };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (RadioConfig.Power == null) return;
        var list = RadioPlayer.Playlist;
        SimpleMenu.Heading("Radio");
        SimpleMenu.Toggle(RadioConfig.Power, "Radio", "Plays the music in your radio folder. Only you hear it." + KeyHint(RadioConfig.PowerKey.Value));
        SimpleMenu.Button("Now playing", Short(RadioPlayer.Track?.Name ?? "-", 34), RadioPlayer.Status + ". Press to skip." + KeyHint(RadioConfig.NextKey.Value),
            RadioPlayer.NextTrack, RadioConfig.Power.Value && list?.Current != null);

        if (list != null && list.Stations.Count > 0)
        {
            var names = new string[list.Stations.Count];
            int at = -1;
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = Short(list.Stations[i].Name, 28);
                if (list.Stations[i] == list.CurrentStation) at = i;
            }
            SimpleMenu.Choice("Station", "Each sub-folder of the radio folder is a station." + KeyHint(RadioConfig.NextStationKey.Value),
                at, names, i => RadioPlayer.SelectStation(list.Stations[i].Name));
        }

        SimpleMenu.FloatChoice("Volume", "How loud the radio plays." + KeyHint(RadioConfig.VolumeUpKey.Value, RadioConfig.VolumeDownKey.Value),
            RadioConfig.Volume.Value, VolumeSteps, v => Mathf.RoundToInt(v * 100f) + "%", v => RadioConfig.Volume.Value = v);
        SimpleMenu.Choice("Shuffle", "Play each station in a random order.", RadioConfig.Shuffle.Value ? 1 : 0, new[] { "Off", "On" },
            i => RadioPlayer.SetShuffle(i == 1));
        SimpleMenu.Choice("Plays", "In your car: only while you are driving in a world. Always: in the menus too.",
            (int)RadioConfig.PlayWhen.Value, PlayWhenLabels, i => RadioConfig.PlayWhen.Value = (RadioPlayWhen)i);
        SimpleMenu.Toggle(RadioConfig.DuckForGameMusic, "Turn down for the game's music", "The radio gets quieter while the game's own music plays.");
        SimpleMenu.Button("Read the folder again", $"{list?.Stations.Count ?? 0} stations, {RadioPlayer.TrackCount} tracks",
            "Picks up files you added while the game runs.", () => RadioPlayer.Rescan(announce: true));
        SimpleMenu.Note("Put mp3, ogg or wav files in BepInEx/config/oth.radio. Files in a sub-folder make a station named after it.");
    }

    private static string KeyHint(Key key) => key == Key.None ? "" : $" Key: {KeybindEditor.KeyName(key)}.";

    private static string KeyHint(Key up, Key down) =>
        up == Key.None && down == Key.None ? "" : $" Keys: {KeybindEditor.KeyName(up)} / {KeybindEditor.KeyName(down)}.";

    private static string Short(string s, int max) => RadioUtil.Short(s, max);
}
