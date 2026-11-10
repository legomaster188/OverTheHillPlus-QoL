using System;
using System.IO;
using OverTheHillPlus.Config;
using OverTheHillPlus.UI;
using UnityEngine;
using OverTheHillPlus.Loader;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A modded car's MODS page, the same for every car (ModInfo.DrawSimple), in the game's own look: small grey section titles
/// (DRIVE, LOOKS, SOUND, TUNING), rows with the name on the left and the value on the right, paint and trim as a strip of
/// colour squares, and one row per tuning group that opens the page of the mod that owns it (Suspension, Tyres, Gears and
/// diffs, Lights). The car's picture is the big image of the MOD BROWSER's details panel on the right (its card), and at
/// the top of the page in the full view. Every row reads and writes the same config entries as before.
/// </summary>
internal static class CarPage
{
    private static readonly System.Collections.Generic.Dictionary<string, string> Edits = new();

    internal static void Draw(CarDefinition def, RuntimeCar car)
    {
        // The car's own failure, or the plugin's (e.g. the model could not be loaded): either way it is off.
        string failed = car?.FailedReason ?? Core.Manager.ModManager.FailedReason(def.Id);
        if (failed != null) SimpleMenu.Note("The car is off this session: " + failed + ".");
        else if (car == null) return; // the mod is switched off or didn't start
        else if (!car.Ready) SimpleMenu.Note("Getting the car ready (waiting for " + (car.Waiting ?? "the game") + ").");
        else SimpleMenu.Note(def.PageNote);
        if (car == null) return;

        // The full view (no details panel beside the page) shows the car's picture at the top.
        SimpleMenu.Picture(car.Assets.Picture ?? car.Assets.Icon);

        string why = car.Drive.Blocker();
        bool driving = car.Drive.Driving;
        string n = def.CarName;
        SimpleMenu.Heading("Drive");
        SimpleMenu.Button("Drive the " + n, driving ? "Driving" : why ?? "Ready",
            $"Swaps your car for the {n} where you stand (while stopped). The save keeps your own car.",
            () => Menu.SetStatus(car.Drive.DriveIt()), why == null && !driving);
        SimpleMenu.Button("Back to my car", why ?? (driving ? "Ready" : "—"),
            $"Swaps the {n} back for the car you were driving.",
            () => Menu.SetStatus(car.Drive.BackToMyCar()), why == null && driving);
        SimpleMenu.Toggle(car.Config.ShowInGarage, "List in the garage", $"Buy and pick the {n} in the garage like the other cars.");

        DrawLook(car);

        // Sound: its own group (the engine is not a colour)
        var def2 = car.Def;
        var cfg = car.Config;
        SimpleMenu.Heading("Sound");
        var engineNames = new string[def2.EngineSounds.Length];
        for (int i = 0; i < engineNames.Length; i++) engineNames[i] = def2.EngineSounds[i].Name;
        int ei = def2.EngineIndexOf(cfg.EngineSound.Value);
        SimpleMenu.Choice("Engine sound", def2.EngineHint, ei, engineNames, i => cfg.EngineSound.Value = def2.EngineSounds[i].Label);

        // Tuning: the groups live on the pages of the mods that own them; a row each, never the same setting twice.
        SimpleMenu.Heading("Tuning");
        SimpleMenu.Links(TuneGroups.Tuning, def.Id);
    }

