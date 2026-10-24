using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Cars;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Each player's look for each modded car, over the mod network: one channel for every modded car
/// (<see cref="Channel"/>, payload <see cref="CarLookWire"/>: the car id travels in the message, the Vera's in the old
/// layout). For each car the session runs, you announce your look to everyone once connected (asking them to answer
/// with theirs) and again whenever it changes, at most once a second; the host also sends them to each guest that
/// joins. Receivers keep the last look of each player for each car and <see cref="CarLooks"/> shows it on that
/// player's car only (a player in another car keeps theirs for when they switch). The skin shows only where the same
/// key with the same hash exists, otherwise the colours. Malformed, oversized and other-version messages are dropped,
/// and so are looks for a car this game doesn't run or the session doesn't run (<see cref="ModManager.ServerModActive"/>);
/// vanilla players never get anything (and can't join a modded-car session).
/// </summary>
internal static class CarLookNet
{
    public const ushort Channel = Net.Channels.CarLook;
    public const byte MsgLook = 1, MsgLookAskBack = 2;
    private const int MaxPeers = 64;
    private const float AskBackGap = 3f;
    private const float ApplyGap = 2f;

    private static readonly Dictionary<(int player, string car), CarLook> Looks = new();
    /// <summary>A player's newest look not shown yet (their previous change was less than <see cref="ApplyGap"/> ago).</summary>
    private static readonly Dictionary<(int, string), CarLook> Pending = new();
    private static readonly Dictionary<(int, string), float> AppliedAt = new();
    private static readonly Dictionary<(int, string), float> LastReply = new();
    private static readonly HashSet<int> Players = new();

    private sealed class Sent
    {
        public byte[] Payload;
        public bool Announced;
        public float Next;
    }

    private static readonly Dictionary<string, Sent> Mine = new(StringComparer.Ordinal);
    private static bool _registered;

    public static int Rejected { get; private set; }

    private static Net.ChannelOptions _options;

