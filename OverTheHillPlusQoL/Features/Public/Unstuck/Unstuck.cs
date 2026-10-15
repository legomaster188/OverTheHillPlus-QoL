using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using VehiclePhysics;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Features.Public.Unstuck;

/// <summary>
/// Puts a car back on its wheels a little above the ground under it, the way the game's own (developer console)
/// reset-player RPC does it (decoded with Cpp2IL): detach the winches, then VehicleBase.Reposition to the car's spot
/// raised a bit, keeping only its heading. Here the height comes from a ground raycast (skipping the car itself,
/// moving bodies and overhangs more than 2 m above it), so a car that sank into the terrain comes back out on top.
///
/// Who can do it: the game that simulates the car. Solo or hosting, that's you. As a guest your car is simulated by
/// the host, so the request goes over the mod network to a modded host, which runs the same reset for you (if it
/// accepts requests). A vanilla host simply can't be asked. 10 s cooldown on both ends.
/// </summary>
internal static class Unstuck
{
    public const float Cooldown = 10f;
    private const float ReplyTimeout = 6f;
    private const float Lift = 1.2f;
    private const byte MsgRequest = 1, MsgReply = 2;

    private enum Reply : byte { Done = 0, Disabled = 1, Cooldown = 2, NoCar = 3, Failed = 4 }

    private static float _lastUse = -100f;
    private static float _pendingSince = -1f;
    private static readonly Dictionary<int, float> HostLastServed = new(); // requester RawEncoded -> time
    private static readonly HashSet<string> LoggedOnce = new();

    /// <summary>Last result, for the toast and the menu.</summary>
    public static string LastMessage { get; private set; } = "";
    public static float LastMessageAt { get; private set; } = -100f;
    public static int ServedForGuests { get; private set; }

    public static float CooldownLeft => Mathf.Max(0f, _lastUse + Cooldown - Time.unscaledTime);
    public static bool Waiting => _pendingSince >= 0f;

    internal static void RegisterChannel()
    {
        Net.Register(Net.Channels.Unstuck, new Net.ChannelOptions
        {
            Name = "unstuck",
            Version = 1,
            MaxPayload = 32,
            RatePerSecond = 0.5f,
            Burst = 3,
            HostOnlySender = true,
            // Guests always listen for answers; a host advertises the channel only while it accepts requests.
            Active = () => !Net.IsHost || UnstuckFeature.AcceptRequests.Value,
        }, OnMessage);
        Net.OnSessionReset(() => { HostLastServed.Clear(); _pendingSince = -1f; });
        Net.OnPeerGone(p => HostLastServed.Remove(p.RawEncoded));
    }

    // ---------------------------------------------------------------- the button / key

    /// <summary>Unstucks your car (solo / host) or asks the host to (guest). Returns a status line.</summary>
    public static string Use()
    {
        string s = UseCore();
        Report(s);
        return s;
    }

    private static string UseCore()
    {
        float left = CooldownLeft;
        if (left > 0f) return $"Blocked: unstuck is ready again in {Mathf.CeilToInt(left)} s";
        if (GameState.IsAuthority)
        {
            if (!GameState.PhysicsAllowed) return "Blocked: your car is simulated elsewhere";
            var car = GameState.LocalCar;
            if (car == null) return "Failed: no car found (are you driving?)";
            if (!ResetCar(car, out string why)) return "Failed: " + why;
            _lastUse = Time.unscaledTime;
            return "Unstuck: your car is back on its wheels";
        }

        // Guest: only the host can move our car.
        if (Waiting) return "Blocked: already asked the host, waiting for an answer";
        if (!Net.Enabled.Value) return "Blocked: turn on the mod network to ask the host (only hosts with this mod can help)";
        switch (Net.HostStatus)
        {
            case Net.HostLink.Asking: return "Blocked: still connecting to the host's mod network, try again in a moment";
            case Net.HostLink.Ready: break;
            case Net.HostLink.Paused: return "Blocked: the host switched its mod network off";
            default: return "Blocked: the host doesn't run this mod, so it can't unstuck your car";
        }
        if (!Net.HostHas(Net.Channels.Unstuck)) return "Blocked: the host doesn't accept unstuck requests";
        var res = Net.SendToHost(Net.Channels.Unstuck, MsgRequest, new NetWriter(4).Byte(1).ToArray());
        switch (res)
        {
            case Net.SendResult.Sent:
                _pendingSince = Time.unscaledTime;
                _lastUse = Time.unscaledTime; // the cooldown starts when asking, so the key can't spam the host
                return "Asked the host to unstuck your car...";
            case Net.SendResult.RateLimited: return "Blocked: too many requests, wait a moment";
            default: return "Failed: could not reach the host (" + res + ")";
        }
    }

