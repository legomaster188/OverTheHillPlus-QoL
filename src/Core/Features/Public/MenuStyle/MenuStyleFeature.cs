using OverTheHillPlus.UI.Native;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.MenuStyle;

/// <summary>
/// "Menu style: Native / Classic". Native draws the mod menu as a uGUI canvas with the game's own fonts, panel and
/// button sprites, highlight bar, colours and UI sounds, plus keyboard / gamepad navigation; Classic is the original
/// IMGUI panel. Every tab and feature section uses the same Menu widget calls in both styles (see UI/Widgets.cs and
/// UI/Native). If the native look can't be built or throws, the menu falls back to Classic for the session.
/// </summary>
internal sealed class MenuStyleFeature : IFeature
{
    private const string SectionName = "MenuStyle";

    public void Register()
    {
        var cfg = CoreConfig.File;
        NativeStyle.Style = cfg.Bind(SectionName, "Style", MenuStyleKind.Native,
            "Native: the mod menu uses the game's own fonts, panels, highlight bar and sounds. Classic: the original dark panel. " +
            "Falls back to Classic by itself if the native look can't be built.");
        NativeStyle.Sounds = cfg.Bind(SectionName, "Sounds", true, "Native style: play the game's menu sounds when selecting and changing items.");
        NativeStyle.GameSprites = cfg.Bind(SectionName, "GameSprites", true,
            "Native style: use the game's panel, button, slider and highlight sprites (off: plain rounded shapes in the game's colours).");
        NativeStyle.KeyboardNav = cfg.Bind(SectionName, "KeyboardNavigation", true,
            "Native style: arrows / Enter / Q and E (or a gamepad's d-pad, A and bumpers) select and change menu items.");

        NativeStyle.GameSprites.SettingChanged += OnSpritesChanged;
        NativeStyle.Style.SettingChanged += OnStyleChanged;

        Registry.OnTick(Tick);
        Registry.Section(Registry.PlusMode, MenuUxFeature.Tab, DrawCard, -10);
    }

    private static void OnSpritesChanged(object sender, System.EventArgs e) => NativeLook.Reset();

    private static void OnStyleChanged(object sender, System.EventArgs e) => MenuNav.Reset(false);

    private static void Tick()
    {
        // The canvas only exists while the menu is drawn in the native style (closed, screenshots, fallback: hidden).
        bool drawn = Time.frameCount - Menu.DrawnFrame <= 1;
        if (!drawn || !Menu.NativeActive || CoreBehaviour.GuiHidden)
        {
            NativeCanvas.Hide();
            if (!drawn) MenuNav.ClearPending();
            return;
        }
        MenuNav.Tick();
    }

    private static void DrawCard()
    {
        Menu.Card("Menu style", "Native draws this menu like the game's own menus: its fonts, panels, highlight bar and sounds.", () =>
        {
            bool bound = NativeStyle.Style != null;
            var style = bound ? NativeStyle.Style.Value : MenuStyleKind.Classic;
            Menu.Chips(2,
                (style == MenuStyleKind.Native ? "[Native]" : "Native", () => SetStyle(MenuStyleKind.Native), bound),
                (style == MenuStyleKind.Classic ? "[Classic]" : "Classic", () => SetStyle(MenuStyleKind.Classic), bound));
            if (NativeStyle.Failed)
            {
                Menu.Small($"<color=#E5534B>The native style failed ({NativeStyle.FailReason}); using Classic this session.</color>");
                Menu.Chips(1, ("Try the native style again", () =>
                {
                    NativeStyle.Retry();
                    Menu.SetStatus("Rebuilding the native menu");
                }, bound && style == MenuStyleKind.Native));
            }
            bool native = style == MenuStyleKind.Native;
            Menu.Switch(NativeStyle.Sounds, "Menu sounds", "The game's own select and click sounds", native);
            Menu.Switch(NativeStyle.GameSprites, "Game sprites", "Panels, buttons and the highlight bar from the game's UI", native);
            Menu.Switch(NativeStyle.KeyboardNav, "Keyboard and gamepad", "Arrows / d-pad select, Enter / A change, Q / E switch tabs", native);
            if (native && !NativeStyle.Failed)
            {
                Menu.Small("Read from the game: " + NativeLook.Summary);
                Menu.Chips(1, ("Read the game's look again", () =>
                {
                    NativeLook.Reset();
                    Menu.SetStatus("Re-reading the game's menu look");
                }, true));
            }
        });
    }

    private static void SetStyle(MenuStyleKind kind)
    {
        if (NativeStyle.Style == null || NativeStyle.Style.Value == kind) return;
        NativeStyle.Style.Value = kind;
        if (kind == MenuStyleKind.Native && NativeStyle.Failed) NativeStyle.Retry();
        Menu.SetStatus(kind == MenuStyleKind.Native ? "Menu style: Native" : "Menu style: Classic");
    }
}
