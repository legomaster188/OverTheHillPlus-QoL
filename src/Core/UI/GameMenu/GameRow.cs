using System;
using System.Collections.Generic;
using FunCore.Style;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// One row of a MODS page as a clone of the game's own settings widget (<see cref="GamePrefabs"/>): the "◂ value ▸"
/// carousel row, the slider row, the button row, or a text line made from their label. <see cref="Bind"/> shows a
/// <see cref="RowModel"/> on it (only what changed is written to the game objects); what the player does on it comes
/// out of <see cref="Input"/>. Clones are made under the hidden template holder and configured (localization off)
/// before they move onto the page, so they wake up with our texts.
/// </summary>
internal sealed class GameRow
{
    public readonly WidgetKind Kind;
    public readonly GameObject Go;
    public readonly Selectable Selectable;
    public readonly TMP_Text Text;

    private readonly UIOptionSelector _option;
    private readonly CarouselOption _carousel;
    private readonly UISliderSelector _slider;
    private readonly Slider _bar;
    private readonly TMP_Text _sliderValue;
    private readonly TMP_InputField _field;
    private readonly ContextUIModCell _cell;
    private readonly GameObject _versionSlot, _sideSlot, _sizeSlot;
    private string _imageKey;

    /// <summary>Our text boxes by native pointer (the browser's search-submit patch leaves them alone).</summary>
    internal static readonly HashSet<IntPtr> TextFields = new();

    /// <summary>True while this row's text box has the keyboard.</summary>
    public bool Editing
    {
        get
        {
            try { return _field != null && _field.isFocused; }
            catch { return false; }
        }
    }

    /// <summary>The model shown, its index in the recorder (-1: a row of the host's own) and the host's action for it.</summary>
    public RowModel Model { get; private set; }
    public int Index;
    public Action Direct;

    /// <summary>Called with what the player did (a step, a press, a slider move).</summary>
    public Action<GameRow, RowInput> Input;

    private string[] _options;
    private int _carouselIndex = -1;
    private float _gameMin, _gameMax = 1f;
    private bool _suppress;
    private float _baseSize = -1f;

    /// <summary>Option rows of ours by native pointer, so the UISelector submit / click patch can tell them apart.</summary>
    internal static readonly Dictionary<IntPtr, GameRow> ByPointer = new();

    private GameRow(WidgetKind kind, GameObject go)
    {
        Kind = kind;
        Go = go;
        Selectable = go.GetComponent<Selectable>();
        switch (kind)
        {
            case WidgetKind.Option:
                _option = go.GetComponent<UIOptionSelector>();
                _carousel = _option != null ? _option.CarouselOption : null;
                Text = LabelOf(go);
                break;
            case WidgetKind.Slider:
                _slider = go.GetComponent<UISliderSelector>();
                _bar = _slider != null ? _slider.Slider : null;
                _sliderValue = _slider != null ? _slider.text : null;
                if (_sliderValue != null && !_sliderValue.transform.IsChildOf(go.transform)) _sliderValue = null;
                Text = LabelOf(go);
                if (_bar != null)
                {
                    _gameMin = _bar.minValue;
                    _gameMax = _bar.maxValue;
                }
                break;
            case WidgetKind.Button:
                Text = go.GetComponentInChildren<TMP_Text>(true);
                break;
            case WidgetKind.Card:
                _cell = go.GetComponent<ContextUIModCell>();
                Selectable = CardSelectable(go, _cell);
                Text = _cell != null ? _cell.NameLabel : null;
                if (_cell != null)
                {
                    _versionSlot = SlotOf(_cell.DownloadsLabel);
                    _sideSlot = SlotOf(_cell.LikesLabel);
                    _sizeSlot = SlotOf(_cell.SizeLabel);
                    // The cell's own subscribe button is about mod.io liveries: not on our cards.
                    var sub = _cell.SubscribeButton;
                    if (sub != null && sub.gameObject.Pointer != go.Pointer && (Selectable == null || sub.Pointer != Selectable.Pointer)) sub.gameObject.SetActive(false);
                }
                break;
            case WidgetKind.TextField:
                _field = go.GetComponentInChildren<TMP_InputField>(true);
                Selectable = _field;
                foreach (var t in go.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (t == null || _field != null && t.transform.IsChildOf(_field.transform)) continue;
                    Text = t;
                    break;
                }
                break;
            default:
                Text = go.GetComponent<TMP_Text>() ?? go.GetComponentInChildren<TMP_Text>(true);
                break;
        }
    }

