using OverTheHillPlus.UI;

namespace OverTheHillPlus.Stacking;

/// <summary>Cosmetic Stacking's settings on its MODS page.</summary>
internal static class StackingSimple
{
    private static readonly string[] Pads = { "None", "Left trigger", "Left stick press", "Right stick press" };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (StackingConfig.ShiftStacks == null) return;
        SimpleMenu.Heading("Garage cosmetics");
        SimpleMenu.Toggle(StackingConfig.ShiftStacks, "Shift + click stacks",
            "Puts the cosmetic on without taking off the one of its type already on. Not for horns (the car plays one).");
        SimpleMenu.Toggle(StackingConfig.ReplaceWholeStack, "Plain click replaces the stack",
            "A plain click takes off every cosmetic of that type that is on (the game alone takes off only one).");
        SimpleMenu.Choice("Gamepad: hold", "Hold this while pressing the submit button to stack (Shift always works).",
            (int)StackingConfig.Gamepad.Value, Pads, i => StackingConfig.Gamepad.Value = (StackingConfig.PadHold)i,
            StackingConfig.ShiftStacks.Value);
        SimpleMenu.Toggle(StackingConfig.ShowHint, "Hint on the cosmetics screen", "A line at the bottom of the screen saying how to stack.");
        SimpleMenu.Note("Take a cosmetic off by clicking it while it is on: only that one comes off. Stacks are saved with " +
                        "your car and shown to every player, also those without the mod.");
        if (StackHint.Problem != null) SimpleMenu.Note("Hint: " + StackHint.Problem);
    }
}
