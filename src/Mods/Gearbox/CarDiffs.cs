using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.GearboxPlus;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The differentials of the cars. The game's 4x4s have an axle differential on each axle (both "locked") joined by an
/// H-drive, with no centre differential; the vehicle physics (VPP) can force an axle differential locked or open at runtime
/// (<c>Driveline.frontDifferentialOverride</c> / <c>rearDifferentialOverride</c>, the physics' own runtime overrides), and
/// the type itself is a settings field. So:
/// - Open and Locked are forced with the overrides (the safe path: an enum the driveline applies itself).
/// - Limited slip sets the axle differentials' type to the clutch-pack one (the shared settings object, and each block's own
///   copy of it). Whether the driveline picks that up after it was built is not known: the block caches its type, so the
///   cached value is written too.
/// - A centre differential is changed only on a car that has centre differential blocks; the game's cars do not (the H-drive
///   joins the axles directly), so for them the centre setting does nothing and the page says so.
/// Everything is put back when the mod is not active, when a car has been left, and when anything about a car throws (that car
/// then keeps the game's differentials for the session).
///
/// <para>The host simulates every car and each game predicts its own, so a car's differentials must be set the same way on
/// both: a guest sends its setup to the host (ModNet, channel <see cref="Channel"/>, on a change and every 5 s) and the host
/// applies it to that player's car; a player's setup that never arrives (a vanilla guest, a guest without the mod) leaves
/// that car as the game has it. Your own car always uses your own setup.</para>
/// </summary>
internal static class CarDiffs
{
    public const ushort Channel = ModNet.Channels.Gearbox;
    private const float TickEvery = 0.5f, HeartbeatEvery = 5f, PeerForgetAfter = 20f;

    private sealed class State
    {
        public VPVehicleController Vehicle;
        public DiffSetup Applied;
        public bool HasApplied, Broken;
        public bool Captured;
        public Differential.Type OrigType;
        public Driveline.Override OrigFront, OrigRear;
        public Differential.Type[] OrigCentre = Array.Empty<Differential.Type>();
        public int Centres;
    }

    private sealed class Peer
    {
        public DiffSetup Setup;
        public float Seen;
    }

    private static readonly Dictionary<IntPtr, State> Cars = new();
    private static readonly Dictionary<int, Peer> Peers = new();
    private static readonly HashSet<IntPtr> Seen = new();
    private static readonly List<IntPtr> Gone = new();
    private static readonly List<int> Forget = new();
    private static readonly HashSet<string> LoggedOnce = new();

    private static float _next, _nextSend, _lastSend;
    private static DiffSetup _sent;
    private static bool _haveSent, _registered;
    private static IntPtr _runner;

    /// <summary>The lock key is on: every differential of your car is locked (until it is pressed again).</summary>
    internal static bool LockAll { get; private set; }

    /// <summary>What is set on your own car now (for the indicator).</summary>
    internal static bool HasLocal { get; private set; }
    internal static DiffSetup Local { get; private set; }
    internal static bool LocalHasCentre { get; private set; }
    internal static bool LocalBroken { get; private set; }

