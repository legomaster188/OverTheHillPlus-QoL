using System;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.HostGovernor;

/// <summary>
/// Host performance governor (PLUS > Network > "Hosting" card). In Host mode this PC simulates every car, so when
/// its frame rate drops the whole lobby feels it. Everything here is local to the host's PC and opt-in:
///
/// - Auto quality: while hosting with at least N players, measures frame time (and whether the Fusion simulation
///   keeps up with its tick rate) and steps render scale, shadow distance and LOD bias down when the PC can't keep
///   the target, back up when there is headroom again. Values are lowered as deltas on top of the current settings
///   and put back exactly when hosting ends, players leave or the option is turned off. Values another feature
///   overrides (Graphics tab, QoL FPS cap, upscalers) are left alone.
/// - Host FPS cap: an optional render FPS ceiling while governing, so rendering doesn't starve the simulation.
/// - Host priority: raises this game's process priority while hosting (restored when hosting ends).
/// - A read-out of frame time, tick rate and Fusion simulation stats.
/// </summary>
internal sealed class HostGovernorFeature : IFeature
{
    internal const string Section = "Host Governor";

    internal static ConfigEntry<bool> Enabled, ScaleRender, ScaleShadows, ScaleLod, Preempt, Priority;
    internal static ConfigEntry<float> TargetFps;
    internal static ConfigEntry<int> MinPlayers, MaxLevel, FpsCap;
    /// <summary>Lowest host FPS cap actually applied (lower values are raised to this).</summary>
    internal const int MinFpsCap = 30;
    internal static ConfigEntry<string> PriorityLevel;

    public void Register()
    {
        var f = ModConfig.File;
        Enabled = f.Bind(Section, "AutoQuality", false,
            "While hosting with enough players, lower render scale / shadow distance / LOD bias step by step when the frame rate " +
            "drops below the target, and raise them again when there is headroom. Your settings come back when you stop hosting.");
        TargetFps = f.Bind(Section, "TargetFps", 50f,
            new ConfigDescription("Frame rate the governor tries to keep while hosting.", new AcceptableValueRange<float>(25f, 144f)));
        MinPlayers = f.Bind(Section, "MinPlayers", 4,
            new ConfigDescription("Only govern when at least this many players (you included) are in your session.", new AcceptableValueRange<int>(1, 16)));
        MaxLevel = f.Bind(Section, "MaxLevel", 3,
            new ConfigDescription("How far quality may be lowered: 1 = a little (90% render scale) ... 5 = a lot (65% render scale, 35% shadow distance).",
                new AcceptableValueRange<int>(1, GovernorRuntime.Levels)));
        Preempt = f.Bind(Section, "PreemptBigLobbies", true,
            "Start one step lower for every 6 players above the minimum (before the frame rate drops).");
        ScaleRender = f.Bind(Section, "ScaleRenderScale", true, "The governor may lower the URP render scale.");
        ScaleShadows = f.Bind(Section, "ScaleShadowDistance", true, "The governor may lower the sun shadow distance.");
        ScaleLod = f.Bind(Section, "ScaleLodBias", true, "The governor may lower the LOD bias (simpler models sooner).");
        FpsCap = f.Bind(Section, "HostFpsCap", 0,
            new ConfigDescription("Render FPS ceiling while the governor is active (0 = none). Only ever lowers the game's cap; not used with VSync on.",
                new AcceptableValueRange<int>(0, 240)));
        Priority = f.Bind(Section, "HostPriority", false,
            "Raise this game's process priority while you are hosting (restored when you stop hosting or quit).");
        PriorityLevel = f.Bind(Section, "HostPriorityLevel", "AboveNormal",
            new ConfigDescription("Priority used by HostPriority.", new AcceptableValueList<string>("AboveNormal", "High")));

        Registry.Section(Registry.PlusMode, "Network", Draw, 5);
        Registry.OnTick(GovernorRuntime.Tick);
    }

    // ---------------------------------------------------------------- menu

