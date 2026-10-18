using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.CarScale;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Car Scale's session logic, a few times a second.
/// - Host (or solo): works out every player car's size (<see cref="ScaleMath.SizeFor"/>), resizes it with its
///   physics (the host simulates every car, so everyone drives against the same giant), and sends the size table to
///   the modded players on ModNet channel 12 when it changes, to a player who just connected, and every 5 s.
/// - Modded guest: resizes the cars it sees by the host's table (visual only: the host's physics is what counts),
///   and may ask the host for its own size.
/// Nothing runs unless <see cref="ModManager.ServerModActive"/> says so. As a private mod Car Scale is never listed in
/// the session's required mods, so peers find each other over the mod network instead: a guest shows sizes only
/// while its host runs channel 12 (ModNet.HostHas), and the host's table only goes to players that run it (Broadcast
/// and SendTo skip peers without the channel). Vanilla players see the normal-size model at the host's physics
/// position. Sizing any car marks <see cref="AdvantageFlag"/>.
/// </summary>
internal static class CarScaleCore
{
    /// <summary>ModNet channel id of Car Scale, reserved in core's ModNet.Channels.</summary>
    public const ushort Channel = ModNet.Channels.ReservedPrivate12;
    private const byte MsgTable = 1, MsgRequest = 2;

    private static readonly Dictionary<IntPtr, ScaledCar> Cars = new();
    /// <summary>Host: sizes picked per player (by player id) on the MODS page, for this session.</summary>
    public static readonly Dictionary<int, float> PerPlayer = new();
    /// <summary>Host: sizes modded players asked for (by player id).</summary>
    public static readonly Dictionary<int, float> Requests = new();
    private static readonly Dictionary<int, float> Sizes = new();
    private static Dictionary<int, float> _hostTable = new();
    private static readonly HashSet<string> LoggedOnce = new();

    private static byte[] _lastSent;
    private static float _nextTick, _nextBroadcast, _nextRequest;
    private static float _requestSent = float.NaN;
    private static bool _answered;
    private static readonly HashSet<IntPtr> Seen = new();
    private static IntPtr _runner;
    private static bool _registered;

    /// <summary>The size the host gives your own car right now (1 = normal).</summary>
    public static float MySize { get; private set; } = 1f;

    /// <summary>Guest: the host runs Car Scale's channel.</summary>
    public static bool HostHasIt => ModNet.HostHas(Channel);

