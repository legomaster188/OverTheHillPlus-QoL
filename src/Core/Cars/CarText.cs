using System;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A modded car's garage name and description. The game shows CarInfo.Name / Description through Unity Localization, so
/// the car's LocalizedStrings point at two keys of our own (OTHP_&lt;CARNAME&gt;_NAME / _DESCRIPTION) in the base car's
/// string table, and those entries are added to the table of the selected language (again whenever the language changes
/// or the table reloads). English text in every language.
/// </summary>
internal sealed class CarText
{
    private readonly RuntimeCar _car;
    private readonly string _nameKey, _descriptionKey;
    private bool _hasNameTable, _hasDescTable;
    private TableReference _nameTable, _descTable;
    private float _next;

    public CarText(RuntimeCar car)
    {
        _car = car;
        _nameKey = car.Def.TextKey("NAME");
        _descriptionKey = car.Def.TextKey("DESCRIPTION");
    }

    public LocalizedString Name(LocalizedString like)
    {
        if (like == null) return null;
        _nameTable = like.TableReference;
        _hasNameTable = true;
        Ensure(force: true);
        return new LocalizedString(_nameTable, (TableEntryReference)_nameKey);
    }

    public LocalizedString Description(LocalizedString like)
    {
        if (like == null) return null;
        _descTable = like.TableReference;
        _hasDescTable = true;
        Ensure(force: true);
        return new LocalizedString(_descTable, (TableEntryReference)_descriptionKey);
    }

    /// <summary>Every two seconds: adds the entries to the selected language's tables when they are missing.</summary>
    public void Tick()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 2f;
        Ensure(force: false);
    }

    private void Ensure(bool force)
    {
        if (!_hasNameTable && !_hasDescTable) return;
        try
        {
            var db = LocalizationSettings.StringDatabase;
            if (db == null) return;
            var locale = LocalizationSettings.SelectedLocale;
            if (_hasNameTable) Add(db, _nameTable, locale, _nameKey, _car.Def.DisplayName);
            if (_hasDescTable) Add(db, _descTable, locale, _descriptionKey, _car.Def.Description);
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "CarText.Ensure");
            if (force) _car.LogOnce("text", "garage name not added to the string table: " + e.Message);
        }
    }

    private static void Add(LocalizedStringDatabase db, TableReference table, UnityEngine.Localization.Locale locale, string key, string text)
    {
        if (!db.IsTableLoaded(table, locale)) return; // loading it here would block: the next tick tries again
        var t = db.GetTable(table, locale);
        if (t == null || t.GetEntry(key) != null) return;
        t.AddEntry(key, text);
    }
}
