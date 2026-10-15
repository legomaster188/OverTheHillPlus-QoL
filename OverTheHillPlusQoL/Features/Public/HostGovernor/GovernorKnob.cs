using System;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.HostGovernor;

/// <summary>
/// One graphics value the governor may lower while it is active, as a delta on top of whatever the value is now.
///
/// Rules that keep it from fighting the game or the other graphics features:
/// - The value found when the governor first touches it is the "base"; the governor writes base * factor (or a
///   cap below the base) and remembers what it wrote.
/// - If the value read back differs from what was written, something else changed it (game options menu, quality
///   preset, another feature): that becomes the new base. If that happens 3 times within 30 s the governor backs
///   off from this value for 2 minutes instead of trading writes with an unknown owner.
/// - When another feature explicitly overrides the value (see <see cref="OwnerCheck"/>) the governor leaves it alone.
///   If that feature started overriding while the governor's value was in place, the governor remembers its own
///   value and base: when the other feature lets go and puts "its" baseline back (which is the governor's value),
///   the governor takes the slot back and restores the real base when it releases.
/// - Releasing writes the base back only if the live value is still exactly what the governor wrote.
/// </summary>
internal sealed class GovernorKnob
{
    public readonly string Name;
    private readonly Func<float> _get;
    private readonly Action<float> _set;
    private readonly bool _isCap;

    /// <summary>Returns a short description of who else owns this value right now, or null when it is free.</summary>
    public Func<string> OwnerCheck;

    /// <summary>Called after a write that changed the value (e.g. refresh GPU instancer parameters).</summary>
    public Action AfterWrite;

    private bool _has;
    private float _base, _written;

    // Taken over by another feature while our value was live.
    private bool _orphan;
    private float _orphanBase, _orphanWritten;

    private int _rebases;
    private float _rebaseWindowStart, _blockedUntil;
    private bool _logged;

    // ---- read by the menu ----
    public string Status { get; private set; } = "";
    public bool Driving => _has;
    public float Base => _base;
    public float Written => _written;

    /// <param name="isCap">True: the parameter passed to <see cref="Apply"/> is an absolute ceiling (FPS cap),
    /// otherwise a multiplier of the base.</param>
    public GovernorKnob(string name, Func<float> get, Action<float> set, bool isCap = false)
    {
        Name = name;
        _get = get;
        _set = set;
        _isCap = isCap;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) <= 0.001f + Math.Abs(b) * 0.0005f;

    private void LogOnce(string what, Exception e)
    {
        if (_logged) return;
        _logged = true;
        Plugin.Logger.LogWarning($"[HostGovernor] {Name}: {what} failed (logged once): {e.Message}");
    }

    /// <summary>
    /// Drives the value. <paramref name="param"/> is the multiplier (1 = untouched) or, for a cap, the ceiling
    /// (0 or less = no cap). <paramref name="want"/> false releases it.
    /// </summary>
    public void Apply(bool want, float param)
    {
        try { ApplyInner(want, param); }
        catch (Exception e)
        {
            LogOnce("apply", e);
            _has = false;
            _orphan = false;
            Status = "error (see log)";
        }
    }

    private void ApplyInner(bool want, float param)
    {
        float now = Time.unscaledTime;
        string other = OwnerCheck?.Invoke();
        if (other != null)
        {
            if (_has)
            {
                // Someone else now owns it and may have taken our value as "its" baseline (or saved it to put back
                // later, like the background FPS cap): remember ours, so it can be recognised and undone later.
                if (!Near(_written, _base))
                {
                    _orphan = true;
                    _orphanBase = _base;
                    _orphanWritten = _written;
                }
                _has = false;
            }
            Status = "left to " + other;
            return;
        }

        if (_orphan)
        {
            float live = _get();
            if (Near(live, _orphanWritten))
            {
                // The other feature let go and put our value back as "its" baseline: take the slot back.
                _has = true;
                _base = _orphanBase;
                _written = live;
            }
            _orphan = false;
        }

        bool active = want && now >= _blockedUntil && (_isCap ? param > 0f : !Near(param, 1f));
        if (!active)
        {
            Release();
            if (now < _blockedUntil) Status = $"paused: something else keeps changing it (retry in {_blockedUntil - now:0} s)";
            else Status = want ? "at your setting" : "";
            return;
        }

        float cur = _get();
        if (!_has)
        {
            _has = true;
            _base = cur;
            _written = cur;
            _rebases = 0;
        }
        else if (!Near(cur, _written))
        {
            _base = cur; // the game (or something else) changed it: follow, re-apply on top
            if (now - _rebaseWindowStart > 30f) { _rebaseWindowStart = now; _rebases = 0; }
            if (++_rebases >= 3)
            {
                Plugin.Logger.LogInfo($"[HostGovernor] {Name} keeps being changed by something else; leaving it alone for 2 minutes.");
                _has = false; // the value isn't ours: nothing to restore
                _blockedUntil = now + 120f;
                Status = "paused: something else keeps changing it";
                return;
            }
        }

        float target = Target(_base, param);
        if (!Near(cur, target))
        {
            _set(target);
            cur = _get();
            AfterWrite?.Invoke();
        }
        _written = cur;
        Status = Describe();
    }

    private float Target(float b, float param)
    {
        if (_isCap)
        {
            // Only ever lowers: an uncapped base (0 or -1) or a higher one becomes the cap.
            return b <= 0f || b > param ? param : b;
        }
        return b * param;
    }

    private string Describe()
    {
        if (_isCap)
        {
            string b = _base <= 0f ? "uncapped" : _base.ToString("0");
            return Near(_written, _base) ? $"{b} (already at or below)" : $"{b} -> {_written:0}";
        }
        return Near(_written, _base) ? $"{_base:0.##}" : $"{_base:0.##} -> {_written:0.##}";
    }

    /// <summary>Puts the base back if the live value is still ours.</summary>
    public void Release()
    {
        if (!_has) return;
        _has = false;
        try
        {
            float live = _get();
            if (Near(live, _written) && !Near(live, _base))
            {
                _set(_base);
                AfterWrite?.Invoke();
            }
        }
        catch (Exception e) { LogOnce("restore", e); }
    }

    /// <summary>Forget everything (also the orphan memory) after restoring what is ours.</summary>
    public void ReleaseAll()
    {
        Release();
        _orphan = false;
        Status = "";
    }
}
