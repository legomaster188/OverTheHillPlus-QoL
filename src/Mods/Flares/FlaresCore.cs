using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Flares;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Player Flares' flow. You press the key (or get stuck, with auto flares on):
/// - solo or hosting: the flare is checked against your own cooldown, shown, and broadcast to every modded guest;
/// - as a guest: your position goes to the host (ModNet channel 33). The host checks the rate (one per 3 s and six a
///   minute for any player), that the flare starts within 60 m of that player's car and that it relays flares at all,
///   picks the colour itself (UIManager.GetPlayerColor, which Big Lobbies extends past four players) and broadcasts
///   the flare to everyone; you see yours when it comes back. A host without the mod, or no host link, shows only your
///   own flare on your screen.
/// A flare is a local visual (<see cref="FlareVisual"/>) plus, for a while, a ping on the game's map
/// (<see cref="MapMarkers"/>), the minimap (<see cref="Pings"/>) and the compass.
/// </summary>
internal static class FlaresCore
{
    public const ushort Channel = ModNet.Channels.Flares;

    private static readonly List<FlareVisual> Flying = new();
    private static readonly FlareRules.Throttle Own = new(10f, 6, 60f);
    private static readonly FlareRules.Throttle HostLimit = new(FlareRules.HostMinInterval, FlareRules.HostMaxPerWindow, FlareRules.HostWindow);
    private static readonly StuckDetector Stuck = new();
    private static readonly HashSet<string> LoggedOnce = new();
    private static readonly List<MapMarkers.Spot> Spots = new();
    private static readonly List<Pings.Ping> PingList = new();

    private static bool _registered;
    private static IntPtr _runner;
    private static Vector3 _lastCarPos;
    private static bool _hasLastPos;
    private static int _pingKey = -1;
    private static float _pingExpiry;
    private static Sprite _compassSprite;

    internal static CompassController Compass;

    /// <summary>Seconds until your next flare may go up (0 = ready).</summary>
    internal static float Wait => Own.Wait(0, Time.unscaledTime);