    public static void Tick()
    {
        if (_pendingSince >= 0f && Time.unscaledTime - _pendingSince > ReplyTimeout)
        {
            _pendingSince = -1f;
            Report("Failed: the host didn't answer the unstuck request");
        }
    }

    private static void Report(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        LastMessage = s;
        LastMessageAt = Time.unscaledTime;
        try { Menu.SetStatus(s); } catch { }
        Plugin.Logger.LogInfo("[Unstuck] " + s);
    }

    // ---------------------------------------------------------------- messages

    private static void OnMessage(Net.Message m)
    {
        if (m.Type == MsgRequest && Net.IsHost) HostServe(m.From);
        else if (m.Type == MsgReply && m.FromHost && !Net.IsHost) GuestAnswer(m.Payload);
    }

    private static void HostServe(PlayerRef from)
    {
        if (!from.IsRealPlayer) return;
        Reply code;
        float wait = 0f;
        string name = NameOf(from);
        if (!UnstuckFeature.AcceptRequests.Value || !GameState.IsAuthority) code = Reply.Disabled;
        else if (HostLastServed.TryGetValue(from.RawEncoded, out float at) && Time.unscaledTime - at < Cooldown - 0.5f)
        {
            code = Reply.Cooldown;
            wait = Cooldown - (Time.unscaledTime - at);
        }
        else
        {
            var car = CarOf(from);
            if (car == null) code = Reply.NoCar;
            else if (ResetCar(car, out string why))
            {
                code = Reply.Done;
                HostLastServed[from.RawEncoded] = Time.unscaledTime;
                ServedForGuests++;
                Report($"Unstuck {name}'s car on their request");
            }
            else
            {
                code = Reply.Failed;
                LogOnce("serve", $"[Unstuck] Could not unstuck {name}'s car: {why}");
            }
        }
        Net.SendTo(from, Net.Channels.Unstuck, MsgReply, new NetWriter(8).Byte((byte)code).Float(wait).ToArray());
    }

    private static void GuestAnswer(byte[] payload)
    {
        var rd = new NetReader(payload);
        var code = (Reply)rd.Byte();
        float wait = Mathf.Clamp(rd.Float(), 0f, 60f);
        if (!rd.Ok) return;
        _pendingSince = -1f;
        switch (code)
        {
            case Reply.Done: Report("Unstuck: the host put your car back on its wheels"); break;
            case Reply.Disabled: Report("Blocked: the host doesn't accept unstuck requests"); _lastUse = -100f; break;
            case Reply.Cooldown:
                _lastUse = Time.unscaledTime - Cooldown + wait;
                Report($"Blocked: the host says wait {Mathf.CeilToInt(wait)} s");
                break;
            case Reply.NoCar: Report("Failed: the host couldn't find your car"); break;
            default: Report("Failed: the host couldn't unstuck your car"); break;
        }
    }

    // ---------------------------------------------------------------- the reset

