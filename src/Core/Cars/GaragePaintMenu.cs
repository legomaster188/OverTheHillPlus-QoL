using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.UI.GameMenu;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A modded car's paints and trims in the garage's own Liveries tab: under the car's liveries, one block per group
/// (<see cref="GarageSwatches.Groups"/>: the car's paints, the shared palette's groups, then the trims), each a copy of
/// the game's own content block with a copy of its own item tile per colour, the colour as a paint chip where a
/// livery's picture goes. Highlighting a chip shows it on the garage's car (after a short rest, so scrolling past forty
/// colours doesn't paint forty textures); picking it sets [Look] Paint or Trim, exactly as the MODS page does (so the
/// page, the save and what other players see stay in step), and the chip in use carries the game's tick.
/// <para>No game item is added: the chips are tiles only (no item id, nothing in the game's save or its mounted-parts
/// bitset), so the car's livery parts, their ids and bits are untouched. The tiles are ours: made when the game builds
/// the Liveries screen (InitializeLiveriesScreen postfix) and destroyed when any garage screen is built again or the
/// garage closes; their buttons get our own select / deselect / click handlers (the game's are never on them).</para>
/// <para>The game's description panel shows the colour's name and one line; the screen scrolls to a highlighted chip
/// with the game's own ScrollToView; the controller moves between the chips in rows and on from the game's last
/// livery row (explicit links, the game's own last row put back when we go).</para>
/// </summary>
internal static class GaragePaintMenu
{
    private const float PreviewRest = 0.2f;
    private const int ChipSize = 128;

    private sealed class Tile
    {
        public Swatch Swatch;
        public GameObject Go;
        public ContextUIMerchantContentIcon Icon;
        public UIMerchantButtonSelector Button;
        public ContextUIMerchantContentBlock Block;
    }

    private static StateMerchantMenu _menu;
    private static RuntimeCar _car;
    private static List<SwatchGroup> _groups;
    private static readonly List<GameObject> Blocks = new();
    private static readonly List<Tile> Tiles = new();
    private static readonly Dictionary<IntPtr, Tile> ByButton = new();
    // The game's last livery row: where its "down" pointed before it came to our first block.
    private static readonly List<(Selectable sel, Selectable oldDown)> GameDown = new();

    private static Tile _hover;
    private static float _hoverAt;
    private static CarLook _preview;
    private static RuntimeCar _previewCar;

    // One handler each, kept alive for as long as the tiles exist.
    private static Il2CppSystem.Action<UIMerchantButtonSelector> _onSelect, _onDeselect, _onClick;

    private static readonly Dictionary<int, Sprite> Chips = new();
    private static readonly HashSet<string> Logged = new();

    /// <summary>The look the garage's car shows while a chip is highlighted (no skin, the paint entry), or null.</summary>
    internal static CarLook PreviewFor(RuntimeCar car) => _preview != null && car != null && _previewCar == car ? _preview : null;

    // ---------------------------------------------------------------- building

    /// <summary>The game built the Liveries screen: the chips go under its liveries when the car shown is a modded one.</summary>
    internal static void LiveriesScreen(StateMerchantMenu menu)
    {
        Clear();
        if (menu == null) return;
        var car = ShownCar(menu);
        if (car == null) return;
        if (!car.Assets.HasZones || car.Config.Paint == null) return; // no paint zones: the colours would do nothing
        try { Build(menu, car); }
        catch (Exception e)
        {
            DebugLog.Error(car.Id, e, "GaragePaintMenu.Build");
            LogOnce("build", $"[Cars] the paints in the garage's Liveries tab could not be made: {e.Message}");
            Clear();
        }
    }

    private static RuntimeCar ShownCar(StateMerchantMenu menu)
    {
        try
        {
            var info = menu.currentCar?.CarInfo;
            if (info == null) return null;
            foreach (var c in ModdedCars.All)
                if (c.Ready && c.Catalog.Info != null && c.Catalog.Info.Pointer == info.Pointer) return c;
        }
        catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.ShownCar"); }
        return null;
    }