    internal static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) FlaresPlugin.Logger?.LogWarning("[Flares] " + message + " (logged once)");
    }

    internal static void Install()
    {
        _registered = ModNet.Register(Channel, new ModNet.ChannelOptions
        {
            Name = "flares",
            Version = 1,
            MaxPayload = 64,
            RatePerSecond = 2f,
            Burst = 4,
            HostOnlySender = true,
            Active = () => ModManager.IsLoaded(FlaresPlugin.ModId),
        }, OnMessage);
        if (!_registered) LogOnce("channel", $"ModNet channel {Channel} is taken: only your own flares show");
        ModNet.OnSessionReset(Reset);
    }

    // ---------------------------------------------------------------- messages

    private static void OnMessage(ModNet.Message m)
    {
        if (m.Type == FlareWire.MsgFlare)
        {
            if (GameState.IsAuthority || !m.FromHost) return;
            if (FlareWire.DecodeFlare(m.Payload, out var f)) Show(f);
            return;
        }
        if (m.Type != FlareWire.MsgRequest || !GameState.IsAuthority || !FlaresConfig.HostRelay.Value) return;
        if (!m.From.IsRealPlayer || !FlareWire.DecodeRequest(m.Payload, out float x, out float y, out float z, out bool auto)) return;
        if (GameState.LocalPlayer is { } me && me.RawEncoded == m.From.RawEncoded) return;
        if (!HostLimit.TryUse(m.From.RawEncoded, Time.unscaledTime)) return;
        bool hasCar = GameMap.TryPlayerCarPosition(m.From, out var car);
        if (!FlareRules.NearCar(hasCar, car.x, car.y, car.z, x, y, z)) return;
        Launch(m.From, new Vector3(x, y, z), auto);
    }

    /// <summary>Solo or host: colours the flare, broadcasts it and shows it here.</summary>
    private static void Launch(PlayerRef owner, Vector3 pos, bool auto)
    {
        var c = ColorOf(owner);
        var info = new FlareInfo(owner.RawEncoded, pos.x, pos.y, pos.z, FlareWire.ToByte(c.r), FlareWire.ToByte(c.g), FlareWire.ToByte(c.b), auto);
        if (_registered && GameState.Runner != null && !GameState.Runner.IsSinglePlayer)
        {
            try { ModNet.Broadcast(Channel, FlareWire.MsgFlare, FlareWire.EncodeFlare(info)); }
            catch (Exception e) { DebugLog.Error(FlaresPlugin.ModId, e, "FlaresCore.Launch"); LogOnce("broadcast", "sending a flare: " + e.Message); }
        }
        Show(info);
    }

    /// <summary>The player's colour as the game (and Big Lobbies) show it, lifted so it is never too dark for the sky.</summary>
    private static Color ColorOf(PlayerRef p)
    {
        var c = new Color(1f, 0.45f, 0.2f);
        try { if (p.IsRealPlayer) c = UIManager.GetPlayerColor(p); }
        catch (Exception e) { DebugLog.Error(FlaresPlugin.ModId, e, "FlaresCore.ColorOf"); }
        float r = c.r, g = c.g, b = c.b;
        FlareRules.Lift(ref r, ref g, ref b);
        return new Color(r, g, b, 1f);
    }

    private static string NameOf(PlayerRef p)
    {
        try { return PinBoard.CleanText(PlayerListRoster.NameOf(OverlandSessionPlayers.Instance, p), 20); }
        catch (Exception e) { DebugLog.Error(FlaresPlugin.ModId, e, "FlaresCore.NameOf"); return ""; }
    }

    // ---------------------------------------------------------------- showing a flare

    private static void Show(in FlareInfo f)
    {
        var owner = PlayerRef.FromEncoded(f.Owner);
        bool mine = GameState.LocalPlayer is { } me && me.RawEncoded == f.Owner;
        if (!mine && !FlaresConfig.ShowFlares.Value) return;
        if (!PinBoard.ValidCoordinate(f.X) || !PinBoard.ValidCoordinate(f.Y) || !PinBoard.ValidCoordinate(f.Z)) return;

        while (Flying.Count >= FlareRules.MaxActive)
        {
            Flying[0].Destroy();
            Flying.RemoveAt(0);
        }
        var color = new Color(f.R / 255f, f.G / 255f, f.B / 255f, 1f);
        string label = mine ? "You" : NameOf(owner);
        var v = FlareVisual.Spawn(new Vector3(f.X, f.Y, f.Z), color, label, f.Owner, Time.unscaledTime, FlaresConfig.Duration.Value, FlaresConfig.Apex.Value);
        if (v == null) return;
        Flying.Add(v);
        AddCompass(v);
        DebugLog.Info(FlaresPlugin.ModId, $"flare from player {f.Owner} at {f.X:0},{f.Y:0},{f.Z:0}{(f.Auto ? " (auto)" : "")}");
        if (mine) FlaresHud.Say(f.Auto ? "Flare fired (stuck)" : "Flare fired");
    }

    /// <summary>A compass icon that follows the flare, the way the game tracks other cars (copying one of its icons' sprite).</summary>
    private static void AddCompass(FlareVisual v)
    {
        if (!FlaresConfig.OnMap.Value) return;
        try
        {
            var c = Compass;
            if (c == null || c.WasCollected || v.Root == null) return;
            var cars = c.trackedCars;
            if (cars == null) return;
            if (_compassSprite == null)
                for (int i = 0; i < cars.Count && _compassSprite == null; i++) _compassSprite = cars[i]?.image?.sprite;
            if (_compassSprite == null) return;
            var icon = c.RegisterTarget(v.Root.transform, _compassSprite, (CompassIcon.Properties)3, new Il2CppSystem.Nullable<PointOfInterestType>());
            if (icon == null) return;
            if (icon.image != null) icon.image.color = v.Color;
            icon.SetBackgroundImageColor(new Color(0f, 0f, 0f, 0f));
            cars.Add(icon);
            v.Icon = icon;
        }
        catch (Exception e)
        {
            DebugLog.Error(FlaresPlugin.ModId, e, "FlaresCore.AddCompass");
            LogOnce("compass", "the compass ping failed: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- firing

    /// <summary>Fires your flare (the key, the MODS page, or the stuck detector).</summary>
    internal static void Fire(bool auto)
    {
        var car = GameState.LocalCar;
        if (car == null) { if (!auto) FlaresHud.Say("Get in a car to fire a flare"); return; }
        Own.MinInterval = FlaresConfig.Cooldown.Value;
        float now = Time.unscaledTime;
        if (!Own.TryUse(0, now))
        {
            if (!auto) FlaresHud.Say($"Flare ready in {Mathf.CeilToInt(Own.Wait(0, now))} s");
            return;
        }
        var pos = car.transform.position;
        var me = GameState.LocalPlayer;
        if (GameState.Runner == null || !me.HasValue || GameState.IsAuthority)
        {
            Launch(me ?? default, pos, auto);
            return;
        }
        var result = ModNet.SendToHost(Channel, FlareWire.MsgRequest, FlareWire.EncodeRequest(pos.x, pos.y, pos.z, auto));
        if (result == ModNet.SendResult.Sent) return; // the host sends it back to everyone, us included
        var c = ColorOf(me.Value);
        Show(new FlareInfo(me.Value.RawEncoded, pos.x, pos.y, pos.z, FlareWire.ToByte(c.r), FlareWire.ToByte(c.g), FlareWire.ToByte(c.b), auto));
        if (!auto) FlaresHud.Say("Only you can see this flare (the host doesn't run Player Flares)");
    }

    internal static void Keys(Keyboard kb)
    {
        if (CoreBehaviour.Pressed(kb, FlaresConfig.FlareKey.Value) && CameraView.Driving) Fire(false);
    }

    // ---------------------------------------------------------------- per frame

    internal static void Tick()
    {
        var runner = GameState.Runner;
        IntPtr rp = runner != null ? runner.Pointer : IntPtr.Zero;
        if (rp != _runner) { _runner = rp; Reset(); }

        NameTags.Tick();
        TickAuto();
        TickFlares();
    }

    private static void TickAuto()
    {
        var car = GameState.LocalCar;
        if (car == null || !FlaresConfig.AutoFlare.Value || !CameraView.Driving)
        {
            _hasLastPos = false;
            Stuck.Reset();
            return;
        }
        var t = car.transform;
        var pos = t.position;
        float dt = Time.unscaledDeltaTime;
        float speed = _hasLastPos && dt > 0f ? Vector3.Distance(pos, _lastCarPos) / dt : 0f;
        _lastCarPos = pos;
        _hasLastPos = true;
        Stuck.Seconds = FlaresConfig.AutoAfter.Value;
        Stuck.RepeatEvery = FlaresConfig.AutoRepeat.Value;
        if (Stuck.Step(dt, t.up.y, speed)) Fire(true);
    }

    private static void TickFlares()
    {
        float now = Time.unscaledTime;
        var cam = Camera.main;
        var camPos = cam != null ? cam.transform.position : Vector3.zero;
        for (int i = Flying.Count - 1; i >= 0; i--)
        {
            var v = Flying[i];
            if (cam != null && v.Update(now, camPos)) continue;
            if (cam == null && v.Root != null && now - v.Born < v.Life) continue; // no camera for a moment: leave it
            v.Destroy();
            Flying.RemoveAt(i);
        }
        PublishPings(now);
    }

    /// <summary>The pings on the game's map and the minimap: flares younger than PingSeconds, at their launch point.</summary>
    private static void PublishPings(float now)
    {
        float keep = FlaresConfig.PingSeconds.Value;
        int key = 0;
        float expiry = float.MaxValue;
        if (FlaresConfig.OnMap.Value)
            foreach (var v in Flying)
                if (now - v.Born < keep) { key = key * 31 + v.Owner * 7 + (int)(v.Born * 10f); expiry = Math.Min(expiry, v.Born + keep); }
        if (key == _pingKey && now < _pingExpiry) return;
        _pingKey = key;
        _pingExpiry = expiry;

        Spots.Clear();
        PingList.Clear();
        if (key != 0)
            foreach (var v in Flying)
            {
                if (now - v.Born >= keep) continue;
                Spots.Add(new MapMarkers.Spot(v.Origin, v.Label, v.Color));
                PingList.Add(new Pings.Ping { Pos = v.Origin, Color = v.Color });
            }
        MapMarkers.Set(FlaresPlugin.ModId, Spots);
        Pings.Set(FlaresPlugin.ModId, PingList);
    }

    private static void Reset()
    {
        foreach (var v in Flying) v.Destroy();
        Flying.Clear();
        Own.Clear();
        HostLimit.Clear();
        Stuck.Reset();
        _hasLastPos = false;
        _pingKey = -1;
        MapMarkers.Set(FlaresPlugin.ModId, null);
        Pings.Set(FlaresPlugin.ModId, null);
    }
}

/// <summary>Remembers the game's compass so a flare can be tracked on it.</summary>
[HarmonyPatch(typeof(CompassController), nameof(CompassController.LateUpdate))]
internal static class FlareCompassPatch
{
    private static void Postfix(CompassController __instance) => FlaresCore.Compass = __instance;
}
