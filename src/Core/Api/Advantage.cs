using OverTheHillPlus.Features.Public.Filter;

namespace OverTheHillPlus.Api;

/// <summary>
/// Steam achievements and fair play. Call <see cref="MarkUsed"/> the moment your mod gives the player something the
/// game wouldn't (moving or spawning cars, handing out items, changing physics...): achievements then stay locked for
/// the rest of the game session, whichever mods are installed.
/// </summary>
public static class Advantage
{
    /// <summary>Keeps Steam achievements locked until the game is restarted.</summary>
    public static void MarkUsed() => AdvantageFlag.MarkUsed();

    /// <summary>True once any mod called <see cref="MarkUsed"/> this game session.</summary>
    public static bool UsedThisSession => AdvantageFlag.UsedThisSession;
}

/// <summary>
/// The Mod Manager's word filter, the one used for player names and chat. Run any text a player typed through it
/// before your mod shows it to other players (for example text sent with <see cref="Net"/>).
/// </summary>
public static class TextFilter
{
    /// <summary>True when <paramref name="text"/> must not be shown to others; <paramref name="reason"/> says why.</summary>
    public static bool IsBlocked(string text, out string reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(text)) return false;
        return NameFilter.IsBlocked(text, out reason);
    }
}