    /// <summary>The game's reset, with a ground raycast. Caller checks authority.</summary>
    internal static bool ResetCar(VehicleBase car, out string why)
    {
        why = null;
        try
        {
            if (car == null) { why = "no car"; return false; }
            var t = car.cachedTransform != null ? car.cachedTransform : car.transform;
            if (t == null) { why = "no car"; return false; }
            Vector3 here = t.position;
            if (!IsFinite(here)) { why = "the car's position is invalid"; return false; }

            Vector3 fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.ProjectOnPlane(-t.up, Vector3.up); // nose straight up or down
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            var rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);

            Vector3 target = GroundUnder(here, car, out bool found);
            target = found ? target + Vector3.up * Lift : here + Vector3.up * 1.5f;

            // Same order as the game: let go of winch anchors first, or the rope yanks the car back.
            try
            {
                var winch = car.GetComponentInChildren<VehicleWinchSystem>();
                if (winch != null) winch.DetachAllWinches();
            }
            catch (Exception e) { LogOnce("winch", "[Unstuck] detach winches: " + e.Message); }

            car.Reposition(target, rot);

            try
            {
                var rb = car.cachedRigidbody;
                if (rb != null && !rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch (Exception e) { LogOnce("stop", "[Unstuck] stop car: " + e.Message); }
            return true;
        }
        catch (Exception e)
        {
            why = e.Message;
            LogOnce("reset", "[Unstuck] reset failed: " + e);
            return false;
        }
    }

    /// <summary>
    /// The highest walkable surface at most 2 m above <paramref name="p"/> (so a roof, bridge or tree canopy above the
    /// car is skipped), ignoring triggers, moving bodies and the car itself; a point under the terrain counts from
    /// the terrain surface. <paramref name="found"/> is false when nothing was hit and no terrain covers the spot.
    /// </summary>
    private static Vector3 GroundUnder(Vector3 p, VehicleBase car, out bool found)
    {
        found = false;
        float refY = p.y;
        bool hasTerrain = TryTerrainHeight(p, out float terrainY);
        if (hasTerrain && terrainY > refY) refY = terrainY;

        float best = float.NegativeInfinity;
        try
        {
            Rigidbody own = null;
            Transform root = null;
            try { own = car.cachedRigidbody; root = car.transform; } catch { }
            var hits = Physics.RaycastAll(new Vector3(p.x, refY + 30f, p.z), Vector3.down, 120f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.normal.y < 0.35f) continue; // walls, cliffs
                var rb = h.rigidbody;
                if (rb != null && ((own != null && rb.Pointer == own.Pointer) || !rb.isKinematic)) continue;
                var col = h.collider;
                if (root != null && col != null && col.transform.IsChildOf(root)) continue;
                float y = h.point.y;
                if (y <= refY + 2f && y > best) best = y;
            }
        }
        catch (Exception e) { LogOnce("ray", "[Unstuck] ground raycast: " + e.Message); }

        if (float.IsNegativeInfinity(best))
        {
            if (!hasTerrain) return p;
            best = terrainY;
        }
        found = true;
        return new Vector3(p.x, best, p.z);
    }

    private static bool TryTerrainHeight(Vector3 p, out float height)
    {
        height = 0f;
        try
        {
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                var o = t.GetPosition();
                var size = t.terrainData.size;
                if (p.x < o.x || p.z < o.z || p.x > o.x + size.x || p.z > o.z + size.z) continue;
                height = t.SampleHeight(p) + o.y;
                return true;
            }
        }
        catch (Exception e) { LogOnce("terrain", "[Unstuck] terrain height: " + e.Message); }
        return false;
    }

    // ---------------------------------------------------------------- helpers

    private static VehicleBase CarOf(PlayerRef p)
    {
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                if (fp.Object.InputAuthority.RawEncoded != p.RawEncoded) continue;
                var car = fp.vehicleBase;
                if (car != null) return car;
            }
            catch { }
        }
        return null;
    }

    private static string NameOf(PlayerRef p)
    {
        try { return PlayerListRoster.NameOf(OverlandSessionPlayers.Instance, p); }
        catch { return "Player " + p.PlayerId; }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    private static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Add(id)) Plugin.Logger.LogWarning(message);
    }
}
