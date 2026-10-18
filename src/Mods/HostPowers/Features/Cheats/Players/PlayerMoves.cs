using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Cheats.Teleport;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.HostPowers;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Players;

/// <summary>
/// Host teleports for the Players tab: go to / bring / send to my pin / swap, everyone to me in a formation and
/// summon everyone to the pin. The host simulates every car, so VehicleBase.Reposition (through
/// <see cref="TeleportCore.MoveCar"/>: winches detached first, speed zeroed) moves vanilla players too.
/// Every drop point comes from <see cref="SafeSpot"/>; a player moved recently is skipped (per-player cooldown).
/// Group moves run a few cars per frame so a big lobby doesn't hitch the host.
/// </summary>
internal static class PlayerMoves
{
    internal sealed class Target
    {
        public PlayerRef Ref;
        public string Name;
        public Color Color;
        public VehicleBase Car;
        public bool IsMe, IsHost;
        public Vector3 Position;
        public float Yaw;
        public bool HasCar => Car != null;
    }

    private sealed class Move
    {
        public Target Who;
        public Vector3 Wanted;
        public float Yaw;
    }

    private sealed class Batch
    {
        public string Label;
        public readonly Queue<Move> Pending = new();
        public readonly SafeSpot.Context Ctx = new();
        public int Moved, Skipped;
        public readonly List<string> Problems = new();
        public string MyResult;
    }

    private const int CarsPerFrame = 2;
    private const float FollowGap = 9f;

    private static readonly Dictionary<int, float> LastMoved = new(); // RawEncoded -> time
    private static Batch _batch;

    private static readonly List<Target> Roster = new();
    private static float _rosterAt = -10f;

    public static bool Busy => _batch != null;
    public static string BusyLabel => _batch?.Label;
    public static int BusyLeft => _batch?.Pending.Count ?? 0;

    /// <summary>Why host moves can't run here (null = they can).</summary>
    public static string NotAllowedReason
    {
        get
        {
            if (!GameState.IsAuthority || GameState.Runner == null) return "Blocked: host only (solo or hosting).";
            if (!GameState.PhysicsAllowed) return "Blocked: this game doesn't simulate the cars here.";
            return null;
        }
    }

    // ---------------------------------------------------------------- roster

    /// <summary>Everyone in the session (PlayerListRoster) with their car, you first. Cached for half a second.</summary>
    public static List<Target> Players()
    {
        if (Time.unscaledTime - _rosterAt < 0.5f && Time.unscaledTime >= _rosterAt) return Roster;
        _rosterAt = Time.unscaledTime;
        Roster.Clear();
        try
        {
            var cars = new Dictionary<int, VehicleBase>();
            foreach (var fp in GameState.FusionPlayers)
            {
                try
                {
                    if (fp == null || fp.Object == null) continue;
                    var car = fp.vehicleBase;
                    if (car == null) continue;
                    cars[fp.Object.InputAuthority.RawEncoded] = car;
                }
                catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PlayerMoves.Players"); }
            }
            OverlandSessionPlayers sp = null;
            try { sp = OverlandSessionPlayers.Instance; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PlayerMoves.Players"); }
            var me = GameState.LocalPlayer;
            var refs = PlayerListRoster.AllPlayers();
            // Solo: the runner may list nobody, but you still have a car.
            if (refs.Count == 0 && me.HasValue && me.Value.IsRealPlayer) refs.Add(me.Value);
            foreach (var p in refs)
            {
                var d = PlayerListRoster.Describe(sp, p);
                var t = new Target
                {
                    Ref = p,
                    Name = Clean(d.Name),
                    Color = d.Color,
                    IsMe = d.IsLocal || (me.HasValue && me.Value.RawEncoded == p.RawEncoded),
                    IsHost = d.IsHost,
                };
                if (cars.TryGetValue(p.RawEncoded, out var car)) t.Car = car;
                else if (t.IsMe && GameState.LocalCar != null) t.Car = GameState.LocalCar;
                Refresh(t);
                Roster.Add(t);
            }
            Roster.Sort((a, b) => a.IsMe != b.IsMe ? (a.IsMe ? -1 : 1) : a.Ref.PlayerId.CompareTo(b.Ref.PlayerId));
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "PlayerMoves.Players"); SafeSpot.LogOnce("roster", "player list: " + e.Message); }
        return Roster;
    }

    public static void InvalidateRoster() => _rosterAt = -10f;

    public static Target Find(PlayerRef p)
    {
        foreach (var t in Players())
            if (t.Ref.RawEncoded == p.RawEncoded) return t;
        return null;
    }

