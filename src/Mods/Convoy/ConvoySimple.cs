using OverTheHillPlus.Features.Cheats.Convoy;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Convoy;

/// <summary>
/// Convoy AI's everyday rows, first on its MODS page (the old CHEATS menu's Convoy page): spawn, orders, help and
/// meteorites, and the main switches. The rest is in its Advanced fold.
/// </summary>
internal static class ConvoySimple
{
    private static readonly float[] ConvoyCountSteps = { 1f, 2f, 4f, 6f, 8f, 12f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (ConvoyFeature.SpawnCount == null) return;
        SimpleMenu.TitledPage("Convoy", ConvoyPage, true);
    }

    private static void ConvoyPage()
    {
        string block = ConvoyAi.BlockReason();
        bool can = block == null;
        int n = ConvoyAi.Agents.Count, pending = ConvoyAi.PendingCount;
        if (block != null) SimpleMenu.Note(block);

        int count = Mathf.RoundToInt(ConvoyFeature.SpawnCount.Value);
        SimpleMenu.FloatChoice("Cars to spawn", "How many AI cars 'Spawn convoy' adds.", count, ConvoyCountSteps,
            v => Mathf.RoundToInt(v).ToString(), v => ConvoyFeature.SpawnCount.Value = Mathf.RoundToInt(v), can);
        SimpleMenu.Button(n == 0 ? $"Spawn convoy ({count})" : $"Add {count} more",
            n == 0 && pending == 0 ? null : pending > 0 ? $"{n} (+{pending})" : $"{n} active",
            "Spawns AI cars behind the leader that follow the trail, wait, winch, repair and flip.",
            () => ConvoyFeature.Report(ConvoyAi.Spawn(count)), can);
        SimpleMenu.Actions("Convoy orders",
            (ConvoyAi.Holding ? "Go" : "Hold", () => ConvoyFeature.Report(ConvoyAi.ToggleHold()), n > 0),
            ("Regroup", () => ConvoyFeature.Report(ConvoyAi.Regroup()), n > 0),
            ("Dismiss all", () => ConvoyFeature.Report(ConvoyAi.Dismiss()), n > 0 || pending > 0));
        SimpleMenu.Actions("Help & meteorites",
            ("Help me (tow)", () => ConvoyFeature.Report(ConvoyAi.HelpMe()), n > 0 && can),
            ("Pull meteorite", () => ConvoyFeature.Report(ConvoyAi.FetchNearest()), n > 0 && can));
        SimpleMenu.Toggle(ConvoyFeature.HelpPlayers, "Help players", "AI cars tow, repair and flip players, not just each other.");
        SimpleMenu.Toggle(ConvoyFeature.FetchMeteorites, "Auto-pull meteorites", "When the leader stops near a meteorite, AI cars winch it out.");
        SimpleMenu.Toggle(ConvoyFeature.ShowOverlay, "Debug overlay", "Shows each AI car's state, speed, target and trail.");
    }
}