    /// <summary>
    /// Sizes the channel's rate and burst for <paramref name="cars"/> registered modded cars (ModNet reads them for every
    /// message it sends, receives or relays, per sender). Everyone in a session registered the same cars (the join hash
    /// holds every registered car's prefab), so sender and receiver always agree on the budget.
    /// </summary>
    public static void FitBudget(int cars)
    {
        if (_options == null) return;
        (_options.RatePerSecond, _options.Burst) = CarLookWire.Budget(cars);
    }

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        _options = new Net.ChannelOptions
        {
            Name = "car look",
            Version = 1,
            MaxPayload = CarLookWire.MaxPayload,
            RatePerSecond = CarLookWire.RatePerCar,
            Burst = CarLookWire.BurstPerCar,
            AllowRelay = true, // guests reach each other through the host
            HostOnlySender = false,
            Active = ModdedCars.AnyReady,
        };
        Net.Register(Channel, _options, OnMessage);
        Net.OnSessionReset(Reset);
        Net.OnPeerGone(p => Forget(p.RawEncoded));
        Net.OnPeerReady(p =>
        {
            // Host: a guest arrived, tell it how your cars look (its own announcements ask the others).
            if (!Net.IsHost) return;
            foreach (var car in ModdedCars.All)
                if (Connected(car)) Net.SendTo(p, Channel, MsgLook, CarLookWire.Encode(car.Id, car.Look.Announced));
        });
    }

    /// <summary>The last look a player sent for a car (null: none yet).</summary>
    public static CarLook Of(int rawPlayer, string carId) => Looks.TryGetValue((rawPlayer, carId), out var l) ? l : null;

    public static int Known => Players.Count;

    private static bool Active(RuntimeCar car) => car != null && car.Ready && ModManager.ServerModActive(car.Id);

    public static bool Connected(RuntimeCar car) =>
        Active(car) && (Net.IsHost ? Net.HostAdvertised : Net.IsGuest && Net.HostHas(Channel));

    public static void Tick()
    {
        PromotePending();
        float now = Time.unscaledTime;
        foreach (var car in ModdedCars.All)
        {
            if (!Mine.TryGetValue(car.Id, out var s)) Mine[car.Id] = s = new Sent();
            if (!Connected(car))
            {
                s.Announced = false;
                s.Payload = null;
                continue;
            }
            if (now < s.Next) continue;
            var payload = CarLookWire.Encode(car.Id, car.Look.Announced);
            if (s.Announced && s.Payload != null && Same(s.Payload, payload)) continue;
            s.Next = now + 1f;
            int n = Net.Broadcast(Channel, s.Announced ? MsgLook : MsgLookAskBack, payload);
            // A host with nobody here yet is done (whoever joins is sent it); a guest needs the host to take it.
            if (n > 0 || Net.IsHost)
            {
                s.Announced = true;
                s.Payload = payload;
            }
        }
    }

    private static void OnMessage(Net.Message m)
    {
        var from = m.From;
        bool fromHost = false;
        if (m.FromHost && !m.Relayed)
        {
            // From the host itself: on a guest the transport's sender may not be the host's player ref.
            fromHost = true;
            try { var sp = OverlandSessionPlayers.Instance; if (sp != null && sp.HostPlayerRef.IsRealPlayer) from = sp.HostPlayerRef; } catch (Exception ex) { DebugLog.Error("core", ex, "CarLookNet.OnMessage"); }
        }
        try
        {
            var r = GameState.Runner;
            if (r != null && r.LocalPlayer.RawEncoded == from.RawEncoded) return; // our own echo
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarLookNet.OnMessage"); }
        if (m.Type != MsgLook && m.Type != MsgLookAskBack) return;
        if (!CarLookWire.TryDecode(m.Payload, out string carId, out var look)) { Rejected++; return; }
        var car = ModdedCars.ById(carId);
        if (!Active(car)) return; // a car this game or this session doesn't run
        int raw = from.RawEncoded;
        if (!Players.Contains(raw) && Players.Count >= MaxPeers) return;
        Players.Add(raw);
        Take((raw, car.Id), look);

        if (m.Type != MsgLookAskBack) return;
        float now = Time.unscaledTime;
        var k = (raw, car.Id);
        if (LastReply.TryGetValue(k, out float at) && now - at < AskBackGap) return;
        LastReply[k] = now;
        var mine = CarLookWire.Encode(car.Id, car.Look.Announced);
        try
        {
            if (Net.IsHost) Net.SendTo(from, Channel, MsgLook, mine);
            else if (fromHost) Net.SendToHost(Channel, MsgLook, mine);
            else Net.SendTo(from, Channel, MsgLook, mine);
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarLookNet.OnMessage"); car.LogOnce("look-send", "sending your look failed: " + e.Message); }
    }

    /// <summary>
    /// A player's look for a car: shown at once the first time and when the last change was at least
    /// <see cref="ApplyGap"/> ago; otherwise kept as pending (the newest wins) and shown when the gap is over. A player
    /// cycling colours thus makes every other game rebuild at most one texture every two seconds per car.
    /// </summary>
    private static void Take((int, string) k, CarLook look)
    {
        if (Looks.TryGetValue(k, out var have) && have.Equals(look)) { Pending.Remove(k); return; }
        float now = Time.unscaledTime;
        if (!Looks.ContainsKey(k) || !AppliedAt.TryGetValue(k, out float at) || now - at >= ApplyGap)
        {
            Looks[k] = look;
            AppliedAt[k] = now;
            Pending.Remove(k);
        }
        else Pending[k] = look;
    }

    private static void PromotePending()
    {
        if (Pending.Count == 0) return;
        float now = Time.unscaledTime;
        List<(int, string)> ready = null;
        foreach (var kv in Pending)
            if (!AppliedAt.TryGetValue(kv.Key, out float at) || now - at >= ApplyGap) (ready ??= new List<(int, string)>()).Add(kv.Key);
        if (ready == null) return;
        foreach (var k in ready)
        {
            Looks[k] = Pending[k];
            AppliedAt[k] = now;
            Pending.Remove(k);
        }
    }

    private static void Forget(int raw)
    {
        Players.Remove(raw);
        foreach (var d in new System.Collections.IDictionary[] { Looks, Pending, AppliedAt, LastReply })
        {
            var drop = new List<(int, string)>();
            foreach ((int, string) k in d.Keys) if (k.Item1 == raw) drop.Add(k);
            foreach (var k in drop) d.Remove(k);
        }
    }

    private static void Reset()
    {
        Looks.Clear();
        Pending.Clear();
        AppliedAt.Clear();
        LastReply.Clear();
        Players.Clear();
        foreach (var s in Mine.Values)
        {
            s.Announced = false;
            s.Payload = null;
        }
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
