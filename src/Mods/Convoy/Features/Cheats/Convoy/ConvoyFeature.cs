using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using OverTheHillPlus.Convoy;
using OverTheHillPlus.Features.Public;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>
/// MODS > Convoy AI > Convoy: spawn AI cars that follow a leader as a convoy and help each other (and the players) when stuck:
/// they wait, tow with the winch, repair, flip cars back and pull meteorites out. Host or solo only. See ConvoyAi.cs
/// for the design.
/// </summary>
internal sealed class ConvoyFeature : IFeature
{
    private const string Sec = "Convoy AI";
    private const string Tab = "Convoy";

    internal static ConfigEntry<float> SpawnCount, MaxCars;
    internal static ConfigEntry<string> CarModel;
    internal static ConfigEntry<float> Spacing, TimeGap, MaxSpeed, Aggressiveness, LaneOffset, MaxClimb;
    internal static ConfigEntry<bool> AvoidWater;
    internal static ConfigEntry<bool> WaitForStuck, WaitForBehind, HelpPlayers, TowAi, RepairPlayers, RepairAi, FlipPlayers;
    internal static ConfigEntry<bool> AllowAiReposition, AllowPlayerReposition, CatchUp;
    internal static ConfigEntry<float> BehindDistance, JoinRadius, RepairAt, RepairKits, RepairSeconds, StuckSeconds;
    internal static ConfigEntry<float> TowWaitSeconds, CatchUpDistance, HelpRange;
    internal static ConfigEntry<bool> FetchMeteorites;
    internal static ConfigEntry<float> FetchRadius;
    internal static ConfigEntry<bool> ShowOverlay, ShowTrail, ShowStatusLine;
    internal static ConfigEntry<Key> HoldKey, RegroupKey, HelpMeKey, FetchKey, OverlayKey, SpawnKey, DismissKey;

