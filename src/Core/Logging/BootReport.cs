using System;
using System.Diagnostics;

namespace OverTheHillPlus.Logging;

/// <summary>
/// One block in the log for every game start: "[Core] start-up: ..." (<see cref="BootLedger"/>), written when the last car
/// stands, so a start can be compared with the one before without digging through timestamps.
/// </summary>
internal static class BootReport
{
    private static readonly BootLedger Ledger = new();
    private static readonly double Freq = Stopwatch.Frequency;
    private static bool _written;

    private static double Ms(long ticks) => ticks * 1000.0 / Freq;

    /// <summary>Seconds since the game's process started.</summary>
    private static double Now
    {
        get
        {
            try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds; }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>A mod's Load is over (ticks of Stopwatch).</summary>
    public static void Loaded(string name, long ticks) => Ledger.Loaded(name, Ms(ticks));

    public static void Patched(string modId, long ticks) => Ledger.Patched(modId, Ms(ticks));

    /// <summary>A milestone of the start (only its first time counts).</summary>
    public static void Mark(string what)
    {
        if (!_written && !Ledger.Has(what)) Ledger.Mark(what, Now);
    }

    /// <summary>Everything is up: writes the block.</summary>
    public static void Write()
    {
        if (_written) return;
        Mark("everything up");
        _written = true;
        Core.CorePlugin.Logger?.LogInfo("[Core] " + Ledger.Text() +
            $" Read ahead: {Cars.AssetPrefetch.ModelsRead} models, {Cars.AssetPrefetch.TexturesDecoded} textures.");
    }
}
