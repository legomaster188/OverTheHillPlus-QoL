using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using UnityEngine.InputSystem;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.Stats;

/// <summary>Trip Stats' advanced cards on its MODS page: the card's look and hotkey, and how a session is counted.</summary>
internal sealed class StatsFeature : IFeature
{
    public void Register() => Registry.Section(Registry.PlusMode, "Trip Stats", DrawAdvanced);

    private static void DrawAdvanced()
    {
        Card("Card", null, () =>
        {
            SliderRow(StatsConfig.CardOpacity, "Background opacity", 0.2f, 1f, "0.00", "", true);
            var key = StatsConfig.CardKey.Value;
            KeyValue("Show / hide hotkey", key == Key.None ? "none" : KeybindEditor.KeyName(key));
            Chips(1, ("Change the hotkey…", () => KeybindEditor.StartCapture(StatsConfig.CardKey), true));
        });
        Card("Session", null, () =>
        {
            Switch(StatsConfig.NewSessionResets, "New session starts the numbers again",
                "Off: the session numbers keep adding up until you restart the game", true);
            Small("All-time numbers are saved to oth.stats.json in the BepInEx config folder every 30 s while you drive, " +
                  "and when the game quits.");
        });
    }
}
