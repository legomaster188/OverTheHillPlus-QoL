using System;
using System.Collections.Generic;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// A settings screen of a mod's own in the game's look, shown on the MODS screen's machinery (<see cref="GameModsTab"/>):
/// the big title, a list of sections on the left (copies of the Settings menu's buttons, like GRAPHICS, AUDIO ...), the
/// section's SimpleMenu rows beside it as the game's own settings rows, and the section name and the selected row's
/// explanation on the right. Opened with <see cref="GameModsScreen.OpenScreen"/>; Esc / B goes from the rows to the list
/// and from the list back to where it was opened (the pause menu, Create Server).
/// </summary>
internal sealed class GameScreenSpec
{
    /// <summary>The big title top left ("HOST SETTINGS").</summary>
    public string Title = "";

    /// <summary>The sections on the left, asked every frame: key (never starting with '#'), label, and a short value
    /// shown dimmed on the right of the button (or null). Headings have <see cref="ModsScreenModel.Entry.Heading"/>.</summary>
    public Func<IReadOnlyList<ModsScreenModel.Entry>> Sections;

    /// <summary>Draws one section's rows (SimpleMenu calls, recorded into the game's widgets).</summary>
    public Action<string> Draw;

    /// <summary>A short line under the section's name on the right (or null).</summary>
    public Func<string, string> Meta;

    /// <summary>The text on the right when the selected row has no explanation of its own (or null).</summary>
    public Func<string, string> About;

    /// <summary>The mod page the full view shows when the screen can't be made.</summary>
    public string FallbackModId;
}
