using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Stacking;

/// <summary>
/// The garage's mount, with stacking. Every cosmetic click of the garage goes through
/// LocalVehicleCustomizationController.MountDismount(Car, VehiclePartSO, bool) (StateMerchantMenu
/// .OnUpgradeButtonClickOrSubmit: mount = the part isn't on yet; also right after buying it), which calls
/// VehicleCustomizationControllerHelpers.MountDismountPart: for a mount it takes off the first part of the same kind
/// found in car.MountedParts (HaveAlreadySameTypeMounted), adds the new one and marks it in use for this car. The
/// patched method only takes classes and a bool (no struct by value).
/// <list type="bullet">
/// <item>Stack (Shift held): the other cosmetics of that type leave car.MountedParts for the duration of the call, so
///   the game finds none to take off; they go back right after (a finalizer, so also if the game throws). Their in-use
///   flags are never touched.</item>
/// <item>Replace (plain click, by default): after a successful mount, any other cosmetic of that type still on (a stack
///   made earlier) comes off with the game's own DismountPart, so a plain click leaves one, as the game intends.</item>
/// <item>Taking a part off, upgrades, liveries and horns: the game's code alone.</item>
/// </list>
/// The garage then refreshes its model (RefreshMountedUpgrades) and, on leaving, sends the car with every mounted part
/// as the game's bitset (Car.GetMountedUpgradesBitSet: one bit per part of the car's list), which the networked car
/// shows part by part for every player.
/// </summary>
[HarmonyPatch(typeof(LocalVehicleCustomizationController), nameof(LocalVehicleCustomizationController.MountDismount))]
internal static class MountDismountPatch
{
    internal sealed class State
    {
        public StackAction Action;
        public int Type;
        public string TypeName;
        public List<VehiclePartSO> SetAside;
    }

    private static void Prefix(Car car, VehiclePartSO part, bool mount, out State __state)
    {
        __state = null;
        try { __state = StackLogic.Before(car, part, mount); }
        catch (Exception e) { DebugLog.Error(StackingPlugin.ModId, e, "MountDismountPatch.Prefix"); }
    }

    private static void Postfix(Car car, VehiclePartSO part, MountingResult __result, State __state)
    {
        try { StackLogic.AfterMount(car, part, __result, __state); }
        catch (Exception e) { DebugLog.Error(StackingPlugin.ModId, e, "MountDismountPatch.Postfix"); }
    }

    private static Exception Finalizer(Exception __exception, Car car, VehiclePartSO part, State __state)
    {
        try { StackLogic.PutBack(car, part, __state, __exception != null); }
        catch (Exception e) { DebugLog.Error(StackingPlugin.ModId, e, "MountDismountPatch.Finalizer"); }
        return __exception;
    }
}

/// <summary>The work behind <see cref="MountDismountPatch"/> (decisions in <see cref="StackRules"/>).</summary>
internal static class StackLogic
{
    private const string Mod = StackingPlugin.ModId;

