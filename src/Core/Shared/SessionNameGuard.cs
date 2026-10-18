using System;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// Keeps a renamed session name readable by every player's server browser. The game cuts each listed name at fixed
/// positions (OverlandSessionInfo: the first 12 characters are the room-code hash, the display name starts at 13), so
/// a name shorter than 13 characters throws inside every viewer's list update, and a different prefix breaks
/// join-by-code. A rename is only published when the original has the game's "&lt;12 hex&gt;_" prefix and the new name
/// keeps it unchanged; otherwise the original name is used.
/// </summary>
public static class SessionNameGuard
{
    public const int HashLength = 12;
    public const int MinLength = HashLength + 1;

    /// <summary>True if <paramref name="name"/> starts with 12 hex digits and a '_' (the game's own format).</summary>
    public static bool HasGamePrefix(string name)
    {
        if (name == null || name.Length < MinLength || name[HashLength] != '_') return false;
        for (int i = 0; i < HashLength; i++)
            if (!Uri.IsHexDigit(name[i])) return false;
        return true;
    }

    /// <summary><paramref name="renamed"/> if it is safe to publish in place of <paramref name="original"/>, otherwise
    /// <paramref name="original"/>. <paramref name="why"/> says why a rename was refused (null when it was kept).</summary>
    public static string Pick(string original, string renamed, out string why)
    {
        why = null;
        if (renamed == original) return original;
        if (!HasGamePrefix(original)) { why = "the game's name has no room-code prefix"; return original; }
        if (renamed == null || renamed.Length < MinLength) { why = "the new name is too short"; return original; }
        if (string.CompareOrdinal(original, 0, renamed, 0, MinLength) != 0) { why = "the new name changes the room-code prefix"; return original; }
        return renamed;
    }
}
