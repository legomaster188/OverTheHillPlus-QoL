using System;
using System.Collections.Generic;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// The native pointers of objects we made (our FunTextTMP clones): the patches on the game's component check it so
/// they only change what happens to our own objects. Main thread only.
/// </summary>
internal sealed class PointerSet
{
    private readonly HashSet<IntPtr> _set = new();

    internal int Count => _set.Count;

    internal void Add(IntPtr p) { if (p != IntPtr.Zero) _set.Add(p); }

    internal bool Contains(IntPtr p) => p != IntPtr.Zero && _set.Contains(p);

    internal void Remove(IntPtr p) => _set.Remove(p);
}
