using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using OverTheHillPlus.Features.Public;
using OverTheHillPlus.HostPowers;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.TrackBuilder;

/// <summary>
/// MODS > Host Powers > Build (host only): ramps, bridges and single planks from the game's portable planks, your winch
/// anchor, and driverless cars (wall, parking row, slalom, single), all placed in front of your car through the game's
/// own networked calls so every player sees them. Each build is one undo step; Clear removes everything it placed.
/// </summary>
internal sealed class TrackBuilderFeature : IFeature
{
    private const string Section = "TrackBuilder";

    internal static ConfigEntry<float> Distance;
    internal static ConfigEntry<float> RampAngle, RampLanes, RampSegments;
    internal static ConfigEntry<float> BridgePlanks, BridgeLanes;
    internal static ConfigEntry<bool> BridgeLevel;
    internal static ConfigEntry<float> LaneGap;
    internal static ConfigEntry<float> CarCount, CarSpacing, SlalomGap, SlalomOffset, ParkingOffset;
    internal static ConfigEntry<string> CarModel;
    internal static ConfigEntry<bool> MixModels;
    internal static ConfigEntry<float> MaxPlanks, MaxCars;
    internal static ConfigEntry<bool> ShowPreview;

    private static BuildPattern _pattern = BuildPattern.Ramp;

    // preview (planned in Tick at most 5x a second while the tab is on screen, drawn in OnGUI)
    private static readonly List<Piece> Preview = new();
    private static string _previewError;
    private static float _tabSeenAt = -100f, _nextPreview;
    private static Camera _cam;
    private static float _nextCam;

    public void Register()
    {
        var cfg = HostPowersConfig.File;
        Distance = Range(cfg, "Distance", 10f, 3f, 60f, "How far in front of your car a build starts (m).");
        RampAngle = Range(cfg, "RampAngle", 15f, 5f, 35f, "Ramp: slope angle (degrees).");
        RampLanes = Range(cfg, "RampLanes", 3f, 1f, 6f, "Ramp: planks side by side.");
        RampSegments = Range(cfg, "RampSegments", 1f, 1f, 4f, "Ramp: planks end to end (longer, higher ramp).");
        BridgePlanks = Range(cfg, "BridgePlanks", 4f, 1f, 16f, "Bridge: planks end to end.");
        BridgeLanes = Range(cfg, "BridgeLanes", 3f, 1f, 4f, "Bridge: planks side by side.");
        BridgeLevel = cfg.Bind(Section, "BridgeLevel", false, "Bridge: stay level at the start height instead of reaching the ground at the far end.");
        LaneGap = Range(cfg, "LaneGap", 0.05f, 0f, 2.5f, "Gap between side-by-side planks (m). About 0.9 m with 2 lanes gives two wheel tracks.");
        CarCount = Range(cfg, "CarCount", 5f, 1f, 12f, "Wall / parking row / slalom: number of cars.");
        CarSpacing = Range(cfg, "CarSpacing", 2.8f, 2f, 8f, "Wall / parking row: distance between cars (m).");
        SlalomGap = Range(cfg, "SlalomGap", 14f, 6f, 40f, "Slalom: distance between cars along your path (m).");
        SlalomOffset = Range(cfg, "SlalomOffset", 2.5f, 0f, 8f, "Slalom: sideways offset, alternating left and right (m).");
        ParkingOffset = Range(cfg, "ParkingOffset", 5f, -15f, 15f, "Parking row: sideways offset of the row (m, positive = right).");
        CarModel = cfg.Bind(Section, "CarModel", "", "Car model used for builder cars (catalog name; empty = first).");
        MixModels = cfg.Bind(Section, "MixModels", true, "Use a different model for each car of a pattern.");
        MaxPlanks = Range(cfg, "MaxPlanks", 40f, 4f, 120f, "Most builder planks alive at once.");
        MaxCars = Range(cfg, "MaxCars", 12f, 1f, 24f, "Most builder cars alive at once (each one is a full physics car on the host).");
        ShowPreview = cfg.Bind(Section, "ShowPreview", true, "While the Build tab is open, mark where the selected build would go.");

        Registry.Section(Registry.PlusMode, "Build", DrawTab);
        Registry.OnTick(Tick);
        Registry.OnGui(DrawPreview);
    }