    public void Register()
    {
        var f = ConvoyConfig.File;
        SpawnCount = Range(f, "SpawnCount", 4f, 1f, 12f, "How many AI cars 'Spawn convoy' adds.");
        MaxCars = Range(f, "MaxCars", 8f, 1f, 12f, "Most AI cars alive at once (each is a full physics car the host simulates).");
        CarModel = f.Bind(Sec, "CarModel", "", "AI car model: empty = same as the leader's car, *mix = a mix of all cars, else a catalog car name.");
        Spacing = Range(f, "Spacing", 12f, 6f, 40f, "Gap between cars when slow (m); grows with speed (see TimeGap).");
        TimeGap = Range(f, "TimeGap", 1.2f, 0.4f, 3f, "Extra gap per m/s of speed (seconds behind the car in front).");
        MaxSpeed = Range(f, "MaxSpeed", 45f, 10f, 100f, "Top speed of the AI cars (km/h).");
        Aggressiveness = Range(f, "Aggressiveness", 0.4f, 0f, 1f, "0 = gentle and careful, 1 = pushes harder (more throttle, faster corners, tighter gaps).");
        LaneOffset = Range(f, "LaneOffset", 0f, 0f, 2.5f, "Staggered formation: every other car drives this far left/right of the leader's line (0 = single file).");
        MaxClimb = Range(f, "MaxClimb", 28f, 15f, 40f, "Steepest slope (degrees) an AI car will choose to drive up when it picks its own way.");
        AvoidWater = f.Bind(Sec, "AvoidWater", true, "Off the trail, steer around deep water (on the trail they follow wherever the leader went).");
        WaitForStuck = f.Bind(Sec, "WaitForStuck", true, "The convoy stops and waits while anyone in it is stuck, flipped or in deep water.");
        WaitForBehind = f.Bind(Sec, "WaitForBehind", true, "The convoy waits for players who fall far behind.");
        BehindDistance = Range(f, "BehindDistance", 90f, 40f, 300f, "How far behind (m) a player can be before the convoy waits.");
        JoinRadius = Range(f, "JoinRadius", 60f, 20f, 200f, "Players within this distance (m) of the leader or an AI car count as part of the convoy.");
        HelpPlayers = f.Bind(Sec, "HelpPlayers", true, "AI cars help players too (tow, repair, flip), not just each other.");
        TowAi = f.Bind(Sec, "TowAi", true, "AI cars tow each other out when one is stuck.");
        TowWaitSeconds = Range(f, "TowWaitSeconds", 60f, 10f, 180f, "How long a stuck AI car waits for a tow before the last resort.");
        RepairAt = Range(f, "RepairAt", 0.6f, 0.2f, 0.95f, "Repair a car once its damage reaches this share of the maximum.");
        RepairKits = Range(f, "RepairKits", 3f, 0f, 10f, "Repair kits per AI car (each is a full repair, like the game's kit).");
        RepairSeconds = Range(f, "RepairSeconds", 3f, 1f, 10f, "How long a repair takes.");
        RepairPlayers = f.Bind(Sec, "RepairPlayers", true, "Repair damaged players.");
        RepairAi = f.Bind(Sec, "RepairAi", true, "Repair damaged AI cars.");
        FlipPlayers = f.Bind(Sec, "FlipPlayers", true, "When a player's car is on its roof, an AI car comes over and puts it back on its wheels (host power).");
        AllowAiReposition = f.Bind(Sec, "AllowAiReposition", true, "Last resort for an AI car that can't get free or is on its roof: put it back on safe ground (host power).");
        AllowPlayerReposition = f.Bind(Sec, "AllowPlayerReposition", false, "Last resort for a player the convoy couldn't tow free: reset the car like the game's reset.");
        CatchUp = f.Bind(Sec, "CatchUp", true, "AI cars left very far behind (or after the leader teleports) are moved up behind the convoy.");
        CatchUpDistance = Range(f, "CatchUpDistance", 350f, 120f, 1500f, "How far (m) an AI car may fall behind before it is moved up.");
        StuckSeconds = Range(f, "StuckSeconds", 3.5f, 1.5f, 12f, "Seconds of trying without moving before a car counts as stuck.");
        HelpRange = Range(f, "HelpRange", 400f, 50f, 1500f, "Furthest an AI car drives (m, along the trail) to help someone.");
        FetchMeteorites = f.Bind(Sec, "FetchMeteorites", true, "When the leader stops near a meteorite in the ground, a team of AI cars pulls it out (players near it get the pebbles).");
        FetchRadius = Range(f, "FetchRadius", 70f, 20f, 250f, "How close (m) to a stopped leader a meteorite must be to be pulled out automatically.");
        ShowOverlay = f.Bind(Sec, "ShowOverlay", false, "Debug overlay: each AI car's state, speed, target and look-ahead point.");
        ShowTrail = f.Bind(Sec, "ShowTrail", true, "With the overlay on, also draw the trail the convoy follows.");
        ShowStatusLine = f.Bind(Sec, "ShowStatusLine", true, "A line at the top of the screen when the convoy waits or something happens.");
        HoldKey = f.Bind(Sec, "HoldKey", Key.Numpad1, "Convoy: hold / go.");
        RegroupKey = f.Bind(Sec, "RegroupKey", Key.Numpad2, "Convoy: regroup on the trail.");
        HelpMeKey = f.Bind(Sec, "HelpMeKey", Key.Numpad3, "Convoy: send the nearest AI car to tow you.");
        FetchKey = f.Bind(Sec, "FetchKey", Key.Numpad4, "Convoy: pull out the nearest meteorite.");
        OverlayKey = f.Bind(Sec, "OverlayKey", Key.Numpad5, "Convoy: debug overlay on/off.");
        SpawnKey = f.Bind(Sec, "SpawnKey", Key.None, "Convoy: spawn the convoy. None = off.");
        DismissKey = f.Bind(Sec, "DismissKey", Key.None, "Convoy: dismiss every AI car. None = off.");

        Registry.Section(Registry.PlusMode, Tab, DrawMain, 0);
        Registry.Section(Registry.PlusMode, Tab, DrawCars, 1);
        Registry.Section(Registry.PlusMode, Tab, DrawDriving, 2);
        Registry.Section(Registry.PlusMode, Tab, DrawTeamwork, 3);
        Registry.Section(Registry.PlusMode, Tab, DrawMeteorites, 4);
        Registry.Section(Registry.PlusMode, Tab, DrawDebug, 5);
        Registry.OnTick(Tick);
        Registry.OnGui(ConvoyOverlay.Draw);
    }

