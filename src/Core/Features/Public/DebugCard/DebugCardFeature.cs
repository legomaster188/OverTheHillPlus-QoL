using System;
using OverTheHillPlus.Logging;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.Features.Public.DebugCard;

/// <summary>
/// The "Debug" card on core's MODS page: the debug log level, the "Write a diagnostics snapshot" row and where the log,
/// the crash reports and the snapshots are. The snapshot hotkey ([Debug] SnapshotKey with Ctrl+Shift, F12 by default)
/// is registered here too. See <see cref="Snapshot"/>.
/// </summary>
internal sealed class DebugCardFeature : IFeature
{
    internal const string Tab = "Debug";

    private static readonly string[] LevelNames = { "Off", "Errors", "Info", "Verbose" };

    public void Register()
    {
        CoreBehaviour.Hotkeys += Snapshot.Hotkey;
        Registry.Section(Registry.PlusMode, Tab, DrawCard, 0);
    }

    private static string KeyText()
    {
        var key = DebugConfig.SnapshotKey?.Value ?? UnityEngine.InputSystem.Key.None;
        if (key == UnityEngine.InputSystem.Key.None) return "no shortcut";
        return (DebugConfig.Get(DebugConfig.SnapshotCtrlShift, true) ? "Ctrl+Shift+" : "") + key;
    }

    private static void DrawCard()
    {
        Menu.Card("Debug log and diagnostics",
            "What the mods write for finding problems: the debug log (BepInEx/oth-debug), crash reports and snapshots (BepInEx/oth-reports).", () =>
            {
                if (DebugConfig.Level != null)
                {
                    SimpleMenu.Choice("Debug log", "Off: nothing. Errors: hangs, spikes and each error once (the default). Info: plus what the mods do (scenes, sessions, settings, cars, network). " +
                                                 "Verbose: plus the detail of every mod's operations. Takes effect at once.",
                        (int)DebugConfig.Level.Value, LevelNames, i => DebugConfig.Level.Value = (DebugLevel)i);
                }
                SimpleMenu.Button("Write a diagnostics snapshot", KeyText(),
                    "Writes BepInEx/oth-reports/snapshot-<time>.txt: every mod's state and last error, hook timing, recent errors, the session, " +
                    "players and car, the mod network, memory, frame rate and the last 200 steps. Send it with a bug report.",
                    () => Menu.SetStatus(Snapshot.Write("MODS page button")));
                string last = Snapshot.LastPath;
                if (Snapshot.LastProblem != null) SimpleMenu.Note("The last snapshot failed: " + Snapshot.LastProblem);
                else if (last != null) SimpleMenu.Note("Last snapshot: " + System.IO.Path.GetFileName(last));
                SimpleMenu.Note(DebugLog.FilePath != null
                    ? "Debug log of this session: " + System.IO.Path.GetFileName(DebugLog.FilePath)
                    : "No debug log this session (level Off).");
                SimpleMenu.Note($"Errors noted this session: {DebugLog.Book.Count} distinct" + (DebugLog.Book.Overflow > 0 ? $" (+{DebugLog.Book.Overflow} not kept)" : ""));
            });
    }
}
