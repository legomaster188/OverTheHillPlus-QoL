using System;
using BepInEx.Configuration;
using UnityEngine;

namespace OverTheHillPlus.Features.Public;

/// <summary>Settings and the "Menu" tab for the menu UX features (scale, search, tooltips, cards, keybinds).</summary>
internal sealed class MenuUxFeature : IFeature
{
    internal const string Tab = "Menu";
    private const string SectionName = "MenuUx";

    public void Register()
    {
        var cfg = CoreConfig.File;
        MenuUx.UiScale = cfg.Bind(SectionName, "UiScale", 1f, new ConfigDescription(
            "Menu size multiplier on top of the automatic resolution scaling (1 = default).", new AcceptableValueRange<float>(0.5f, 2.5f)));
        MenuUx.Tooltips = cfg.Bind(SectionName, "Tooltips", true, "Show a setting's full description when you hover it in the menu.");
        MenuUx.SearchBar = cfg.Bind(SectionName, "SearchBar", true, "Show the settings search box under the menu tabs (Ctrl+F focuses it).");
        MenuUx.FoldCards = cfg.Bind(SectionName, "FoldCards", true, "Advanced view: cards start folded, except the first card of each tab.");

        Registry.OnTick(() =>
        {
            MenuUx.Tick();
            KeybindEditor.Tick();
        });

        Registry.Section(Registry.PlusMode, Tab, DrawTab, 0);
    }

    private static void DrawTab()
    {
        Menu.Card("Look & feel", null, () =>
        {
            Menu.SliderRow(MenuUx.UiScale, "Menu size", 0.5f, 2.5f, "0.00", "×", true, percent: true);
            Menu.Chips(5,
                ("75%", () => SetScale(0.75f), true),
                ("90%", () => SetScale(0.9f), true),
                ("100%", () => SetScale(1f), true),
                ("125%", () => SetScale(1.25f), true),
                ("150%", () => SetScale(1.5f), true));
            Menu.Small($"Screen {Screen.width}×{Screen.height}: the menu already grows with resolution above 1080p; this multiplies that.");
            Menu.Switch(MenuUx.Tooltips, "Hover tooltips", "Full descriptions when you rest the mouse on a setting", true);
        });

        Menu.Card("Cards", "Advanced settings: click a card's title to open or fold it. Your choice is kept until the game restarts.", () =>
        {
            Menu.Switch(MenuUx.FoldCards, "Start with cards folded", "Only the first card under each heading opens by itself", true);
            Menu.KeyValue("Folded now", MenuUx.CollapsedCount.ToString());
            Menu.Chips(2,
                ("Fold all cards", () => { MenuUx.CollapseAll(true); Menu.SetStatus("Folded every card you've seen (except these settings)"); }, true),
                ("Unfold all", () => { MenuUx.CollapseAll(false); Menu.SetStatus("All cards unfolded"); }, MenuUx.CollapsedCount > 0));
        });

        Menu.Card("Keybinds", "Click a key, then press the new one.", KeybindEditor.DrawRows);
    }

    private static void SetScale(float v)
    {
        if (MenuUx.UiScale == null) return;
        MenuUx.UiScale.Value = v;
        Menu.SetStatus($"Menu size {Mathf.RoundToInt(v * 100f)}%");
    }
}
