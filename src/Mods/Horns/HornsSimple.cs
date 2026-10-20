using System.Collections.Generic;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Horns;

/// <summary>Custom Horns' everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class HornsSimple
{
    private static readonly float[] VolumeSteps = { 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f };
    private static readonly string[] ModeLabels = { "While held", "Whole clip" };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (HornsConfig.Horn == null) return;
        SimpleMenu.Heading("Your horn");

        // Every choice: the game's horn, the built-in ones, then your valid files.
        var values = new List<string> { HornsConfig.StockValue };
        var labels = new List<string> { "Game's horn" };
        foreach (var p in HornSynth.All)
        {
            values.Add(HornsConfig.BuiltInValue(p.Id));
            labels.Add(p.Name);
        }
        var files = HornSounds.Files();
        int bad = 0;
        foreach (var f in files)
        {
            if (!f.Ok) { bad++; continue; }
            values.Add(HornsConfig.CustomValue(f.Name));
            labels.Add(Short(f.Name, 26));
        }
        int at = values.FindIndex(v => string.Equals(v, HornsConfig.Horn.Value, System.StringComparison.OrdinalIgnoreCase));
        SimpleMenu.Choice("Horn", "Players with this mod hear it when you honk; everyone else hears the game's horn.",
            at, labels.ToArray(), i => { HornsConfig.Horn.Value = values[i]; HornsFeature.RefreshMine(force: true); },
            shown: at < 0 ? Label(HornsConfig.Horn.Value) : null);

        var kind = HornsConfig.Parse(HornsConfig.Horn.Value, out _, out _);
        if (kind == HornKind.Custom)
            SimpleMenu.Choice("Your file plays", "While held: loops for as long as you hold the horn. Whole clip: plays once per press.",
                (int)HornsConfig.CustomMode.Value, ModeLabels, i => { HornsConfig.CustomMode.Value = (HornMode)i; HornsFeature.RefreshMine(force: true); });
        SimpleMenu.Button("Try it", kind == HornKind.Stock ? "" : "only you hear this", "Plays your horn once, to you only.",
            () => Menu.SetStatus(HornsFeature.PreviewMine() ?? "Playing your horn"), kind != HornKind.Stock);
        if (HornsFeature.OwnProblem != null) SimpleMenu.Note("Can't use " + HornsFeature.OwnProblem + ". The game's horn plays instead.");

        SimpleMenu.Heading("Other players");
        SimpleMenu.Toggle(HornsConfig.HearOthers, "Hear their horns", "Off: every car sounds like the game's horn to you.");
        SimpleMenu.Toggle(HornsConfig.OthersCustomFiles, "Play their own files", "Downloads a player's own wav (up to 256 KB, 10 s) once per session.",
            HornsConfig.HearOthers.Value);
        SimpleMenu.FloatChoice("Horn volume", "How loud the horns from this mod play.", HornsConfig.Volume.Value, VolumeSteps,
            v => Mathf.RoundToInt(v * 100f) + "%", v => HornsConfig.Volume.Value = v);
        SimpleMenu.Note("Here: " + HornNet.Describe());
        if (HornSounds.FolderLinked) SimpleMenu.Note("The horn folder is a link (symlink or junction), so nothing in it is used: put the files in the folder itself.");
        SimpleMenu.Note("Your own horns: wav files of up to 256 KB and 10 s in BepInEx/config/oth.horns." +
                        (bad > 0 ? $" {bad} file(s) there can't be used (see the Advanced fold)." : ""));
    }

    /// <summary>A Horn setting as text for the page.</summary>
    internal static string Label(string value)
    {
        switch (HornsConfig.Parse(value, out byte id, out string file))
        {
            case HornKind.BuiltIn: return HornSynth.Find(id)?.Name ?? "?";
            case HornKind.Custom: return Short(file, 26);
            default: return "Game's horn";
        }
    }

    private static string Short(string s, int max) => HornsUtil.Short(s, max);
}
