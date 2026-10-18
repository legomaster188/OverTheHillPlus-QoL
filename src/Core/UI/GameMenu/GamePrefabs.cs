using System;
using FunCore.Managers;
using Il2CppInterop.Runtime;
using TMPro;
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

    /// <summary>A copy of the MOD BROWSER's mod cell (StateLiveryModsMenu.modCellPrefab, a ContextUIModCell), the
    /// template of the MODS lists' cards; null until a browser opened, or when it wasn't found.</summary>
    internal static GameObject ModCell;

    /// <summary>The browser's grid: its cell size and spacing (zero when its grid has no GridLayoutGroup) and columns.</summary>
    internal static Vector2 CellSize, CellSpacing;
    internal static int GridColumns = 4;

    private static bool _cellBroken, _cellLogged;

    /// <summary>True when the MODS lists show cards (the cell was copied and no card failed to be made).</summary>
    internal static bool CardsReady => ModCell != null && !_cellBroken;

    /// <summary>
    /// Copies the browser's mod cell once (StateLiveryModsMenu.Open): its prefab, else one of the cells in its grid.
    /// Not found: logged once, the MODS lists keep their rows.
    /// </summary>
    internal static void TakeModCell(StateLiveryModsMenu state, ContextUILiveryModsMenu ctx)
    {
        if (ModCell != null || _cellBroken || state == null) return;
        try
        {
            OverTheHillPlus.CrashTrail.Mark("MODS cards: copying the MOD BROWSER's mod cell");
            GameObject source = null;
            string from = "modCellPrefab";
            try
            {
                var prefab = state.modCellPrefab;
                if (prefab != null && prefab.GetComponent<ContextUIModCell>() != null) source = prefab;
            }
            catch { }
            if (source == null && ctx != null && ctx.Content != null)
            {
                var cell = ctx.Content.GetComponentInChildren<ContextUIModCell>(true);
                if (cell != null)
                {
                    source = cell.gameObject;
                    from = "a cell of the grid";
                }
            }
            try { GridColumns = CardGrid.Columns(StateLiveryModsMenu.GridColumns); } catch { GridColumns = 4; }
            var grid = ctx != null && ctx.Content != null ? ctx.Content.GetComponent<UnityEngine.UI.GridLayoutGroup>() : null;
            if (grid != null)
            {
                CellSize = grid.cellSize;
                CellSpacing = grid.spacing;
            }
            if (source == null)
            {
                if (!_cellLogged) Core.CorePlugin.Logger?.LogWarning("[Core] MODS cards: the MOD BROWSER's mod cell was not found; the MODS lists show rows");
                _cellLogged = true;
                return;
            }
            if (grid == null || CellSize.x < 1f)
            {
                var rt = source.GetComponent<RectTransform>();
                CellSize = rt != null ? rt.rect.size : Vector2.zero;
                if (CellSize.x < 1f && rt != null) CellSize = rt.sizeDelta;
                if (CellSpacing == Vector2.zero) CellSpacing = new Vector2(16f, 16f);
            }
            EnsureHolder();
            ModCell = Copy(source, "OTH_ModCell");
            Core.CorePlugin.Logger?.LogInfo($"[Core] MODS cards: cells are copies of the MOD BROWSER's {from} ({PathOf(source.transform)}), " +
                                            $"{GridColumns} columns (page size {PageSizeText()}), cell {CellSize} spacing {CellSpacing}" +
                                            (grid != null ? " from the grid's layout" : " from the cell") + "; cell parts: " + Parts(ModCell.transform, 0));
        }
        catch (Exception e)
        {
            _cellBroken = true;
            Core.CorePlugin.Logger?.LogWarning("[Core] MODS cards: copying the MOD BROWSER's mod cell failed (the MODS lists show rows): " + e.Message);
        }
    }

    /// <summary>A card could not be made from the cell: logged once, the lists show rows from now on.</summary>
    internal static void DropModCell(string why)
    {
        if (_cellBroken) return;
        _cellBroken = true;
        Core.CorePlugin.Logger?.LogWarning("[Core] MODS cards: " + why + "; the MODS lists show rows");
    }

    private static string PageSizeText()
    {
        try { return StateLiveryModsMenu.PageSize.ToString(); }
        catch { return "?"; }
    }

    /// <summary>The cell's objects and their parts, for the log (a game update that moves them shows here).</summary>
    private static string Parts(Transform t, int depth)
    {
        if (t == null || depth > 3) return "";
        var sb = new System.Text.StringBuilder(t.name);
        try
        {
            var kinds = new System.Collections.Generic.List<string>();
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetIl2CppType().Name;
                if (n != "RectTransform" && n != "CanvasRenderer") kinds.Add(n);
            }
            if (kinds.Count > 0) sb.Append('<').Append(string.Join(",", kinds)).Append('>');
            if (t.childCount > 0)
            {
                sb.Append(" [");
                for (int i = 0; i < t.childCount; i++)
                {
                    if (i > 0) sb.Append("; ");
                    sb.Append(Parts(t.GetChild(i), depth + 1));
                }
                sb.Append(']');
            }
        }
        catch { }
        return sb.ToString();
    }

    private static string PathOf(Transform t)
    {
        string path = t.name;
        var p = t.parent;
        for (int i = 0; i < 3 && p != null; i++, p = p.parent) path = p.name + "/" + path;
        return path;
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
            if (Ready) return true;
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

        EnsureHolder();
        if (Option == null && option != null) Option = Copy(option.gameObject, "OTH_OptionRow");
        if (Slider == null && slider != null) Slider = Copy(slider.gameObject, "OTH_SliderRow");
        if (Button == null && button != null) Button = Copy(button.gameObject, "OTH_ButtonRow");
        if (Label == null && Option != null)
        {
            var text = GameRow.LabelOf(Option);
            if (text != null) Label = Copy(text.gameObject, "OTH_Label");
        }
        Summary = $"option row {Name(option)}, slider row {Name(slider)}, button row {Name(button)}, label {(Label != null ? "from the option row" : "missing")}" +
                  (from.Length > 0 ? " (from " + from.TrimEnd(',', ' ') + ")" : "") +
                  (_routes.Length > 0 ? "; prefabs: " + _routes : "");
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