    /// <summary>The template a kind of widget is cloned from (null: not found in this game version).</summary>
    internal static GameObject TemplateFor(WidgetKind kind) => kind switch
    {
        WidgetKind.Option => GamePrefabs.Option,
        WidgetKind.Slider => GamePrefabs.Slider,
        WidgetKind.Button => GamePrefabs.Button,
        WidgetKind.Card => GamePrefabs.ModCell,
        _ => GamePrefabs.Label,
    };

    /// <summary>Clones the game's widget for <paramref name="kind"/> onto <paramref name="parent"/> (null when the template is missing).</summary>
    internal static GameRow Create(WidgetKind kind, Transform parent)
    {
        var template = TemplateFor(kind);
        var holder = GamePrefabs.Holder;
        if (template == null || holder == null || parent == null) return null;
        GameObject go;
        if (kind == WidgetKind.TextField)
        {
            go = MakeTextRow(holder);
            if (go == null) return null;
        }
        else
        {
            if (kind == WidgetKind.Card) OverTheHillPlus.CrashTrail.Mark("MODS cards: making a card");
            go = Object.Instantiate(template, holder, false);
            go.name = "OTH_Row_" + kind;
            Prepare(go, kind);
        }
        var row = new GameRow(kind, go);
        // The copy must hold its own carousel / slider: a template wired to another object's would drive that one.
        bool ok = kind switch
        {
            WidgetKind.Option => row._option != null && row._carousel != null && row._carousel.transform.IsChildOf(go.transform),
            WidgetKind.Slider => row._slider != null && row._bar != null && row._bar.transform.IsChildOf(go.transform),
            WidgetKind.Button => go.GetComponent<UIButtonSelector>() != null,
            WidgetKind.Card => row._cell != null && row.Selectable != null && row.Selectable.TryCast<UIButtonSelector>() != null && row.Text != null,
            WidgetKind.TextField => row._field != null && row._field.textComponent != null && row._field.transform.IsChildOf(go.transform) &&
                                    row._field.textComponent.transform.IsChildOf(go.transform),
            _ => row.Text != null,
        };
        if (!ok)
        {
            Object.Destroy(go);
            return null;
        }
        float height = TemplateHeight(kind == WidgetKind.TextField ? GamePrefabs.Option ?? template : template);
        if (kind != WidgetKind.Text && kind != WidgetKind.Heading && kind != WidgetKind.Card) // the cards' grid sizes them
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
            le.flexibleHeight = 0f;
        }
        if (row.Text != null) row.Text.richText = true; // badges and details are small and dimmed with rich text
        if (row._carousel != null && row._carousel.Option != null) row._carousel.Option.richText = true; // colour swatches
        go.transform.SetParent(parent, false); // wakes up here (Awake / OnEnable of the game's components)
        row.Hook();
        return row;
    }

    /// <summary>
    /// A text row: the settings rows' label on the left and a copy of the browser's search field on the right, side by
    /// side in a row as tall as the option rows.
    /// </summary>
    private static GameObject MakeTextRow(Transform holder)
    {
        if (GamePrefabs.Label == null || GamePrefabs.TextField == null) return null;
        var row = new GameObject("OTH_Row_TextField");
        var rt = row.AddComponent<RectTransform>();
        rt.SetParent(holder, false);
        var h = row.AddComponent<HorizontalLayoutGroup>();
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        h.spacing = 16f;
        h.padding = new RectOffset(16, 8, 6, 6);

        var label = Object.Instantiate(GamePrefabs.Label, rt, false);
        label.name = "Label";
        Prepare(label, WidgetKind.Text);
        var lle = label.AddComponent<LayoutElement>();
        lle.flexibleWidth = 1f;
        lle.minWidth = 120f;

        var field = Object.Instantiate(GamePrefabs.TextField, rt, false);
        field.name = "Field";
        Prepare(field, WidgetKind.Button);
        var fle = field.GetComponent<LayoutElement>() ?? field.AddComponent<LayoutElement>();
        fle.flexibleWidth = 1.3f;
        fle.minWidth = 200f;
        fle.ignoreLayout = false;
        return row;
    }

    /// <summary>Localization components removed, the FunTextTMP clones registered with FunTexts (our texts stay), sizes left to our layout, text lines set up to wrap.</summary>
    private static void Prepare(GameObject go, WidgetKind kind)
    {
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            string type = c.GetIl2CppType().FullName ?? "";
            if (type.Contains("Locali", StringComparison.OrdinalIgnoreCase)) Object.DestroyImmediate(c);
        }
        // The page's column sizes the row: a fitter on the row itself would fight it (a text line keeps no fixed size either).
        var fitter = go.GetComponent<ContentSizeFitter>();
        if (fitter != null) Object.DestroyImmediate(fitter);
        if (kind is WidgetKind.Text or WidgetKind.Heading)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le != null) Object.DestroyImmediate(le);
        }
        foreach (var t in go.GetComponentsInChildren<FunTextTMP>(true))
        {
            FunTexts.Register(t);
            if (kind is WidgetKind.Text or WidgetKind.Heading)
            {
                try { t.EnableStyleFunctionality = false; } catch { }
            }
        }
        if (kind is WidgetKind.Text or WidgetKind.Heading)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
            }
            var text = go.GetComponent<TMP_Text>();
            if (text != null)
            {
                text.enableAutoSizing = false;
                text.enableWordWrapping = true;
                text.overflowMode = TextOverflowModes.Overflow;
                text.richText = true;
                text.raycastTarget = false;
                text.alignment = TextAlignmentOptions.MidlineLeft;
            }
        }
    }

    private static float TemplateHeight(GameObject template)
    {
        try
        {
            var rt = template.GetComponent<RectTransform>();
            float h = rt != null ? rt.rect.height : 0f;
            if (h < 1f && rt != null) h = rt.sizeDelta.y;
            return h >= 16f ? h : 64f;
        }
        catch { return 64f; }
    }

    /// <summary>The row's name text: a text that isn't the carousel's value, inside its arrows or the slider's value.</summary>
    internal static TMP_Text LabelOf(GameObject go)
    {
        if (go == null) return null;
        var option = go.GetComponent<UIOptionSelector>();
        var car = option != null ? option.CarouselOption : null;
        var slider = go.GetComponent<UISliderSelector>();
        IntPtr value = car != null && car.Option != null ? car.Option.Pointer : IntPtr.Zero;
        IntPtr sliderText = slider != null && slider.text != null ? slider.text.Pointer : IntPtr.Zero;
        Transform bar = slider != null && slider.Slider != null && slider.Slider.transform.Pointer != go.transform.Pointer ? slider.Slider.transform : null;
        TMP_Text first = null;
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(true))
        {
            if (t == null) continue;
            first ??= t;
            if (t.Pointer == value || t.Pointer == sliderText) continue;
            if (car != null && (Under(t.transform, car.Left) || Under(t.transform, car.Right))) continue;
            if (bar != null && t.transform.IsChildOf(bar)) continue;
            return t;
        }
        return option == null && slider == null ? first : null;
    }

    private static bool Under(Transform t, Component c) => c != null && t.IsChildOf(c.transform);

    private void Hook()
    {
        if (_option != null)
        {
            _option.add_OnValueChanged(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OptionChanged)));
            ByPointer[_option.Pointer] = this;
        }
        if (_slider != null)
            _slider.add_OnSliderValueChanged(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(SliderChanged)));
        if (_field != null)
        {
            TextFields.Add(_field.Pointer);
            _field.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<string>>(
                new Action<string>(t => { if (!_suppress) Send(RowInput.Typed(t)); })));
            try
            {
                var ph = _field.placeholder != null ? _field.placeholder.TryCast<TMP_Text>() : null;
                if (ph != null) ph.text = "";
            }
            catch { }
        }
        var button = Kind == WidgetKind.Button ? Go.GetComponent<UIButtonSelector>()
            : Kind == WidgetKind.Card && Selectable != null ? Selectable.TryCast<UIButtonSelector>() : null;
        if (button != null)
            button.add_OnPointerClickOrSubmit(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIButtonSelector>>(
                new Action<UIButtonSelector>(_ => Send(RowInput.Pressed()))));
    }

    private void OptionChanged()
    {
        if (_suppress || _carousel == null || _options == null) return;
        int now = _carousel.GetSelected();
        int step = RowMap.CarouselStep(_carouselIndex, now, _options.Length);
        _carouselIndex = now;
        if (step != 0) Send(RowInput.Stepped(step));
    }

    private void SliderChanged()
    {
        if (_suppress || _bar == null || Model == null) return;
        Send(RowInput.Slid(RowMap.FromGame(_bar.value, Model.Min, Model.Max, _gameMin, _gameMax)));
    }

    /// <summary>Enter / A / a click on an option row (from the UISelector patch).</summary>
    internal void Submitted() => Send(RowInput.Pressed());

    private void Send(RowInput input)
    {
        try { Input?.Invoke(this, input); }
        catch (Exception e) { GameModsTab.Fail("row input", e); }
    }

    // ---------------------------------------------------------------- showing a model

    public void Bind(RowModel m)
    {
        Model = m;
        switch (Kind)
        {
            case WidgetKind.Option: BindOption(m); break;
            case WidgetKind.Slider: BindSlider(m); break;
            case WidgetKind.Button: SetText(Text, RowMap.ButtonText(m)); break;
            case WidgetKind.TextField: BindField(m); break;
            case WidgetKind.Card: BindCard(m); break;
            case WidgetKind.Heading: BindLine(m, true); break;
            default: BindLine(m, false); break;
        }
        if (Selectable != null && Selectable.interactable != m.Enabled) Selectable.interactable = m.Enabled;
    }

    private void BindOption(RowModel m)
    {
        SetText(Text, m.Kind == RowKind.ListItem ? RowMap.ListLabel(m) : m.Label);
        var options = RowMap.OptionsFor(m, out int index);
        _suppress = true;
        try
        {
            if (!Same(options, _options))
            {
                var list = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var o in options) list.Add(o);
                _option.Initialize(list, index);
                _options = options;
                _carouselIndex = index;
            }
            else if (_carouselIndex != index || _carousel.GetSelected() != index)
            {
                _option.SetSelected(index);
                _carouselIndex = index;
            }
        }
        finally { _suppress = false; }
        // Arrows only where they do something (a row greyed out later loses them too).
        bool arrows = m.Enabled && options.Length > 1;
        if (_arrows != arrows)
        {
            try { _carousel.CarouselInteractable = arrows; } catch { }
            _arrows = arrows;
        }
    }

    private bool? _arrows;

    // ---------------------------------------------------------------- cards

    /// <summary>What a card is pressed / selected through: the cell's own button, else a button inside it that isn't
    /// its subscribe button, else that one.</summary>
    private static Selectable CardSelectable(GameObject go, ContextUIModCell cell)
    {
        var own = go.GetComponent<UIButtonSelector>();
        if (own != null) return own;
        IntPtr sub = cell != null && cell.SubscribeButton != null ? cell.SubscribeButton.Pointer : IntPtr.Zero;
        foreach (var b in go.GetComponentsInChildren<UIButtonSelector>(true))
            if (b != null && b.Pointer != sub) return b;
        if (cell != null && cell.SubscribeButton != null) return cell.SubscribeButton;
        return go.GetComponent<Selectable>();
    }

    /// <summary>What to show / hide for one of the cell's small labels: its own group (the label with its icon) when
    /// that holds nothing else of the cell, else the label alone. Icons beside it are hidden (they are about liveries:
    /// downloads, likes).</summary>
    private GameObject SlotOf(TMP_Text label)
    {
        if (label == null) return null;
        var p = label.transform.parent;
        bool own = p != null && p.Pointer != Go.transform.Pointer;
        if (own)
        {
            foreach (var other in new TMP_Text[] { _cell.NameLabel, _cell.StatusLabel, _cell.DownloadsLabel, _cell.LikesLabel, _cell.SizeLabel })
                if (other != null && other.Pointer != label.Pointer && other.transform.IsChildOf(p)) { own = false; break; }
            if (own && _cell.Image != null && _cell.Image.transform.IsChildOf(p)) own = false;
        }
        if (!own) return label.gameObject;
        for (int i = 0; i < p.childCount; i++)
        {
            var c = p.GetChild(i);
            if (c == null || c.Pointer == label.transform.Pointer) continue;
            if (c.GetComponentInChildren<TMP_Text>(true) == null && c.GetComponent<Graphic>() != null) c.gameObject.SetActive(false);
        }
        return p.gameObject;
    }

    private void BindCard(RowModel m)
    {
        if (_cell == null) return;
        var c = m.Card;
        SetText(Text, m.Label);
        string status = c?.Status ?? "";
        if (_cell.StatusLabel != null) SetText(_cell.StatusLabel, status);
        Show(_cell.Status, status.Length > 0);
        Slot(_cell.DownloadsLabel, _versionSlot, ModCards.VersionText(c?.Version));
        Slot(_cell.LikesLabel, _sideSlot, c?.Side);
        Slot(_cell.SizeLabel, _sizeSlot, c?.Size);
        string key = c?.ImageKey;
        if (key == _imageKey || _cell.Image == null) return;
        _imageKey = key;
        try
        {
            var sprite = ModImages.For(c);
            if (sprite != null)
            {
                _cell.Image.sprite = sprite;
                _cell.Image.color = Color.white;
                if (!_cell.Image.enabled) _cell.Image.enabled = true;
            }
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogWarning($"[Core] MODS cards: the image of {m.Label}: {e.Message}"); }
    }

    private static void Slot(TMP_Text label, GameObject slot, string text)
    {
        if (label == null) return;
        bool show = !string.IsNullOrEmpty(text);
        if (show) SetText(label, text);
        Show(slot ?? label.gameObject, show);
    }

    private static void Show(GameObject go, bool on)
    {
        if (go != null && go.activeSelf != on) go.SetActive(on);
    }

    private void BindSlider(RowModel m)
    {
        SetText(Text, m.Label);
        if (_sliderValue != null) SetText(_sliderValue, m.Value ?? "");
        float g = RowMap.ToGame(m.Number, m.Min, m.Max, _gameMin, _gameMax);
        if (Mathf.Abs(_bar.value - g) > Mathf.Abs(_gameMax - _gameMin) * 0.002f)
        {
            _suppress = true;
            try { _bar.SetValueWithoutNotify(g); }
            finally { _suppress = false; }
        }
    }

    private void BindField(RowModel m)
    {
        SetText(Text, m.Label);
        if (_field.characterLimit != m.MaxLength) _field.characterLimit = m.MaxLength;
        // While the player types the box holds the text; the page's value catches up through the inputs.
        if (Editing) return;
        string want = m.Value ?? "";
        if (_field.text != want)
        {
            _suppress = true;
            try { _field.SetTextWithoutNotify(want); }
            finally { _suppress = false; }
        }
    }

    private void BindLine(RowModel m, bool heading)
    {
        if (Text == null) return;
        if (_baseSize < 0f) _baseSize = Mathf.Max(12f, Text.fontSize);
        string text = heading ? (m.Label ?? "").ToUpperInvariant() : RowMap.LineText(m);
        Color color = heading ? SimpleMenu.Yellow
            : m.Kind == RowKind.Banner ? FromRgb(m.Color)
            : m.Kind == RowKind.KeyValue ? new Color(1f, 1f, 1f, 0.75f)
            : new Color(1f, 1f, 1f, 0.62f);
        float size = heading ? _baseSize * 0.9f : _baseSize * 0.72f;
        if (Math.Abs(Text.fontSize - size) > 0.01f) Text.fontSize = size;
        if (Text.color != color) Text.color = color;
        if (heading && Text.fontStyle != FontStyles.Bold) Text.fontStyle = FontStyles.Bold;
        SetText(Text, text);
    }

    private static void SetText(TMP_Text t, string text)
    {
        if (t == null) return;
        text ??= "";
        if (t.text != text) t.text = text;
    }

    private static bool Same(string[] a, string[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static Color FromRgb(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);

    public void Destroy()
    {
        try
        {
            if (_option != null) ByPointer.Remove(_option.Pointer);
            if (_field != null) TextFields.Remove(_field.Pointer);
            if (Go == null) return;
            Go.SetActive(false); // out of the column now (Destroy happens at the end of the frame)
            Object.Destroy(Go);
        }
        catch { /* already gone with its screen */ }
    }
}
