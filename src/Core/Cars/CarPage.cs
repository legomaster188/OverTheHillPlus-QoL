using System;
using System.IO;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Cars;

/// <summary>A modded car's MODS page rows: its state, the "Drive the &lt;car&gt;" button, the garage switch, and its
/// look (paint, trim, engine sound, skin). The same rows for every car (ModInfo.DrawSimple).</summary>
internal static class CarPage
{
    private static readonly int[] ChannelSteps = BuildSteps();
    private static readonly System.Collections.Generic.Dictionary<string, string> Edits = new();

    internal static void Draw(CarDefinition def, RuntimeCar car)
    {
        SimpleMenu.Heading(def.DisplayName);
        // The car's own failure, or the plugin's (e.g. the model could not be loaded): either way it is off.
        string failed = car?.FailedReason ?? Core.Manager.ModManager.FailedReason(def.Id);
        if (failed != null) SimpleMenu.Note("The car is off this session: " + failed + ".");
        else if (car == null) return; // the mod is switched off or didn't start
        else if (!car.Ready) SimpleMenu.Note("Getting the car ready (waiting for " + (car.Waiting ?? "the game") + ").");
        else SimpleMenu.Note(def.PageNote);
        if (car == null) return;

        string why = car.Drive.Blocker();
        bool driving = car.Drive.Driving;
        string n = def.CarName;
        SimpleMenu.Button("Drive the " + n, driving ? "Driving" : why ?? "Ready",
            $"Swaps your car for the {n} where you stand (while stopped). The save keeps your own car.",
            () => Menu.SetStatus(car.Drive.DriveIt()), why == null && !driving);
        SimpleMenu.Button("Back to my car", why ?? (driving ? "Ready" : "—"),
            $"Swaps the {n} back for the car you were driving.",
            () => Menu.SetStatus(car.Drive.BackToMyCar()), why == null && driving);
        SimpleMenu.Toggle(car.Config.ShowInGarage, "List in the garage", $"Buy and pick the {n} in the garage like the other cars.");

        DrawLook(car);
    }

    private static void DrawLook(RuntimeCar car)
    {
        var def = car.Def;
        var cfg = car.Config;
        SimpleMenu.Heading("Look");
        var skins = Api.Cars.SkinsFor(def.Id);
        string skin = cfg.Skin.Value;
        bool skinOn = !string.IsNullOrEmpty(skin);
        bool zones = car.Assets.HasZones;
        string colourHint = zones
            ? skinOn ? "A skin is on: it replaces the paint (set Skin to None to use the colours)." : null
            : "This build of the car has no paint zones yet, so it keeps its standard paint (skins still work).";

        var paintLabels = CarPaint.PaintLabels(def.Paints);
        int pi = IndexOf(paintLabels, cfg.Paint.Value);
        SimpleMenu.Choice("Paint", colourHint ?? "The body colour. Other players with this mod see it too.", pi, paintLabels,
            i => cfg.Paint.Value = paintLabels[i], zones && !skinOn);
        if (string.Equals(cfg.Paint.Value, CarPaint.Custom, StringComparison.OrdinalIgnoreCase))
            ColourRows(def.Id + "|paint", "Paint colour", cfg.PaintCustom, zones && !skinOn);

        var trimLabels = CarPaint.TrimLabels(def.Trims);
        int ti = IndexOf(trimLabels, cfg.Trim.Value);
        SimpleMenu.Choice("Trim", colourHint ?? def.TrimHint, ti, trimLabels,
            i => cfg.Trim.Value = trimLabels[i], zones && !skinOn);
        if (string.Equals(cfg.Trim.Value, CarPaint.Custom, StringComparison.OrdinalIgnoreCase))
            ColourRows(def.Id + "|trim", "Trim colour", cfg.TrimCustom, zones && !skinOn);

        var engineNames = new string[def.EngineSounds.Length];
        for (int i = 0; i < engineNames.Length; i++) engineNames[i] = def.EngineSounds[i].Name;
        int ei = def.EngineIndexOf(cfg.EngineSound.Value);
        SimpleMenu.Choice("Engine sound", def.EngineHint, ei, engineNames, i => cfg.EngineSound.Value = def.EngineSounds[i].Label);

        var labels = new string[skins.Count + 1];
        labels[0] = "None";
        int si = skinOn ? -1 : 0;
        for (int i = 0; i < skins.Count; i++)
        {
            labels[i + 1] = skins[i].Name;
            if (skinOn && skins[i].Key == skin) si = i + 1;
        }
        string skinHint = skins.Count == 0
            ? $"No skins found. A skin is a livery pack with \"car\": \"{def.CarName}\" (see the note below)."
            : si > 0 ? $"{skins[si - 1].Key} ({skins[si - 1].Side}x{skins[si - 1].Side}). Players with the same pack see it; the others see your colours."
            : "A skin from a livery pack replaces the paint. Players with the same pack see it; the others see your colours.";
        SimpleMenu.Choice("Skin", skinHint, si, labels, i => cfg.Skin.Value = i == 0 ? "" : skins[i - 1].Key,
            skins.Count > 0 || skinOn, si < 0 ? "Not found" : null);
        if (car.Look.SkinStatus != null) SimpleMenu.Note(car.Look.SkinStatus);

        SimpleMenu.Button("Find skins again", $"{skins.Count} skin(s)", "Reads the livery pack folders again (after adding or changing a pack).",
            () =>
            {
                Api.Cars.Rescan();
                Menu.SetStatus($"{Api.Cars.SkinsFor(def.Id).Count} {def.CarName} skin(s) found.");
            });
        SimpleMenu.Button("Open the skins folder", null, SkinsFolder, OpenFolder);
        SimpleMenu.Note("Make a skin: a folder in BepInEx/config/oth.liveries/ (or the game's LocalLiveries folder) with " +
                        $"manifest.json {{\"liveries\":[{{\"car\":\"{def.CarName}\",\"name\":\"…\",\"texture\":\"x.png\",\"icon\":\"x_icon.png\"}}]}} " +
                        $"and a square PNG (2048 pixels, up to 16 MB) painted on the {def.CarName}'s UV layout; body, rims and tyres share it.");
    }