    private static ConfigEntry<float> Range(ConfigFile cfg, string key, float def, float min, float max, string desc) =>
        cfg.Bind(Sec, key, def, new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));

    // ------------------------------------------------------------------ per frame

    private static void Tick()
    {
        HandleKeys();
        ConvoyAi.Tick();
    }

    private static void HandleKeys()
    {
        Keyboard kb;
        try { kb = Keyboard.current; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyFeature.HandleKeys"); return; }
        if (kb == null) return;
        if (MenuUx.HotkeysCaptured || MenuUx.Typing || KeybindEditor.Capturing) return;
        if (GUIUtility.keyboardControl != 0) return;
        if (Pressed(kb, SpawnKey)) Report(ConvoyAi.Spawn(Mathf.RoundToInt(SpawnCount.Value)));
        if (!ConvoyAi.Active) return;
        if (Pressed(kb, HoldKey)) Report(ConvoyAi.ToggleHold());
        if (Pressed(kb, RegroupKey)) Report(ConvoyAi.Regroup());
        if (Pressed(kb, HelpMeKey)) Report(ConvoyAi.HelpMe());
        if (Pressed(kb, FetchKey)) Report(ConvoyAi.FetchNearest());
        if (Pressed(kb, OverlayKey))
        {
            ShowOverlay.Value = !ShowOverlay.Value;
            Report(ShowOverlay.Value ? "Convoy overlay on." : "Convoy overlay off.");
        }
        if (Pressed(kb, DismissKey)) Report(ConvoyAi.Dismiss());
    }

    private static bool Pressed(Keyboard kb, ConfigEntry<Key> e)
    {
        var k = e.Value;
        if (k == Key.None) return false;
        try { return kb[k].wasPressedThisFrame; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyFeature.Pressed"); return false; }
    }

    internal static void Report(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        try { Menu.SetStatus(s); } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyFeature.Report"); }
        ConvoyAi.LastEvent = s;
        ConvoyAi.LastEventAt = Time.unscaledTime;
    }

    // ------------------------------------------------------------------ menu

    private static void DrawMain()
    {
        string block = ConvoyAi.BlockReason();
        if (block != null) Menu.Banner(block, Theme.Info);
        bool can = block == null;
        int n = ConvoyAi.Agents.Count, pending = ConvoyAi.PendingCount;

        Menu.Card("Convoy", "AI cars that follow the leader's tracks, wait for anyone stuck and come back to tow, repair or flip them.", () =>
        {
            Menu.KeyValue("AI cars", n == 0 && pending == 0 ? "none" : pending > 0 ? $"{n} (+{pending} spawning)" : n.ToString());
            Menu.KeyValue("Leader", ConvoyAi.Leader != null ? ConvoyAi.Leader.Name + (ConvoyAi.Leader.IsLocal ? " (you)" : "") : "you");
            Menu.KeyValue("State", StateText());
            int count = Mathf.RoundToInt(SpawnCount.Value);
            float v = Menu.RawSlider("Cars to spawn", count, 1, 12, count.ToString(), true, SpawnCount.Description?.Description);
            int nv = Mathf.Clamp(Mathf.RoundToInt(v), 1, 12);
            if (nv != count) SpawnCount.Value = nv;
            Menu.Chips(3,
                (n == 0 ? $"Spawn convoy ({nv})" : $"Add {nv} more", () => Report(ConvoyAi.Spawn(nv)), can),
                ("Add one", () => Report(ConvoyAi.Spawn(1)), can),
                ("Dismiss all", () => Report(ConvoyAi.Dismiss()), n > 0 || pending > 0));
            Menu.Chips(3,
                (ConvoyAi.Holding ? "Go" : "Hold", () => Report(ConvoyAi.ToggleHold()), n > 0),
                ("Regroup", () => Report(ConvoyAi.Regroup()), n > 0),
                (ConvoyAi.Parked ? "Unpark" : "Park", () => Report(ConvoyAi.TogglePark()), n > 0));
            Menu.Chips(3,
                ("Help me (tow)", () => Report(ConvoyAi.HelpMe()), n > 0 && can),
                ("Pull meteorite", () => Report(ConvoyAi.FetchNearest()), n > 0 && can),
                ("Release ropes", () => Report(ConvoyAi.DetachAll()), n > 0));
            Menu.Chips(2,
                ("Remove last car", () => Report(ConvoyAi.DismissOne()), n > 0),
                ("Refill repair kits", () => Report(ConvoyAi.RefillKits()), n > 0));
            Menu.Small($"Keys: {KeyLabel(HoldKey)} hold/go, {KeyLabel(RegroupKey)} regroup, {KeyLabel(HelpMeKey)} help me, {KeyLabel(FetchKey)} meteorite, {KeyLabel(OverlayKey)} overlay.");
        });

        Menu.Card("Leader & cars", "Who the convoy follows and what it drives.", () =>
        {
            var players = ConvoyMembers.Players;
            var leader = ConvoyAi.Leader;
            Menu.KeyValue("Following", leader != null ? leader.Name : "you");
            if (players.Count > 1)
            {
                Menu.Chips(3,
                    ("◀ Previous", () => Report(CycleLeader(-1)), true),
                    ("Me", () => Report(ConvoyAi.SetLeader(null)), ConvoyAi.LeaderRaw != int.MinValue),
                    ("Next ▶", () => Report(CycleLeader(1)), true));
            }
            else Menu.Small("Other players show up here once they are in the session (the convoy can follow any of them).");
            Menu.KeyValue("Car model", ConvoyModels.Label());
            Menu.Chips(2, ("◀ Previous model", () => ConvoyModels.Step(-1), true), ("Next model ▶", () => ConvoyModels.Step(1), true));
            int max = Mathf.RoundToInt(MaxCars.Value);
            float m = Menu.RawSlider("Car limit", max, 1, 12, max.ToString(), true, MaxCars.Description?.Description);
            int mv = Mathf.Clamp(Mathf.RoundToInt(m), 1, 12);
            if (mv != max) MaxCars.Value = mv;
        });
    }

    private static string StateText()
    {
        if (!ConvoyAi.Active) return "not spawned";
        if (ConvoyAi.Parked) return "parked";
        if (ConvoyAi.HoldReason != null) return ConvoyAi.HoldReason;
        return $"driving (trail {ConvoyAi.Trail.EndS - ConvoyAi.Trail.StartS:0} m)";
    }

    private static string CycleLeader(int delta)
    {
        var players = ConvoyMembers.Players;
        if (players.Count == 0) return "No players.";
        int i = 0;
        for (int k = 0; k < players.Count; k++) if (players[k] == ConvoyAi.Leader) { i = k; break; }
        i = ((i + delta) % players.Count + players.Count) % players.Count;
        return ConvoyAi.SetLeader(players[i]);
    }

    private static string KeyLabel(ConfigEntry<Key> e) => e.Value == UnityEngine.InputSystem.Key.None ? "—" : KeybindEditor.KeyName(e.Value);

    private static void DrawCars()
    {
        if (!ConvoyAi.Active) return;
        Menu.Card("AI cars", "Live status of every AI car, open incidents and recent events.", () =>
        {
            foreach (var a in ConvoyAi.Agents)
            {
                string job = a.Job != null ? $" · {a.Job.Status}" : "";
                string stuck = a.Stuck.IsStuck ? $" · {StuckMonitor.Describe(a.Stuck.Kind)} {a.Stuck.StuckFor:0}s" : "";
                Menu.KeyValue($"{a.Name} ({a.Model})", $"{a.Mode} · {Mathf.Abs(a.Speed) * 3.6f:0} km/h · dmg {a.Damage01 * 100f:0}%{stuck}{job}");
                Menu.Small("   " + a.Activity);
            }
            if (ConvoyAi.Incidents.Count > 0)
            {
                Menu.Small("Incidents:");
                foreach (var inc in ConvoyAi.Incidents)
                    if (!inc.Closed) Menu.Small($"   {inc.Describe()} — {(inc.Helper != null ? inc.Helper.Name + ": " + inc.Job?.Phase : inc.Note)}");
            }
            if (ConvoyAi.Fetch != null) Menu.Small($"Meteorite: {ConvoyAi.Fetch.Phase}");
            if (ConvoyAi.Events.Count > 0)
            {
                Menu.Small("Recent:");
                for (int i = ConvoyAi.Events.Count - 1; i >= 0 && i >= ConvoyAi.Events.Count - 6; i--) Menu.Small("   " + ConvoyAi.Events[i]);
            }
        });
    }

    private static void DrawDriving()
    {
        Menu.Card("Driving", "How the AI cars drive. They follow the leader's exact tracks (known to be drivable).", () =>
        {
            Menu.SliderRow(Spacing, "Gap when slow", 6f, 40f, "0", " m", true);
            Menu.SliderRow(TimeGap, "Extra gap per speed", 0.4f, 3f, "0.0", " s", true);
            Menu.SliderRow(MaxSpeed, "Top speed", 10f, 100f, "0", " km/h", true);
            Menu.SliderRow(Aggressiveness, "Aggressiveness", 0f, 1f, "0", "", true, true);
            Menu.SliderRow(LaneOffset, "Staggered formation", 0f, 2.5f, "0.0", " m", true);
            Menu.SliderRow(MaxClimb, "Steepest climb (own way)", 15f, 40f, "0", "°", true);
            Menu.Switch(AvoidWater, "Avoid deep water", "Off the trail, steer around rivers and lakes", true);
        });
    }

    private static void DrawTeamwork()
    {
        Menu.Card("Teamwork", "What the convoy does when someone is stuck, flipped, in deep water, damaged or behind.", () =>
        {
            Menu.Switch(WaitForStuck, "Wait for stuck cars", "The convoy stops while anyone in it is stuck", true);
            Menu.Switch(WaitForBehind, "Wait for players behind", "Stop when a player falls far behind", true);
            Menu.SliderRow(BehindDistance, "…how far behind", 40f, 300f, "0", " m", WaitForBehind.Value);
            Menu.Switch(HelpPlayers, "Help players", "Tow, repair and flip players, not just AI cars", true);
            Menu.Switch(TowAi, "Tow each other", "A stuck AI car calls another one to winch it out", true);
            Menu.SliderRow(StuckSeconds, "Stuck after", 1.5f, 12f, "0.0", " s", true);
            Menu.SliderRow(TowWaitSeconds, "Wait for a tow", 10f, 180f, "0", " s", true);
            Menu.SliderRow(HelpRange, "Help up to", 50f, 1500f, "0", " m", true);
            Menu.Switch(RepairPlayers, "Repair players", "Using the AI car's repair kits", HelpPlayers.Value);
            Menu.Switch(RepairAi, "Repair AI cars", null, true);
            Menu.SliderRow(RepairAt, "Repair at damage", 0.2f, 0.95f, "0", "", true, true);
            Menu.SliderRow(RepairKits, "Repair kits per car", 0f, 10f, "0", "", true);
            Menu.SliderRow(RepairSeconds, "Repair time", 1f, 10f, "0.0", " s", true);
            Menu.Switch(FlipPlayers, "Flip players back", "An AI car drives over and puts a flipped player back on its wheels", HelpPlayers.Value);
            Menu.Switch(AllowAiReposition, "Rescue AI cars (last resort)", "Move an AI car that can't get free back onto the trail", true);
            Menu.Switch(AllowPlayerReposition, "Reset players (last resort)", "When towing fails, reset the player's car like the game's reset", HelpPlayers.Value);
            Menu.Switch(CatchUp, "Catch up far-behind cars", "Move AI cars that fell very far behind up to the convoy", true);
            Menu.SliderRow(CatchUpDistance, "…when behind by", 120f, 1500f, "0", " m", CatchUp.Value);
            Menu.SliderRow(JoinRadius, "Players count as in the convoy within", 20f, 200f, "0", " m", true);
        });
    }

    private static void DrawMeteorites()
    {
        Menu.Card("Meteorites", "AI cars winch meteorites out of the ground together (the game needs enough taut ropes pulling the same way).", () =>
        {
            Menu.Switch(FetchMeteorites, "Pull meteorites automatically", "When the leader stops near one", true);
            Menu.SliderRow(FetchRadius, "…within", 20f, 250f, "0", " m", FetchMeteorites.Value);
            Menu.Chips(1, ("Pull the nearest meteorite now", () => Report(ConvoyAi.FetchNearest()), ConvoyAi.Agents.Count > 0));
            var t = ConvoyAi.Fetch;
            Menu.KeyValue("Now", t == null ? "idle" : $"{t.Phase} ({t.Jobs.Count} car(s), needs {t.Required})");
            Menu.Small("Pebbles go to players near the meteorite when it comes out, so stay close.");
        });
    }

    private static void DrawDebug()
    {
        Menu.Card("Overlay & keys", null, () =>
        {
            Menu.Switch(ShowOverlay, "Debug overlay", "State, speed, target and look-ahead point of each AI car", true);
            Menu.Switch(ShowTrail, "Draw the trail", "With the overlay", ShowOverlay.Value);
            Menu.Switch(ShowStatusLine, "Status line", "Top of the screen when the convoy waits or something happens", true);
            Menu.KeyValue("Physics queries", $"{ConvoySense.QueriesPerSecond}/s");
            foreach (var e in new[] { HoldKey, RegroupKey, HelpMeKey, FetchKey, OverlayKey, SpawnKey, DismissKey })
            {
                var entry = e;
                Menu.Chips(1, ($"{entry.Description.Description.Split(':')[^1].Trim()}  [{KeyLabel(entry)}]  change…", () => KeybindEditor.StartCapture(entry), true));
            }
        });
    }
}

/// <summary>Which car models the AI drives.</summary>
internal static class ConvoyModels
{
    public const string Same = "", Mix = "*mix";
    private static int _mixStart = -1;

    public static IReadOnlyList<ItemCatalog.Item> All => ConvoyCars.All;

    public static string Label()
    {
        string c = ConvoyFeature.CarModel.Value ?? "";
        if (c == Same) return "same as the leader's car";
        if (c == Mix) return "a mix of every car";
        foreach (var m in All) if (m.RawName == c) return m.Name;
        return c;
    }

    public static void Step(int delta)
    {
        var all = All;
        var options = new List<string> { Same, Mix };
        foreach (var m in all) options.Add(m.RawName);
        int i = options.IndexOf(ConvoyFeature.CarModel.Value ?? "");
        if (i < 0) i = 0;
        i = ((i + delta) % options.Count + options.Count) % options.Count;
        ConvoyFeature.CarModel.Value = options[i];
    }

    public static List<ItemCatalog.Item> Pick(int count, PlayerMember leader)
    {
        var result = new List<ItemCatalog.Item>();
        var all = All;
        if (all.Count == 0) return result;
        string c = ConvoyFeature.CarModel.Value ?? "";
        if (c == Mix)
        {
            if (_mixStart < 0) _mixStart = UnityEngine.Random.Range(0, all.Count);
            for (int i = 0; i < count; i++) result.Add(all[(_mixStart++) % all.Count]);
            return result;
        }
        ItemCatalog.Item pick = null;
        if (c == Same) pick = LeaderModel(leader);
        else foreach (var m in all) if (m.RawName == c) { pick = m; break; }
        pick ??= all[0];
        for (int i = 0; i < count; i++) result.Add(pick);
        return result;
    }

    private static string Norm(string s) => (s ?? "").Replace(" ", "").Replace("_", "").ToLowerInvariant();

    /// <summary>The catalog car matching the leader's car prefab ("Car_Lynx(Clone)" -> Lynx).</summary>
    private static ItemCatalog.Item LeaderModel(PlayerMember leader)
    {
        if (leader == null) return null;
        string name = null;
        try { name = leader.Fp != null ? leader.Fp.gameObject.name : null; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyModels.LeaderModel"); }
        try { if (name == null || !name.StartsWith("Car_")) name = leader.Car.gameObject.name; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyModels.LeaderModel"); }
        if (string.IsNullOrEmpty(name)) return null;
        name = name.Replace("(Clone)", "");
        if (name.StartsWith("Car_")) name = name.Substring(4);
        string key = Norm(name);
        foreach (var m in All)
            if (Norm(m.Name) == key || Norm(m.RawName) == key) return m;
        return null;
    }
}
