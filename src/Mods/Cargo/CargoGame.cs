using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.UI;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;
using Random = System.Random;

namespace OverTheHillPlus.Cargo;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The session's cargo jobs.
/// <list type="bullet">
/// <item>Host (or solo): keeps the <see cref="CargoBoard"/> (jobs between the map's points of interest), takes and
///   drops jobs for players, and judges pick-ups and deliveries from the car positions it simulates itself; it pays
///   itself directly and sends each guest a payment for that guest's delivery. Pay per player per hour is capped
///   (<see cref="CargoConfig.HourlyPayCap"/>). The board goes to modded guests on change and every few seconds.</item>
/// <item>Guest: shows the board, asks the host to take or drop a job, and adds pebbles to its own inventory only for
///   a payment from the host itself (never relayed), for the job it carries, at most that job's pay, once per job and
///   receipt, within its own hourly cap.</item>
/// </list>
/// Channel <see cref="Channel"/>. Networked behaviour only runs while ModManager.ServerModActive("cargo").
/// </summary>
internal static class CargoGame
{
    /// <summary>ModNet channel 9 (Cargo Jobs). Listed in ModNet.Channels.</summary>
    internal const ushort Channel = Net.Channels.Cargo;
    private const byte MsgBoard = 1, MsgTake = 2, MsgAbandon = 3, MsgReply = 4, MsgPaid = 5;
    private const float CheckEvery = 0.25f, ExpireEvery = 5f, BroadcastEvery = 3f, StaleAfter = 12f, MissingCarGrace = 30f;

    private enum Reply : byte { Taken = 0, NoSuchJob = 1, AlreadyTaken = 2, HaveAJob = 3, Abandoned = 4, Loaded = 5, Rejected = 6, TimedOut = 7, Off = 8 }

    // host
    private static CargoBoard _board;
    private static IntPtr _boardScene;
    private static readonly PayLedger HostLedger = new();
    private static readonly Random Rng = new();
    private static int _receipt = new Random().Next(1, int.MaxValue / 2);
    // Job ids run on across boards (a new map makes a new board) and start at random, so a guest never sees an id
    // again that it was already paid for.
    private static int _nextJobId = new Random().Next(1, 1 << 30);
    /// <summary>A guest's own pay is always this player's: one fixed key, whatever its session slot.</summary>
    private const string SelfKey = "self";
    private static float _nextCheck, _nextExpire, _nextBroadcast;
    private static bool _dirty;
    private static readonly Dictionary<int, float> MissingSince = new();
    private static readonly List<(float at, PlayerRef to, byte[] payload, string key, int amount, int job)> Retries = new();

    // everyone
    private static CargoJob _myJob;
    private static readonly PayReceipts Receipts = new();
    private static readonly PayLedger OwnLedger = new();
    private static int _pendingPay;
    private static float _askedAt = -100f;
    private static readonly HashSet<string> LoggedOnce = new();

    /// <summary>The board as last seen (the host's own, or the one it sent); null: none.</summary>
    public static List<CargoJob> View { get; private set; }
    public static float ViewAt { get; private set; } = -100f;

    public static readonly List<(string text, float at, Color color)> Notices = new();

    internal static bool Active => ModManager.ServerModActive(CargoPlugin.ModId);

    /// <summary>This game keeps the board: solo or hosting, in a world.</summary>
    internal static bool Authority
    {
        get
        {
            var r = GameState.Runner;
            try { return r != null && r.IsRunning && GameState.IsAuthority; } catch (Exception ex) { DebugLog.Error("cargo", ex, "CargoGame.Authority"); return false; }
        }
    }

    internal static bool Fresh => View != null && (Authority || Time.unscaledTime - ViewAt < StaleAfter);

    /// <summary>Your job (as last seen on the board), or null.</summary>
    internal static CargoJob MyJob
    {
        get
        {
            if (!Fresh) return null;
            int me = CargoCars.Me;
            foreach (var j in View) if (j.Carrier == me && j.State != JobState.Open) return j;
            return null;
        }
    }

    internal static bool Waiting => Time.unscaledTime - _askedAt < 5f;

    // ---------------------------------------------------------------- setup