    /// <summary>A #RRGGBB box and R / G / B steps (for a gamepad) for a custom colour.</summary>
    private static void ColourRows(string key, string label, ConfigEntry<string> entry, bool enabled)
    {
        if (!CarRgb.TryParse(entry.Value, out var c)) c = new CarRgb(128, 128, 128);
        Edits.TryGetValue(key, out string edit);
        if (edit == null || CarRgb.TryParse(edit, out var e) && e != c) edit = c.ToHex();
        if (enabled)
        {
            string v = SimpleMenu.Text(label, edit, 7, "Type a colour as #RRGGBB, or use the steps below.");
            if (v != edit)
            {
                edit = v;
                if (CarRgb.TryParse(v, out var typed) && typed != c) entry.Value = typed.ToHex();
            }
        }
        else SimpleMenu.Button(label, c.ToHex(), null, null, false);
        Edits[key] = edit;
        var cur = c;
        SimpleMenu.IntChoice("  Red", null, cur.R, ChannelSteps, Fmt, v => entry.Value = new CarRgb((byte)v, cur.G, cur.B).ToHex(), enabled);
        SimpleMenu.IntChoice("  Green", null, cur.G, ChannelSteps, Fmt, v => entry.Value = new CarRgb(cur.R, (byte)v, cur.B).ToHex(), enabled);
        SimpleMenu.IntChoice("  Blue", null, cur.B, ChannelSteps, Fmt, v => entry.Value = new CarRgb(cur.R, cur.G, (byte)v).ToHex(), enabled);
    }

    private static string Fmt(int v) => v.ToString();

    private static int[] BuildSteps()
    {
        var s = new int[16];
        for (int i = 0; i < 16; i++) s[i] = i * 17;
        return s;
    }

    private static int IndexOf(string[] labels, string value)
    {
        for (int i = 0; i < labels.Length; i++)
            if (string.Equals(labels[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string SkinsFolder => Path.Combine(BepInEx.Paths.ConfigPath, "oth.liveries");

    private static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(SkinsFolder);
            Application.OpenURL(new Uri(SkinsFolder).AbsoluteUri);
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarPage.OpenFolder"); Menu.SetStatus("Failed: " + e.Message); }
    }
}
