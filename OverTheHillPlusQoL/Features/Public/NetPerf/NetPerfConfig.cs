using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;

namespace OverTheHillPlus.Features.Public.NetPerf;

/// <summary>
/// Pre-start tweaks to the Fusion project config. Fusion copies the config when the runner starts, so these are
/// written in a prefix of NetworkRunner.StartGame (the same place the lobby size is raised, which is verified to
/// reach the session). The game's own values are snapshotted the first time anything is changed and written back
/// whenever a tweak is off, so turning an option off (or joining someone else's game) restores the defaults exactly.
///
/// Only the server send interval is touched, never the simulation tick rate: the car physics run per tick, so
/// changing the tick rate would change how cars drive. The resolved tick rates are part of the session handshake,
/// so guests use the host's values and don't need the mod.
/// </summary>
internal static class NetPerfConfig
{
    /// <summary>Lowest snapshot rate we allow when dividing (below this remote cars get visibly choppy).</summary>
    private const int MinSendHz = 10;

    private sealed class Snapshot
    {
        public TickRate.Selection Selection;
        public double Timeout;
    }

    private static readonly Dictionary<IntPtr, Snapshot> Originals = new();

    /// <summary>What happened at the last StartGame (shown in the menu).</summary>
    internal static string LastApplied = "";

    private static bool _warned;

    internal static void OnStartGame(StartGameArgs args)
    {
        GameMode mode;
        try { mode = args.GameMode; } catch { return; }
        bool hosting = mode == GameMode.Host || mode == GameMode.Server || mode == GameMode.AutoHostOrClient;

        var seen = new HashSet<IntPtr>();
        var notes = new List<string>();
        void Do(Func<NetworkProjectConfig> get)
        {
            try
            {
                var cfg = get();
                if (cfg == null || !seen.Add(cfg.Pointer)) return;
                string n = Apply(cfg, hosting);
                if (!string.IsNullOrEmpty(n) && !notes.Contains(n)) notes.Add(n);
            }
            catch (Exception e)
            {
                if (_warned) return;
                _warned = true;
                Plugin.Logger.LogWarning("[NetPerf] config tweak failed: " + e.Message);
            }
        }
        Do(() => NetworkProjectConfig.Global);
        Do(() => NetworkProjectConfigAsset.Global?.Config);

        try
        {
            var own = args.Config;
            if (own != null && !seen.Contains(own.Pointer))
                Plugin.Logger.LogInfo("[NetPerf] StartGame carries its own config object; tweaks went to the global config only.");
        }
        catch { }

        if (notes.Count > 0)
        {
            LastApplied = $"{mode}: " + string.Join("; ", notes);
            Plugin.Logger.LogInfo("[NetPerf] " + LastApplied);
        }
        else if (hosting) LastApplied = $"{mode}: game defaults (no pre-start tweaks on)";
    }

    /// <summary>Applies (hosting) or restores (anything else) the tweaks on one config object.</summary>
    private static string Apply(NetworkProjectConfig cfg, bool hosting)
    {
        var sim = cfg.Simulation;
        var net = cfg.Network;
        if (sim == null || net == null) return null;

        bool wants = hosting && (NetPerfFeature.SendDivisor.Value > 1 || NetPerfFeature.PatientTimeout.Value);
        if (!Originals.TryGetValue(cfg.Pointer, out var snap))
        {
            // Never touched and nothing to do: leave the game's config exactly as it is.
            if (!wants) return null;
            snap = new Snapshot
            {
                Selection = sim.TickRateSelection,
                Timeout = net.ConnectionTimeout,
            };
            Originals[cfg.Pointer] = snap;
            Plugin.Logger.LogInfo($"[NetPerf] Game defaults: {Describe(snap.Selection)}, timeout {snap.Timeout:0.#} s, " +
                                  $"AOI {(SafeAoi(sim) ? "on" : "off")}");
        }

        var parts = new List<string>();

        string sendNote = null;
        var selection = snap.Selection;
        if (hosting && NetPerfFeature.SendDivisor.Value > 1)
            selection = WithSendDivisor(snap.Selection, NetPerfFeature.SendDivisor.Value, out sendNote);
        sim.TickRateSelection = selection;
        parts.Add(sendNote ?? "snapshot rate default");

        double timeout = hosting && NetPerfFeature.PatientTimeout.Value ? Math.Max(snap.Timeout * 2.0, 20.0) : snap.Timeout;
        net.ConnectionTimeout = timeout;
        if (timeout != snap.Timeout) parts.Add($"timeout {snap.Timeout:0.#} -> {timeout:0.#} s");

        if (!hosting) return "joined as guest: game defaults restored";
        return string.Join(", ", parts);
    }

    private static bool SafeAoi(SimulationConfig sim)
    {
        try { return sim.AreaOfInterestEnabled; } catch { return false; }
    }

    /// <summary>
    /// The selection with the server send interval multiplied by <paramref name="divisor"/> (or the largest smaller
    /// divisor that Fusion accepts and that keeps at least <see cref="MinSendHz"/>). Falls back to the original.
    /// </summary>
    internal static TickRate.Selection WithSendDivisor(TickRate.Selection original, int divisor, out string note)
    {
        var baseSel = original;
        try
        {
            if (baseSel.ServerSendInterval <= 0) baseSel.ConvertObsoleteIndicesToIntervals();
            if (baseSel.Validate() != TickRate.ValidateResult.Ok)
            {
                note = "snapshot rate default (game selection didn't validate)";
                return original;
            }
            int baseInterval = Math.Max(1, baseSel.ServerSendInterval);
            int baseHz = TickRate.Resolve(baseSel).ServerSend;
            for (int k = divisor; k >= 2; k--)
            {
                var s = baseSel;
                s.ServerSendInterval = baseInterval * k;
                if (s.Validate() != TickRate.ValidateResult.Ok) continue;
                var r = TickRate.Resolve(s);
                if (r.ServerSend < MinSendHz) continue;
                note = $"snapshots {baseHz} -> {r.ServerSend} Hz";
                return s;
            }
            note = "snapshot rate default (no valid slower rate)";
        }
        catch (Exception e) { note = "snapshot rate default (" + e.Message + ")"; }
        return original;
    }

    internal static string Describe(TickRate.Selection sel)
    {
        try
        {
            var s = sel;
            if (s.ServerSendInterval <= 0) s.ConvertObsoleteIndicesToIntervals();
            if (s.Validate() != TickRate.ValidateResult.Ok) return "tick rate n/a";
            var r = TickRate.Resolve(s);
            return $"tick {r.Server} Hz, host snapshots {r.ServerSend} Hz, guest input {r.ClientSend} Hz";
        }
        catch { return "tick rate n/a"; }
    }

    /// <summary>The selection the next hosted game would start from (the game's own, if we changed it).</summary>
    internal static bool TryGetBaseSelection(out TickRate.Selection sel)
    {
        sel = default;
        try
        {
            var cfg = NetworkProjectConfig.Global;
            if (cfg?.Simulation == null) return false;
            sel = Originals.TryGetValue(cfg.Pointer, out var snap) ? snap.Selection : cfg.Simulation.TickRateSelection;
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Runs alongside the lobby-size StartGame prefix; Harmony runs both.</summary>
[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class NetPerfStartGamePatch
{
    private static void Prefix(StartGameArgs args)
    {
        try { NetPerfConfig.OnStartGame(args); }
        catch (Exception e) { Plugin.Logger.LogWarning("[NetPerf] StartGame prefix: " + e.Message); }
    }
}
