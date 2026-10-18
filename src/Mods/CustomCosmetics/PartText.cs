using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// The parts' garage names and descriptions. The game shows an item's Name / Description through Unity Localization, so
/// each part's LocalizedStrings point at two keys of our own (OTHP_CC_&lt;ID&gt;_NAME / _DESCRIPTION) in the string table of
/// a stock cosmetic, and those entries are added to the table of the selected language (again whenever the language
/// changes or the table reloads). English text in every language. Same approach as core's CarText.
/// </summary>
internal static class PartText
{
    private static TableReference _table;
    private static bool _has;
    private static float _next;
    private static readonly List<(string key, string text)> Entries = new();

    /// <summary>A LocalizedString for the entry <paramref name="key"/> in the table <paramref name="like"/> uses; null when
    /// there is no table to put it in.</summary>
    public static LocalizedString Make(LocalizedString like, string key, string text)
    {
        if (like == null) return null;
        if (!_has) { _table = like.TableReference; _has = true; }
        Entries.Add((key, text));
        Ensure();
        return new LocalizedString(_table, (TableEntryReference)key);
    }

    /// <summary>Every two seconds: adds missing entries to the selected language's table.</summary>
    public static void Tick()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 2f;
        Ensure();
    }

    private static void Ensure()
    {
        if (!_has || Entries.Count == 0) return;
        try
        {
            var db = LocalizationSettings.StringDatabase;
            if (db == null) return;
            var locale = LocalizationSettings.SelectedLocale;
            if (!db.IsTableLoaded(_table, locale)) return; // loading it here would block: the next tick tries again
            var t = db.GetTable(_table, locale);
            if (t == null) return;
            foreach (var (key, text) in Entries)
                if (t.GetEntry(key) == null) t.AddEntry(key, text);
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "PartText.Ensure"); }
    }
}
