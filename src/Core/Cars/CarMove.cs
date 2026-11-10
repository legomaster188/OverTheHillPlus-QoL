using UnityEngine;
#if MELON
using Il2CppFusion.Addons.Physics;
using Il2CppVehiclePhysics;
#else
using Fusion.Addons.Physics;
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Cars;

/// <summary>
/// Moving a car to another place. VehicleBase.Reposition alone doesn't: it sets the transform, and with the game's scripted physics
/// the next step puts the transform back where the rigidbody is. Setting the rigidbody too works for a short hop, but a long one is
/// undone as well. The game's own teleport RPC (RPC_TeleportPlayer) moves a player's car any distance, and is the way the
/// game's own tools and its portable jack go; a car nobody plays is moved the way the trailers are, through its NetworkRigidbody3D.
/// </summary>
public static class CarMove
{
    public static void Place(VehicleBase car, Vector3 position, Quaternion rotation)
    {
        var manager = CheatsManager.Instance;
        var player = car.GetComponent<FusionPlayer>();
        if (manager != null && player != null && player.Object != null && player.Object.IsValid)
        {
            manager.RPC_TeleportPlayer(player.Object.InputAuthority, position, rotation);
            return;
        }
        car.Reposition(position, rotation);
        var body = car.cachedRigidbody;
        if (body == null) return;
        body.position = position;
        body.rotation = rotation;
        car.GetComponent<NetworkRigidbody3D>()?.Teleport(new Il2CppSystem.Nullable<Vector3>(position), new Il2CppSystem.Nullable<Quaternion>(rotation));
    }
}
