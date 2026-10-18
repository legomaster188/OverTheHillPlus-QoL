using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Logging;

/// <summary>
/// The cheap path of <c>DebugLog.Error</c> for a catch block that fails again and again (every frame): the first few
/// failures of one place (mod, context, exception type) take the full path, which builds the stack text and the
/// signature with the top frame, so several different frames are still told apart; after that each failure only
/// counts on the entry of the last one, with no string work and no allocation. Thread-safe. Pure.
/// </summary>
internal sealed class ErrorSites
{
    /// <summary>One place: how often it failed and the book entry of its latest distinct error.</summary>
    internal sealed class Site
    {
        public int Seen;
        public ErrorBook.Entry Last;
    }

    private readonly Dictionary<(string, string, Type), Site> _sites = new();
    private readonly int _fullPathCalls;
    private readonly int _capacity;
    private readonly object _lock = new();

    public ErrorSites(int fullPathCalls = 8, int capacity = 4000)
    {
        _fullPathCalls = Math.Max(1, fullPathCalls);
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Places tracked.</summary>
    public int Count
    {
        get { lock (_lock) return _sites.Count; }
    }

    /// <summary>
    /// Notes one failure of the place. True when it was only counted (a known place past its first calls): the caller
    /// is done. False when the caller must take the full path and then call <see cref="Remember"/> with the entry.
    /// </summary>
    public bool TryRepeat(ErrorBook book, string modId, string context, Type type, double now, out Site site)
    {
        site = null;
        if (type == null) return false;
        lock (_lock)
        {
            var key = (modId ?? "?", context ?? "", type);
            if (!_sites.TryGetValue(key, out site))
            {
                if (_sites.Count >= _capacity) { site = null; return false; }
                _sites[key] = site = new Site();
            }
            site.Seen++;
            if (site.Seen <= _fullPathCalls || site.Last == null) return false;
            book.Repeat(site.Last, now);
            return true;
        }
    }

    /// <summary>The full path's entry for the place (null when the book refused it).</summary>
    public void Remember(Site site, ErrorBook.Entry entry)
    {
        if (site == null || entry == null) return;
        lock (_lock) site.Last = entry;
    }
}