    private static ConfigEntry<float> Range(ConfigFile cfg, string key, float def, float min, float max, string desc) =>
        cfg.Bind(Section, key, def, new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));

    // ---- per frame ----

    private static void Tick()
    {
        BuildPlacer.Tick();
        bool tabOpen = Time.unscaledTime - _tabSeenAt < 0.5f;
        if (!tabOpen || !ShowPreview.Value)
        {
            if (Preview.Count > 0) Preview.Clear();
            return;
        }
        if (Time.unscaledTime < _nextPreview) return;
        _nextPreview = Time.unscaledTime + 0.2f;
        try { _previewError = BuildPlanner.Plan(_pattern, Preview); }
        catch (Exception e)
        {
            DebugLog.Error("hostpowers", e, "TrackBuilderFeature.Tick");
            Preview.Clear();
            _previewError = "Preview failed: " + e.Message;
        }
    }

    private static void Build()
    {
        var pieces = new List<Piece>();
        string err;
        try { err = BuildPlanner.Plan(_pattern, pieces); }
        catch (Exception e) { err = "Planning failed: " + e.Message; HostPowersPlugin.Logger.LogError("[Build] " + e); }
        Menu.SetStatus(err != null ? "Failed: " + err : BuildPlacer.Start(_pattern, pieces));
    }

    // ---- menu ----

    private static string Mark(BuildPattern p) => (p == _pattern ? "• " : "") + BuildPlanner.Name(p);

    private static (string, Action, bool) Pick(BuildPattern p) => (Mark(p), () => { _pattern = p; _nextPreview = 0f; }, true);

    private static void DrawTab()
    {
        if (!MenuUx.Indexing) _tabSeenAt = Time.unscaledTime;
        string block = BuildPlacer.BlockReason(_pattern);
        if (block != null) Menu.Banner(block, Theme.Info);

        Menu.Card("Build", "Pieces go in front of your car and everyone in the session sees them. Each build is one undo step.", () =>
        {
            Menu.Small("Planks");
            Menu.Chips(4, Pick(BuildPattern.Ramp), Pick(BuildPattern.Bridge), Pick(BuildPattern.Plank), Pick(BuildPattern.Anchor));
            Menu.Small("Driverless cars");
            Menu.Chips(4, Pick(BuildPattern.Wall), Pick(BuildPattern.Parking), Pick(BuildPattern.Slalom), Pick(BuildPattern.Car));
        });

        Menu.Card(BuildPlanner.Name(_pattern), Describe(_pattern), () =>
        {
            Menu.SliderRow(Distance, "Distance ahead", 3f, 60f, "0", " m", true);
            switch (_pattern)
            {
                case BuildPattern.Ramp:
                    Menu.SliderRow(RampAngle, "Angle", 5f, 35f, "0", "°", true);
                    IntSlider(RampLanes, "Planks wide", 1, 6);
                    IntSlider(RampSegments, "Planks long", 1, 4);
                    Menu.SliderRow(LaneGap, "Gap between planks", 0f, 2.5f, "0.00", " m", RampLanes.Value > 1.5f);
                    break;
                case BuildPattern.Bridge:
                    IntSlider(BridgePlanks, "Planks long", 1, 16);
                    IntSlider(BridgeLanes, "Planks wide", 1, 4);
                    Menu.SliderRow(LaneGap, "Gap between planks", 0f, 2.5f, "0.00", " m", BridgeLanes.Value > 1.5f);
                    Menu.Switch(BridgeLevel, "Keep it level", "Stay at the start height instead of meeting the ground at the far end", true);
                    break;
                case BuildPattern.Wall:
                    IntSlider(CarCount, "Cars", 1, 12);
                    Menu.SliderRow(CarSpacing, "Spacing", 2f, 8f, "0.0", " m", true);
                    break;
                case BuildPattern.Parking:
                    IntSlider(CarCount, "Cars", 1, 12);
                    Menu.SliderRow(CarSpacing, "Spacing", 2f, 8f, "0.0", " m", true);
                    Menu.SliderRow(ParkingOffset, "Sideways (+ right)", -15f, 15f, "0.0", " m", true);
                    break;
                case BuildPattern.Slalom:
                    IntSlider(CarCount, "Cars", 1, 12);
                    Menu.SliderRow(SlalomGap, "Gap along the path", 6f, 40f, "0", " m", true);
                    Menu.SliderRow(SlalomOffset, "Left / right offset", 0f, 8f, "0.0", " m", true);
                    break;
            }
            if (BuildPlanner.UsesCars(_pattern)) DrawModelPicker();

            bool can = block == null && !BuildPlacer.Busy;
            Menu.Chips(2,
                (BuildPlacer.Busy ? $"Placing… ({BuildPlacer.PendingCount} left)" : "Build " + BuildPlanner.Name(_pattern), Build, can),
                ("Undo last", () => Menu.SetStatus(BuildPlacer.UndoLast()), BuildPlacer.Groups.Count > 0));
            Menu.Switch(ShowPreview, "Show where it goes", "Markers on screen while this tab is open", true);
            if (ShowPreview.Value)
            {
                if (_previewError != null) Menu.Small(_previewError);
                else if (Preview.Count > 0) Menu.Small(PreviewSummary());
            }
            if (BuildPlanner.UsesPlanks(_pattern) && !PlankShape.Measured)
                Menu.Small("Plank size not read yet (it is once a map is loaded or the first plank is placed): spacing uses a 4 m guess.");
        });

        DrawPlaced();
    }

    private static void IntSlider(ConfigEntry<float> e, string label, int min, int max)
    {
        int n = Mathf.RoundToInt(e.Value);
        float v = Menu.RawSlider(label, n, min, max, n.ToString(), true, e.Description?.Description);
        int nv = Mathf.Clamp(Mathf.RoundToInt(v), min, max);
        if (nv != n) e.Value = nv;
    }

    private static void DrawModelPicker()
    {
        var all = BuildCars.All;
        if (all.Count == 0) { Menu.Small("No car models found yet (load into a world)."); return; }
        var sel = BuildCars.Selected;
        Menu.KeyValue("Car model", sel != null ? sel.Name : "—");
        Menu.Chips(2, ("◀ Previous", () => { BuildCars.Step(-1); _nextPreview = 0f; }, true), ("Next ▶", () => { BuildCars.Step(1); _nextPreview = 0f; }, true));
        if (_pattern != BuildPattern.Car) Menu.Switch(MixModels, "Mix models", "Each car of the pattern uses the next model in the list", true);
    }

    private static string PreviewSummary()
    {
        int planks = 0, cars = 0;
        foreach (var p in Preview)
        {
            if (p.Kind == PieceKind.Plank) planks++;
            else if (p.Kind == PieceKind.Car) cars++;
        }
        return planks > 0 ? $"Will place {planks} plank(s)." : cars > 0 ? $"Will spawn {cars} car(s)." : "Will move your winch anchor here.";
    }

    private static string Describe(BuildPattern p) => p switch
    {
        BuildPattern.Ramp => "Planks tilted up from the ground, ready to jump.",
        BuildPattern.Bridge => "Planks end to end from the ground ahead, across gaps and streams.",
        BuildPattern.Plank => "One plank flat on the ground ahead.",
        BuildPattern.Anchor => "Your portable winch anchor (one per player, like the game's item: an existing one is moved).",
        BuildPattern.Wall => "A row of cars across your path, facing you.",
        BuildPattern.Parking => "Cars side by side beside your path.",
        BuildPattern.Slalom => "Cars ahead, alternating left and right.",
        _ => "One driverless car ahead.",
    };

    private static void DrawPlaced()
    {
        int planks = BuildPlacer.Live(PieceKind.Plank), cars = BuildPlacer.Live(PieceKind.Car);
        Menu.Card("Placed", $"{BuildPlacer.Groups.Count} build(s) this session.", () =>
        {
            int maxP = Mathf.RoundToInt(MaxPlanks.Value), maxC = Mathf.RoundToInt(MaxCars.Value);
            Menu.Bar("Planks", maxP > 0 ? planks / (float)maxP : 0f, planks >= maxP ? Theme.Warn : Theme.Good, $"{planks} / {maxP}");
            Menu.Bar("Cars", maxC > 0 ? cars / (float)maxC : 0f, cars >= maxC ? Theme.Warn : Theme.Good, $"{cars} / {maxC}");
            if (BuildPlacer.AnchorPlaced) Menu.KeyValue("Winch anchor", "placed (undoing it also removes your map pin)");

            int shown = 0;
            for (int i = BuildPlacer.Groups.Count - 1; i >= 0 && shown < 6; i--, shown++)
                Menu.Small((shown == 0 ? "Last: " : "") + BuildPlacer.Groups[i].Label);

            Menu.Chips(2,
                ("Undo last", () => Menu.SetStatus(BuildPlacer.UndoLast()), BuildPlacer.Groups.Count > 0),
                ("Clear all", () => Menu.SetStatus(BuildPlacer.ClearAll()), BuildPlacer.Groups.Count > 0));
            IntSlider(MaxPlanks, "Plank limit", 4, 120);
            IntSlider(MaxCars, "Car limit", 1, 24);
            Menu.Small("Builder planks have their own owner keys, so they don't replace yours or anyone's. A player placing their own planks can still knock out builder planks touching their old ones, as in the game. Everything is forgotten when the session ends.");
        });
    }

    // ---- preview overlay ----

    private static void DrawPreview()
    {
        if (Preview.Count == 0 || !ShowPreview.Value) return;
        if (Time.unscaledTime - _tabSeenAt > 0.5f) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (Time.unscaledTime >= _nextCam || _cam == null)
        {
            _nextCam = Time.unscaledTime + 2f;
            _cam = Camera.main;
        }
        if (_cam == null) return;
        Theme.Ensure();
        foreach (var p in Preview)
        {
            Vector3 world;
            Color c;
            float size;
            switch (p.Kind)
            {
                case PieceKind.Plank: world = BuildPlanner.PlankCenter(p); c = Theme.Accent; size = 8f; break;
                case PieceKind.Car: world = p.Pos; c = Theme.Info; size = 14f; break;
                default: world = p.Pos; c = Theme.Good; size = 12f; break;
            }
            var sp = _cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) continue;
            float x = sp.x, y = Screen.height - sp.y;
            if (x < -20f || x > Screen.width + 20f || y < -20f || y > Screen.height + 20f) continue;
            Theme.Fill(new Rect(x - size / 2f - 1f, y - size / 2f - 1f, size + 2f, size + 2f), new Color(0f, 0f, 0f, 0.6f), (int)(size / 2f));
            Theme.Fill(new Rect(x - size / 2f, y - size / 2f, size, size), c, (int)(size / 2f));

            if (p.Kind == PieceKind.Plank)
            {
                // the two ends, so a ramp's slope and a bridge's line read on screen
                var half = p.Rot * (PlankShape.LongX ? Vector3.right : Vector3.forward) * (PlankShape.Length * 0.5f);
                DrawEnd(world + half, c);
                DrawEnd(world - half, c);
            }
        }
    }

    private static void DrawEnd(Vector3 world, Color c)
    {
        var sp = _cam.WorldToScreenPoint(world);
        if (sp.z <= 0f) return;
        Theme.Fill(new Rect(sp.x - 2.5f, Screen.height - sp.y - 2.5f, 5f, 5f), new Color(c.r, c.g, c.b, 0.8f), 2);
    }
}