    internal static void Register()
    {
        Net.Register(Channel, new Net.ChannelOptions
        {
            Name = "cargo",
            Version = 1,
            MaxPayload = Net.HardMaxPayload, // the board is encoded to at most CargoBoard.MaxEncodedBytes
            RatePerSecond = 3f,
            Burst = 8,
            HostOnlySender = true,
            Active = () => Active,
        }, OnMessage);
        Net.OnPeerReady(p => _nextBroadcast = 0f);
        Net.OnPeerGone(p => { if (_board != null) { _board.PlayerLeft(p.RawEncoded); _dirty = true; } });
        Net.OnSessionReset(Reset);
    }

    private static void Reset()
    {
        if (_board != null || View != null) DebugLog.Info(CargoPlugin.ModId, $"job board dropped ({_board?.Jobs.Count ?? View?.Count ?? 0} jobs)");
        if (_board != null) _nextJobId = _board.NextId;
        _board = null;
        _boardScene = IntPtr.Zero;
        View = null;
        ViewAt = -100f;
        _myJob = null;
        Receipts.Clear();
        MissingSince.Clear();
        foreach (var r in Retries) HostLedger.Refund(r.key, r.amount); // the session ended before they were sent
        Retries.Clear();
        _askedAt = -100f;
    }

    // ---------------------------------------------------------------- the player's actions

    public static string Take(int jobId)
    {
        if (!Active) return "Blocked: Cargo Jobs is not running in this session";
        if (Authority)
        {
            if (_board == null) return "Failed: no job board yet";
            var res = _board.Take(CargoCars.Me, jobId, Time.unscaledTime);
            DebugLog.Info(CargoPlugin.ModId, $"take job {jobId} (own): {res}");
            if (res == TakeResult.Ok) _dirty = true;
            return Text(ReplyOf(res));
        }
        if (!Net.HostHas(Channel)) return "Blocked: the host doesn't run Cargo Jobs";
        if (Waiting) return "Blocked: waiting for the host";
        var sent = Net.SendToHost(Channel, MsgTake, new NetWriter(8).Int(jobId).ToArray());
        DebugLog.Info(CargoPlugin.ModId, $"asked the host for job {jobId}: {sent}");
        if (sent != Net.SendResult.Sent) return "Failed: could not reach the host (" + sent + ")";
        _askedAt = Time.unscaledTime;
        return "Asked the host for the job...";
    }

    public static string Abandon()
    {
        if (!Active) return "Blocked: Cargo Jobs is not running in this session";
        if (Authority)
        {
            if (_board == null || !_board.Abandon(CargoCars.Me)) return "You have no job";
            DebugLog.Info(CargoPlugin.ModId, "own job dropped, back on the board");
            _dirty = true;
            return "Job dropped: it's back on the board";
        }
        if (!Net.HostHas(Channel)) return "Blocked: the host doesn't run Cargo Jobs";
        var sent = Net.SendToHost(Channel, MsgAbandon, new NetWriter(4).Byte(1).ToArray());
        if (sent != Net.SendResult.Sent) return "Failed: could not reach the host (" + sent + ")";
        _askedAt = Time.unscaledTime;
        return "Asked the host to drop the job...";
    }

