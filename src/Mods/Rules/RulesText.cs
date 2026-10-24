using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Rules;

/// <summary>Text helpers for the welcome message, the rules and announcements (pure; compiled by the tests).</summary>
internal static class RulesText
{
    /// <summary>The template with {name} (the player who joined) and {host} (you) filled in, trimmed.</summary>
    public static string Format(string template, string name, string host) =>
        (template ?? "").Replace("{name}", name ?? "").Replace("{host}", host ?? "").Trim();

    /// <summary>
    /// The lines worth sending: trimmed, blank ones dropped, cut to <paramref name="maxChars"/>, ones that fail
    /// <paramref name="blocked"/> (the word filter) dropped, at most <see cref="RulesWire.MaxRules"/>.
    /// </summary>
    public static List<string> Lines(IEnumerable<string> raw, int maxChars, Func<string, bool> blocked)
    {
        var list = new List<string>();
        if (raw == null) return list;
        foreach (string r in raw)
        {
            if (list.Count >= RulesWire.MaxRules) break;
            string s = Clip((r ?? "").Trim(), maxChars);
            if (s.Length == 0 || (blocked != null && blocked(s))) continue;
            list.Add(s);
        }
        return list;
    }

    /// <summary>A rule as a notice for players without the mod: "Rule 2: No ramming".</summary>
    public static string RuleNotice(int index, string rule) => $"Rule {index + 1}: {rule}";

    /// <summary>At most <paramref name="max"/> UTF-16 units, never cutting an emoji in half.</summary>
    public static string Clip(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        int n = max;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s.Substring(0, n).TrimEnd();
    }
}

/// <summary>Timed announcements: one message every interval, in turn, skipping blank ones (pure).</summary>
internal sealed class Announcer
{
    private float _due;
    private int _next;
    private bool _started;

    /// <summary>Starts counting from <paramref name="now"/> (a new session, or the list was switched on).</summary>
    public void Reset(float now)
    {
        _due = now;
        _next = 0;
        _started = true;
    }

    /// <summary>The message to send now, or null. The first one goes out one interval after <see cref="Reset"/>.</summary>
    public string Next(float now, float intervalSeconds, IReadOnlyList<string> messages)
    {
        if (!_started || intervalSeconds <= 0f || messages == null || messages.Count == 0) return null;
        if (now < _due + intervalSeconds) return null;
        for (int i = 0; i < messages.Count; i++)
        {
            int at = (_next + i) % messages.Count;
            string m = messages[at]?.Trim();
            if (string.IsNullOrEmpty(m)) continue;
            _next = at + 1;
            _due = now;
            return m;
        }
        return null;
    }
}
