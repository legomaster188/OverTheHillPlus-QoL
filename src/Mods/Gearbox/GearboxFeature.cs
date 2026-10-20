using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using UnityEngine.InputSystem;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.GearboxPlus;

/// <summary>Gearbox+'s advanced card: the keys for neutral, for shifting up and down and for the differential lock.</summary>
internal sealed class GearboxFeature : IFeature
{
    public void Register() => Registry.Section(Registry.PlusMode, "Gearbox", DrawAdvanced);

    private static void DrawAdvanced()
    {
        Card("Keys", null, () =>
        {
            KeyRow("Neutral key", GearboxConfig.NeutralKey);
            KeyRow("Shift up key", GearboxConfig.ShiftUpKey);
            KeyRow("Shift down key", GearboxConfig.ShiftDownKey);
            Small("Extra to the game's own gear buttons (change those in the game's Controls). All three press the game's own " +
                  "gear buttons, so they work on the manual gearbox only.");
            KeyRow("Differential lock key", GearboxConfig.DiffLockKey);
            Small("Locks every differential while it is on, and goes back to your setting when pressed again. Works once the differentials " +
                  "are set to the real car's or your own (MODS page): the game's own are locked already.");
        });
    }

    private static void KeyRow(string label, BepInEx.Configuration.ConfigEntry<Key> entry)
    {
        var key = entry.Value;
        KeyValue(label, key == Key.None ? "none" : KeybindEditor.KeyName(key));
        Chips(1, ("Change the key…", () => KeybindEditor.StartCapture(entry), true));
    }
}