    public static Target Me()
    {
        foreach (var t in Players())
            if (t.IsMe) return t;
        return null;
    }

    /// <summary>Re-reads a target's car position and heading (false when the car is gone).</summary>
    private static bool Refresh(Target t)
    {
        try
        {
            if (t.Car == null) { t.Car = null; return false; }
            var tr = t.Car.cachedTransform != null ? t.Car.cachedTransform : t.Car.transform;
            t.Position = tr.position;
            t.Yaw = FlatYaw(tr);
            return SafeSpot.IsFinite(t.Position);
        }
        catch (Exception ex)
        {
            DebugLog.Error("hostpowers", ex, "PlayerMoves.Refresh");
            t.Car = null;
            return false;
        }
    }

    internal static float FlatYaw(Transform t)
    {
        var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.ProjectOnPlane(-t.up, Vector3.up); // nose straight up or down
        return fwd.sqrMagnitude < 0.01f ? t.eulerAngles.y : Quaternion.LookRotation(fwd.normalized, Vector3.up).eulerAngles.y;
    }

    private static float YawTowards(Vector3 from, Vector3 to, float fallback)
    {
        var d = Vector3.ProjectOnPlane(to - from, Vector3.up);
        return d.sqrMagnitude < 1f ? fallback : Quaternion.LookRotation(d.normalized, Vector3.up).eulerAngles.y;
    }

    private static string Clean(string s) => string.IsNullOrEmpty(s) ? "?" : s.Replace('<', '‹').Replace('>', '›');

    // ---------------------------------------------------------------- cooldown

    public static float CooldownLeft(Target t)
    {
        if (t == null || t.IsMe) return 0f;
        float cd = PlayersFeature.Cooldown?.Value ?? 3f;
        return LastMoved.TryGetValue(t.Ref.RawEncoded, out float at) ? Mathf.Max(0f, at + cd - Time.unscaledTime) : 0f;
    }

    public static void Forget(PlayerRef p) => LastMoved.Remove(p.RawEncoded);

    public static void ResetSession()
    {
        LastMoved.Clear();
        _batch = null;
        InvalidateRoster();
    }

    // ---------------------------------------------------------------- single moves

    private static string Precheck(Target t, bool needMyCar, out Target me, bool movesThem = true)
    {
        me = null;
        var blocked = NotAllowedReason;
        if (blocked != null) return blocked;
        if (Busy) return $"Blocked: still moving players ({_batch.Label}).";
        if (t == null || !Refresh(t)) return "Failed: that player has no car yet.";
        if (needMyCar)
        {
            me = Me();
            if (me == null || !Refresh(me)) return "Failed: you have no car (are you driving?).";
            if (t.Ref.RawEncoded == me.Ref.RawEncoded) return "That's you.";
        }
        float cd = movesThem ? CooldownLeft(t) : 0f;
        if (cd > 0f) return $"Blocked: {t.Name} was just moved, wait {Mathf.CeilToInt(cd)} s.";
        return null;
    }

    /// <summary>You drive to a spot behind the player, facing their way.</summary>
    public static string GoTo(Target t)
    {
        var err = Precheck(t, true, out var me, movesThem: false);
        if (err != null) return err;
        var ctx = new SafeSpot.Context();
        ctx.Ignore(me.Car);
        var wanted = t.Position - Quaternion.Euler(0f, t.Yaw, 0f) * Vector3.forward * FollowGap;
        var spot = SafeSpot.Find(wanted, t.Yaw, ctx);
        if (!spot.Ok) return $"Failed: no safe spot near {t.Name} ({spot.Why}).";
        string s = TeleportCore.TeleportSelf(spot.Ground + Vector3.up * Clearance, spot.Yaw, t.Name, snap: false, remember: true);
        return s;
    }

    /// <summary>The player is dropped behind you, facing your way.</summary>
    public static string Bring(Target t)
    {
        var err = Precheck(t, true, out var me);
        if (err != null) return err;
        var ctx = new SafeSpot.Context();
        ctx.Ignore(t.Car);
        var wanted = me.Position - Quaternion.Euler(0f, me.Yaw, 0f) * Vector3.forward * FollowGap;
        return Place(t, wanted, me.Yaw, ctx, $"Brought {t.Name} to you");
    }

