using System;
using System.Collections.Generic;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>What a recorded row is (which page widget drew it). See <see cref="RowRecorder"/>.</summary>
internal enum RowKind
{
    /// <summary>A small upper-case title (SimpleMenu.Heading, a group title on a mod page).</summary>
    Heading,
    /// <summary>A muted line of text (notes, Menu.Small, labels of a custom row).</summary>
    Note,
    /// <summary>A coloured notice (Menu.Banner).</summary>
    Banner,
    /// <summary>A key and its value (Menu.KeyValue, Menu.Bar).</summary>
    KeyValue,
    /// <summary>An Off / On setting (Menu.Switch).</summary>
    Toggle,
    /// <summary>"◂ value ▸" over a list of labels (SimpleMenu.Choice / Toggle / FloatChoice / IntChoice).</summary>
    Choice,
    /// <summary>A number between two ends (Menu.SliderRow / RawSlider).</summary>
    Slider,
    /// <summary>A row that does something when pressed (SimpleMenu.Button, Menu.Button, chips, actions).</summary>
    Button,
    /// <summary>A text box (SimpleMenu.Text, Menu.TextRow).</summary>
    Text,
    /// <summary>One entry of a list (SimpleMenu.ListRow: a mod with its switch).</summary>
    ListItem,
    /// <summary>The title of a card that folds open and shut (Menu.Card).</summary>
    Fold,
}

/// <summary>Which of the game's settings widgets shows a row.</summary>
internal enum WidgetKind
{
    /// <summary>Not selectable: a text line (a clone of the settings rows' label).</summary>
    Text,
    /// <summary>Not selectable: a section title.</summary>
    Heading,
    /// <summary>The game's "◂ value ▸" carousel row (UIOptionSelector).</summary>
    Option,
    /// <summary>The game's slider row (UISliderSelector).</summary>
    Slider,
    /// <summary>The game's button row (UIButtonSelector).</summary>
    Button,
    /// <summary>A label and the game's text box (a copy of the MOD BROWSER's search field).</summary>
    TextField,
    /// <summary>A mod's card: a copy of the MOD BROWSER's own mod cell (a list row that carries a <see cref="CardInfo"/>).</summary>
    Card,
}

/// <summary>What the player did to a row.</summary>
internal enum RowInputKind
{
    /// <summary>Left / right on a carousel (-1 / +1 in <see cref="RowInput.Step"/>).</summary>
    Step,
    /// <summary>Enter / A / a click on the row (a button press, a carousel goes round, a list row opens).</summary>
    Press,
    /// <summary>A slider moved to <see cref="RowInput.Value"/>.</summary>
    SetValue,
    /// <summary>A text box now holds <see cref="RowInput.Text"/>.</summary>
    SetText,
}

internal readonly struct RowInput
{
    public readonly RowInputKind Kind;
    public readonly int Step;
    public readonly float Value;
    public readonly string Text;

    private RowInput(RowInputKind kind, int step, float value, string text = null)
    {
        Kind = kind;
        Step = step;
        Value = value;
        Text = text;
    }

    public static RowInput Stepped(int step) => new(RowInputKind.Step, step < 0 ? -1 : 1, 0f);
    public static RowInput Pressed() => new(RowInputKind.Press, 0, 0f);
    public static RowInput Slid(float value) => new(RowInputKind.SetValue, 0, value);
    public static RowInput Typed(string text) => new(RowInputKind.SetText, 0, 0f, text ?? "");
}