    private static void DrawLook(RuntimeCar car)
    {
        var def = car.Def;
        var cfg = car.Config;
        SimpleMenu.Heading(TuneGroups.Looks);
        var skins = Api.Cars.SkinsFor(def.Id);
        string skin = cfg.Skin.Value;
        bool skinOn = !string.IsNullOrEmpty(skin);
        bool zones = car.Assets.HasZones;
        // Paint and trim stay usable with a skin or livery on: one with see-through parts sits on top of them.
        string colourHint = zones
            ? skinOn ? "A skin is on: a full one hides these colours, a see-through one sits on top of them." : null
            : "This build of the car has no paint zones yet, so it keeps its standard paint (skins still work).";

        var main = CarPaint.MainColour(def.Paints, cfg.Paint.Value, cfg.PaintCustom.Value);

        var paintLabels = CarPaint.PaintLabels(def.Paints);
        var paintColours = new int[paintLabels.Length];
        for (int i = 0; i < def.Paints.Length; i++) paintColours[i] = Rgb(def.Paints[i].Rgb);
        paintColours[def.Paints.Length] = Rgb(CarRgb.TryParse(cfg.PaintCustom.Value, out var pc) ? pc : new CarRgb(128, 128, 128));
        int pi = IndexOf(paintLabels, cfg.Paint.Value);
        SimpleMenu.SwatchChoice("Paint", colourHint ?? "The body colour. Other players with this mod see it too.", pi, paintLabels, paintColours,
            i => cfg.Paint.Value = paintLabels[i], zones, pi < 0 ? CarPalette.FindPaint(cfg.Paint.Value)?.Name : null);
        foreach (var g in CarPalette.Groups)
        {
            var shared = System.Array.FindAll(g.Paints, p => CarPalette.Offered(def.Paints, p.Name));
            if (shared.Length == 0) continue;
            var colours = new int[shared.Length];
            for (int i = 0; i < shared.Length; i++) colours[i] = Rgb(shared[i].Rgb);
            int gi = System.Array.FindIndex(shared, p => string.Equals(p.Name, cfg.Paint.Value, StringComparison.OrdinalIgnoreCase));
            SimpleMenu.SwatchGroup(g.Title, gi, colours, i => cfg.Paint.Value = shared[i].Name, zones);
        }
        if (string.Equals(cfg.Paint.Value, CarPaint.Custom, StringComparison.OrdinalIgnoreCase))
            ColourRows(def.Id + "|paint", "Paint", cfg.PaintCustom, zones);

        var trimLabels = CarPaint.TrimLabels(def.Trims);
        var trimColours = new int[trimLabels.Length];
        for (int i = 0; i < def.Trims.Length; i++) trimColours[i] = Rgb(def.Trims[i].BodyColour ? main : def.Trims[i].Rgb);
        trimColours[def.Trims.Length] = Rgb(CarRgb.TryParse(cfg.TrimCustom.Value, out var tc) ? tc : new CarRgb(128, 128, 128));
        int ti = IndexOf(trimLabels, cfg.Trim.Value);
        SimpleMenu.SwatchChoice("Trim", colourHint ?? def.TrimHint, ti, trimLabels, trimColours,
            i => cfg.Trim.Value = trimLabels[i], zones, ti < 0 ? CarPalette.FindTrim(cfg.Trim.Value)?.Name : null);
        var sharedTrims = System.Array.FindAll(CarPalette.Trims, t => CarPalette.Offered(def.Trims, t.Name));
        if (sharedTrims.Length > 0)
        {
            var colours = new int[sharedTrims.Length];
            for (int i = 0; i < colours.Length; i++) colours[i] = Rgb(sharedTrims[i].BodyColour ? main : sharedTrims[i].Rgb);
            int gi = System.Array.FindIndex(sharedTrims, t => string.Equals(t.Name, cfg.Trim.Value, StringComparison.OrdinalIgnoreCase));
            SimpleMenu.SwatchGroup("More trims", gi, colours, i => cfg.Trim.Value = sharedTrims[i].Name, zones);
        }
        if (string.Equals(cfg.Trim.Value, CarPaint.Custom, StringComparison.OrdinalIgnoreCase))
            ColourRows(def.Id + "|trim", "Trim", cfg.TrimCustom, zones);

        var labels = new string[skins.Count + 1];
        labels[0] = "None";
        int si = skinOn ? -1 : 0;
        for (int i = 0; i < skins.Count; i++)
        {
            labels[i + 1] = skins[i].Source == Api.Cars.SkinSource.ModIo ? skins[i].Name + " (mod.io)" : skins[i].Name;
            if (skinOn && skins[i].Key == skin) si = i + 1;
        }
        string skinHint = skins.Count == 0
            ? $"No skins found. A skin is a livery pack with \"car\": \"{def.CarName}\"; the Open the skins folder row says how."
            : si > 0 ? SkinHint(skins[si - 1])
            : "A skin goes over the garage's livery and your paint. Players who have the same one see it; the others see your colours.";
        SimpleMenu.Choice("Skin", skinHint, si, labels, i => cfg.Skin.Value = i == 0 ? "" : skins[i - 1].Key,
            skins.Count > 0 || skinOn, si < 0 ? "Not found" : null);
        if (car.Look.SkinStatus != null) SimpleMenu.Note(car.Look.SkinStatus);

        SimpleMenu.Button("Find skins again", $"{skins.Count} skin(s)", "Reads the livery pack folders again (after adding or changing a pack).",
            () =>
            {
                Api.Cars.Rescan();
                Menu.SetStatus($"{Api.Cars.SkinsFor(def.Id).Count} {def.CarName} skin(s) found.");
            });
        string liveries = car.Liveries.Any
            ? $" The {def.CarName}'s own liveries are in the garage's Liveries tab too; everyone with this mod sees the one you mount."
            : "";
        SimpleMenu.Button("Open the skins folder", null,
            "Make a skin: a folder in " + OverTheHillPlus.Loader.LoaderText.ConfigDir + "/oth.liveries/ (or the game's LocalLiveries folder) with a manifest.json " +
            $"{{\"liveries\":[{{\"car\":\"{def.CarName}\",\"name\":\"...\",\"texture\":\"x.png\",\"icon\":\"x_icon.png\"}}]}} " +
            $"and a square PNG (2048 pixels, up to 16 MB) painted on the {def.CarName}'s UV layout. A mod.io livery for the {def.CarName} works once installed." + liveries,
            OpenFolder);

        // The other pages that change a car's look (custom parts, stacking, packs) are one row each.
        SimpleMenu.Links(TuneGroups.LookPages, def.Id);
    }

