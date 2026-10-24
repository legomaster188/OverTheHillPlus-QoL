using System.Collections.Generic;

namespace OverTheHillPlus.Horns;

/// <summary>Horns+'s small text and log helpers.</summary>
internal static class HornsUtil
{
    private static readonly HashSet<string> Logged = new();

    /// <summary>Cut to <paramref name="max"/> characters, with angle brackets swapped for look-alikes so no rich-text tag applies.</summary>
    public static string Short(string s, int max)
    {
        s = (s ?? "").Replace('<', '‹').Replace('>', '›');
        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    /// <summary>A warning logged once per key (at most 100 keys).</summary>
    public static void LogOnce(string key, string message)
    {
        if (Logged.Count < 100 && Logged.Add(key)) HornsPlugin.Logger.LogWarning(message);
    }
}
