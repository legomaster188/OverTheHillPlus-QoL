using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.Gfx;

/// <summary>
/// Keeps a game value at a mod-chosen target without losing the game's own value.
///
/// Each driven value gets a slot keyed by owner object + name. The slot remembers the game's value (the "base")
/// and what we last wrote. If the value read back differs from what we wrote, the game changed it (settings menu,
/// quality preset, scene load), so that becomes the new base and the target is re-applied on top of it.
/// When the override is switched off the base is written back, but only if the value is still ours.
/// A value that refuses to take (unsupported mode, clamped by the engine) is not retried until the target or
/// the game's value changes, so nothing is hammered every second.
/// </summary>
internal static class LightingDriver
{
    private abstract class SlotBase
    {
        public bool Touched;
        public Object Owner;
        public Func<bool> Wanted;
        public abstract void Restore();
    }

    private sealed class Slot<T> : SlotBase
    {
        public T Base, Written, Stuck;
        public bool HasStuck;
        public int Rebases;
        public Func<T> Get;
        public Action<T> Set;
        public Func<T, T, bool> Eq;

        public override void Restore()
        {
            if (Eq(Get(), Written)) Set(Base);
        }
    }

    private static readonly Dictionary<string, SlotBase> Slots = new();
    private static readonly HashSet<string> Logged = new();

    public static int Count => Slots.Count;

    public static string Key(string name, Object owner)
    {
        long p = 0;
        try { p = owner == null ? 0 : owner.Pointer.ToInt64(); } catch { }
        return name + ":" + p.ToString("X");
    }

    /// <summary>
    /// Drives one value. <paramref name="wanted"/> says whether the override is on (also used later to restore
    /// slots whose owner is no longer visited), <paramref name="target"/> maps the game's base to our value.
    /// </summary>
    public static void Drive<T>(string name, Object owner, Func<bool> wanted, Func<T> get, Action<T> set,
        Func<T, T> target, Func<T, T, bool> eq)
    {
        string key = Key(name, owner);
        try
        {
            Slots.TryGetValue(key, out var sb);
            var s = sb as Slot<T>;
            if (!wanted())
            {
                if (sb != null)
                {
                    Slots.Remove(key);
                    sb.Restore();
                }
                return;
            }

            T cur = get();
            if (s == null)
            {
                s = new Slot<T> { Base = cur, Written = cur, Get = get, Set = set, Eq = eq, Owner = owner, Wanted = wanted };
                Slots[key] = s;
            }
            else if (!eq(cur, s.Written))
            {
                s.Base = cur; // the game changed it underneath us
                s.HasStuck = false;
                if (++s.Rebases == 5)
                    LogOnce(key + "fight", $"[Graphics] the game keeps resetting {name}; the override may flicker.");
            }
            else s.Rebases = 0;
            s.Touched = true;

            T t = target(s.Base);
            if (eq(cur, t))
            {
                s.Written = cur;
                return;
            }
            if (s.HasStuck && eq(s.Stuck, t)) return;

            set(t);
            T after = get();
            s.Written = after;
            if (!eq(after, t))
            {
                s.Stuck = t;
                s.HasStuck = true;
                LogOnce(key + "stuck", $"[Graphics] {name} did not take value {t} (now {after}); not retrying until something changes.");
            }
        }
        catch (Exception e)
        {
            LogOnce(key, $"[Graphics] {name} failed: {e.Message}");
        }
    }

    /// <summary>Convenience for VolumeParameter floats: drives (value, overrideState) together.</summary>
    public static void DriveParam(string name, Object owner, Func<bool> wanted,
        Func<float> getV, Action<float> setV, Func<bool> getOv, Action<bool> setOv, Func<float, float> target)
    {
        Drive<(float v, bool ov)>(name, owner, wanted,
            () => (getV(), getOv()),
            x => { setOv(x.ov); setV(x.v); },
            b => (target(b.v), true),
            (a, b) => a.ov == b.ov && Mathf.Abs(a.v - b.v) < 0.0005f);
    }

    /// <summary>Call after a full pass: forgets dead owners, restores slots whose override was switched off.</summary>
    public static void EndPass()
    {
        List<string> remove = null;
        foreach (var kv in Slots)
        {
            var s = kv.Value;
            if (s.Touched) { s.Touched = false; continue; }
            bool alive;
            try { alive = s.Owner != null && s.Owner.Pointer != IntPtr.Zero && s.Owner; }
            catch { alive = false; }
            if (!alive) { (remove ??= new()).Add(kv.Key); continue; }
            bool wanted;
            try { wanted = s.Wanted(); } catch { wanted = false; }
            if (wanted) continue;
            try { s.Restore(); } catch { }
            (remove ??= new()).Add(kv.Key);
        }
        if (remove != null) foreach (var k in remove) Slots.Remove(k);
    }

    public static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) Plugin.Logger.LogWarning(message);
    }
}
