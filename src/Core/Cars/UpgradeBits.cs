using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
#if MELON
using Il2CppFusion;
#else
using Fusion;
#endif

namespace OverTheHillPlus.Cars;

/// <summary>
/// The game's mounted-parts set (Fusion's BitSet256, 32 bytes), as four words. The interop's struct holds only the first word of
/// the fixed buffer, so a BitSet256 in a managed variable is 8 bytes: a read past bit 63 sees what happens to sit after it on
/// the stack, and one passed to the game sends that as the other 192 bits. That put random parts and horns on cars and made a
/// livery come and go (the liveries sit past bit 63). So nothing keeps a BitSet256: reads take the boxed value the game
/// returns, and the change-car RPC is called on a buffer of the real size.
/// </summary>
public static unsafe class UpgradeBits
{
    private static IntPtr _onCar, _ofCar, _changeCar;

    /// <summary>What this car has switched on, as the game syncs it.</summary>
    public static ulong[] OnCar(VehicleCustomizationController vcc) =>
        Read(vcc, Method<VehicleCustomizationController>(ref _onCar, "get_EnabledUpgrades", 0));

    /// <summary>A saved loadout's parts.</summary>
    public static ulong[] Of(Car car) => Read(car, Method<Car>(ref _ofCar, "GetMountedUpgradesBitSet", 0));

    /// <summary>SpawnManager.RPC_ChangeCarAtCurrentPosition, the garage's "change car where it stands".</summary>
    public static void ChangeCar(SpawnManager sm, PlayerRef player, uint carId, ulong[] words, long modId, int subId)
    {
        ulong* set = stackalloc ulong[UpgradeWords.Count];
        for (int i = 0; i < UpgradeWords.Count; i++) set[i] = words != null && i < words.Length ? words[i] : 0UL;
        IntPtr* args = stackalloc IntPtr[5];
        args[0] = (IntPtr)(&player);
        args[1] = (IntPtr)(&carId);
        args[2] = (IntPtr)set;
        args[3] = (IntPtr)(&modId);
        args[4] = (IntPtr)(&subId);
        IntPtr exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(Method<SpawnManager>(ref _changeCar, "RPC_ChangeCarAtCurrentPosition", 5), sm.Pointer, (void**)args, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
    }

    private static ulong[] Read(Il2CppObjectBase target, IntPtr method)
    {
        IntPtr exception = IntPtr.Zero;
        IntPtr boxed = IL2CPP.il2cpp_runtime_invoke(method, target.Pointer, null, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        var set = (ulong*)IL2CPP.il2cpp_object_unbox(boxed);
        var words = new ulong[UpgradeWords.Count];
        for (int i = 0; i < UpgradeWords.Count; i++) words[i] = set[i];
        return words;
    }

    private static IntPtr Method<T>(ref IntPtr cached, string name, int args)
    {
        if (cached != IntPtr.Zero) return cached;
        cached = IL2CPP.il2cpp_class_get_method_from_name(Il2CppClassPointerStore<T>.NativeClassPtr, name, args);
        if (cached == IntPtr.Zero) throw new MissingMethodException(typeof(T).Name, name);
        return cached;
    }
}
