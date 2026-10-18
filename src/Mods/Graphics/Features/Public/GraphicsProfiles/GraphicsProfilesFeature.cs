using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using UnityEngine;
using OverTheHillPlus.Graphics;

namespace OverTheHillPlus.Features.Public.GraphicsProfiles;

/// <summary>
/// Graphics profiles: one-click Beauty / Balanced / Performance / Potato, plus saving, loading and sharing your own
/// looks. A profile covers every mod setting in a config section starting with "Graphics" (read generically, so any
/// graphics feature is included without knowing about this one) and, if switched on, the game's own graphics options.
/// Card at the top of PLUS → Graphics.
/// </summary>
internal sealed class GraphicsProfilesFeature : IFeature
{
    public const string Section = "Graphics.Profiles";
    public const string Tab = "Graphics";

    private static ConfigEntry<bool> _includeGame;
    private static ConfigEntry<string> _lastApplied;
    private static ConfigEntry<string> _lastFingerprint;

    // UI state
    private static string _name = "";
    private static int _preview = -1;
    private static List<GraphicsTiers.Change> _previewPlan;
    private static float _previewAt = -100f;
    private static GraphicsProfile _undo;
    private static string _undoLabel;
    private static (string name, string print) _undoPrev;  // "current look" before the undoable action
    private static string _confirm;      // action id awaiting a second click
    private static float _confirmUntil;

    // cached stats (refreshed at most once a second while the card is drawn)
    private static float _statsAt = -100f;
    private static int _entryCount, _sectionCount, _tierCount;
    private static bool _modified;

    public void Register()
    {
        var cfg = GraphicsConfig.File;
        _includeGame = cfg.Bind(Section, "IncludeGameSettings", false,
            "Tiers and profiles also set the game's own Settings > Graphics (quality preset, shadows, vegetation, AO...). " +
            "Resolution, display mode, v-sync, frame cap, anti-aliasing and upscaling are never touched.");
        _lastApplied = cfg.Bind(Section, "LastApplied", "", "Name of the last tier or profile applied (informational).");
        _lastFingerprint = cfg.Bind(Section, "LastFingerprint", "", "Internal: detects changes made after applying a profile.");

        Registry.Section(Registry.PlusMode, Tab, Draw, -100);
    }

    // ---- actions ----

    private static GraphicsProfile Snapshot(string label) => GraphicsProfile.FromCurrent(label, _includeGame.Value);

    private static void Remember(string name)
    {
        _lastApplied.Value = name ?? "";
        _lastFingerprint.Value = GraphicsCatalog.Fingerprint(GraphicsCatalog.Snapshot());
        _statsAt = -100f;
        _previewAt = -100f;
    }

