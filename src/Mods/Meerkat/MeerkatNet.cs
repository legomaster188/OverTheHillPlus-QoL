using System;
using System.Collections.Generic;
using OverTheHillPlus.Core.Shared;
using OverTheHillPlus.Features.Public.ModNet;
using UnityEngine;

namespace OverTheHillPlus.Meerkat;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Every player's Meerkat build, on every peer (mod-net channel 50, <see cref="MeerkatWire"/>): a guest tells the host its
/// own (when it changes, and every 10 s), the host sends everyone the table of all of them (its own included) the same
/// way. This game's own build always shows at once from its own career or page. Vanilla players get nothing.
/// </summary>
internal static class MeerkatNet
{
    internal enum Roles { None, Solo, Host, Guest }

    internal readonly struct Row
    {
        public readonly MeerkatBuild Build;
        public readonly bool Free;
        public Row(MeerkatBuild b, bool free) { Build = b; Free = free; }
    }

    private static readonly Dictionary<int, Row> Claims = new();   // host: what the guests said
    private static readonly Dictionary<int, Row> Table = new();    // guest: the host's last table
    private static bool _channel;
    private static IntPtr _runner;
    private static float _next, _nextSend, _nextTable;
    private static byte[] _lastSent, _lastTable;

    internal static Roles Role
    {
        get
        {
            var r = GameState.Runner;
            if (r == null) return Roles.None;
            try
            {
                if (r.IsSinglePlayer) return Roles.Solo;
                return GameState.IsAuthority ? Roles.Host : Roles.Guest;
            }
            catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatNet.Role"); return Roles.None; }
        }
    }

    internal static int Me => GameState.LocalPlayer?.RawEncoded ?? 0;

    internal static void Install()
    {
        _channel = ModNet.Register(MeerkatWire.Channel, new ModNet.ChannelOptions
        {
            Name = "meerkat", Version = MeerkatWire.Version, MinVersion = 1, MaxPayload = MeerkatWire.MaxPayload,
            RatePerSecond = 2f, Burst = 6, Active = () => ModManager.IsEnabled(MeerkatPlugin.ModId),
        }, OnMessage);
        if (!_channel) MeerkatPlugin.LogOnce("channel", $"mod-net channel {MeerkatWire.Channel} is taken: other players will see your Meerkat as it comes");
        ModNet.OnPeerReady(p => { if (Role == Roles.Host) _lastTable = null; });
        ModNet.OnPeerGone(p => Claims.Remove(p.RawEncoded));
        ModNet.OnSessionReset(ResetSession);
    }

    private static void ResetSession()
    {
        Claims.Clear();
        Table.Clear();
        _lastSent = _lastTable = null;
    }

    private static void OnMessage(ModNet.Message m)
    {
        try
        {
            switch (m.Type)
            {
                case MeerkatWire.MsgBuild:
                    if (Role != Roles.Host || !MeerkatWire.ReadBuild(m.Payload, out var b, out bool free)) return;
                    Claims[m.From.RawEncoded] = new Row(b, free);
                    break;
                case MeerkatWire.MsgTable:
                    if (Role != Roles.Guest || !m.FromHost || !MeerkatWire.ReadTable(m.Payload, out var rows)) return;
                    Table.Clear();
                    foreach (var r in rows) Table[r.Player] = new Row(r.Build, r.Free);
                    break;
            }
        }
        catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatNet.OnMessage"); }
    }

    /// <summary>Twice a second.</summary>
    internal static void Tick()
    {
        var r = GameState.Runner;
        var ptr = r == null ? IntPtr.Zero : r.Pointer;
        if (ptr != _runner)
        {
            _runner = ptr;
            ResetSession();
        }
        float now = Time.unscaledTime;
        if (now < _next && now >= _next - 2f) return;
        _next = now + 0.5f;
        if (!_channel) return;
        var role = Role;
        var mine = MeerkatCareer.LocalBuild(out bool free);
        if (role == Roles.Guest)
        {
            if (!ModNet.HostHas(MeerkatWire.Channel)) return;
            var bytes = MeerkatWire.Build(mine, free);
            bool same = _lastSent != null && ((ReadOnlySpan<byte>)bytes).SequenceEqual(_lastSent);
            if (same && now < _nextSend && now >= _nextSend - 20f) return;
            _lastSent = bytes;
            _nextSend = now + 10f;
            ModNet.SendToHost(MeerkatWire.Channel, MeerkatWire.MsgBuild, bytes);
        }
        else if (role == Roles.Host)
        {
            var rows = new List<(int, MeerkatBuild, bool)> { (Me, mine, free) };
            foreach (var kv in Claims)
            {
                if (kv.Key == Me) continue;
                var g = HostView(kv.Value);
                rows.Add((kv.Key, g.Build, g.Free));
            }
            var bytes = MeerkatWire.Table(rows);
            bool same = _lastTable != null && ((ReadOnlySpan<byte>)bytes).SequenceEqual(_lastTable);
            if (same && now < _nextTable && now >= _nextTable - 20f) return;
            _lastTable = bytes;
            _nextTable = now + 10f;
            ModNet.Broadcast(MeerkatWire.Channel, MeerkatWire.MsgTable, bytes);
        }
    }

    /// <summary>A player's build: this game's own from its career or page, others' from the host's table (or, on the host,
    /// from what they said). False when nothing is known (the car shows as the dealer sells it).</summary>
    internal static bool TryGet(int player, out Row row)
    {
        if (player == Me || player == 0)
        {
            var b = MeerkatCareer.LocalBuild(out bool free);
            row = new Row(b, free);
            return true;
        }
        if (Role == Roles.Host)
        {
            if (!Claims.TryGetValue(player, out var claim)) { row = default; return false; }
            row = HostView(claim);
            return true;
        }
        return Table.TryGetValue(player, out row);
    }

    /// <summary>The host's view of a guest's claim (the career's rules on guests' upgrades).</summary>
    private static Row HostView(Row claim)
    {
        var b = MeerkatCareer.GuestBuild(claim.Build, claim.Free, out bool free);
        return new Row(b, free);
    }
}