    private static string SkinHint(Api.Cars.Skin s) => s.Source switch
    {
        Api.Cars.SkinSource.Bundled => "Built into this car mod (also in the garage's Liveries tab). Everyone with this mod sees it.",
        Api.Cars.SkinSource.ModIo => $"From mod.io ({s.Side}x{s.Side}). Players who have it installed see it; the others see your colours.",
        _ => $"{s.Key} ({s.Side}x{s.Side}). Players with the same pack see it; the others see your colours.",
    };

    private static int Rgb(CarRgb c) => c.R << 16 | c.G << 8 | c.B;

    /// <summary>A #RRGGBB box and Red / Green / Blue sliders for a custom colour.</summary>
    private static void ColourRows(string key, string name, ConfigEntry<string> entry, bool enabled)
    {
        if (!CarRgb.TryParse(entry.Value, out var c)) c = new CarRgb(128, 128, 128);
        Edits.TryGetValue(key, out string edit);
        if (edit == null || CarRgb.TryParse(edit, out var e) && e != c) edit = c.ToHex();
        if (enabled)
        {
            string v = SimpleMenu.Text(name + " colour", edit, 7, "Type a colour as #RRGGBB, or use the sliders below.");
            if (v != edit)
            {
                edit = v;
                if (CarRgb.TryParse(v, out var typed) && typed != c) entry.Value = typed.ToHex();
            }
        }
        else SimpleMenu.Button(name + " colour", c.ToHex(), null, null, false);
        Edits[key] = edit;
        var cur = c;
        SimpleMenu.StepSlider(name + " red", null, cur.R, 0f, 255f, 17f, Fmt, v => entry.Value = new CarRgb((byte)v, cur.G, cur.B).ToHex(), enabled);
        SimpleMenu.StepSlider(name + " green", null, cur.G, 0f, 255f, 17f, Fmt, v => entry.Value = new CarRgb(cur.R, (byte)v, cur.B).ToHex(), enabled);
        SimpleMenu.StepSlider(name + " blue", null, cur.B, 0f, 255f, 17f, Fmt, v => entry.Value = new CarRgb(cur.R, cur.G, (byte)v).ToHex(), enabled);
    }

    private static string Fmt(float v) => Mathf.RoundToInt(v).ToString();

    private static int IndexOf(string[] labels, string value)
    {
        for (int i = 0; i < labels.Length; i++)
            if (string.Equals(labels[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string SkinsFolder => Path.Combine(OthLoader.ConfigDir, "oth.liveries");

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
