using System;
using System.Collections.Generic;
using CommandTerminal;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace OverTheHillPlus.UnlockAll;

/// <summary>
/// The page's bulk actions, each a list of steps: mostly the game's own developer unlock commands (CheatsController
/// statics, the ones TestAreaUnlocker and UnlockAllAll use, run with no arguments like the game does); the upgrades
/// step is <see cref="CarUnlocks"/> over every car, so it follows the Owned / Buyable switch. Everything runs inside
/// <see cref="DlcGuard"/>. Also <see cref="Guarded"/>, the shared wrapper of the per-car, per-map and item actions.
/// Host or solo only, with a loaded profile.
/// </summary>
internal static class Unlocker
{
    internal enum What { Cars, Upgrades, Areas, Liveries, Everything }

    /// <summary>One step: a game command (run with a prepared console) or one of ours (returns how many it added).</summary>
    private sealed class Step
    {
        public readonly string Name;
        public readonly Action<Il2CppReferenceArray<CommandArg>> Command;
        public readonly Func<Tally, int> Ours;

        public Step(string name, Action<Il2CppReferenceArray<CommandArg>> command, Func<Tally, int> ours = null)
        {
            Name = name;
            Command = command;
            Ours = ours;
        }
    }

    private static Step Ours(string name, Func<Tally, int> run) => new(name, null, run);

    private static readonly Step UpgradesStep = Ours("upgrades", t => CarUnlocks.ApplyAll(UnlockPlan.CarScope.Upgrades, t));

    private static readonly Step[] Cars = { new("cars", CheatsController.UnlockAllCars) };

    private static readonly Step[] Upgrades = { UpgradesStep };

    private static readonly Step[] Areas =
    {
        new("areas", CheatsController.UnlockAllAreas),
        new("cabins", CheatsController.UnlockAllCabins),
    };

    private static readonly Step[] Liveries = { new("liveries", CheatsController.UnlockAllLiveries) };

    // The order of CheatsController.UnlockAllAll, one step at a time so one failing doesn't stop the rest.
    private static readonly Step[] Everything =
    {
        new("areas", CheatsController.UnlockAllAreas),
        new("cabins", CheatsController.UnlockAllCabins),
        new("cars", CheatsController.UnlockAllCars),
        new("cosmetics", CheatsController.UnlockAllCosmetics),
        new("liveries", CheatsController.UnlockAllLiveries),
        new("tool slots", CheatsController.UnlockAllToolSlots),
        UpgradesStep,
    };

    /// <summary>Why the unlocks can't run right now, or null when they can.</summary>
    internal static string Blocker
    {
        get
        {
            if (!DlcGuard.Installed) return "Off: the DLC guard could not be installed (see the log).";
            if (!GameState.IsAuthority) return "Host or solo only: as a guest your unlocks would not count.";
            try
            {
                if (PlayerInventoryManager.Instance == null || PlayerInventoryManager.Instance.Inventory == null)
                    return "Load into the world first.";
                if (PlayerInventoryManager.InventoryItemsCatalog?.IdToSO == null) return "Load into the world first.";
            }
            catch (Exception ex) { DebugLog.Error("unlockall", ex, "Unlocker.Blocker"); return "Load into the world first."; }
            return null;
        }
    }

    /// <summary>Runs one bulk action; returns the status line for the page.</summary>
    internal static string Run(What what)
    {
        string blocked = Blocker;
        if (blocked != null) return blocked;
        var steps = what switch
        {
            What.Cars => Cars,
            What.Upgrades => Upgrades,
            What.Areas => Areas,
            What.Liveries => Liveries,
            _ => Everything,
        };

        AdvantageFlag.MarkUsed(); // core: achievements stay locked for the session
        var failed = new List<string>();
        var tally = new Tally();
        int before = ItemCount(), ours = 0;
        int skipped = DlcGuard.Run(() =>
        {
            foreach (var step in steps)
            {
                try
                {
                    if (step.Ours != null)
                    {
                        int n = tally.Failed.Count;
                        ours += step.Ours(tally);
                        if (tally.Failed.Count > n) failed.Add(step.Name);
                        continue;
                    }
                    PrepareConsole();
                    step.Command(new Il2CppReferenceArray<CommandArg>(0));
                    string error = LastConsoleError();
                    if (error != null)
                    {
                        failed.Add(step.Name);
                        UnlockAllPlugin.Logger.LogWarning($"[UnlockAll] {step.Name}: the game said \"{error}\"");
                    }
                }
                catch (Exception e)
                {
                    DebugLog.Error("unlockall", e, "Unlocker.Run");
                    failed.Add(step.Name);
                    UnlockAllPlugin.Logger.LogWarning($"[UnlockAll] {step.Name} failed: {e.GetBaseException().Message}");
                }
            }
        });
        skipped += tally.DlcSkipped;
        // UnlockAllAreas and our upgrades don't save on their own; the others did already (saving again is harmless).
        Save();

        int added = Math.Max(0, ItemCount() - before) + ours;
        DebugLog.Info(UnlockAllPlugin.ModId, $"{what}: {added} new inventory entries, {skipped} DLC items left alone, {steps.Length} step(s)" +
                                             (failed.Count > 0 ? ", failed: " + string.Join(", ", failed) : ""));
        UnlockAllPlugin.Logger.LogInfo($"[UnlockAll] skipped {skipped} DLC items ({what}: {added} new inventory entries" +
                                       (failed.Count > 0 ? ", failed: " + string.Join(", ", failed) : "") + ")");
        string label = what switch
        {
            What.Cars => "All cars",
            What.Upgrades => UnlockAllConfig.Owned ? "All upgrades (owned)" : "All upgrades (in the shop)",
            What.Areas => "All areas",
            What.Liveries => "All liveries",
            _ => "Everything",
        };
        string result = failed.Count == steps.Length ? $"{label}: failed (see the log)."
            : failed.Count > 0 ? $"{label} unlocked, except {string.Join(", ", failed)} (see the log)."
            : $"{label} unlocked.";
        return result + DlcNote(skipped);
    }

