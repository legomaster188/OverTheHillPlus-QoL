using System.Collections.Generic;

namespace OverTheHillPlus.Stacking;

/// <summary>What a click on a cosmetic in the garage does.</summary>
public enum StackAction
{
    /// <summary>The game's own behaviour, untouched (upgrades, liveries, horns, taking a cosmetic off, mod off).</summary>
    Vanilla,
    /// <summary>Shift held: the cosmetic goes on next to the ones of its type already on.</summary>
    Stack,
    /// <summary>A plain click: the game replaces the cosmetic of that type, and any others of it stacked earlier go too.</summary>
    Replace,
}

/// <summary>One part on the car, as the stacking rules see it.</summary>
public readonly struct MountedPart
{
    public readonly uint ItemId;
    /// <summary>A VehicleCosmetic (not an upgrade or a livery).</summary>
    public readonly bool IsCosmetic;
    /// <summary>Its CosmeticType as a number (only meaningful for cosmetics).</summary>
    public readonly int Type;

    public MountedPart(uint itemId, bool isCosmetic, int type)
    {
        ItemId = itemId;
        IsCosmetic = isCosmetic;
        Type = type;
    }
}

/// <summary>
/// Cosmetic Stacking's decisions, pure (unit-tested). The game's mount (VehicleCustomizationControllerHelpers
/// .MountDismountPart) takes off the first mounted part of the same kind (HaveAlreadySameTypeMounted: same
/// CosmeticType for a cosmetic) before it adds the new one; a stack is the same per-car "in use" flags the game already
/// saves and sends (one bit per part), just with several of one type set.
/// </summary>
public static class StackRules
{
    /// <summary>What a click does. <paramref name="stackableType"/>: the cosmetic's type allows several (not the horn,
    /// which the game plays one of, nor the hidden type); <paramref name="mount"/>: the game is about to put the part on
    /// (false: take it off, always only that one).</summary>
    public static StackAction Decide(bool enabled, bool isCosmetic, bool stackableType, bool mount, bool modifierHeld,
        bool replaceWholeStack)
    {
        if (!enabled || !isCosmetic || !stackableType || !mount) return StackAction.Vanilla;
        if (modifierHeld) return StackAction.Stack;
        return replaceWholeStack ? StackAction.Replace : StackAction.Vanilla;
    }

    /// <summary>Indices in <paramref name="mounted"/> of the other cosmetics of <paramref name="type"/> (never the part
    /// <paramref name="partId"/> itself): what a stack sets aside while the game mounts, and what a plain click takes off
    /// after it.</summary>
    public static List<int> SameType(IReadOnlyList<MountedPart> mounted, int type, uint partId)
    {
        var found = new List<int>();
        if (mounted == null) return found;
        for (int i = 0; i < mounted.Count; i++)
        {
            var m = mounted[i];
            if (m.IsCosmetic && m.Type == type && m.ItemId != partId) found.Add(i);
        }
        return found;
    }

    /// <summary>How many cosmetics of <paramref name="type"/> are on (for the log).</summary>
    public static int CountOfType(IReadOnlyList<MountedPart> mounted, int type)
    {
        int n = 0;
        if (mounted == null) return 0;
        foreach (var m in mounted)
            if (m.IsCosmetic && m.Type == type) n++;
        return n;
    }

    /// <summary>The hint line at the bottom of the cosmetics screen.</summary>
    public static string HintText(string gamepadHold)
    {
        string text = "Shift + click: add without removing the current one";
        if (!string.IsNullOrEmpty(gamepadHold)) text += " (pad: hold " + gamepadHold + ")";
        return text;
    }
}
