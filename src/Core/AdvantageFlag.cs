using System;

namespace OverTheHillPlus;

/// <summary>
/// Set once a mod gives the player something the game wouldn't (moved or spawned cars, handed-out items, changed
/// physics...): Steam achievements then stay locked for the rest of the game session (AdvantageAchievementGuard).
/// Shared by every mod that can do that, so achievements stay locked whichever of them is installed. Game-free (unit
/// tested).
/// </summary>
internal static class AdvantageFlag
{
    /// <summary>True once <see cref="MarkUsed"/> was called this game session.</summary>
    public static bool UsedThisSession { get; private set; }

    public static void MarkUsed() => UsedThisSession = true;

    /// <summary>
    /// Whether achievements are held back after <see cref="MarkUsed"/>. True unless a mod with a setting for it says
    /// otherwise (the safe default when that mod isn't installed or is switched off).
    /// </summary>
    public static Func<bool> HoldAchievements = () => true;

    /// <summary>True when achievements must stay locked right now.</summary>
    internal static bool Holding => Holds(UsedThisSession, HoldAchievements);

    /// <summary>
    /// The rule behind <see cref="Holding"/>: nothing is held before anything was used; after that achievements are
    /// held unless the hold setting says no. A missing or throwing setting holds (the safe side).
    /// </summary>
    internal static bool Holds(bool used, Func<bool> hold)
    {
        if (!used) return false;
        try { return hold == null || hold(); }
        catch (Exception ex) { DebugLog.Error("core", ex, "AdvantageFlag.Holds"); return true; }
    }
}