    private static void IntSlider(ConfigEntry<int> e, string label, int min, int max, Func<int, string> shown, bool enabled)
    {
        int v = Mathf.RoundToInt(Menu.RawSlider(label, e.Value, min, max, shown(e.Value), enabled, keyStep: 1f));
        if (v != e.Value) e.Value = Mathf.Clamp(v, min, max);
    }

    private static void Draw()
    {
        var g = GovernorRuntime.Snapshot;

        Menu.Card("Hosting", "Keeps the session smooth when you host a big lobby. Everything here only changes your own PC.", () =>
        {
            Menu.KeyValue("Governor", g.StateLine);
            if (g.Hosting || g.Frames > 0)
            {
                float target = Math.Max(1f, g.TargetMs);
                Color c = g.AvgMs <= target * 1.05f ? Theme.Good : g.AvgMs <= target * 1.25f ? Theme.Warn : Theme.Bad;
                Menu.Bar("Frame time", g.AvgMs > 0f ? Mathf.Clamp01(target / g.AvgMs) : 0f, c,
                    g.AvgMs > 0f ? $"{g.AvgMs:0.0} ms" : "-");
                Menu.KeyValue("Frames", g.FrameLine);
            }
            if (g.Hosting)
            {
                Menu.KeyValue("Simulation", g.TickLine);
                if (!string.IsNullOrEmpty(g.FusionLine)) Menu.Small(g.FusionLine);
            }
            if (!string.IsNullOrEmpty(g.PriorityLine)) Menu.KeyValue("Priority", g.PriorityLine);
        });

        Menu.Card("Auto quality while hosting", "Steps graphics down when your PC can't keep up, back up when it can", () =>
        {
            bool on = Enabled.Value;
            Menu.Switch(Enabled, "Auto quality", on ? "On: only while you host with enough players" : "Off: your graphics settings are never touched", true);
            Menu.SliderRow(TargetFps, "Target FPS", 25f, 144f, "0", " fps", on);
            IntSlider(MinPlayers, "From players", 1, 16, v => v <= 1 ? "always" : v.ToString(), on);
            IntSlider(MaxLevel, "Lowest quality", 1, GovernorRuntime.Levels, v => GovernorRuntime.LevelName(v), on);
            Menu.Switch(Preempt, "Pre-empt big lobbies", "One step lower per 6 players above the minimum", on);
            Menu.Switch(ScaleRender, "Render scale", "Biggest win on GPU-bound PCs", on);
            Menu.Switch(ScaleShadows, "Shadow distance", "Fewer shadow casters to draw", on);
            Menu.Switch(ScaleLod, "LOD bias", "Simpler models sooner", on);
            IntSlider(FpsCap, "Host FPS cap", 0, 240, v => v <= 0 ? "off" : Math.Max(MinFpsCap, v) + " fps", on);
            if (on)
            {
                foreach (var (name, status) in g.Knobs)
                    if (!string.IsNullOrEmpty(status)) Menu.KeyValue(name, status);
            }
            Menu.Small("Values another setting overrides (Graphics tab render scale, LOD and shadow overrides, the QoL FPS cap, " +
                       "DLSS/FSR upscalers, VSync) are left to that setting. Everything comes back when you stop hosting, players leave " +
                       "or you switch this off.");
        });

        Menu.Card("Host priority", "Gives the game more CPU time than background apps while you host", () =>
        {
            Menu.Switch(Priority, "Raise priority while hosting", "Restored when you stop hosting or quit", true);
            string cur = PriorityLevel.Value;
            Menu.Chips(2,
                ((cur == "AboveNormal" ? "> " : "") + "Above normal", () => { PriorityLevel.Value = "AboveNormal"; Menu.SetStatus("Host priority: above normal"); }, Priority.Value),
                ((cur == "High" ? "> " : "") + "High", () => { PriorityLevel.Value = "High"; Menu.SetStatus("Host priority: high"); }, Priority.Value));
            Menu.Small("Above normal is usually enough. High can make other apps (voice chat, streaming) stutter on PCs with few cores.");
        });
    }
}
