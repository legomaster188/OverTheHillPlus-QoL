using System;
#if MELON
using Il2CppFunCore.Managers;
#else
using FunCore.Managers;
#endif
using Il2CppInterop.Runtime;
#if MELON
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// The game's own settings widgets, as templates for the MODS pages: the "◂ value ▸" row (UIOptionSelector), the
/// slider row (UISliderSelector) and the button row (UIButtonSelector) of the SETTINGS screens, and their label
/// text. Found by component type first (the settings menus' context components, wherever they are loaded: prefab
/// assets or open screens), then by the game's own prefab paths (Prefabs/UI/Menus/Settings/..., loaded through the
/// game's ResourcesManager like its screens are, then Resources). Each template is copied once into a hidden,
/// inactive holder that lives for the session, so the pages never depend on a settings screen being open.
/// What was found is logged once; when the option or button row isn't found the MODS pages use the full view.
/// </summary>
internal static class GamePrefabs
{
    internal const string HudPath = "Prefabs/UI/Menus/Settings/HUDSettingsMenu";
    internal const string AudioPath = "Prefabs/UI/Menus/Settings/AudioSettingsMenu";
    internal const string CameraPath = "Prefabs/UI/Menus/Settings/CameraSettingsMenu";
    internal const string BrowserPath = "Prefabs/UI/Menus/LiveryMods/LiveryModsMenu";
    internal const string SettingsPath = "Prefabs/UI/Menus/Settings/SettingsMenu";

    /// <summary>Template copies (inactive, under <see cref="Holder"/>).</summary>
    internal static GameObject Option, Slider, Button, Label;

    /// <summary>A copy of the MOD BROWSER's search field (the game's text box), for text rows.</summary>
    internal static GameObject TextField;

    /// <summary>Copies the browser's search field once (StateLiveryModsMenu.Open), as the text rows' template.</summary>
    internal static void TakeTextField(UITextMeshProInputFieldSelector field)
    {
        try
        {
            if (TextField != null || field == null || field.textComponent == null) return;
            EnsureHolder();
            TextField = Copy(field.gameObject, "OTH_TextField");
            Core.CorePlugin.Logger?.LogInfo("[Core] MODS pages: text boxes are copies of the MOD BROWSER's search field " + Name(field));
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogWarning("[Core] MODS pages: copying the browser's search field failed: " + e.Message); }
    }

    /// <summary>A copy of the MOD BROWSER's mod cell (a ContextUIModCell), the template of card rows. The MODS screen
    /// lists mods as buttons, so no card is made any more: null, and <see cref="CardsReady"/> is false.</summary>
    internal static GameObject ModCell;

    /// <summary>The browser's grid: its cell size and spacing and columns (for card rows, unused while there are none).</summary>
    internal static Vector2 CellSize, CellSpacing;
    internal static int GridColumns = 4;

    private static bool _cellBroken;

    /// <summary>True when the MODS lists show cards (the cell was copied and no card failed to be made).</summary>
    internal static bool CardsReady => ModCell != null && !_cellBroken;

    /// <summary>A card could not be made from the cell: logged once, the lists show rows from now on.</summary>
    internal static void DropModCell(string why)
    {
        if (_cellBroken) return;
        _cellBroken = true;
        Core.CorePlugin.Logger?.LogWarning("[Core] MODS cards: " + why + "; the MODS lists show rows");
    }

    /// <summary>A copy of one of the Settings menu's own buttons (GRAPHICS, AUDIO ...): the MODS screen's list of mods
    /// on the left is made of these, so it looks like the Settings menu itself. Null until the Settings menu opened.</summary>
    internal static GameObject MenuButton;

    /// <summary>Copies a button of the Settings menu once (StateSettingsMenu.Open), as the template of the mod list.</summary>
    internal static void TakeMenuButton(UIButtonSelector button)
    {
        try
        {
            if (MenuButton != null || button == null) return;
            EnsureHolder();
            MenuButton = Copy(button.gameObject, "OTH_MenuButton");
            Core.CorePlugin.Logger?.LogInfo("[Core] MODS screen: the list of mods is made of copies of the Settings menu's " + Name(button));
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogWarning("[Core] MODS screen: copying the Settings menu's button failed (the list uses the settings' button row): " + e.Message); }
    }

    /// <summary>True when <paramref name="field"/> is one of our text boxes (made from <see cref="TextField"/>).</summary>
    internal static bool IsOurs(UITextMeshProInputFieldSelector field) =>
        field != null && GameRow.TextFields.Contains(field.Pointer);

    /// <summary>The hidden holder: clones are made under it (so they don't wake up) before they move onto a page.</summary>
    internal static Transform Holder => _holder == null ? null : _holder.transform;