    private static void Build(StateMerchantMenu menu, RuntimeCar car)
    {
        var ui = menu.merchantMenuUIContext;
        // The game's blocks of this screen: ours go after the last one, in the same container.
        ContextUIMerchantContentBlock lastGame = null;
        var pool = menu.contextUIMerchantContentBlockPool;
        if (pool != null)
            for (int i = 0; i < pool.Count; i++)
            {
                var b = pool[i];
                if (b != null && b.gameObject.activeSelf) lastGame = b;
            }
        Transform container = lastGame != null ? lastGame.transform.parent : ui?.ContentContainer;
        if (container == null) { LogOnce("container", "[Cars] garage paints: the Liveries tab has no content container."); return; }
        GameObject blockTemplate = menu.merchantContentBlockGO != null ? menu.merchantContentBlockGO : lastGame?.gameObject;
        GameObject iconTemplate = menu.merchantContentIconGO;
        if (iconTemplate == null)
        {
            var icons = menu.contextUIMerchantContentIconPool;
            if (icons != null)
                for (int i = 0; i < icons.Count && iconTemplate == null; i++)
                    if (icons[i] != null) iconTemplate = icons[i].gameObject;
        }
        if (blockTemplate == null || iconTemplate == null) { LogOnce("templates", "[Cars] garage paints: the game's tile templates weren't found."); return; }

        _menu = menu;
        _car = car;
        _groups = GarageSwatches.Groups(car.Def.Paints, car.Def.Trims);
        _onSelect ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIMerchantButtonSelector>>(new Action<UIMerchantButtonSelector>(OnSelected));
        _onDeselect ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIMerchantButtonSelector>>(new Action<UIMerchantButtonSelector>(OnDeselected));
        _onClick ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIMerchantButtonSelector>>(new Action<UIMerchantButtonSelector>(OnClicked));

        int gi = 0;
        foreach (var g in _groups)
        {
            var bgo = Object.Instantiate(blockTemplate, container, false);
            bgo.name = "OTH_Paints_" + gi++;
            bgo.SetActive(true);
            bgo.transform.SetAsLastSibling();
            Blocks.Add(bgo);
            var block = bgo.GetComponent<ContextUIMerchantContentBlock>() ?? bgo.GetComponentInChildren<ContextUIMerchantContentBlock>(true);
            if (block == null || block.GridLayoutGroup == null) throw new InvalidOperationException("the game's content block has no grid");
            var grid = block.GridLayoutGroup.transform;
            // A copy of a live block brings its tiles along: only ours go in.
            for (int i = grid.childCount - 1; i >= 0; i--) Object.Destroy(grid.GetChild(i).gameObject);
            try { block.SetHeaderActive(true, false); } catch (Exception e) { DebugLog.Error(car.Id, e, "GaragePaintMenu.Header"); }
            var header = block.MerchantContentHeader?.Text;
            if (header != null)
            {
                FunTexts.Register(header); // the game's localization must not write over it
                header.text = g.Title;
            }
            foreach (var s in g.Items)
            {
                var igo = Object.Instantiate(iconTemplate, grid, false);
                igo.name = "OTH_Paint_" + s.Name;
                igo.SetActive(true);
                igo.transform.SetAsLastSibling();
                var icon = igo.GetComponent<ContextUIMerchantContentIcon>() ?? igo.GetComponentInChildren<ContextUIMerchantContentIcon>(true);
                if (icon == null || icon.Button == null) { Object.Destroy(igo); continue; }
                var t = new Tile { Swatch = s, Go = igo, Icon = icon, Button = icon.Button, Block = block };
                Plain(icon);
                var button = icon.Button;
                button.OnMerchantButtonSelected = _onSelect;
                button.OnMerchantButtonDeselected = _onDeselect;
                button.OnMerchantButtonPointerClickOrSubmit = _onClick;
                Tiles.Add(t);
                ByButton[button.Pointer] = t;
            }
        }
        Refresh();
        try
        {
            // (an interop Transform is never a C# RectTransform: cast on the game's side)
            var rt = container.TryCast<RectTransform>() ?? container.GetComponent<RectTransform>();
            if (rt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }
        catch (Exception e) { DebugLog.Error(car.Id, e, "GaragePaintMenu.Layout"); }
        Link(lastGame);
        car.Info($"garage: {Tiles.Count} paint and trim chip(s) in {Blocks.Count} block(s) under the Liveries tab.");
    }

    /// <summary>A fresh tile of the game's: no "new" dot, no corner text, no local-livery mark.</summary>
    private static void Plain(ContextUIMerchantContentIcon icon)
    {
        try { if (icon.NewItemIndicator != null) icon.NewItemIndicator.gameObject.SetActive(false); } catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.Plain"); }
        try
        {
            if (icon.TopRightText != null)
            {
                FunTexts.Register(icon.TopRightText);
                icon.TopRightText.text = "";
                icon.TopRightText.gameObject.SetActive(false);
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.Plain"); }
        try { if (icon.LocalLivery != null) icon.LocalLivery.SetActive(false); } catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.Plain"); }
        if (icon.MainImage != null)
        {
            icon.MainImage.color = Color.white;
            icon.MainImage.preserveAspect = true;
        }
    }

    /// <summary>Each chip's colour and the tick on the ones in use (after a pick, the body-colour trim follows the paint).</summary>
    private static void Refresh()
    {
        var car = _car;
        if (car == null) return;
        var cfg = car.Config;
        var main = CarPaint.MainColour(car.Def.Paints, cfg.Paint.Value, cfg.PaintCustom.Value);
        var paint = GarageSwatches.Current(_groups, SwatchKind.Paint, cfg.Paint.Value);
        var trim = GarageSwatches.Current(_groups, SwatchKind.Trim, cfg.Trim.Value);
        Sprite tick = null;
        try { tick = _menu?.checkmarkSprite; } catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.Refresh"); }
        foreach (var t in Tiles)
        {
            try
            {
                if (t.Icon == null) continue;
                bool inUse = t.Swatch == paint || t.Swatch == trim;
                if (t.Icon.MainImage != null) t.Icon.MainImage.sprite = Chip(t.Swatch.Shown(main));
                var status = t.Icon.StatusImage;
                if (status != null)
                {
                    if (inUse && tick != null) status.sprite = tick;
                    status.enabled = inUse && tick != null;
                    status.gameObject.SetActive(inUse && tick != null);
                }
                t.Button.IsInUse = inUse;
                t.Button.IsOwnedAndNotInUse = !inUse;
                t.Button.IsAvailableToBuy = false;
            }
            catch (Exception e) { DebugLog.Error(car.Id, e, "GaragePaintMenu.Refresh"); }
        }
    }

    private static Sprite Chip(CarRgb c)
    {
        int key = c.R << 16 | c.G << 8 | c.B;
        if (Chips.TryGetValue(key, out var have) && have != null) return have;
        var px = GarageSwatches.ChipPixels(c, ChipSize);
        var tex = new Texture2D(ChipSize, ChipSize, TextureFormat.RGBA32, false, false) { name = "OTH_chip_" + c.ToHex(), wrapMode = TextureWrapMode.Clamp };
        var arr = new Il2CppStructArray<Color32>(ChipSize * ChipSize);
        px.AsSpan().CopyTo(MemoryMarshal.AsBytes(arr.AsSpan()));
        tex.SetPixels32(arr);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(tex);
        var sprite = Sprite.Create(tex, new Rect(0, 0, ChipSize, ChipSize), new Vector2(0.5f, 0.5f));
        sprite.name = tex.name;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(sprite);
        Chips[key] = sprite;
        return sprite;
    }

    // ---------------------------------------------------------------- controller navigation

    private static void Link(ContextUIMerchantContentBlock lastGame)
    {
        var game = new List<Selectable>();
        if (lastGame != null && lastGame.GridLayoutGroup != null)
        {
            var g = lastGame.GridLayoutGroup.transform;
            for (int i = 0; i < g.childCount; i++)
            {
                var c = g.GetChild(i);
                if (c == null || !c.gameObject.activeSelf) continue;
                var icon = c.GetComponent<ContextUIMerchantContentIcon>();
                if (icon != null && icon.Button != null) game.Add(icon.Button);
            }
        }
        int columns = Columns();
        if (columns <= 0)
        {
            // The layout wasn't worked out yet: Unity finds the neighbours by position instead.
            foreach (var t in Tiles)
            {
                var n = t.Button.navigation;
                n.mode = Navigation.Mode.Automatic;
                t.Button.navigation = n;
            }
            DebugLog.Verbose("core", "garage paints: grid columns unknown, automatic navigation");
            return;
        }
        var counts = new List<int> { game.Count };
        var all = new List<Selectable>(game);
        foreach (var bgo in Blocks)
        {
            int n = 0;
            foreach (var t in Tiles) if (t.Block != null && t.Block.gameObject == bgo) { n++; all.Add(t.Button); }
            counts.Add(n);
        }
        var nav = GarageSwatches.Nav(counts, columns);
        Selectable At(int i) => i >= 0 && i < all.Count ? all[i] : null;
        for (int i = game.Count; i < all.Count; i++)
        {
            var n = all[i].navigation;
            n.mode = Navigation.Mode.Explicit;
            n.selectOnUp = At(nav[i].up);
            n.selectOnDown = At(nav[i].down);
            n.selectOnLeft = At(nav[i].left);
            n.selectOnRight = At(nav[i].right);
            all[i].navigation = n;
        }
        for (int i = 0; i < game.Count; i++)
        {
            if (nav[i].down < game.Count) continue; // not the last row
            var n = game[i].navigation;
            if (n.mode != Navigation.Mode.Explicit) continue; // automatic: it finds our row below by position
            GameDown.Add((game[i], n.selectOnDown));
            n.selectOnDown = At(nav[i].down);
            game[i].navigation = n;
        }
    }

    /// <summary>The grid's columns: the game's fixed count, else counted from the laid-out tiles of our biggest block
    /// (0 when the layout isn't there yet).</summary>
    private static int Columns()
    {
        if (Tiles.Count == 0) return 0;
        try
        {
            var grid = Tiles[0].Block.GridLayoutGroup;
            if (grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount && grid.constraintCount > 0) return grid.constraintCount;
        }
        catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.Columns"); }
        int best = 0;
        foreach (var bgo in Blocks)
        {
            var rows = new List<Vector3>();
            foreach (var t in Tiles) if (t.Block != null && t.Block.gameObject == bgo) rows.Add(t.Go.transform.localPosition);
            if (rows.Count < 2) continue;
            int first = 0;
            bool spread = false;
            foreach (var p in rows)
            {
                if (Mathf.Abs(p.y - rows[0].y) < 1f) first++;
                if (Mathf.Abs(p.x - rows[0].x) >= 1f) spread = true;
            }
            if (!spread) return 0; // every tile at one spot: not laid out
            if (first < rows.Count) best = Math.Max(best, first); // a full row with more below: that's the width
        }
        return best;
    }

    // ---------------------------------------------------------------- the player

    private static void OnSelected(UIMerchantButtonSelector button)
    {
        try
        {
            if (button == null || !ByButton.TryGetValue(button.Pointer, out var t)) return;
            _hover = t;
            _hoverAt = Time.unscaledTime;
            GaragePreview.Clear(); // a livery highlighted before: the chip shows over the paint entry now
            try { _menu?.ScrollToView(t.Block.RectTransform, button.RectTransform); } catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.Scroll"); }
            Describe(t);
            Sound(false);
        }
        catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.OnSelected"); }
    }

    private static void OnDeselected(UIMerchantButtonSelector button)
    {
        try
        {
            if (button == null || _hover == null || _hover.Button.Pointer != button.Pointer) return;
            _hover = null;
            if (_preview != null)
            {
                _preview = null;
                if (_menu != null) CarLooks.MerchantDummy(_menu);
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.OnDeselected"); }
    }

    private static void OnClicked(UIMerchantButtonSelector button)
    {
        try
        {
            if (button == null || !ByButton.TryGetValue(button.Pointer, out var t) || _car == null) return;
            var cfg = _car.Config;
            var entry = t.Swatch.Kind == SwatchKind.Paint ? cfg.Paint : cfg.Trim;
            if (string.Equals(entry.Value, t.Swatch.Name, StringComparison.OrdinalIgnoreCase)) return; // in use already
            entry.Value = t.Swatch.Name;
            _car.Look.Refresh();
            _car.Info($"garage: {(t.Swatch.Kind == SwatchKind.Paint ? "paint" : "trim")} {t.Swatch.Name} picked.");
            Refresh();
            Describe(t);
            if (_hover == t) _hoverAt = -PreviewRest; // the preview is the real look now: no rest needed
            if (_menu != null) CarLooks.MerchantDummy(_menu);
            Sound(true);
        }
        catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.OnClicked"); }
    }

    /// <summary>The colour's name and line in the game's description panel, and "Apply" / "Applied" on the submit prompt.</summary>
    private static void Describe(Tile t)
    {
        var ui = _menu?.merchantMenuUIContext;
        var car = _car;
        if (ui == null || car == null) return;
        string livery = null;
        try
        {
            int mounted = car.Liveries.ForGarage(_menu);
            if (mounted > 0) livery = car.Liveries[mounted]?.Name;
        }
        catch (Exception e) { DebugLog.Error(car.Id, e, "GaragePaintMenu.Describe"); }
        bool skin = !string.IsNullOrEmpty(car.Config.Skin?.Value);
        bool inUse = t.Button.IsInUse;
        try { if (ui.DescriptionTitleText != null) ui.DescriptionTitleText.text = t.Swatch.Name; } catch (Exception e) { DebugLog.Error(car.Id, e, "GaragePaintMenu.Describe"); }
        try { if (ui.DescriptionText != null) ui.DescriptionText.text = GarageSwatches.Description(t.Swatch, car.Def.TrimWhat, skin, livery); } catch (Exception e) { DebugLog.Error(car.Id, e, "GaragePaintMenu.Describe"); }
        try { if (ui.SubmitButtonText != null) ui.SubmitButtonText.text = inUse ? "Applied" : "Apply"; } catch (Exception e) { DebugLog.Error(car.Id, e, "GaragePaintMenu.Describe"); }
        try { if (ui.RequirementPanel != null) ui.RequirementPanel.gameObject.SetActive(false); } catch (Exception e) { DebugLog.Error(car.Id, e, "GaragePaintMenu.Describe"); }
    }

    /// <summary>The garage's own item-highlight or livery sound.</summary>
    private static void Sound(bool apply)
    {
        try
        {
            var ui = _menu?.merchantMenuUIContext;
            var ev = ui == null ? null : apply ? ui.ApplyLiveryEventRef : ui.ItemSelectionEventRef;
            if (ev == null) return;
            var cam = Camera.main;
            AudioUtils.PlayOneShot(ev, cam != null ? cam.transform.position : Vector3.zero);
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "GaragePaintMenu.Sound");
            LogOnce("sound", "[Cars] garage paints: the garage's sound didn't play: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>A chip that stayed highlighted for a moment goes on the garage's car (one colour texture at a time).</summary>
    internal static void Tick()
    {
        if (Tiles.Count == 0 && _preview == null) return;
        try
        {
            if (Blocks.Count > 0 && Blocks[0] == null) { Clear(); return; } // the garage's screen went with its objects
            var t = _hover;
            if (t == null || _car == null || _menu == null) return;
            if (Time.unscaledTime - _hoverAt < PreviewRest || CarLooks.PaintBuilding) return;
            var look = PreviewLook(_car, t.Swatch);
            if (look.Equals(_preview) && _previewCar == _car) return;
            _preview = look;
            _previewCar = _car;
            CarLooks.MerchantDummy(_menu);
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "GaragePaintMenu.Tick");
            Clear();
        }
    }

    /// <summary>Your look with the chip's colour in place (no skin: the colour is what you look at).</summary>
    private static CarLook PreviewLook(RuntimeCar car, Swatch s)
    {
        var cfg = car.Config;
        var local = car.Look.Local;
        CarRgb main, trim;
        if (s.Kind == SwatchKind.Paint)
        {
            main = s.Rgb;
            trim = CarPaint.TrimColour(car.Def.Trims, cfg.Trim.Value, cfg.TrimCustom.Value, main);
        }
        else
        {
            main = CarPaint.MainColour(car.Def.Paints, cfg.Paint.Value, cfg.PaintCustom.Value);
            trim = s.Shown(main);
        }
        return new CarLook { Main = main, Trim = trim, Engine = local?.Engine ?? car.Def.DefaultEngine };
    }

    // ---------------------------------------------------------------- going

    /// <summary>Another garage screen is built (or the garage closes): our blocks go, the game's last row links back.</summary>
    internal static void Clear()
    {
        bool had = _preview != null;
        var menu = _menu;
        foreach (var (sel, oldDown) in GameDown)
        {
            try
            {
                if (sel == null) continue;
                var n = sel.navigation;
                if (n.mode != Navigation.Mode.Explicit) continue;
                var down = n.selectOnDown;
                if (down != null && !ByButton.ContainsKey(down.Pointer)) continue; // the game linked it again meanwhile
                n.selectOnDown = oldDown;
                sel.navigation = n;
            }
            catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.Clear"); }
        }
        GameDown.Clear();
        foreach (var b in Blocks)
        {
            try { if (b != null) Object.Destroy(b); }
            catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.Clear"); }
        }
        Blocks.Clear();
        Tiles.Clear();
        ByButton.Clear();
        _hover = null;
        _preview = null;
        _previewCar = null;
        _groups = null;
        _car = null;
        _menu = null;
        if (had && menu != null)
        {
            try { CarLooks.MerchantDummy(menu); } catch (Exception e) { DebugLog.Error("core", e, "GaragePaintMenu.Clear"); }
        }
    }

    private static void LogOnce(string key, string text)
    {
        if (Logged.Add(key)) Core.CorePlugin.Logger?.LogWarning(text);
    }
}