    /// <summary>The player goes to your map pin.</summary>
    public static string SendToMyPin(Target t)
    {
        var err = Precheck(t, false, out _);
        if (err != null) return err;
        if (t.IsMe) return "Use Teleport > map pin for yourself.";
        if (!TeleportCore.TryGetPin(out var pin)) return "Failed: no map pin. Open the map and place a pin first.";
        var ctx = new SafeSpot.Context();
        ctx.Ignore(t.Car);
        return Place(t, pin, YawTowards(t.Position, pin, t.Yaw), ctx, $"Sent {t.Name} to your pin");
    }

    /// <summary>You and the player trade places (and headings).</summary>
    public static string Swap(Target t)
    {
        var err = Precheck(t, true, out var me);
        if (err != null) return err;
        var ctx = new SafeSpot.Context();
        ctx.Ignore(t.Car);
        ctx.Ignore(me.Car);
        var forThem = SafeSpot.Find(me.Position, me.Yaw, ctx);
        if (!forThem.Ok) return $"Failed: no safe spot at your position ({forThem.Why}).";
        ctx.Occupied.Add(forThem.Ground);
        var forMe = SafeSpot.Find(t.Position, t.Yaw, ctx);
        if (!forMe.Ok) return $"Failed: no safe spot at {t.Name}'s position ({forMe.Why}).";
        try
        {
            TeleportCore.MoveCar(t.Car, forThem.Ground + Vector3.up * Clearance, forThem.Yaw, false);
            LastMoved[t.Ref.RawEncoded] = Time.unscaledTime;
            AdvantageFlag.MarkUsed();
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "PlayerMoves.Swap"); return "Failed: " + e.Message; }
        string mine = TeleportCore.TeleportSelf(forMe.Ground + Vector3.up * Clearance, forMe.Yaw, t.Name + "'s spot", snap: false, remember: true);
        return mine.StartsWith("Failed") || mine.StartsWith("Blocked") ? $"{t.Name} moved, but you didn't: {mine}" : $"Swapped places with {t.Name}.";
    }

    /// <summary>Moves someone else's car to a safe spot near <paramref name="wanted"/>. Caller checked the rules.</summary>
    internal static string Place(Target t, Vector3 wanted, float yaw, SafeSpot.Context ctx, string done)
    {
        var spot = SafeSpot.Find(wanted, yaw, ctx);
        if (!spot.Ok) return $"Failed: no safe spot for {t.Name} ({spot.Why}).";
        try
        {
            TeleportCore.MoveCar(t.Car, spot.Ground + Vector3.up * Clearance, spot.Yaw, false);
            LastMoved[t.Ref.RawEncoded] = Time.unscaledTime;
            AdvantageFlag.MarkUsed();
            DebugLog.Info(HostPowersPlugin.ModId, $"{done} (player {t.Ref.PlayerId} moved to {spot.Ground.x:0},{spot.Ground.z:0}, {spot.Offset:0.#} m off)");
            return spot.Offset > 1f ? $"{done} ({spot.Offset:0} m off the exact spot to stay safe)." : done + ".";
        }
        catch (Exception e)
        {
            DebugLog.Error("hostpowers", e, "PlayerMoves.Place");
            HostPowersPlugin.Logger.LogWarning("[Players] move failed: " + e);
            return "Failed: " + e.Message;
        }
    }

    /// <summary>Host: moves a guest who asked (see <see cref="TeleportRequests"/>) next to a player or to a point.</summary>
    internal static string MoveRequester(Target who, Target toPlayer, Vector3? toPoint)
    {
        var blocked = NotAllowedReason;
        if (blocked != null) return blocked;
        if (who == null || !Refresh(who)) return "Failed: the player has no car.";
        var ctx = new SafeSpot.Context();
        ctx.Ignore(who.Car);
        if (toPlayer != null)
        {
            if (!Refresh(toPlayer)) return $"Failed: {toPlayer.Name} has no car.";
            var wanted = toPlayer.Position - Quaternion.Euler(0f, toPlayer.Yaw, 0f) * Vector3.forward * FollowGap;
            return Place(who, wanted, toPlayer.Yaw, ctx, $"Moved {who.Name} to {toPlayer.Name}");
        }
        if (toPoint.HasValue)
            return Place(who, toPoint.Value, YawTowards(who.Position, toPoint.Value, who.Yaw), ctx, $"Moved {who.Name} to their pin");
        return "Failed: nowhere to go.";
    }

    private static float Clearance => TeleportFeature.Clearance?.Value ?? 1.5f;

    // ---------------------------------------------------------------- group moves

    /// <summary>
    /// Formation slots behind a leader point: rows of <paramref name="columns"/>, centred, <paramref name="spacing"/>
    /// apart side to side and <paramref name="rowGap"/> apart front to back. Row 0 is the leader's own row.
    /// </summary>
    internal static List<Vector3> Formation(Vector3 leader, float yaw, int count, int firstRow, int columns, float spacing, float rowGap)
    {
        var list = new List<Vector3>(count);
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var fwd = rot * Vector3.forward;
        var right = rot * Vector3.right;
        columns = Mathf.Max(1, columns);
        for (int i = 0; i < count; i++)
        {
            int row = i / columns;
            int col = i % columns;
            int inRow = Mathf.Min(columns, count - row * columns);
            float lateral = (col - (inRow - 1) * 0.5f) * spacing;
            list.Add(leader - fwd * (rowGap * (row + firstRow)) + right * lateral);
        }
        return list;
    }

    /// <summary>Everyone else lines up behind you in rows, facing your way.</summary>
    public static string EveryoneToMe()
    {
        var blocked = NotAllowedReason;
        if (blocked != null) return blocked;
        if (Busy) return $"Blocked: still moving players ({_batch.Label}).";
        InvalidateRoster();
        var me = Me();
        if (me == null || !Refresh(me)) return "Failed: you have no car (are you driving?).";
        var others = Movable(me, out int cooling, out int noCar);
        if (others.Count == 0) return Nobody(cooling, noCar);
        // Nearest players fill the front rows (they have the least far to go, so the line looks tidy sooner).
        others.Sort((a, b) => (a.Position - me.Position).sqrMagnitude.CompareTo((b.Position - me.Position).sqrMagnitude));
        var slots = Formation(me.Position, me.Yaw, others.Count, 1, Columns, Spacing, RowGap);
        var batch = new Batch { Label = "everyone to you", Skipped = cooling };
        AddStayers(batch, others, null);
        for (int i = 0; i < others.Count; i++) batch.Pending.Enqueue(new Move { Who = others[i], Wanted = slots[i], Yaw = me.Yaw });
        Start(batch);
        return $"Lining up {others.Count} player(s) behind you...";
    }

    /// <summary>Everyone (and you, if set) to your map pin, in the same formation, facing away from where you were.</summary>
    public static string SummonAllToPin(bool includeMe)
    {
        var blocked = NotAllowedReason;
        if (blocked != null) return blocked;
        if (Busy) return $"Blocked: still moving players ({_batch.Label}).";
        if (!TeleportCore.TryGetPin(out var pin)) return "Failed: no map pin. Open the map and place a pin first.";
        InvalidateRoster();
        var me = Me();
        bool haveMe = me != null && Refresh(me);
        float yaw = haveMe ? YawTowards(me.Position, pin, me.Yaw) : 0f;
        var others = Movable(me, out int cooling, out int noCar);
        if (others.Count == 0 && !(includeMe && haveMe)) return Nobody(cooling, noCar);
        others.Sort((a, b) => (a.Position - pin).sqrMagnitude.CompareTo((b.Position - pin).sqrMagnitude));

        var batch = new Batch { Label = "summon to pin", Skipped = cooling };
        bool meToo = includeMe && haveMe;
        if (meToo) batch.Pending.Enqueue(new Move { Who = me, Wanted = pin, Yaw = yaw });
        // With you at the pin the others start one row back; without you the first row is centred on the pin.
        var slots = Formation(pin, yaw, others.Count, meToo ? 1 : 0, Columns, Spacing, RowGap);
        for (int i = 0; i < others.Count; i++) batch.Pending.Enqueue(new Move { Who = others[i], Wanted = slots[i], Yaw = yaw });
        // Cars that stay put (you, unless you come along; players on cooldown) keep their space.
        AddStayers(batch, others, meToo ? me : null);
        Start(batch);
        return $"Summoning {others.Count + (meToo ? 1 : 0)} car(s) to your pin...";
    }

    private static List<Target> Movable(Target me, out int cooling, out int noCar)
    {
        cooling = noCar = 0;
        var list = new List<Target>();
        foreach (var t in Players())
        {
            if (t.IsMe || (me != null && t.Ref.RawEncoded == me.Ref.RawEncoded)) continue;
            if (!Refresh(t)) { noCar++; continue; }
            if (CooldownLeft(t) > 0f) { cooling++; continue; }
            list.Add(t);
        }
        return list;
    }

    /// <summary>Every car that is not part of the batch is an obstacle the formation keeps its distance from.</summary>
    private static void AddStayers(Batch b, List<Target> moving, Target movingMe)
    {
        var set = new HashSet<int>();
        foreach (var t in moving) set.Add(t.Ref.RawEncoded);
        if (movingMe != null) set.Add(movingMe.Ref.RawEncoded);
        foreach (var t in Players())
            if (!set.Contains(t.Ref.RawEncoded) && t.HasCar && SafeSpot.IsFinite(t.Position)) b.Ctx.Occupied.Add(t.Position);
    }

    private static string Nobody(int cooling, int noCar)
    {
        if (cooling > 0) return $"Blocked: everyone was just moved, wait a moment ({cooling} on cooldown).";
        if (noCar > 0) return "Nobody to move yet (players still loading in).";
        return "No other players here.";
    }

    private static void Start(Batch b)
    {
        _batch = b;
        AdvantageFlag.MarkUsed();
    }

    /// <summary>Runs a couple of the queued group moves per frame.</summary>
    public static void Tick()
    {
        var b = _batch;
        if (b == null) return;
        if (NotAllowedReason != null)
        {
            _batch = null;
            PlayersFeature.Report("Stopped moving players: no longer host.");
            return;
        }
        for (int n = 0; n < CarsPerFrame && b.Pending.Count > 0; n++)
        {
            var m = b.Pending.Dequeue();
            try { RunMove(b, m); }
            catch (Exception e)
            {
                DebugLog.Error("hostpowers", e, "PlayerMoves.Tick");
                b.Skipped++;
                b.Problems.Add($"{m.Who?.Name}: {e.Message}");
                SafeSpot.LogOnce("batch", "group move: " + e);
            }
        }
        if (b.Pending.Count > 0) return;
        _batch = null;
        PlayersFeature.Report(Summary(b));
    }

    private static void RunMove(Batch b, Move m)
    {
        var t = m.Who;
        if (t == null || !Refresh(t)) { b.Skipped++; b.Problems.Add($"{t?.Name ?? "?"}: car gone"); return; }
        b.Ctx.Ignore(t.Car);
        var spot = SafeSpot.Find(m.Wanted, m.Yaw, b.Ctx);
        if (!spot.Ok) { b.Skipped++; b.Problems.Add($"{t.Name}: {spot.Why}"); return; }
        var at = spot.Ground + Vector3.up * Clearance;
        if (t.IsMe)
        {
            string s = TeleportCore.TeleportSelf(at, spot.Yaw, "your pin", snap: false, remember: true);
            b.MyResult = s;
            if (s.StartsWith("Failed") || s.StartsWith("Blocked")) { b.Skipped++; b.Problems.Add("you: " + s); return; }
        }
        else
        {
            TeleportCore.MoveCar(t.Car, at, spot.Yaw, false);
            LastMoved[t.Ref.RawEncoded] = Time.unscaledTime;
        }
        b.Ctx.Occupied.Add(spot.Ground);
        b.Moved++;
    }

    private static string Summary(Batch b)
    {
        string s = $"Done ({b.Label}): moved {b.Moved}";
        if (b.Skipped > 0) s += $", skipped {b.Skipped}";
        if (b.Problems.Count > 0) s += " - " + string.Join("; ", b.Problems.GetRange(0, Math.Min(3, b.Problems.Count)));
        if (b.Moved == 0 && b.Problems.Count > 0) s = "Failed: " + s;
        return s + ".";
    }

    private static int Columns => PlayersFeature.Columns != null ? Mathf.RoundToInt(PlayersFeature.Columns.Value) : 3;
    private static float Spacing => PlayersFeature.Spacing?.Value ?? 6f;
    private static float RowGap => PlayersFeature.RowGap?.Value ?? 9f;

    // ---------------------------------------------------------------- pins of other players (host reads the networked table)

    /// <summary>A player's map pin from the game's networked pin table (any player, as host or guest).</summary>
    public static bool TryGetPinOf(PlayerRef p, out Vector3 pin)
    {
        pin = default;
        try
        {
            var mnm = MapNetworkManager.Instance;
            if (mnm == null || mnm.Object == null || !mnm.Object.IsValid) return false;
            var positions = mnm.PlayerRefToPinpointPosition;
            Vector3 v = default;
            if (positions.TryGet(p, out v) && SafeSpot.IsFinite(v) && v.sqrMagnitude > 0.01f) { pin = v; return true; }
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "PlayerMoves.TryGetPinOf"); SafeSpot.LogOnce("pinof", "reading a player's pin: " + e.Message); }
        return false;
    }
}
