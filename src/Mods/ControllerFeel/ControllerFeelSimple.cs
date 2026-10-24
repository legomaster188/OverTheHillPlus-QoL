using System;
using System.Text;
using BepInEx.Configuration;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.ControllerFeel;

/// <summary>Rumble+'s MODS page: the switches, a strength for each effect, the game's own rumble, and a motor test.</summary>
internal static class ControllerFeelSimple
{
    private static readonly float[] Strengths = { 0f, 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };
    private static readonly float[] GameLevels = { 1f, 0.5f, 0f };

    private static string Percent(float v) => (v * 100f).ToString("0") + "%";

    private static void Strength(string label, string hint, ConfigEntry<float> entry, bool enabled) =>
        SimpleMenu.FloatChoice(label, hint, entry.Value, Strengths, Percent, v => entry.Value = v, enabled);

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (ControllerFeelConfig.Rumble == null) return;
        string problem = ControllerFeelCore.Problem();
        if (problem != null) SimpleMenu.Note(problem);

        SimpleMenu.Heading("Rumble");
        SimpleMenu.Toggle(ControllerFeelConfig.Rumble, "Rumble+", "Master switch. Off: this mod never touches the gamepad's motors.");
        bool on = ControllerFeelConfig.Rumble.Value;
        SimpleMenu.Toggle(ControllerFeelConfig.OnlyWithPad, "Only when playing with the gamepad", "No rumble while the game is set to keyboard and mouse.", on);
        SimpleMenu.Actions("Test the motors", ("Test now", ControllerFeelCore.StartTest, on && problem == null));

        SimpleMenu.Heading("Strength");
        Strength("All effects", "Every effect at once. The game's own Controller Vibration setting also applies.", ControllerFeelConfig.Master, on);
        Strength("Surface", "Gravel, rock, snow, sand, mud and the rest under the wheels, by speed.", ControllerFeelConfig.Surface, on);
        Strength("Bumps and bottoming out", "Hits from the suspension and the bump stop.", ControllerFeelConfig.Bumps, on);
        Strength("Landings", "The thump after a jump or a drop.", ControllerFeelConfig.Landing, on);
        Strength("Wheelspin", "A driven wheel spinning under power.", ControllerFeelConfig.Wheelspin, on);
        Strength("Lock-ups and skids", "Braked wheels locking up and the car sliding sideways.", ControllerFeelConfig.Lockup, on);
        Strength("Engine idle", "The shake of the engine at idle.", ControllerFeelConfig.Idle, on);
        Strength("Winch strain", "A taut winch rope pulling on the car.", ControllerFeelConfig.Winch, on);

        SimpleMenu.Heading("The game's own rumble");
        int level = Array.IndexOf(GameLevels, (float)Math.Round(ControllerFeelConfig.GameRumble.Value * 2f) / 2f);
        SimpleMenu.Choice("Keep the game's rumble", "The game has its own rumble now (bumps, wheelspin, winch...). All: both together. None: Rumble+ only.",
            level, new[] { "All of it", "Half", "None" }, i => ControllerFeelConfig.GameRumble.Value = GameLevels[i], on, wrap: true);
        SimpleMenu.Note(Info());
    }

    private static string Info()
    {
        var sb = new StringBuilder();
        string pad = PadOutput.PadName;
        sb.Append(pad == null ? "No gamepad seen yet. " : $"Pad: {pad}. ");
        sb.Append(PadOutput.TriggersAvailable
            ? "Trigger motors: yes (lock-ups on the left trigger, wheelspin on the right). "
            : "Two motors (heavy and light). Trigger motors and DualSense adaptive triggers cannot be reached through the game's input system, so they are not used. ");
        string closed = ControllerFeelCore.ClosedBecause;
        if (closed != null) sb.Append("Quiet now: ").Append(closed).Append('.');
        else
        {
            var p = ControllerFeelCore.Parts;
            sb.Append("Now: ");
            for (int i = 0; i < FeelMixer.EffectNames.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(FeelMixer.EffectNames[i]).Append(' ').Append(Percent(p[i]));
            }
            sb.Append('.');
        }
        return sb.ToString();
    }
}
