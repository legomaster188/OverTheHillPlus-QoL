using System;
using Fusion;
using HarmonyLib;
using UnityEngine;
using VehiclePhysics;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Tag;

/// <summary>
/// Host: two player cars touched. VehicleBase.OnCollisionEnter is the car's own Unity collision message; it runs on
/// the machine that simulates the car, which for every car in a hosted session is the host. Both cars get it, so a
/// touch arrives twice; the round's tag-back rule and the sides in hide and seek make the second one a no-op.
/// </summary>
[HarmonyPatch(typeof(VehicleBase), nameof(VehicleBase.OnCollisionEnter))]
internal static class TagCollisionPatch
{
    private static bool _loggedError;

    private static void Postfix(VehicleBase __instance, Collision collision)
    {
        if (collision == null || !TagGame.Hosting) return;
        try
        {
            int a = TagCars.OfCar(__instance.Pointer);
            if (a < 0) return;
            var rb = collision.rigidbody;
            if (rb == null) return;
            int b = TagCars.OfBody(rb.Pointer);
            if (b >= 0 && b != a) TagGame.QueueContact(a, b);
        }
        catch (Exception e)
        {
            DebugLog.Error("tag", e, "TagCollisionPatch.Postfix");
            if (_loggedError) return;
            _loggedError = true;
            TagPlugin.Logger.LogWarning("[Tag] collision check failed (further errors suppressed): " + e.Message);
        }
    }
}

/// <summary>
/// Hide and seek's head start: seekers' cars stand still (no throttle, brakes and handbrake on), written onto the car's
/// data bus right before VPP's blocks read it, the same physics-step hook the other mods use (no struct parameters).
/// The host holds the seekers it simulates: itself and seekers who run the mod (they see the blackout and the timer),
/// plus seekers without the mod when <see cref="TagConfig.HoldVanillaSeekers"/> is on. A modded guest holds its own
/// predicted car too, so its prediction agrees with the host.
/// </summary>
internal static class TagHold
{
    /// <summary>Set by <see cref="TagGame.Tick"/>: a head start is running (the patch does nothing otherwise).</summary>
    internal static bool Active;

    private static bool _ids;
    private static int _chInput, _throttle, _brake, _handbrake;

    internal static void Step(VPVehicleController car)
    {
        var view = TagGame.View;
        if (view == null || view.Phase != TagPhase.HeadStart) return;
        int id = TagCars.OfCar(car.Pointer);
        if (id < 0) return;
        var row = view.Find(id);
        if (row == null || !row.Seeker) return;
        int me = TagCars.Me;
        if (GameState.PhysicsAllowed)
        {
            bool modded = id == me || Net.PeerHas(PlayerRef.FromEncoded(id), TagGame.Channel);
            if (!modded && !TagConfig.HoldVanillaSeekers.Value) return;
        }
        else if (id != me) return;

        if (!_ids)
        {
            _chInput = Channel.Input;
            _throttle = InputData.Throttle;
            _brake = InputData.Brake;
            _handbrake = InputData.Handbrake;
            _ids = true;
        }
        var data = car.data;
        if (data == null) return;
        data.Set(_chInput, _throttle, 0);
        data.Set(_chInput, _brake, 10000);
        data.Set(_chInput, _handbrake, 10000);
    }
}

[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class TagHoldPatch
{
    private static bool _loggedError;

    private static void Prefix(VPVehicleController __instance)
    {
        if (!TagHold.Active) return;
        try { TagHold.Step(__instance); }
        catch (Exception e)
        {
            DebugLog.Error("tag", e, "TagHoldPatch.Prefix");
            if (_loggedError) return;
            _loggedError = true;
            TagPlugin.Logger.LogWarning("[Tag] head-start hold failed (further errors suppressed): " + e.Message);
        }
    }
}
