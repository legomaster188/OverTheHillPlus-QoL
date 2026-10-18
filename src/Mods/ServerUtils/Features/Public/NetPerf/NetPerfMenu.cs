using System;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.NetPerf;

/// <summary>The PLUS "Network" tab: explains each host tweak and shows per-player network health.</summary>
internal static class NetPerfMenu
{
    private static readonly string[] RatePreview = new string[5];
    private static float _nextPreview;

    internal static void Draw()
    {
        NetPerfRuntime.RefreshStats();
        var r = GameState.Runner;
        bool inSession = r != null;
        bool hosting = NetPerfRuntime.IsHosting(r);

        if (hosting)
            Menu.Banner("You are hosting. Live tweaks apply now; the snapshot and timeout settings apply the next time you start a server.", Theme.Good);
        else if (inSession)
            Menu.Banner("You are a guest. The host decides the network settings, so nothing here does anything until you host your own game.", Theme.Info);
        else
            Menu.Banner("Host-side tweaks for big lobbies (8 to 16 players). Guests don't need the mod. Nothing here changes how cars drive.", Theme.Info);

        Menu.Card("Network performance", "What the session is using right now", () =>
        {
            if (inSession)
            {
                if (!string.IsNullOrEmpty(NetPerfRuntime.SessionLine)) Menu.KeyValue("Rates", NetPerfRuntime.SessionLine);
                if (!string.IsNullOrEmpty(NetPerfRuntime.InterestLine)) Menu.KeyValue("Interest", NetPerfRuntime.InterestLine);
                if (!string.IsNullOrEmpty(NetPerfRuntime.StatsLine)) Menu.Small(NetPerfRuntime.StatsLine);
            }
            else
            {
                Menu.KeyValue("Session", "not in a game");
            }
            if (!string.IsNullOrEmpty(NetPerfConfig.LastApplied)) Menu.KeyValue("Last start", NetPerfConfig.LastApplied);
            Menu.Small("The preset is a good start for 12+ players: half snapshot rate, patient timeout, " +
                       "80% interest distance with every car kept visible.");
            Menu.Chips(2,
                ("Big lobby preset", () => { NetPerfFeature.ApplyBigLobbyPreset(); Menu.SetStatus("Big lobby network preset on (snapshot settings apply when you next host)"); }, true),
                ("Game defaults", () => { NetPerfFeature.ResetAll(); Menu.SetStatus("Network tweaks off: game defaults"); }, NetPerfFeature.AnyActive));
        });

        Menu.Card("Before you host", "Written to the Fusion config when you start a server; the game's values come back when off", () =>
        {
            RefreshPreview();
            Menu.Small("Snapshot rate: how often the host sends the world to each guest. Every guest gets every car, so upload " +
                       "grows with players squared. Half rate roughly halves the host's upload; remote cars are shown a few " +
                       "milliseconds further in the past (your own car is unaffected). The simulation tick rate is never changed.");
            int cur = NetPerfFeature.SendDivisor.Value;
            (string, Action, bool) Opt(int k, string name) =>
                ((cur == k ? "> " : "") + name + (string.IsNullOrEmpty(RatePreview[k]) ? "" : " " + RatePreview[k]),
                 () => { NetPerfFeature.SendDivisor.Value = k; Menu.SetStatus($"Snapshot rate {name}{(hosting ? " (restart the server to apply)" : "")}"); },
                 true);
            Menu.Chips(4, Opt(1, "Full"), Opt(2, "1/2"), Opt(3, "1/3"), Opt(4, "1/4"));

            Menu.Switch(NetPerfFeature.PatientTimeout, "Patient connections",
                "Double the timeout (min 20 s): a guest with a hitch isn't kicked", true);
        });

        Menu.Card("While hosting", "Live: changes apply within 2 seconds", () =>
        {
            Menu.Small("Interest distance: each guest only receives objects inside their camera's view cone. A shorter cone " +
                       "means less data per guest (animals, props and far cars stop updating sooner); a longer one shows " +
                       "more of the world at the cost of upload.");
            Menu.SliderRow(NetPerfFeature.InterestScale, "Interest distance", 0.4f, 2f, "0.00", "", true, percent: true);
            if (hosting && NetPerfRuntime.SampleConeOriginal > 0f)
                Menu.KeyValue("View cone", NetPerfRuntime.ConeCount > 0
                    ? $"{NetPerfRuntime.SampleConeOriginal:0} m -> {NetPerfRuntime.SampleConeNow:0} m on {NetPerfRuntime.ConeCount} players"
                    : $"{NetPerfRuntime.SampleConeOriginal:0} m (game default) on {NetPerfRuntime.ConesSeen} players");
            else if (hosting)
                Menu.KeyValue("View cones", $"{NetPerfRuntime.ConesSeen} found, game default length");

            Menu.Switch(NetPerfFeature.KeepCarsVisible, "Keep every car visible",
                "Player cars always replicate to everyone, whatever the distance", true);
            if (hosting)
            {
                string mode = NetPerfRuntime.CarInterestMode;
                string note = mode == "Global" ? "cars already go to everyone" : mode == "?" ? "" : $"{NetPerfRuntime.PinnedCount} pairs pinned";
                Menu.KeyValue("Car interest", mode == "?" ? "unknown yet" : $"{mode}{(note.Length > 0 ? " (" + note + ")" : "")}");
            }
        });

        if (!hosting) return;

        Menu.Card("Players", "Ping and how much of the world each guest receives (refreshes every 2 s)", () =>
        {
            var rows = NetPerfRuntime.Rows;
            if (rows.Count == 0) { Menu.Small("No players found yet."); return; }

            // One row per player: name, then ping (coloured), link, objects and cells.
            int worst = 0;
            foreach (var row in rows)
            {
                string ping = row.IsYou ? "host" : row.RttMs >= 0 ? $"{row.RttMs:0} ms" : "-";
                var pc = row.IsYou || row.RttMs < 0 ? Theme.Muted : row.RttMs < 90 ? Theme.Good : row.RttMs < 180 ? Theme.Warn : Theme.Bad;
                if (!row.IsYou && row.RttMs >= 180) worst++;
                var lc = row.Connection == "relayed" ? Theme.Warn : Theme.Muted;
                Menu.KeyValue(Clip(row.Name, 22) + (row.IsYou ? " (you)" : ""),
                    $"{Colored(ping, pc)}  ·  {Colored(row.Connection, lc)}  ·  {(row.Objects >= 0 ? row.Objects.ToString() : "-")} objects  ·  {(row.Cells >= 0 ? row.Cells.ToString() : "-")} cells");
            }
            Menu.Small("Relayed = traffic goes through Photon's relay (no direct route), which adds latency; nothing the host can change. " +
                       "Objects = network objects this guest currently receives; lower interest distance to cut it.");
            if (worst > 0) Menu.Small($"{worst} player(s) above 180 ms: try the half snapshot rate or a lower interest distance.");
        });
    }

    private static void RefreshPreview()
    {
        if (Time.unscaledTime < _nextPreview) return;
        _nextPreview = Time.unscaledTime + 3f;
        for (int i = 0; i < RatePreview.Length; i++) RatePreview[i] = "";
        try
        {
            if (!NetPerfConfig.TryGetBaseSelection(out var sel)) return;
            var baseSel = sel;
            if (baseSel.ServerSendInterval <= 0) baseSel.ConvertObsoleteIndicesToIntervals();
            if (baseSel.Validate() != Fusion.TickRate.ValidateResult.Ok) return;
            RatePreview[1] = $"({Fusion.TickRate.Resolve(baseSel).ServerSend} Hz)";
            for (int k = 2; k <= 4; k++)
            {
                var s = NetPerfConfig.WithSendDivisor(sel, k, out _);
                if (s.Validate() != Fusion.TickRate.ValidateResult.Ok) continue;
                RatePreview[k] = $"({Fusion.TickRate.Resolve(s).ServerSend} Hz)";
            }
        }
        catch (Exception ex) { DebugLog.Error("serverutils", ex, "NetPerfMenu.RefreshPreview"); }
    }

    private static string Colored(string text, Color c) => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{text}</color>";

    private static string Clip(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