    private static void ApplyTier(GraphicsTier tier)
    {
        string name = GraphicsTiers.Names[(int)tier];
        try
        {
            var before = Snapshot("Before " + name);
            var plan = GraphicsTiers.Plan(tier);
            var result = GraphicsCatalog.Apply(GraphicsTiers.ToValues(plan));
            string game = null;
            if (_includeGame.Value) game = GameGraphicsBridge.ApplyTier(tier) ?? "game settings failed (see log)";
            _undo = before;
            _undoPrev = (_lastApplied.Value, _lastFingerprint.Value);
            _undoLabel = name;
            Remember(name);
            string mod = plan.Count == 0 ? "no mod settings needed changing" : result.Describe();
            DebugLog.Info(GraphicsPlugin.ModId, $"quality tier '{name}' applied: {mod}" + (game != null ? "; " + game : ""));
            Menu.SetStatus($"{name}: {mod}{(game != null ? "; " + game : "")}");
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfilesFeature.ApplyTier"); Fail(name, e); }
    }

    private static void ApplyProfile(GraphicsProfile p, string label)
    {
        try
        {
            var before = Snapshot("Before " + label);
            var result = GraphicsCatalog.Apply(p.ToValues());
            string game = null;
            if (p.Game != null)
            {
                game = _includeGame.Value
                    ? GameGraphicsBridge.Apply(p.Game) ?? "game settings failed (see log)"
                    : "game settings in it skipped (switch is off)";
            }
            _undo = before;
            _undoPrev = (_lastApplied.Value, _lastFingerprint.Value);
            _undoLabel = label;
            Remember(p.Name ?? label);
            DebugLog.Info(GraphicsPlugin.ModId, $"profile '{label}' loaded: {result.Describe()}" + (game != null ? "; " + game : ""));
            Menu.SetStatus($"Loaded '{label}': {result.Describe()}{(game != null ? "; " + game : "")}");
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfilesFeature.ApplyProfile"); Fail("Load", e); }
    }

    private static void Undo()
    {
        if (_undo == null) return;
        var u = _undo;
        string label = _undoLabel;
        _undo = null;
        try
        {
            var result = GraphicsCatalog.Apply(u.ToValues());
            string game = u.Game != null && _includeGame.Value ? GameGraphicsBridge.Apply(u.Game) : null;
            _lastApplied.Value = _undoPrev.name ?? "";
            _lastFingerprint.Value = _undoPrev.print ?? "";
            _statsAt = -100f;
            _previewAt = -100f;
            DebugLog.Info(GraphicsPlugin.ModId, $"undid '{label}': {result.Describe()}");
            Menu.SetStatus($"Undid {label}: {result.Describe()}{(game != null ? "; " + game : "")}");
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfilesFeature.Undo"); Fail("Undo", e); }
    }

    private static void ResetDefaults()
    {
        try
        {
            var before = Snapshot("Before reset");
            var result = GraphicsCatalog.Apply(GraphicsCatalog.Defaults());
            _undo = before;
            _undoPrev = (_lastApplied.Value, _lastFingerprint.Value);
            _undoLabel = "reset";
            Remember("Defaults");
            Menu.SetStatus("Graphics mod settings reset to defaults: " + result.Describe());
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfilesFeature.ResetDefaults"); Fail("Reset", e); }
    }

    private static void SaveCurrent()
    {
        string name = Plain(_name).Trim();
        if (name.Length == 0) { Menu.SetStatus("Failed: type a name for the profile first."); return; }
        if (GraphicsProfileStore.Exists(name) && !Confirmed("overwrite:" + name))
        {
            Menu.SetStatus($"Blocked: '{name}' exists. Click Save again to overwrite it.");
            return;
        }
        try
        {
            var p = Snapshot(name);
            GraphicsProfileStore.Save(p);
            Remember(name);
            Menu.SetStatus($"Saved '{name}': {p.SettingCount} settings{(p.Game != null ? " + game options" : "")}.");
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfilesFeature.SaveCurrent"); Fail("Save", e); }
    }

    private static void CopyCode(GraphicsProfile p, string what)
    {
        try
        {
            GUIUtility.systemCopyBuffer = GraphicsProfileStore.ToShareCode(p);
            Menu.SetStatus($"Copied a share code for {what} to the clipboard.");
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfilesFeature.CopyCode"); Fail("Copy", e); }
    }

    private static void Import()
    {
        try
        {
            string text = GUIUtility.systemCopyBuffer;
            var p = GraphicsProfileStore.FromText(text, out string error);
            if (p == null) { Menu.SetStatus("Failed: " + error + "."); return; }
            string baseName = string.IsNullOrWhiteSpace(p.Name) ? "Imported" : p.Name.Trim();
            string name = baseName;
            for (int i = 2; GraphicsProfileStore.Exists(name) && i < 100; i++) name = $"{baseName} ({i})";
            p.Name = name;
            GraphicsProfileStore.Save(p);
            int unknown = p.ToValues().Sum(s => s.Value.Count(kv => GraphicsCatalog.Find(s.Key, kv.Key) == null));
            Menu.SetStatus($"Imported '{name}' ({p.SettingCount} settings{(unknown > 0 ? $", {unknown} for features you don't have" : "")}). Click Load to use it.");
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfilesFeature.Import"); Fail("Import", e); }
    }

    private static void Delete(GraphicsProfileStore.Entry e)
    {
        if (!Confirmed("delete:" + e.Path))
        {
            Menu.SetStatus($"Blocked: click Delete again to remove '{e.Profile.Name}'.");
            return;
        }
        try
        {
            GraphicsProfileStore.Delete(e);
            Menu.SetStatus($"Deleted '{e.Profile.Name}'.");
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "GraphicsProfilesFeature.Delete"); Fail("Delete", ex); }
    }

    /// <summary>True on the second click of the same action within a few seconds.</summary>
    private static bool Confirmed(string id)
    {
        float now = Time.unscaledTime;
        if (_confirm == id && now < _confirmUntil) { _confirm = null; return true; }
        _confirm = id;
        _confirmUntil = now + 4f;
        return false;
    }

    private static void Fail(string what, Exception e)
    {
        GraphicsPlugin.Logger.LogWarning($"Graphics profiles: {what} failed: {e}");
        Menu.SetStatus($"Failed: {what}: {e.Message}");
    }

    // ---- Simple view (GraphicsSimple: Quality) ----

    /// <summary>The tier in effect (index into <see cref="GraphicsTiers.Names"/>), or -1 for a custom look.</summary>
    internal static int CurrentTier()
    {
        if (_lastApplied == null) return -1;
        RefreshStats();
        return _modified ? -1 : Array.IndexOf(GraphicsTiers.Names, _lastApplied.Value);
    }

    /// <summary>The tier applied last (even if settings changed since), or -1.</summary>
    internal static int LastTier() => _lastApplied == null ? -1 : Array.IndexOf(GraphicsTiers.Names, _lastApplied.Value);

    internal static void ApplyTierAt(int index)
    {
        if (_lastApplied == null || index < 0 || index >= GraphicsTiers.Names.Length) return;
        ApplyTier((GraphicsTier)index);
        _statsAt = -100f;
    }

    // ---- menu ----

    private static void RefreshStats()
    {
        if (Features.Public.MenuUx.Indexing) return; // menu search indexing runs every draw function: no scans then
        float now = Time.unscaledTime;
        if (now - _statsAt < 1f) return;
        _statsAt = now;
        try
        {
            var entries = GraphicsCatalog.Entries();
            _entryCount = entries.Count;
            _sectionCount = entries.Select(e => e.Definition.Section).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _tierCount = GraphicsTiers.CoveredCount();
            _modified = _lastApplied.Value.Length > 0
                        && _lastFingerprint.Value != GraphicsCatalog.Fingerprint(GraphicsCatalog.Snapshot());
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfilesFeature.RefreshStats"); GraphicsCatalog.LogOnce("stats", "Graphics profiles stats failed: " + e.Message); }
    }

    private static void Draw()
    {
        RefreshStats();
        Menu.Card("Graphics profiles", "One click for the whole look, or save your own and share it with a code.", () =>
        {
            string active = _lastApplied.Value.Length == 0 ? "Custom" : Plain(_lastApplied.Value) + (_modified ? "  (changed since)" : "");
            Menu.KeyValue("Current look", active);
            Menu.KeyValue("Profiles cover", _entryCount == 0
                ? "no graphics settings installed yet"
                : $"{_entryCount} settings in {_sectionCount} section{(_sectionCount == 1 ? "" : "s")}"
                  + (_includeGame.Value ? " + game" : ""));
            if (_entryCount > 0 || _includeGame.Value)
                Menu.KeyValue("Tiers adjust", $"{_tierCount} of them" + (_includeGame.Value ? " + the game's quality preset" : ""));

            Menu.Chips(4,
                ("Beauty", () => ApplyTier(GraphicsTier.Beauty), true),
                ("Balanced", () => ApplyTier(GraphicsTier.Balanced), true),
                ("Performance", () => ApplyTier(GraphicsTier.Performance), true),
                ("Potato", () => ApplyTier(GraphicsTier.Potato), true));
            Menu.Small("Beauty: everything up. Balanced: the defaults. Performance: the expensive effects off and "
                       + "distances shorter. Potato: as fast as it goes. Colours and other looks are never changed by a tier.");

            Menu.Chips(3,
                (_undo != null ? $"Undo {_undoLabel}" : "Undo", Undo, _undo != null),
                ("Reset to defaults", ResetDefaults, _entryCount > 0),
                (_preview >= 0 ? "Hide preview" : "Preview a tier", () => { _preview = _preview >= 0 ? -1 : 0; _previewAt = -100f; }, true));

            if (_preview >= 0) DrawPreview();

            Menu.Switch(_includeGame, "Include the game's own graphics settings",
                "Quality preset, shadows, vegetation, AO... Never resolution, v-sync, frame cap or upscaling.", true);
            if (_includeGame.Value && GameGraphicsBridge.LastError.Length > 0)
                Menu.Small($"<color=#E5534B>Game settings: {GameGraphicsBridge.LastError}</color>");
        });

        Menu.Card("My graphics profiles", null, DrawProfiles);
    }

    private static void DrawPreview()
    {
        Menu.Chips(4,
            (Mark(0, "Beauty"), () => SetPreview(0), true),
            (Mark(1, "Balanced"), () => SetPreview(1), true),
            (Mark(2, "Performance"), () => SetPreview(2), true),
            (Mark(3, "Potato"), () => SetPreview(3), true));

        float now = Time.unscaledTime;
        if (_previewPlan == null || now - _previewAt > 1f)
        {
            _previewAt = now;
            try { _previewPlan = GraphicsTiers.Plan((GraphicsTier)_preview); }
            catch (Exception ex) { DebugLog.Error("graphics", ex, "GraphicsProfilesFeature.DrawPreview"); _previewPlan = new List<GraphicsTiers.Change>(); }
        }
        var plan = _previewPlan;
        string tierName = GraphicsTiers.Names[_preview];
        if (plan.Count == 0)
            Menu.Small(_entryCount == 0
                ? "Nothing to change: no graphics features with settings are installed."
                : $"{tierName} would not change any mod setting (already there, or no setting it recognises).");
        else
        {
            Menu.Small($"{tierName} would change {plan.Count} mod setting{(plan.Count == 1 ? "" : "s")}:");
            const int max = 30;
            foreach (var c in plan.Take(max))
            {
                string area = GraphicsCatalog.AreaOf(c.Entry.Definition.Section);
                Menu.KeyValue(Short($"{area} · {c.Entry.Definition.Key}", 30), $"{Short(c.From, 18)}  →  <color=#F2B33D>{Short(c.To, 18)}</color>");
            }
            if (plan.Count > max) Menu.Small($"...and {plan.Count - max} more.");
        }
        if (_includeGame.Value)
        {
            string q = _preview switch { 0 => "its highest", 1 => "its default", 2 => "Low", _ => "Lowest" };
            Menu.Small($"The game's own quality preset would go to {q}, with that preset's advanced options.");
        }
    }

    private static string Mark(int i, string label) => _preview == i ? "● " + label : label;

    private static void SetPreview(int i)
    {
        _preview = i;
        _previewAt = -100f;
        _previewPlan = null;
    }

    /// <summary>Removes rich-text markup characters and control characters (labels render rich text).</summary>
    private static string Plain(string s) =>
        new string((s ?? "").Where(c => c != '<' && c != '>' && !char.IsControl(c)).ToArray());

    private static string Short(string s, int max)
    {
        s = Plain(s);
        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    private static void DrawProfiles()
    {
        _name = Menu.TextRow("Name", _name, 48);
        Menu.Chips(3,
            ("Save current look", SaveCurrent, true),
            ("Copy current as code", () => CopyCode(Snapshot(string.IsNullOrWhiteSpace(_name) ? "Shared look" : _name.Trim()), "the current look"), true),
            ("Import from clipboard", Import, true));

        var list = GraphicsProfileStore.List();
        if (list.Count == 0)
        {
            Menu.Small("No saved profiles yet. Save one above, or copy a friend's code and press Import.");
        }
        else
        {
            foreach (var e in list) DrawProfileRow(e);
        }
        Menu.Small($"Saved as JSON files in {GraphicsProfileStore.Folder}");
    }

    /// <summary>A saved profile: its name (amber when it is the one applied last) and Load / Share / Delete, then its details.</summary>
    private static void DrawProfileRow(GraphicsProfileStore.Entry e)
    {
        var p = e.Profile;
        bool active = string.Equals(_lastApplied.Value, p.Name, StringComparison.OrdinalIgnoreCase);
        bool armed = _confirm == "delete:" + e.Path && Time.unscaledTime < _confirmUntil;
        SimpleMenu.Actions(active ? $"<color=#F2B33D>{p.Name}</color>" : p.Name,
            ("Load", () => ApplyProfile(p, p.Name), true),
            ("Share", () => CopyCode(p, $"'{p.Name}'"), true),
            (armed ? "Sure?" : "Delete", () => Delete(e), true));
        Menu.Small($"{p.SettingCount} settings" + (p.Game != null ? " + game" : "") + (string.IsNullOrEmpty(p.Created) ? "" : "  ·  " + p.Created));
    }
}
