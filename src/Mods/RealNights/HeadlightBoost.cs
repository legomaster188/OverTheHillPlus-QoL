using System;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheHillPlus.RealNights;

/// <summary>
/// Makes the headlights shine brighter and farther as it gets dark (HeadlightBoost), so they stay useful on a night
/// that is darker than the game's. Each headlight keeps the game's own value as its base: whatever the game last set
/// (switching on, a tween, a quality change) becomes the new base, and the boost is applied on top, ten times a second.
/// At boost 1, or by day, the base is written back and the light is left alone.
/// </summary>
internal static class HeadlightBoost
{
    private const float Every = 0.1f;

    private sealed class Entry
    {
        public UnityEngine.Light Light;
        public float BaseIntensity, BaseRange, WroteIntensity, WroteRange;
    }

    private static readonly Dictionary<IntPtr, Entry> Entries = new();
    private static readonly List<IntPtr> Dead = new();
    private static float _next;

    internal static int Count => Entries.Count;

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _next) return;
        _next = now + Every;

        float boost = Mathf.Clamp(RealNightsConfig.HeadlightBoost.Value, 0.5f, 3f);
        float mul = Mathf.Lerp(1f, boost, NightSky.Look.Lamp);
        if (Mathf.Abs(mul - 1f) < 0.001f && Entries.Count == 0) return;
        CarScan.Tick();

        foreach (var car in CarScan.Cars)
        {
            if (!CarScan.Alive(car.Lights)) continue;
            var lights = car.Lights.headlights?.Lights;
            if (lights == null) continue;
            for (int i = 0; i < lights.Count; i++)
            {
                var l = lights[i];
                if (!CarScan.Alive(l)) continue;
                Apply(l, mul);
            }
        }

        Dead.Clear();
        foreach (var kv in Entries)
            if (!CarScan.Alive(kv.Value.Light)) Dead.Add(kv.Key);
        foreach (var k in Dead) Entries.Remove(k);
    }

    private static void Apply(UnityEngine.Light l, float mul)
    {
        float intensity = l.intensity, range = l.range;
        if (!Entries.TryGetValue(l.Pointer, out var e))
        {
            if (Mathf.Abs(mul - 1f) < 0.001f) return;
            e = new Entry { Light = l, BaseIntensity = intensity, BaseRange = range, WroteIntensity = intensity, WroteRange = range };
            Entries[l.Pointer] = e;
        }
        else
        {
            // A value that is not the one written was set by the game: that is the base now.
            if (!Same(intensity, e.WroteIntensity)) e.BaseIntensity = intensity;
            if (!Same(range, e.WroteRange)) e.BaseRange = range;
        }
        float wantI = e.BaseIntensity * mul, wantR = e.BaseRange * mul;
        if (!Same(intensity, wantI)) l.intensity = wantI;
        if (!Same(range, wantR)) l.range = wantR;
        e.WroteIntensity = wantI;
        e.WroteRange = wantR;
        if (Mathf.Abs(mul - 1f) < 0.001f) Entries.Remove(l.Pointer); // back to the game's values: nothing more to keep
    }

    private static bool Same(float a, float b) => Math.Abs(a - b) <= 1e-4f * Math.Max(1f, Math.Abs(a));
}