    internal static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) CarScalePlugin.Logger?.LogWarning("[Car Scale] " + message + " (logged once)");
    }

    public static void Install()
    {
        _registered = ModNet.Register(Channel, new ModNet.ChannelOptions
        {
            Name = "car scale",
            Version = 1,
            MaxPayload = ScaleMath.EncodedSize(ScaleMath.MaxEntries),
            RatePerSecond = 2f,
            Burst = 6,
            HostOnlySender = true,
            Active = () => ModManager.IsLoaded(CarScalePlugin.ModId),
        }, OnMessage);
        if (!_registered) LogOnce("channel", $"ModNet channel {Channel} is taken: sizes stay on this game only");
        ModNet.OnPeerReady(SendTableTo);
        ModNet.OnSessionReset(ResetSession);
    }

    // ---------------------------------------------------------------- messages

    private static void OnMessage(ModNet.Message m)
    {
        if (m.Type == MsgTable)
        {
            if (GameState.IsAuthority || !m.FromHost) return;
            _hostTable = ScaleMath.DecodeTable(m.Payload);
            _answered = true; // the host has seen our request (it applies it or not): no more retries
            return;
        }
        if (m.Type == MsgRequest)
        {
            if (!GameState.IsAuthority) return;
            var size = ScaleMath.DecodeRequest(m.Payload);
            if (size == null) return;
            int id = m.From.PlayerId;
            if (ScaleMath.IsNormal(size.Value)) Requests.Remove(id);
            else Requests[id] = size.Value;
        }
    }

    private static void SendTableTo(PlayerRef p)
    {
        if (!GameState.IsAuthority || _lastSent == null) return;
        try { ModNet.SendTo(p, Channel, MsgTable, _lastSent); } catch (Exception e) { DebugLog.Error("carscale", e, "CarScaleCore.SendTableTo"); LogOnce("sendto", "send table: " + e.Message); }
    }

    private static void ResetSession()
    {
        DebugLog.Info(CarScalePlugin.ModId, $"session reset: {Cars.Count} resized car(s) put back, {PerPlayer.Count} player pick(s) and {Requests.Count} request(s) dropped");
        RestoreAll();
        PerPlayer.Clear();
        Requests.Clear();
        _hostTable = new Dictionary<int, float>();
        _lastSent = null;
        _requestSent = float.NaN;
        _answered = false;
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        if (CarScaleConfig.Mode == null) return;
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 0.25f;

        var runner = GameState.Runner;
        IntPtr rp = IntPtr.Zero;
        try { rp = runner != null ? runner.Pointer : IntPtr.Zero; } catch (Exception ex) { DebugLog.Error("carscale", ex, "CarScaleCore.Tick"); }
        if (rp != _runner)
        {
            _runner = rp;
            ResetSession();
        }
        if (runner == null || !ModManager.ServerModActive(CarScalePlugin.ModId))
        {
            RestoreAll();
            MySize = 1f;
            return;
        }
        Seen.Clear();
        if (GameState.IsAuthority) HostTick();
        else GuestTick();
        RestoreUnseen();
        Prune();
    }

    private static IEnumerable<(PlayerRef who, VehicleBase car, bool mine)> PlayerCars()
    {
        var me = GameState.LocalPlayer;
        foreach (var fp in GameState.FusionPlayers)
        {
            VehicleBase car = null;
            PlayerRef p = default;
            bool ok = false;
            try
            {
                if (fp == null || fp.Object == null) continue;
                car = fp.vehicleBase;
                p = fp.Object.InputAuthority;
                ok = car != null && p.IsRealPlayer; // driverless cars (AI, spawned) have no player: never resized
            }
            catch (Exception ex) { DebugLog.Error("carscale", ex, "CarScaleCore.foreach"); }
            if (!ok) continue;
            bool mine = me.HasValue ? me.Value.PlayerId == p.PlayerId : car.Pointer == GameState.LocalCarPtr;
            yield return (p, car, mine);
        }
    }

    private static void HostTick()
    {
        var mode = CarScaleConfig.Mode.Value;
        float size = CarScaleConfig.Size.Value;
        bool allow = CarScaleConfig.AllowRequests.Value;
        bool physics = GameState.PhysicsAllowed;
        Sizes.Clear();
        float mine = 1f;
        foreach (var (who, car, isMe) in PlayerCars())
        {
            int id = who.PlayerId;
            float? pick = PerPlayer.TryGetValue(id, out var pv) ? pv : null;
            float? asked = Requests.TryGetValue(id, out var rv) ? rv : null;
            float s = ScaleMath.SizeFor(mode, isMe, size, pick, asked, allow);
            Sizes[id] = s;
            if (isMe) mine = s;
            Resize(car, s, physics);
        }
        MySize = mine;

        if (!_registered) return;
        var bytes = ScaleMath.EncodeTable(Sizes);
        bool changed = _lastSent == null || !bytes.SequenceEqual(_lastSent);
        if (!changed && Time.unscaledTime < _nextBroadcast) return;
        _lastSent = bytes;
        _nextBroadcast = Time.unscaledTime + 5f;
        if (changed) DebugLog.Verbose(CarScalePlugin.ModId, () => $"size table sent: {Sizes.Count} entries, {bytes.Length} B, mode {mode}");
        try { ModNet.Broadcast(Channel, MsgTable, bytes); } catch (Exception e) { DebugLog.Error("carscale", e, "CarScaleCore.HostTick"); LogOnce("broadcast", "send table: " + e.Message); }
    }

    private static void GuestTick()
    {
        if (!HostHasIt)
        {
            RestoreAll();
            MySize = 1f;
            return;
        }
        float mine = 1f;
        foreach (var (who, car, isMe) in PlayerCars())
        {
            float s = _hostTable.TryGetValue(who.PlayerId, out var v) ? v : 1f;
            if (isMe) mine = s;
            Resize(car, s, physics: false);
        }
        MySize = mine;
        SendRequestIfNeeded();
    }

    /// <summary>Guest: asks the host for <see cref="CarScaleConfig.MySize"/> when it changed (again every 15 s while the
    /// host shows another size, in case the host restarted or missed it). Never as a public guest.</summary>
    private static void SendRequestIfNeeded()
    {
        if (GameState.IsPublicGuest || Time.unscaledTime < _nextRequest) return;
        float want = ScaleMath.Clamp(CarScaleConfig.MySize.Value);
        bool asked = !float.IsNaN(_requestSent);
        // Nothing asked yet this session: only a size other than normal is worth a message.
        bool changed = asked ? !ScaleMath.Same(want, _requestSent) : !ScaleMath.IsNormal(want);
        // Retried only until the host's next table came back: a host that doesn't let players choose answers with
        // another size, and asking again would not change that.
        bool retry = asked && !ScaleMath.Same(want, MySize) && !_answered;
        if (!changed && !retry) return;
        var r = ModNet.SendToHost(Channel, MsgRequest, ScaleMath.EncodeRequest(want));
        bool sent = r == ModNet.SendResult.Sent;
        DebugLog.Info(CarScalePlugin.ModId, $"asked the host for size {want:0.00}: {r}");
        if (sent)
        {
            _requestSent = want;
            _answered = false;
        }
        _nextRequest = Time.unscaledTime + (sent ? 15f : 2f);
    }

    /// <summary>Guest: the size setting changed on the page, ask the host on the next check.</summary>
    public static void RequestSoon() => _nextRequest = 0f;

    // ---------------------------------------------------------------- resizing

    private static void Resize(VehicleBase car, float size, bool physics)
    {
        IntPtr ptr = car.Pointer;
        Seen.Add(ptr);
        bool normal = ScaleMath.IsNormal(size);
        if (!Cars.TryGetValue(ptr, out var sc))
        {
            if (normal) return;
            try { sc = new ScaledCar(car); }
            catch (Exception e) { DebugLog.Error("carscale", e, "CarScaleCore.Resize"); LogOnce("new", "reading a car: " + e.Message); return; }
            Cars[ptr] = sc;
            DebugLog.Verbose(CarScalePlugin.ModId, () => $"a car is resized to {size:0.00} ({Cars.Count} resized car(s), physics {physics})");
        }
        try
        {
            sc.SetVisual(size);
            if (physics) sc.SetPhysics(size, CarScaleConfig.ScaleSuspension.Value);
            else if (!ScaleMath.IsNormal(sc.Physics)) sc.RestorePhysics(); // no longer simulating it here
        }
        catch (Exception e) { DebugLog.Error("carscale", e, "CarScaleCore.Resize"); LogOnce("resize", "resizing a car: " + e.Message); }
        if (!normal) AdvantageFlag.MarkUsed();
        else if (ScaleMath.IsNormal(sc.Physics)) Cars.Remove(ptr);
    }

    private static readonly List<IntPtr> Dead = new();

    /// <summary>
    /// Cars resized earlier that this round's player list no longer has (the player left, or the car stopped being a
    /// player car): back to normal, so nothing stays giant without a reason.
    /// </summary>
    private static void RestoreUnseen()
    {
        if (Cars.Count == 0) return;
        Dead.Clear();
        foreach (var kv in Cars) if (!Seen.Contains(kv.Key)) Dead.Add(kv.Key);
        foreach (var k in Dead)
        {
            var sc = Cars[k];
            if (sc.Alive) sc.Restore();
            Cars.Remove(k);
        }
    }

    private static void Prune()
    {
        Dead.Clear();
        foreach (var kv in Cars) if (!kv.Value.Alive) Dead.Add(kv.Key);
        foreach (var k in Dead) Cars.Remove(k);
    }

    public static void RestoreAll()
    {
        if (Cars.Count == 0) return;
        foreach (var sc in Cars.Values) if (sc.Alive) sc.Restore();
        Cars.Clear();
    }

    // ---------------------------------------------------------------- for the page

    public sealed class PlayerRow
    {
        public int Id;
        public string Name;
        public bool IsMe;
        public float Size;
        public float? Pick, Asked;
    }

    /// <summary>The player cars and their sizes now (host page).</summary>
    public static List<PlayerRow> Rows()
    {
        var list = new List<PlayerRow>();
        OverlandSessionPlayers sp = null;
        try { sp = OverlandSessionPlayers.Instance; } catch (Exception ex) { DebugLog.Error("carscale", ex, "CarScaleCore.Rows"); }
        foreach (var (who, _, isMe) in PlayerCars())
        {
            int id = who.PlayerId;
            string name;
            try { name = PlayerListRoster.NameOf(sp, who); } catch (Exception ex) { DebugLog.Error("carscale", ex, "CarScaleCore.Rows"); name = null; }
            list.Add(new PlayerRow
            {
                Id = id,
                Name = string.IsNullOrEmpty(name) ? $"Player {id}" : name,
                IsMe = isMe,
                Size = Sizes.TryGetValue(id, out var s) ? s : 1f,
                Pick = PerPlayer.TryGetValue(id, out var pv) ? pv : null,
                Asked = Requests.TryGetValue(id, out var rv) ? rv : null,
            });
        }
        list.Sort((a, b) => a.IsMe != b.IsMe ? (a.IsMe ? -1 : 1) : a.Id.CompareTo(b.Id));
        return list;
    }
}