    /// <summary>Shift (keyboard) or the configured gamepad button is held.</summary>
    internal static bool ModifierHeld()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb != null && kb.shiftKey.isPressed) return true;
        }
        catch (Exception e) { DebugLog.Error(Mod, e, "StackLogic.ModifierHeld"); }
        try
        {
            var hold = StackingConfig.Gamepad?.Value ?? StackingConfig.PadHold.None;
            if (hold == StackingConfig.PadHold.None) return false;
            var pad = Gamepad.current;
            if (pad == null) return false;
            return hold switch
            {
                StackingConfig.PadHold.LeftTrigger => pad.leftTrigger.isPressed,
                StackingConfig.PadHold.LeftStickPress => pad.leftStickButton.isPressed,
                StackingConfig.PadHold.RightStickPress => pad.rightStickButton.isPressed,
                _ => false,
            };
        }
        catch (Exception e) { DebugLog.Error(Mod, e, "StackLogic.ModifierHeld"); return false; }
    }

    /// <summary>The cosmetic's type, or null for anything that is not a VehicleCosmetic.</summary>
    private static CosmeticType? TypeOf(VehiclePartSO p)
    {
        if (p == null) return null;
        var c = p.TryCast<VehicleCosmetic>();
        return c == null ? null : c.CosmeticType;
    }

    /// <summary>Horns: the car plays one (VehicleCustomizationController.MountedHorn). Hidden: not a visible slot.</summary>
    private static bool Stackable(CosmeticType t) => t != CosmeticType.Horns && t != CosmeticType.Hidden;

    private static List<MountedPart> Snapshot(Car car)
    {
        var list = new List<MountedPart>();
        var mounted = car?.MountedParts;
        if (mounted == null) return list;
        for (int i = 0; i < mounted.Count; i++)
        {
            var p = mounted[i];
            var t = TypeOf(p);
            list.Add(new MountedPart(p != null ? p.ItemId : 0, t != null, t != null ? (int)t.Value : -1));
        }
        return list;
    }

    internal static MountDismountPatch.State Before(Car car, VehiclePartSO part, bool mount)
    {
        if (car == null || part == null) return null;
        var type = TypeOf(part);
        bool enabled = StackingConfig.ShiftStacks != null && StackingConfig.ShiftStacks.Value;
        bool held = type != null && mount && ModifierHeld();
        var action = StackRules.Decide(enabled, type != null, type != null && Stackable(type.Value), mount, held,
            StackingConfig.ReplaceWholeStack == null || StackingConfig.ReplaceWholeStack.Value);
        var snap = Snapshot(car);
        if (type == null)
        {
            DebugLog.Verbose(Mod, () => $"{(mount ? "mount" : "take off")} {Name(part)} (not a cosmetic): the game's own");
            return null;
        }
        int t = (int)type.Value;
        int ofType = StackRules.CountOfType(snap, t);
        if (!mount)
        {
            DebugLog.Info(Mod, $"take off {Name(part)} ({type.Value}): only it comes off, {Math.Max(0, ofType - 1)} other(s) of its type stay");
            return null;
        }
        var state = new MountDismountPatch.State { Action = action, Type = t, TypeName = type.Value.ToString() };
        if (action == StackAction.Vanilla)
        {
            DebugLog.Info(Mod, $"mount {Name(part)} ({type.Value}): the game's own" +
                               (Stackable(type.Value) ? "" : " (this type is never stacked)") + $", {ofType} of its type on before");
            return state;
        }
        if (action == StackAction.Stack)
        {
            var others = StackRules.SameType(snap, t, part.ItemId);
            state.SetAside = new List<VehiclePartSO>(others.Count);
            var mounted = car.MountedParts;
            foreach (int i in others) state.SetAside.Add(mounted[i]);
            foreach (var p in state.SetAside) mounted.Remove(p);
            DebugLog.Info(Mod, $"stack {Name(part)} ({type.Value}) next to {Names(state.SetAside)}");
        }
        else DebugLog.Info(Mod, $"mount {Name(part)} ({type.Value}): replaces {ofType} of its type ({NamesOfType(car, t)})");
        return state;
    }

    internal static void AfterMount(Car car, VehiclePartSO part, MountingResult result, MountDismountPatch.State s)
    {
        if (s == null || car == null || part == null) return;
        if (s.Action != StackAction.Replace) return;
        if (result != MountingResult.Mounted)
        {
            DebugLog.Info(Mod, $"mount {Name(part)}: the game said {result}, nothing else taken off");
            return;
        }
        var snap = Snapshot(car);
        var others = StackRules.SameType(snap, s.Type, part.ItemId);
        if (others.Count == 0) return;
        var mounted = car.MountedParts;
        var off = new List<VehiclePartSO>(others.Count);
        foreach (int i in others) off.Add(mounted[i]);
        foreach (var p in off) VehicleCustomizationControllerHelpers.DismountPart(car, p);
        DebugLog.Info(Mod, $"plain click: also took off the stacked {Names(off)} ({s.TypeName})");
    }

    internal static void PutBack(Car car, VehiclePartSO part, MountDismountPatch.State s, bool threw)
    {
        if (s == null || s.Action != StackAction.Stack || s.SetAside == null || car?.MountedParts == null) return;
        var mounted = car.MountedParts;
        int back = 0;
        foreach (var p in s.SetAside)
        {
            if (p == null || mounted.Contains(p)) continue;
            mounted.Add(p);
            back++;
        }
        bool on = part != null && mounted.Contains(part);
        DebugLog.Info(Mod, $"stack {Name(part)}: {(on ? "on" : "not on (the game refused it)")}{(threw ? " (the game threw)" : "")}, " +
                           $"{back} put back; {s.TypeName} now: {NamesOfType(car, s.Type)}; {mounted.Count} part(s) on the car");
    }

    private static string Name(VehiclePartSO p)
    {
        if (p == null) return "(none)";
        try { return $"{p.name} [0x{p.ItemId:X8}]"; }
        catch { return "(unnamed)"; }
    }

    private static string Names(List<VehiclePartSO> parts)
    {
        if (parts == null || parts.Count == 0) return "nothing";
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(sb.Length > 0 ? ", " : "").Append(Name(p));
        return sb.ToString();
    }

    private static string NamesOfType(Car car, int type)
    {
        var mounted = car?.MountedParts;
        if (mounted == null) return "nothing";
        var sb = new StringBuilder();
        for (int i = 0; i < mounted.Count; i++)
        {
            var t = TypeOf(mounted[i]);
            if (t != null && (int)t.Value == type) sb.Append(sb.Length > 0 ? ", " : "").Append(Name(mounted[i]));
        }
        return sb.Length == 0 ? "nothing" : sb.ToString();
    }
}

/// <summary>The cosmetics screen opened: the hint goes on.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.InitializeCosmeticUpgradesScreen))]
internal static class CosmeticsScreenPatch
{
    private static void Postfix(StateMerchantMenu __instance)
    {
        try { StackHint.OnCosmeticsScreen(__instance); }
        catch (Exception e) { DebugLog.Error(StackingPlugin.ModId, e, "CosmeticsScreenPatch.Postfix"); }
    }
}

/// <summary>The garage closed: the hint goes.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.Close))]
internal static class MerchantClosePatch
{
    private static void Postfix()
    {
        try { StackHint.OnClose(); }
        catch (Exception e) { DebugLog.Error(StackingPlugin.ModId, e, "MerchantClosePatch.Postfix"); }
    }
}
