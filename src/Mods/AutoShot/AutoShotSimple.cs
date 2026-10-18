using OverTheHillPlus.UI;

namespace OverTheHillPlus.AutoShot;

/// <summary>Auto Screenshot's settings on its MODS page.</summary>
internal static class AutoShotSimple
{
    private static readonly string[] Folders = { "Mod folder", "Pictures", "Game screenshots" };
    private static readonly float[] Airs = { 0.5f, 0.75f, 1f, 1.5f, 2f, 3f };
    private static readonly float[] Cooldowns = { 5f, 10f, 20f, 30f, 60f, 120f, 300f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (AutoShotConfig.Jumps == null) return;
        SimpleMenu.Heading("Take a screenshot on");
        SimpleMenu.Toggle(AutoShotConfig.Jumps, "Big jumps", "Mid-air, once the car has been off the ground long enough.");
        SimpleMenu.FloatChoice("Big means", "Seconds in the air before a jump counts.", AutoShotConfig.MinAirTime.Value, Airs,
            v => v.ToString("0.##") + " s", v => AutoShotConfig.MinAirTime.Value = v, AutoShotConfig.Jumps.Value);
        SimpleMenu.Toggle(AutoShotConfig.Flips, "Flips", "When the car turns over in the air.");
        SimpleMenu.Toggle(AutoShotConfig.Landmarks, "New places",
            AutoShotFrame.LandmarksAvailable ? "When you reach a place for the first time." : "Not available: the game's event could not be found (see the log).");
        SimpleMenu.FloatChoice("Cooldown", "Seconds at least between two screenshots.", AutoShotConfig.Cooldown.Value, Cooldowns,
            v => v < 60f ? v.ToString("0") + " s" : (v / 60f).ToString("0.#") + " min", v => AutoShotConfig.Cooldown.Value = v);

        SimpleMenu.Heading("Files");
        SimpleMenu.Choice("Save to", "Mod folder: BepInEx/config/oth.screenshots. Pictures: Pictures/Over the Hill.",
            (int)AutoShotConfig.SaveTo.Value, Folders, i => AutoShotConfig.SaveTo.Value = (AutoShotConfig.Folder)i);
        SimpleMenu.Toggle(AutoShotConfig.ShowToast, "Note on screen", "A short note after each screenshot (never in the picture).");
        string last = AutoShotCapture.Count == 0 ? "none yet" : $"{AutoShotCapture.Count} this session";
        SimpleMenu.Button("Open the folder", last, AutoShotCapture.FolderPath, AutoShotCapture.OpenFolder);
        if (AutoShotCapture.LastError != null) SimpleMenu.Note("Last problem: " + AutoShotCapture.LastError);
    }
}
