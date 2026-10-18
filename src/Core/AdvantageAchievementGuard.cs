using HarmonyLib;

namespace OverTheHillPlus;

/// <summary>Keeps achievements locked in a session where <see cref="AdvantageFlag.MarkUsed"/> was called.</summary>
[HarmonyPatch(typeof(AchievementManager), nameof(AchievementManager.Unlock))]
internal static class AdvantageAchievementGuard
{
    private static bool _logged;

    private static bool Prefix()
    {
        if (!AdvantageFlag.Holding) return true;
        if (!_logged)
        {
            _logged = true;
            Core.CorePlugin.Logger?.LogInfo("Achievement unlock held back: a mod changed the game this session.");
        }
        return false;
    }
}
