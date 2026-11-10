using System;

namespace OverTheHillPlus.Logging;

/// <summary>
/// When an overlay hook has nothing to do, core skips it entirely (no crash-trail step, no timing, no GUI reset).
/// <see cref="RepaintOnly"/>: the hook only draws in the Repaint event (it starts with a Repaint check). <see cref="Wants"/>:
/// a pure managed check, false when the hook would return right away. A hook that handles input or needs the Layout
/// event (buttons, GUILayout, text fields) must not be repaint-only. Pure.
/// </summary>
internal sealed class GuiGate
{
    public readonly Func<bool> Wants;
    public readonly bool RepaintOnly;

    public GuiGate(Func<bool> wants, bool repaintOnly)
    {
        Wants = wants;
        RepaintOnly = repaintOnly;
    }

    /// <summary>True when the hook can be skipped in this event. A check that throws counts as "wanted": the hook runs.</summary>
    public bool Skips(bool repaint)
    {
        if (RepaintOnly && !repaint) return true;
        if (Wants == null) return false;
        try { return !Wants(); }
        catch { return false; }
    }
}

/// <summary>
/// The subscribers of one multicast delegate with one entry made for each, rebuilt only when the delegate instance
/// changes (a += or -= makes a new instance). Saves the per-frame GetInvocationList array and per-subscriber reflection.
/// </summary>
internal sealed class InvocationCache<T> where T : class
{
    private readonly Func<Delegate, T> _make;
    private Delegate _source;
    private T[] _items = Array.Empty<T>();

    /// <summary>How many times the list was rebuilt (for tests).</summary>
    public int Rebuilds { get; private set; }

    public InvocationCache(Func<Delegate, T> make) => _make = make;

    public T[] Get(Delegate hook)
    {
        if (ReferenceEquals(hook, _source)) return _items;
        Rebuilds++;
        if (hook == null) _items = Array.Empty<T>();
        else
        {
            var list = hook.GetInvocationList();
            var items = new T[list.Length];
            for (int i = 0; i < list.Length; i++) items[i] = _make(list[i]);
            _items = items;
        }
        _source = hook;
        return _items;
    }
}