/// <summary>
/// One row of a page as the page's draw code described it in a recording pass. The game-widget host turns each into
/// a clone of the game's settings widget (<see cref="RowMap.WidgetFor"/>) and sends what the player does back as a
/// <see cref="RowInput"/>, which the same draw code applies on the next pass. Pure data, unit tested.
/// </summary>
internal sealed class RowModel
{
    public RowKind Kind { get; set; }
    public string Label { get; set; } = "";
    /// <summary>The value on the right (button value, list switch text, slider's shown value, text box value).</summary>
    public string Value { get; set; }
    /// <summary>A muted line under the label (list rows).</summary>
    public string Detail { get; set; }
    /// <summary>A small upper-case tag (list rows: CLIENT / HOST / SERVER, PRIVATE, OTHER CREATOR).</summary>
    public string Badge { get; set; }
    /// <summary>The row's explanation, shown while it is selected.</summary>
    public string Hint { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>List rows: the switch is on. Folds: the card is open.</summary>
    public bool On { get; set; }
    /// <summary>List rows: left / right switch it.</summary>
    public bool CanStep { get; set; }
    /// <summary>Choices: the labels and the current one (-1: none of them, <see cref="Value"/> shows instead).</summary>
    public string[] Options { get; set; }
    public int Index { get; set; }
    /// <summary>Choices: the arrows go round at the ends.</summary>
    public bool Wrap { get; set; } = true;
    /// <summary>Sliders: the ends and the current value.</summary>
    public float Min { get; set; }
    public float Max { get; set; }
    public float Number { get; set; }
    /// <summary>Text boxes: the most characters it takes.</summary>
    public int MaxLength { get; set; } = 64;
    /// <summary>Banners: the colour as 0xRRGGBB.</summary>
    public int Color { get; set; }

    /// <summary>
    /// An explicit identity for rows of a list (a mod, a player, a saved spot and the action on it): when set it is the
    /// row's key as it is, digits and all ("Spot 1 · Go" is never "Spot 2 · Go"), whatever the label shows.
    /// </summary>
    public string Id { get; set; }

    /// <summary>List rows of the MODS list: the mod's card (shown as one of the browser's mod cells), or null.</summary>
    public CardInfo Card { get; set; }

    /// <summary>
    /// What identifies the row from one pass to the next (an input only goes to the same row): its <see cref="Id"/>,
    /// else its label without digits, so a label with a live count or value in it ("Back (3)", "Apply 1.5×") is still
    /// the same row a frame later.
    /// </summary>
    public string Key => Id != null ? Kind + "#" + Id : Kind + "|" + WithoutDigits(Label);

    private static string WithoutDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (!char.IsDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>Pure rules for showing rows with the game's widgets. Unit tested.</summary>
internal static class RowMap
{
    /// <summary>The game widget for a kind of row.</summary>
    public static WidgetKind WidgetFor(RowKind kind) => kind switch
    {
        RowKind.Heading => WidgetKind.Heading,
        RowKind.Note or RowKind.Banner or RowKind.KeyValue => WidgetKind.Text,
        RowKind.Toggle or RowKind.Choice or RowKind.ListItem => WidgetKind.Option,
        RowKind.Slider => WidgetKind.Slider,
        RowKind.Text => WidgetKind.TextField,
        _ => WidgetKind.Button, // Button, Fold
    };

    /// <summary>The game widget for a row: a list row with a card is a card when <paramref name="cards"/> (the
    /// browser's mod cell was found), else by its kind.</summary>
    public static WidgetKind WidgetFor(RowModel row, bool cards) =>
        cards && row.Kind == RowKind.ListItem && row.Card != null ? WidgetKind.Card : WidgetFor(row.Kind);

    /// <summary>True for rows the player can select (keyboard / gamepad stop on them).</summary>
    public static bool Selectable(WidgetKind w) => w is WidgetKind.Option or WidgetKind.Slider or WidgetKind.Button or WidgetKind.TextField or WidgetKind.Card;

    private static readonly string[] OffOn = { "OFF", "ON" };

    /// <summary>
    /// The labels the carousel shows and which is current. A choice whose value is none of its labels shows that
    /// value first (the arrows then step onto the labels); a list row shows its switch text on the side it is on and
    /// the other state beside it, so the one arrow that can move switches it (the game's carousel stops at the ends); a
    /// row that can't step shows its one value.
    /// </summary>
    public static string[] OptionsFor(RowModel row, out int index)
    {
        switch (row.Kind)
        {
            case RowKind.Toggle:
                index = row.On ? 1 : 0;
                return OffOn;
            case RowKind.ListItem:
                string value = string.IsNullOrEmpty(row.Value) ? (row.On ? "ON" : "OFF") : row.Value;
                if (!row.CanStep)
                {
                    index = 0;
                    return new[] { value };
                }
                index = row.On ? 1 : 0;
                return row.On ? new[] { "OFF", value } : new[] { value, "ON" };
            default:
                var labels = row.Options ?? Array.Empty<string>();
                if (row.Index >= 0 && row.Index < labels.Length)
                {
                    index = row.Index;
                    return labels;
                }
                var withCustom = new string[labels.Length + 1];
                withCustom[0] = string.IsNullOrEmpty(row.Value) ? "Custom" : row.Value;
                Array.Copy(labels, 0, withCustom, 1, labels.Length);
                index = 0;
                return withCustom;
        }
    }

    /// <summary>
    /// The step the player made on a carousel of <paramref name="count"/> labels that showed <paramref name="before"/>
    /// and now shows <paramref name="after"/>: -1, +1 (going round at the ends counts as one step) or 0.
    /// </summary>
    public static int CarouselStep(int before, int after, int count)
    {
        if (before == after || count < 2) return 0;
        if (count > 2)
        {
            if (before == count - 1 && after == 0) return 1;
            if (before == 0 && after == count - 1) return -1;
        }
        return after > before ? 1 : -1;
    }

    /// <summary>
    /// The value a slider of the game (from <paramref name="gameMin"/> to <paramref name="gameMax"/>) shows for a
    /// setting from <paramref name="min"/> to <paramref name="max"/>, and back with <see cref="FromGame"/>.
    /// </summary>
    public static float ToGame(float value, float min, float max, float gameMin, float gameMax)
    {
        if (max <= min) return gameMin;
        float t = Clamp01((value - min) / (max - min));
        return gameMin + t * (gameMax - gameMin);
    }

    public static float FromGame(float gameValue, float min, float max, float gameMin, float gameMax)
    {
        if (gameMax <= gameMin) return min;
        float t = Clamp01((gameValue - gameMin) / (gameMax - gameMin));
        return min + t * (max - min);
    }

    private static float Clamp01(float t) => t < 0f ? 0f : t > 1f ? 1f : t;

    /// <summary>
    /// <paramref name="text"/> at the given alpha (two hex digits), then back to opaque. TextMeshPro has no
    /// closing alpha tag (a literal "&lt;/alpha&gt;" is printed as text), so the alpha is reset with <alpha=#FF>.
    /// </summary>
    public static string Alpha(string hex, string text) => "<alpha=#" + hex + ">" + text + "<alpha=#FF>";

    /// <summary>The text of a button row: the label, then its value (dimmed) when it has one.</summary>
    public static string ButtonText(RowModel row)
    {
        string label = row.Label ?? "";
        if (row.Kind == RowKind.Fold) return label + "   " + Alpha("99", row.On ? "HIDE" : "SHOW");
        return string.IsNullOrEmpty(row.Value) ? label : label + "   " + Alpha("99", row.Value);
    }

    /// <summary>The label of a list row: the name, then its badge and detail small and dimmed.</summary>
    public static string ListLabel(RowModel row)
    {
        string label = row.Label ?? "";
        string small = Join(row.Badge, row.Detail);
        return small.Length == 0 ? label : label + "  <size=70%>" + Alpha("AA", small) + "</size>";
    }

    /// <summary>The text of a text line: key and value for KeyValue rows, the text otherwise.</summary>
    public static string LineText(RowModel row) => row.Kind == RowKind.KeyValue
        ? (row.Label ?? "") + "   " + Alpha("FF", row.Value ?? "")
        : row.Label ?? "";

    private static string Join(string a, string b)
    {
        bool ha = !string.IsNullOrEmpty(a), hb = !string.IsNullOrEmpty(b);
        if (ha && hb) return a + "  ·  " + b;
        return ha ? a : hb ? b : "";
    }
}

/// <summary>Where a choice goes when the player steps it. Shared by the page's own rows and the game widgets. Unit tested.</summary>
internal static class ChoiceStep
{
    /// <summary>
    /// The next index of a choice of <paramref name="count"/> labels at <paramref name="index"/> (-1: none of them)
    /// after a step of <paramref name="step"/>. <paramref name="cycle"/> (Enter / a click) always goes round, arrows
    /// stop at the ends unless <paramref name="wrap"/>. From -1 stepping starts at <paramref name="from"/> when that
    /// is one of the labels, otherwise at the first (step right) or last (step left) label.
    /// </summary>
    public static int Next(int index, int count, int step, bool cycle, bool wrap, int from = -1)
    {
        if (count <= 0 || step == 0) return index;
        int at = index >= 0 ? index : from >= 0 && from < count ? from : -1;
        if (at < 0) return step > 0 ? 0 : count - 1;
        if (wrap || cycle) return ((at + step) % count + count) % count;
        int next = at + step;
        return next < 0 ? 0 : next > count - 1 ? count - 1 : next;
    }
}

/// <summary>Keyboard / gamepad order of a page's rows: up and down go to the previous / next selectable row. Unit tested.</summary>
internal static class NavOrder
{
    /// <summary>For each row, the row up and down go to (-1: none, the tab button above the page for the first row).</summary>
    public static (int up, int down)[] Build(IReadOnlyList<WidgetKind> widgets)
    {
        var result = new (int, int)[widgets.Count];
        int previous = -1;
        for (int i = 0; i < widgets.Count; i++)
        {
            result[i] = (-1, -1);
            if (!RowMap.Selectable(widgets[i])) continue;
            result[i].Item1 = previous;
            if (previous >= 0) result[previous].Item2 = i;
            previous = i;
        }
        return result;
    }

    /// <summary>The first selectable row (-1: none), where the selection goes when a page opens.</summary>
    public static int First(IReadOnlyList<WidgetKind> widgets)
    {
        for (int i = 0; i < widgets.Count; i++)
            if (RowMap.Selectable(widgets[i])) return i;
        return -1;
    }

    /// <summary>The selectable row nearest to <paramref name="index"/> (the same one, then below, then above; -1: none),
    /// where the selection goes when the row it was on is gone.</summary>
    public static int Nearest(IReadOnlyList<WidgetKind> widgets, int index)
    {
        if (widgets.Count == 0) return -1;
        if (index < 0) index = 0;
        if (index >= widgets.Count) index = widgets.Count - 1;
        for (int i = index; i < widgets.Count; i++)
            if (RowMap.Selectable(widgets[i])) return i;
        for (int i = index - 1; i >= 0; i--)
            if (RowMap.Selectable(widgets[i])) return i;
        return -1;
    }
}
