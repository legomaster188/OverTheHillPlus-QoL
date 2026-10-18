using System;
using HarmonyLib;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// Our clones of the game's text component (FunTextTMP) keep their localization data, so the component's own
/// lifecycle (OnEnable registers a change handler, OnDisable / OnDestroy clear it) has what it needs. What it must not
/// do is write the localized string over the text we set: UpdateString is skipped for our clones.
/// </summary>
internal static class FunTexts
{
    /// <summary>The clones (their native pointers), removed again when the game destroys them.</summary>
    internal static readonly PointerSet Ours = new();

    internal static void Register(FunTextTMP t)
    {
        try { if (t != null) Ours.Add(t.Pointer); } catch { }
    }

    internal static bool IsOurs(FunTextTMP t)
    {
        try { return t != null && Ours.Contains(t.Pointer); }
        catch { return false; }
    }
}

[HarmonyPatch(typeof(FunTextTMP), nameof(FunTextTMP.UpdateString))]
internal static class FunTextUpdateStringPatch
{
    private static bool Prefix(FunTextTMP __instance) => !FunTexts.IsOurs(__instance);
}

// Defensive: a null reference inside the game's lifecycle on one of our clones is swallowed; any other object's
// exception goes on as it would have.
[HarmonyPatch(typeof(FunTextTMP), "OnEnable")]
internal static class FunTextEnableGuardPatch
{
    private static Exception Finalizer(Exception __exception, FunTextTMP __instance) =>
        __exception is NullReferenceException && FunTexts.IsOurs(__instance) ? null : __exception;
}

[HarmonyPatch(typeof(FunTextTMP), "OnDisable")]
internal static class FunTextDisableGuardPatch
{
    private static Exception Finalizer(Exception __exception, FunTextTMP __instance) =>
        __exception is NullReferenceException && FunTexts.IsOurs(__instance) ? null : __exception;
}

[HarmonyPatch(typeof(FunTextTMP), "OnDestroy")]
internal static class FunTextDestroyGuardPatch
{
    private static Exception Finalizer(Exception __exception, FunTextTMP __instance)
    {
        if (!FunTexts.IsOurs(__instance)) return __exception;
        try { FunTexts.Ours.Remove(__instance.Pointer); } catch { }
        return __exception is NullReferenceException ? null : __exception;
    }
}
