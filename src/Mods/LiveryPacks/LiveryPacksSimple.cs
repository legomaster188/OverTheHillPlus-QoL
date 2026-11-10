using System;
using OverTheHillPlus.Shared;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.LiveryPacks;

/// <summary>Liveries+'s MODS page: the folder, each pack's state, reload, and the garage's local-mods switch.</summary>
internal static class LiveryPacksSimple
{
    private static readonly string[] OffOn = { "Off", "On" };

    internal static void Draw()
    {
        if (LiveryPacksConfig.LoadPacks == null) return;
        // On demand only: the first time the page shows, then on Reload (and whenever the game refreshes its list).
        if (!LiveryPacksLoader.Scanned) LiveryPacksLoader.Rescan();

        SimpleMenu.Heading("Livery packs");
        SimpleMenu.Toggle(LiveryPacksConfig.LoadPacks, "Add packs to the garage", "Applies on the next reload (below) or the next start.");
        bool? show = LiveryPacksLoader.ShowLocalInGarage;
        if (show != null)
            SimpleMenu.Choice("Show local mods in the garage", "The game's own switch in the garage's Livery Mods tab: the packs show there while it is on.",
                show.Value ? 1 : 0, OffOn, i => LiveryPacksLoader.ShowLocalInGarage = i == 1);
        SimpleMenu.Button("Reload packs", null, "Reads the folder again and refreshes the garage's livery list (like its Refresh).",
            () => Menu.SetStatus(LiveryPacksLoader.Reload()));
        SimpleMenu.Button("Open the packs folder", null, LiveryPacksLoader.Root, OpenFolder);
        SimpleMenu.Note("Put each pack in its own folder: manifest.json plus its PNG textures, the same format as the " +
                        "game's livery mods. Only you see them, unless other players have the same pack (same folder " +
                        "name) installed too.");

        DrawModdedCars();

        var packs = LiveryPacksLoader.Packs;
        SimpleMenu.Heading(packs.Count == 0 ? "No packs found" : $"Packs ({packs.Count})");
        foreach (var pack in packs)
        {
            string state;
            if (pack.Problem != null) state = "Refused: " + pack.Problem;
            else if (!pack.Usable) state = "Nothing loads: " + (pack.Warnings.Count > 0 ? pack.Warnings[0] : "no liveries");
            else if (LiveryPacksLoader.ShadowedByGame.Contains(pack.Folder)) state = "Loaded from the game's LocalLiveries folder instead";
            else if (LiveryPacksLoader.Registered.Contains(pack.Folder)) state = "In the garage";
            else state = LiveryPacksLoader.HookSeen ? "Reload to add it" : "Added when the game reads its livery list";
            string warn = pack.Usable && pack.Warnings.Count > 0 ? $" · {pack.Warnings.Count} warning(s): {pack.Warnings[0]}" : "";
            string line = pack.Folder + ": " + state + warn;
            SimpleMenu.Button(pack.Folder, $"{pack.Loadable} livery(s)", state + warn, () => Menu.SetStatus(line));
        }

        SimpleMenu.Heading("Also changes the look");
        SimpleMenu.Links(TuneGroups.LookPages, LiveryPacksPlugin.ModId);
    }

    /// <summary>
    /// One skin row per modded car (cars a mod adds, like the Vera Crossover), through core's Api.Cars: skins are pack
    /// entries whose "car" is that car's livery name. Liveries+ never references the car's mod.
    /// </summary>
    private static void DrawModdedCars()
    {
        var cars = Api.Cars.Registered;
        if (cars.Count == 0) return;
        // The skin of each modded car is picked on that car's own page (Looks > Skin): a row here shows which one is on and
        // opens that page, so the same setting is not listed twice.
        SimpleMenu.Heading("Modded cars");
        foreach (var car in cars)
        {
            var skins = Api.Cars.SkinsFor(car.Id);
            string current = Api.Cars.CurrentSkin(car.Id);
            string shown = "No skin";
            if (current != null)
            {
                shown = "Not found";
                foreach (var s in skins)
                    if (s.Key == current) { shown = s.Name; break; }
            }
            string id = car.Id;
            string hint = $"{skins.Count} skin(s) found (pack entries with \"car\": \"{car.LiveryCar}\"). Pick the skin on the car's page.";
            SimpleMenu.Button(car.DisplayName, shown, hint, () => ModsPage.Open(id), Core.Manager.ModManager.Find(id) != null);
        }
    }

    private static void OpenFolder()
    {
        try
        {
            LiveryPacksLoader.EnsureFolder();
            Application.OpenURL(new Uri(LiveryPacksLoader.Root).AbsoluteUri);
        }
        catch (Exception e) { DebugLog.Error("liverypacks", e, "LiveryPacksSimple.OpenFolder"); Menu.SetStatus("Failed: " + e.Message); }
    }
}