    internal static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) GearboxPlugin.Logger?.LogWarning("[Gearbox+] " + message + " (logged once)");
    }

    internal static void Install()
    {
        _registered = ModNet.Register(Channel, new ModNet.ChannelOptions
        {
            Name = "gearbox",
            Version = 1,
            MaxPayload = 32,
            RatePerSecond = 4f,
            Burst = 8,
            // Advertised whenever the mod is loaded: it is how the host knows whose differentials to set.
            Active = () => ModManager.IsLoaded(GearboxPlugin.ModId),
        }, OnMessage);
        if (!_registered) LogOnce("channel", $"ModNet channel {Channel} is taken: the host cannot learn your differential setup");
        ModNet.OnSessionReset(ResetSession);
        ModNet.OnPeerGone(p => Peers.Remove(p.PlayerId));
    }

    // ---------------------------------------------------------------- the lock key

    /// <summary>The lock key: all differentials of your car locked, or back to your setting.</summary>
    internal static void ToggleLock()
    {
        if (GearboxConfig.DiffFrom.Value == DiffSource.Stock) return; // the game's differentials are locked already
        LockAll = !LockAll;
        _next = 0f; // applied at once
        _nextSend = 0f;
    }

    // ---------------------------------------------------------------- messages

    private static void OnMessage(ModNet.Message m)
    {
        if (m.Type != DiffWire.MsgSetup || !GameState.IsAuthority) return;
        if (!DiffWire.Decode(m.Payload, out var setup)) return;
        int id = m.From.PlayerId;
        if (!Peers.TryGetValue(id, out var p)) Peers[id] = p = new Peer();
        p.Setup = setup;
        p.Seen = Time.unscaledTime;
    }

    private static void ResetSession()
    {
        DebugLog.Info(GearboxPlugin.ModId, $"session reset: {Cars.Count} car(s) put back");
        RestoreAll();
        Peers.Clear();
        _haveSent = false;
    }

    private static void SendIfNeeded(DiffSetup local, float now)
    {
        if (!_registered || GameState.IsAuthority || !ModNet.HostHas(Channel) || now < _nextSend) return;
        bool changed = !_haveSent || !_sent.Equals(local);
        if (!changed && now < _lastSend + HeartbeatEvery) return;
        var r = ModNet.SendToHost(Channel, DiffWire.MsgSetup, DiffWire.Encode(local));
        if (r == ModNet.SendResult.Sent)
        {
            _sent = local; _haveSent = true; _lastSend = now; _nextSend = now + 0.25f;
        }
        else _nextSend = now + 1f;
    }

    // ---------------------------------------------------------------- tick

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _next) return;
        _next = now + TickEvery;

        IntPtr rp = IntPtr.Zero;
        var runner = GameState.Runner;
        try { rp = runner != null ? runner.Pointer : IntPtr.Zero; } catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "CarDiffs.Tick"); }
        if (rp != _runner)
        {
            _runner = rp;
            ResetSession();
        }

        HasLocal = false;
        if (!ModManager.ServerModActive(GearboxPlugin.ModId))
        {
            if (Cars.Count > 0) RestoreAll();
            LockAll = false;
            return;
        }
        var source = GearboxConfig.DiffFrom.Value;
        if (source == DiffSource.Stock) LockAll = false;

        bool authority = GameState.IsAuthority;
        Seen.Clear();
        DiffSetup mine = DiffSetup.Stock;
        bool haveMine = false;
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                var vpc = fp.GetComponent<VPVehicleController>();
                if (vpc == null || !vpc.isActiveAndEnabled) continue;
                var owner = fp.Object.InputAuthority;
                bool isMine = fp.HasInputAuthority;
                DiffSetup setup;
                if (isMine)
                {
                    setup = DiffMath.For(source, vpc.gameObject.name, GearboxConfig.DiffFront.Value, GearboxConfig.DiffRear.Value,
                        GearboxConfig.DiffCentre.Value, LockAll, ModdedLookup.DiffsOf);
                    // A guest whose host does not run the channel (an older Gearbox+ of the same major version) cannot tell it its
                    // setup, and the host would simulate this car with the game's differentials: it keeps them too.
                    if (!authority && !ModNet.HostHas(Channel)) setup = DiffSetup.Stock;
                    mine = setup;
                    haveMine = true;
                }
                else if (authority && owner.IsRealPlayer && ModNet.PeerHas(owner, Channel) && Peers.TryGetValue(owner.PlayerId, out var peer)) setup = peer.Setup;
                else continue;

                var ptr = vpc.Pointer;
                Seen.Add(ptr);
                if (!Cars.TryGetValue(ptr, out var st)) Cars[ptr] = st = new State { Vehicle = vpc };
                if (setup.IsStock && !st.HasApplied) { if (isMine) PublishLocal(setup, st); continue; }
                Apply(st, setup);
                if (isMine)
                {
                    PublishLocal(setup, st);
                    if (!setup.IsStock && !st.Broken) AdvantageFlag.MarkUsed(); // changed car physics
                }
            }
            catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "CarDiffs.Tick"); LogOnce("visit", "differentials: " + e.Message); }
        }

        Gone.Clear();
        foreach (var k in Cars.Keys) if (!Seen.Contains(k)) Gone.Add(k);
        foreach (var k in Gone) Release(k);

        if (authority)
        {
            Forget.Clear();
            foreach (var kv in Peers) if (now - kv.Value.Seen > PeerForgetAfter) Forget.Add(kv.Key);
            foreach (var k in Forget) Peers.Remove(k);
        }
        else if (haveMine) SendIfNeeded(mine, now);
    }

    private static void PublishLocal(DiffSetup setup, State st)
    {
        HasLocal = true;
        Local = setup;
        LocalHasCentre = st.Centres > 0;
        LocalBroken = st.Broken;
    }

    // ---------------------------------------------------------------- writing to the car

    private static void Apply(State st, DiffSetup setup)
    {
        if (st.Broken) return;
        if (st.HasApplied && st.Applied.Equals(setup)) return;
        try
        {
            if (!Write(st, setup)) return; // the car's driveline is not ready yet: tried again next tick
            st.Applied = setup;
            st.HasApplied = true;
        }
        catch (Exception e)
        {
            // This car goes back to the game's differentials and stays there.
            st.Broken = true;
            DebugLog.Error(GearboxPlugin.ModId, e, "CarDiffs.Apply");
            try { Put(st, DiffSetup.Stock); } catch (Exception ex) { DebugLog.Error(GearboxPlugin.ModId, ex, "CarDiffs.Apply"); }
            LogOnce("apply", "a car keeps the game's differentials after an error: " + e.Message);
        }
    }

    private static bool Write(State st, DiffSetup setup)
    {
        var v = st.Vehicle;
        var dl = v != null ? v.m_driveline : null;
        if (dl == null) return false;
        if (!st.Captured) Capture(st, v, dl);
        Put(st, setup);
        DebugLog.Verbose(GearboxPlugin.ModId, () => $"differentials: {DiffMath.Line(setup, st.Centres > 0)}");
        return true;
    }

    private static void Capture(State st, VPVehicleController v, Driveline dl)
    {
        st.OrigType = v.differential != null ? v.differential.type : Differential.Type.Locked;
        st.OrigFront = dl.frontDifferentialOverride;
        st.OrigRear = dl.rearDifferentialOverride;
        var centres = dl.m_centerDifferentials;
        int n = centres != null ? centres.Length : 0;
        st.OrigCentre = new Differential.Type[n];
        for (int i = 0; i < n; i++)
        {
            var b = centres[i];
            st.OrigCentre[i] = b != null && b.settings != null ? b.settings.type : Differential.Type.Locked;
        }
        st.Centres = n;
        st.Captured = true;
    }

    /// <summary>Writes the setup (Stock puts the game's own back).</summary>
    private static void Put(State st, DiffSetup setup)
    {
        var v = st.Vehicle;
        var dl = v != null ? v.m_driveline : null;
        if (dl == null || !st.Captured) return;
        var drive = DiffMath.Resolve(setup);

        // the axle differentials' type (shared by both axles) and each block's own copy of it
        var type = drive.LimitedSlip ? Differential.Type.ClutchPack : st.OrigType;
        if (v.differential != null && v.differential.type != type) v.differential.type = type;
        SetBlockTypes(dl.m_frontDifferentials, type);
        SetBlockTypes(dl.m_rearDifferentials, type);

        // what is forced on each axle
        dl.frontDifferentialOverride = ToOverride(drive.Front, st.OrigFront);
        dl.rearDifferentialOverride = ToOverride(drive.Rear, st.OrigRear);

        // the centre, on a car that has one
        var centres = dl.m_centerDifferentials;
        var cmode = DiffMath.CentreMode(setup);
        for (int i = 0; centres != null && i < centres.Length; i++)
        {
            var b = centres[i];
            if (b == null) continue;
            var orig = i < st.OrigCentre.Length ? st.OrigCentre[i] : Differential.Type.Locked;
            var ctype = cmode == DiffMode.LimitedSlip ? Differential.Type.TorqueBias : orig;
            SetBlockType(b, ctype);
            b.forceOverride = cmode == DiffMode.Open ? Differential.Override.ForceOpen
                            : cmode == DiffMode.Locked ? Differential.Override.ForceLocked : Differential.Override.None;
        }
        dl.UpdateOverrides();
    }

    private static Driveline.Override ToOverride(AxleForce force, Driveline.Override original) => force switch
    {
        AxleForce.Locked => Driveline.Override.ForceLocked,
        AxleForce.Open => Driveline.Override.ForceUnlocked,
        _ => original,
    };

    private static void SetBlockTypes(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Differential> blocks, Differential.Type type)
    {
        if (blocks == null) return;
        for (int i = 0; i < blocks.Length; i++)
            if (blocks[i] != null) SetBlockType(blocks[i], type);
    }

    private static void SetBlockType(Differential b, Differential.Type type)
    {
        if (b.settings != null && b.settings.type != type) b.settings.type = type;
        if (b.m_type != type) b.m_type = type; // the block caches its type
    }

    // ---------------------------------------------------------------- putting cars back

    private static void Release(IntPtr ptr)
    {
        if (!Cars.TryGetValue(ptr, out var st)) return;
        Cars.Remove(ptr);
        if (!st.HasApplied) return;
        try { if (st.Vehicle != null) Put(st, DiffSetup.Stock); }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "CarDiffs.Release"); /* the car is gone: nothing to put back */ }
    }

    internal static void RestoreAll()
    {
        foreach (var k in Cars.Keys.ToList()) Release(k);
        HasLocal = false;
    }
}