    /// <summary>What was found, for the log and the report.</summary>
    internal static string Summary { get; private set; } = "not looked for yet";

    private static GameObject _holder;
    private static float _nextTry;
    private static bool _logged, _loggedReady;
    private static string _routes = "";

    /// <summary>True when the option and button rows (the minimum for a page) are there.</summary>
    internal static bool Ready => Option != null && Button != null && Label != null;

    /// <summary>Finds the templates if they aren't there yet (at most every few seconds). True when ready.</summary>
    internal static bool Ensure()
    {
        try
        {
            if (Ready && ((TextField != null && MenuButton != null) || _textTried)) return true;
            if (Ready)
            {
                // The rows are there; text boxes and the Settings menu's button once more from the game's prefabs (the
                // settings' rows and buttons stand in for them otherwise).
                _textTried = true;
                FindTextField();
                FindMenuButton();
                return true;
            }
            if (Time.unscaledTime < _nextTry) return false;
            _nextTry = Time.unscaledTime + 5f;
            Find();
        }
        catch (Exception e)
        {
            Summary = "lookup failed: " + e.Message;
            Core.CorePlugin.Logger?.LogWarning("[Core] MODS pages: looking for the game's settings widgets failed: " + e);
        }
        // Once when first looked for, and once more if they turn up later (a settings screen loaded them).
        if (!_logged || (Ready && !_loggedReady))
        {
            Core.CorePlugin.Logger?.LogInfo("[Core] MODS pages, the game's settings widgets: " + Summary);
            _logged = true;
            _loggedReady = Ready;
        }
        return Ready;
    }

    private static void Find()
    {
        UIOptionSelector option = null;
        UISliderSelector slider = null;
        UIButtonSelector button = null;
        string from = "";

        // 1. By component type: the settings screens' context components, wherever they are loaded.
        Pick(First<ContextUIHUDSettingsMenu>(), ref option, ref slider, ref button, ref from, "HUD settings (loaded)");
        Pick(First<ContextUIAudioSettingsMenu>(), ref option, ref slider, ref button, ref from, "audio settings (loaded)");
        Pick(First<ContextUICameraSettingsMenu>(), ref option, ref slider, ref button, ref from, "camera settings (loaded)");

        // 2. By the game's prefab paths.
        if (option == null || button == null || slider == null)
        {
            OverTheHillPlus.CrashTrail.Mark("MODS pages: loading settings widgets");
            Pick(Load(HudPath)?.GetComponentInChildren<ContextUIHUDSettingsMenu>(true), ref option, ref slider, ref button, ref from, HudPath);
            Pick(Load(AudioPath)?.GetComponentInChildren<ContextUIAudioSettingsMenu>(true), ref option, ref slider, ref button, ref from, AudioPath);
            Pick(Load(CameraPath)?.GetComponentInChildren<ContextUICameraSettingsMenu>(true), ref option, ref slider, ref button, ref from, CameraPath);
        }

        FindTextField();
        FindMenuButton();

        EnsureHolder();
        if (Option == null && option != null) Option = Copy(option.gameObject, "OTH_OptionRow");
        if (Slider == null && slider != null) Slider = Copy(slider.gameObject, "OTH_SliderRow");
        if (Button == null && button != null) Button = Copy(button.gameObject, "OTH_ButtonRow");
        if (Label == null && Option != null)
        {
            var text = GameRow.LabelOf(Option);
            if (text != null) Label = Copy(text.gameObject, "OTH_Label");
        }
        Summary = $"option row {Name(option)}, slider row {Name(slider)}, button row {Name(button)}, label {(Label != null ? "from the option row" : "missing")}, text box {(TextField != null ? "found" : "missing")}" +
                  (from.Length > 0 ? " (from " + from.TrimEnd(',', ' ') + ")" : "") +
                  (_routes.Length > 0 ? "; prefabs: " + _routes : "");
    }

    private static bool _textTried;

    /// <summary>The Settings menu's button (the MODS screen's list), from its prefab when the menu wasn't opened yet (a
    /// mod's own button opened the screen straight away).</summary>
    private static void FindMenuButton()
    {
        if (MenuButton != null) return;
        try
        {
            var menu = First<ContextUISettingsMenu>() ?? Load(SettingsPath)?.GetComponentInChildren<ContextUISettingsMenu>(true);
            if (menu != null) TakeMenuButton(menu.GraphicsButton);
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogDebug("[Core] MODS pages: the Settings menu's button: " + e.Message); }
    }