    // ---------------------------------------------------------------- every frame

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (Notices.Count > 0 && now - Notices[0].at > 6f) Notices.RemoveAt(0);
        PayOut();
        if (!Active) { if (_board != null || View != null) Reset(); return; }
        if (Authority) HostTick(now);
        else if (_board != null) { _nextJobId = _board.NextId; _board = null; View = null; }
        int me = CargoCars.Me;
        var mine = MyJob;
        if (mine != null && mine.Carrier == me) _myJob = mine;
    }

    private static void HostTick(float now)
    {
        IntPtr scene = IntPtr.Zero;
        try { scene = SceneReferences.Instance != null ? SceneReferences.Instance.Pointer : IntPtr.Zero; } catch (Exception ex) { DebugLog.Error("cargo", ex, "CargoGame.HostTick"); }
        if (scene == IntPtr.Zero) return; // garage / loading
        if (_board == null || scene != _boardScene)
        {
            if (_board != null) _nextJobId = _board.NextId;
            _board = new CargoBoard(new CargoSettings(), _nextJobId);
            DebugLog.Info(CargoPlugin.ModId, $"hosting: new job board for this map (next job id {_nextJobId})");
            _boardScene = scene;
            MissingSince.Clear();
            _nextExpire = 0f;
            _dirty = true;
        }
        Apply(_board.Settings);

        if (now >= _nextCheck)
        {
            _nextCheck = now + CheckEvery;
            CargoCars.Refresh();
            foreach (var job in _board.Jobs.Where(j => j.State != JobState.Open).ToList())
            {
                int who = job.Carrier;
                if (!CargoCars.TryPos(who, out var p))
                {
                    if (!MissingSince.TryGetValue(who, out float since)) MissingSince[who] = now;
                    else if (now - since > MissingCarGrace) { _board.PlayerLeft(who); MissingSince.Remove(who); _dirty = true; }
                    continue;
                }
                MissingSince.Remove(who);
                string to = job.ToName;
                var ev = _board.Update(who, new Vec3(p.x, p.y, p.z), now);
                if (ev.HasValue) Handle(ev.Value, to);
            }
        }
        if (now >= _nextExpire)
        {
            _nextExpire = now + ExpireEvery;
            foreach (var ev in _board.Expire(now)) Handle(ev, null);
            if (_board.Jobs.Count < _board.Settings.BoardSize)
            {
                int made = _board.Refill(CargoPois.Get(), Rng);
                if (made > 0)
                {
                    _dirty = true;
                    DebugLog.Verbose(CargoPlugin.ModId, () => $"{made} job(s) generated, {_board.Jobs.Count} on the board (size {_board.Settings.BoardSize})");
                }
            }
        }
        RetryPayments(now);
        Publish(now);
    }

    /// <summary>The second and last try of a payment that couldn't be sent; one that still fails isn't counted
    /// against the player's cap.</summary>
    private static void RetryPayments(float now)
    {
        for (int i = Retries.Count - 1; i >= 0; i--)
        {
            var r = Retries[i];
            if (now < r.at) continue;
            Retries.RemoveAt(i);
            var res = Net.SendTo(r.to, Channel, MsgPaid, r.payload);
            if (res == Net.SendResult.Sent) continue;
            HostLedger.Refund(r.key, r.amount);
            CargoPlugin.Logger.LogWarning($"[Cargo] Could not send a player their pay for job {r.job} ({res}); not counted against their cap");
        }
    }

    private static void Apply(CargoSettings s)
    {
        s.BoardSize = CargoConfig.BoardSize.Value;
        s.PayPerKm = CargoConfig.PayPerKm.Value;
        s.PickupRadius = CargoConfig.PickupRadius.Value;
        s.DeliveryRadius = CargoConfig.DeliveryRadius.Value;
    }

    private static void Handle(CargoEvent ev, string toName)
    {
        _dirty = true;
        DebugLog.Info(CargoPlugin.ModId, $"job {ev.JobId}: {ev.Kind} (player {ev.Player}{(ev.Kind == CargoEventKind.Delivered ? ", pay " + ev.Pay : "")})");
        int me = CargoCars.Me;
        switch (ev.Kind)
        {
            case CargoEventKind.Loaded: Tell(ev.Player, Reply.Loaded); break;
            case CargoEventKind.Rejected:
                Tell(ev.Player, Reply.Rejected);
                LogOnce("rejected" + ev.Player, $"[Cargo] Refused a delivery by {CargoCars.ShortName(ev.Player)}: faster than a car can drive");
                break;
            case CargoEventKind.TimedOut: Tell(ev.Player, Reply.TimedOut); break;
            case CargoEventKind.Delivered:
                string key = LedgerKeyOf(ev.Player);
                int granted = HostLedger.Grant(key, ev.Pay, Time.unscaledTime, CargoConfig.HourlyPayCap.Value);
                if (ev.Player == me)
                {
                    if (granted > 0)
                    {
                        if (OwnLedger.Grant(SelfKey, granted, Time.unscaledTime, 0) > 0) _pendingPay += granted;
                        Notice($"Delivered! +{granted:N0} pebbles", Theme.Good);
                    }
                    else Notice("Delivered, but you hit the hourly pay cap", Theme.Warn);
                }
                else
                {
                    int receipt = ++_receipt;
                    byte[] payload = new NetWriter(16).Int(receipt).Int(ev.JobId).Int(granted).Int(ev.Pay).ToArray();
                    var to = PlayerRef.FromEncoded(ev.Player);
                    var res = Net.SendTo(to, Channel, MsgPaid, payload);
                    // Not sent (rate limit, link busy): once more in a second, with the same receipt.
                    if (res != Net.SendResult.Sent) Retries.Add((Time.unscaledTime + 1f, to, payload, key, granted, ev.JobId));
                }
                CargoPlugin.Logger.LogInfo($"[Cargo] {CargoCars.ShortName(ev.Player)} delivered job {ev.JobId}: {granted} of {ev.Pay} pebbles");
                if (CargoConfig.Announce.Value && Net.IsHost)
                    HostNotice.Announce($"{HostNotice.SafeName(CargoCars.NameOf(ev.Player))} delivered cargo to {toName ?? "its destination"}" +
                                       (granted > 0 ? $" (+{granted:N0} pebbles)" : ""), new Color(0.36f, 0.89f, 0.54f));
                break;
        }
    }

    /// <summary>Host: tells a player (itself: a notice) what happened to their job.</summary>
    private static void Tell(int player, Reply code)
    {
        if (player == CargoCars.Me) { Notice(Text(code), ColorOf(code)); return; }
        if (player < 0) return;
        Net.SendTo(PlayerRef.FromEncoded(player), Channel, MsgReply, new NetWriter(4).Byte((byte)code).ToArray());
    }

    private static void Publish(float now)
    {
        if (_board == null || (!_dirty && now < _nextBroadcast)) return;
        _dirty = false;
        _nextBroadcast = now + BroadcastEvery;
        byte[] bytes = _board.Encode(now);
        DebugLog.Verbose(CargoPlugin.ModId, () => $"board published: {_board.Jobs.Count} job(s), {bytes.Length} B, {Net.PeerCount} modded guest(s)");
        var view = CargoBoard.Decode(bytes);
        if (view != null) { View = view; ViewAt = now; }
        if (Net.IsHost && Net.PeerCount > 0)
        {
            try { Net.Broadcast(Channel, MsgBoard, bytes); }
            catch (Exception e) { DebugLog.Error("cargo", e, "CargoGame.Publish"); LogOnce("send", "[Cargo] broadcast failed: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- messages

    private static void OnMessage(Net.Message m)
    {
        if (!Active) return;
        if (Net.IsHost)
        {
            if (!m.From.IsRealPlayer || _board == null || m.Relayed) return;
            int who = m.From.RawEncoded;
            if (m.Type == MsgTake)
            {
                var rd = new NetReader(m.Payload);
                int id = rd.Int();
                if (!rd.Ok) return;
                var res = _board.Take(who, id, Time.unscaledTime);
                if (res == TakeResult.Ok) _dirty = true;
                Tell(who, ReplyOf(res));
            }
            else if (m.Type == MsgAbandon)
            {
                if (_board.Abandon(who)) { _dirty = true; Tell(who, Reply.Abandoned); }
            }
            return;
        }
        if (!m.FromHost) return;
        switch (m.Type)
        {
            case MsgBoard:
                var board = CargoBoard.Decode(m.Payload);
                if (board == null) { LogOnce("bad", "[Cargo] dropped a job board that didn't decode"); return; }
                foreach (var j in board) { j.FromName = HostNotice.Escape(j.FromName); j.ToName = HostNotice.Escape(j.ToName); }
                View = board;
                ViewAt = Time.unscaledTime;
                break;
            case MsgReply:
            {
                var rd = new NetReader(m.Payload);
                var code = (Reply)rd.Byte();
                if (!rd.Ok) return;
                _askedAt = -100f;
                Notice(Text(code), ColorOf(code));
                break;
            }
            case MsgPaid:
            {
                var rd = new NetReader(m.Payload);
                int receipt = rd.Int(), jobId = rd.Int(), amount = rd.Int(), full = rd.Int();
                if (!rd.Ok) return;
                GuestPaid(receipt, jobId, amount, full);
                break;
            }
        }
    }

    private static void GuestPaid(int receipt, int jobId, int amount, int full)
    {
        if (amount <= 0)
        {
            Notice("Delivered, but you hit the host's hourly pay cap", Theme.Warn);
            return;
        }
        if (!Receipts.Check(receipt, jobId, amount, _myJob, out string why))
        {
            LogOnce("refused" + why, $"[Cargo] Ignored a payment of {amount} for job {jobId}: {why}");
            return;
        }
        int allowed = OwnLedger.Grant(SelfKey, amount, Time.unscaledTime, CargoConfig.HourlyPayCap.Value);
        if (allowed <= 0) { Notice("Delivered, but you hit your hourly pay cap", Theme.Warn); return; }
        _pendingPay += allowed;
        Notice($"Delivered! +{allowed:N0} pebbles" + (allowed < full ? " (capped)" : ""), Theme.Good);
    }

    // ---------------------------------------------------------------- pebbles

    /// <summary>Adds earned pay to this player's own inventory (the game's money item), once it is loaded.</summary>
    private static void PayOut()
    {
        if (_pendingPay <= 0) return;
        try
        {
            var m = PlayerInventoryManager.Instance;
            if (m == null || m.Inventory == null) return; // retried next frame
            int amount = _pendingPay;
            _pendingPay = 0;
            m.Inventory.AddItem(m.MoneyItemId, amount, false, false);
        }
        catch (Exception e) { DebugLog.Error("cargo", e, "CargoGame.PayOut"); LogOnce("payout", "[Cargo] could not add pebbles: " + e.Message); }
    }

    /// <summary>Pay received in the last hour, and the cap ("" when there is none).</summary>
    internal static string HourText()
    {
        int cap = CargoConfig.HourlyPayCap.Value;
        int paid = OwnLedger.PaidInWindow(SelfKey, Time.unscaledTime);
        return cap <= 0 ? $"{paid:N0} pebbles" : $"{paid:N0} of {cap:N0} pebbles";
    }

    /// <summary>Host: the key a player's hourly pay is counted under, their account id when the session has it.</summary>
    private static string LedgerKeyOf(int player)
    {
        string user = null;
        try { user = GameState.Runner?.GetPlayerUserId(PlayerRef.FromEncoded(player)); } catch (Exception ex) { DebugLog.Error("cargo", ex, "CargoGame.LedgerKeyOf"); }
        if (string.IsNullOrWhiteSpace(user)) LogOnce("nouser", "[Cargo] A player's account id is unknown: their hourly pay is counted per session slot");
        return CargoRules.LedgerKey(user, player);
    }

    // ---------------------------------------------------------------- texts

    private static Reply ReplyOf(TakeResult r) => r switch
    {
        TakeResult.Ok => Reply.Taken,
        TakeResult.NoSuchJob => Reply.NoSuchJob,
        TakeResult.AlreadyTaken => Reply.AlreadyTaken,
        _ => Reply.HaveAJob,
    };

    private static string Text(Reply r) => r switch
    {
        Reply.Taken => "Job taken: drive to the pick-up",
        Reply.NoSuchJob => "That job is gone",
        Reply.AlreadyTaken => "Someone else took that job",
        Reply.HaveAJob => "You already have a job: deliver or drop it first",
        Reply.Abandoned => "Job dropped: it's back on the board",
        Reply.Loaded => "Cargo loaded: now deliver it",
        Reply.Rejected => "Delivery refused: that was faster than any car drives",
        Reply.TimedOut => "Your job ran out of time and went back on the board",
        _ => "The host has Cargo Jobs off",
    };

    private static Color ColorOf(Reply r) => r switch
    {
        Reply.Taken or Reply.Loaded => Theme.Info,
        Reply.Abandoned => Theme.Muted,
        Reply.Rejected or Reply.TimedOut => Theme.Bad,
        _ => Theme.Warn,
    };

    private static void Notice(string text, Color color)
    {
        Notices.Add((text, Time.unscaledTime, color));
        if (Notices.Count > 4) Notices.RemoveAt(0);
        try { Menu.SetStatus(text); } catch (Exception ex) { DebugLog.Error("cargo", ex, "CargoGame.Notice"); }
    }

    internal static string Status()
    {
        if (!Active) return GameState.Runner == null ? "Load into a world" : "Not running in this session";
        if (!Fresh) return Authority ? "Setting up the job board..." : "Waiting for the host's job board";
        return $"{View.Count(j => j.State == JobState.Open)} open jobs · earned this hour: {HourText()}";
    }

    internal static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Add(id)) CargoPlugin.Logger.LogWarning(message);
    }
}