    // ---------------------------------------------------------------- the per-car, per-map and item actions

    /// <summary>What one guarded action did, filled in by the action.</summary>
    internal sealed class Tally
    {
        /// <summary>Adds (or changes) made.</summary>
        public int Added;
        /// <summary>DLC entries the plan left out (the guard's own refusals are added on top).</summary>
        public int DlcSkipped;
        /// <summary>Parts of the action that threw (logged already).</summary>
        public readonly List<string> Failed = new();
        /// <summary>The status line, when the action has a better one than "N added".</summary>
        public string Message;
    }

    /// <summary>
    /// Runs one of the page's smaller actions with every rule of the mod: host or solo with a loaded profile only,
    /// AdvantageFlag marked, the DLC guard on, the profile saved after (unless <paramref name="save"/> is false because
    /// the game call saves its own file). Returns the status line.
    /// </summary>
    internal static string Guarded(string label, Action<Tally> body, bool save = true)
    {
        string blocked = Blocker;
        if (blocked != null) return blocked;
        AdvantageFlag.MarkUsed(); // core: achievements stay locked for the session
        var t = new Tally();
        int refused = 0;
        try { refused = DlcGuard.Run(() => body(t)); }
        catch (Exception e)
        {
            DebugLog.Error("unlockall", e, "Unlocker.Guarded");
            t.Failed.Add(label);
            UnlockAllPlugin.Logger.LogWarning($"[UnlockAll] {label} failed: {e.GetBaseException().Message}");
        }
        if (save) Save();
        int dlc = t.DlcSkipped + refused;
        int added = Math.Max(0, t.Added - refused);
        DebugLog.Info(UnlockAllPlugin.ModId, $"{label}: {added} added, {dlc} DLC items left alone" + (t.Failed.Count > 0 ? ", failed: " + string.Join(", ", t.Failed) : ""));
        UnlockAllPlugin.Logger.LogInfo($"[UnlockAll] skipped {dlc} DLC items ({label}: {added} added" +
                                       (t.Failed.Count > 0 ? ", failed: " + string.Join(", ", t.Failed) : "") + ")");
        string result = t.Message ?? (added > 0 ? $"{label}: {added} unlocked." : $"{label}: nothing new to unlock.");
        if (t.Failed.Count > 0) result += $" Failed: {string.Join(", ", t.Failed)} (see the log).";
        return result + DlcNote(dlc);
    }

    private static string DlcNote(int dlc) => dlc > 0 ? $" {dlc} DLC item{(dlc == 1 ? "" : "s")} left alone." : "";

    private static void Save()
    {
        try { OverlandSaver.SaveGame(); }
        catch (Exception e) { DebugLog.Error("unlockall", e, "Unlocker.Save"); UnlockAllPlugin.Logger.LogWarning("[UnlockAll] save failed: " + e.GetBaseException().Message); }
    }

    private static int ItemCount()
    {
        try
        {
            var inv = PlayerInventoryManager.Instance?.Inventory;
            if (inv == null) return 0;
            return (inv.Items?.Count ?? 0) + (inv.VirtualItems?.Count ?? 0);
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "Unlocker.ItemCount"); return 0; }
    }

    /// <summary>
    /// UnlockAllAreas, UnlockAllCabins and the AddItem command behind UnlockAllToolSlots start by reading
    /// Terminal.Shell (they throw when the console was never started), stop early when the shell still holds an
    /// earlier error, and log to Terminal.Buffer; UnlockAllCars, UnlockAllCosmetics and UnlockAllLiveries never touch
    /// the console (preparing it before them is harmless). Gives the console a bare shell and log when it has none
    /// (Terminal.Start replaces both with its own when the real console starts) and clears the last error.
    /// </summary>
    private static void PrepareConsole()
    {
        if (Terminal.Shell == null) Terminal._Shell_k__BackingField = new CommandShell();
        if (Terminal.Buffer == null) Terminal._Buffer_k__BackingField = new CommandLog(64);
        Terminal.Shell._IssuedErrorMessage_k__BackingField = null;
    }

    private static string LastConsoleError()
    {
        try { return Terminal.Shell?.IssuedErrorMessage; }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "Unlocker.LastConsoleError"); return null; }
    }
}