    /// <summary>Text boxes: a copy of the MOD BROWSER's search field, from its prefab (the browser itself never needs to open).</summary>
    private static void FindTextField()
    {
        if (TextField != null) return;
        try
        {
            var browser = First<ContextUILiveryModsMenu>() ?? Load(BrowserPath)?.GetComponentInChildren<ContextUILiveryModsMenu>(true);
            if (browser != null) TakeTextField(browser.SearchField);
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogDebug("[Core] MODS pages: the MOD BROWSER's search field: " + e.Message); }
    }

    private static void Pick(Component context, ref UIOptionSelector option, ref UISliderSelector slider, ref UIButtonSelector button, ref string from, string where)
    {
        if (context == null) return;
        bool used = false;
        try
        {
            var hud = context.TryCast<ContextUIHUDSettingsMenu>();
            if (hud != null)
            {
                if (option == null && Alive(hud.ShowTime)) { option = hud.ShowTime; used = true; }
                if (button == null && Alive(hud.ResetToDefaultButton)) { button = hud.ResetToDefaultButton; used = true; }
            }
            var audio = context.TryCast<ContextUIAudioSettingsMenu>();
            if (audio != null)
            {
                if (slider == null && Alive(audio.MasterVolume)) { slider = audio.MasterVolume; used = true; }
                if (option == null && Alive(audio.VoiceChat)) { option = audio.VoiceChat; used = true; }
                if (button == null && Alive(audio.ResetToDefaultButton)) { button = audio.ResetToDefaultButton; used = true; }
            }
            var camera = context.TryCast<ContextUICameraSettingsMenu>();
            if (camera != null)
            {
                if (slider == null && Alive(camera.FieldOfViewSlider)) { slider = camera.FieldOfViewSlider; used = true; }
                if (option == null && Alive(camera.InvertYAxisSelector)) { option = camera.InvertYAxisSelector; used = true; }
                if (button == null && Alive(camera.ResetToDefaultButton)) { button = camera.ResetToDefaultButton; used = true; }
            }
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogDebug($"[Core] MODS pages: {where}: {e.Message}"); }
        if (used) from += where + ", ";
    }

    private static bool Alive(Object o) => o != null;

    private static T First<T>() where T : Component
    {
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
        {
            var c = o?.TryCast<T>();
            if (c != null) return c;
        }
        return null;
    }

    /// <summary>A screen prefab by the path the game opens it with: its ResourcesManager's cache first (only filled once
    /// the player has opened that screen), then Addressables directly, then Resources. Which route found it is logged.</summary>
    private static GameObject Load(string path)
    {
        GameObject go = null;
        try
        {
            var rm = HybridSingleton<ResourcesManager>.Instance;
            if (rm != null && rm.AddressablesInitialized() && rm.TryResolveResourceLocation(path, out _))
            {
                go = rm.Load<GameObject>(path, null);
                if (go != null) return Found(path, go, "the game's loader");
            }
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogDebug($"[Core] MODS pages: {path} through the game's loader: {e.Message}"); }
        try
        {
            // Only a key some catalog has (an unknown key makes Addressables log an InvalidKeyException). The handle is
            // kept (never released): the templates are copied out of it and the asset stays loaded for the session.
            if (OverTheHillPlus.Shared.RuntimeAddressables.HasKey(path))
            {
                go = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>((Il2CppSystem.String)path).WaitForCompletion();
                if (go != null) return Found(path, go, "Addressables");
            }
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogDebug($"[Core] MODS pages: {path} through Addressables: {e.Message}"); }
        try
        {
            go = Resources.Load<GameObject>(path);
            if (go != null) return Found(path, go, "Resources");
        }
        catch { }
        return null;
    }

    private static GameObject Found(string path, GameObject go, string route)
    {
        _routes += (_routes.Length > 0 ? ", " : "") + path.Substring(path.LastIndexOf('/') + 1) + " via " + route;
        return go;
    }

    /// <summary>The hidden holder (made on first use).</summary>
    internal static Transform EnsureHolder()
    {
        if (_holder == null)
        {
            _holder = new GameObject("OTH_GameWidgetTemplates");
            _holder.SetActive(false);
            Object.DontDestroyOnLoad(_holder);
        }
        return _holder.transform;
    }

    private static GameObject Copy(GameObject source, string name)
    {
        var go = Object.Instantiate(source, _holder.transform, false);
        go.name = name;
        go.SetActive(true); // active itself, inactive through the holder: clones wake up when they move onto a page
        return go;
    }

    private static string Name(Component c)
    {
        if (c == null) return "missing";
        try
        {
            string path = c.gameObject.name;
            var t = c.transform.parent;
            for (int i = 0; i < 3 && t != null; i++, t = t.parent) path = t.name + "/" + path;
            return path;
        }
        catch { return "found"; }
    }
}
