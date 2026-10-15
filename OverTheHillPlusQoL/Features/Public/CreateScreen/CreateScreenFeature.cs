using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using OverTheHillPlus.Features.Public.Autosave;
using OverTheHillPlus.Features.Public.Chat;
using OverTheHillPlus.Features.Public.JoinSmoothing;
using OverTheHillPlus.Features.Public.ServerNameStyles;
using OverTheHillPlus.Features.Public.ServerPlayers;
using OverTheHillPlus.Multiplayer;
using UnityEngine.EventSystems;

namespace OverTheHillPlus.Features.Public.CreateScreen;

/// <summary>
/// The common server settings as native rows on the game's Create Server screen (between Server Region and Create),
/// so they don't need the PLUS menu: server name, name colour and highlight, join message, names in the listing,
/// mod network and chat, smooth joins, autosave. The PLUS LOBBY SETTINGS button stays last for everything else.
/// </summary>
internal sealed class CreateScreenFeature : IFeature
{
    internal const string NameRowId = "name";
    internal static ConfigEntry<bool> NativeRows;

    public void Register()
    {
        NativeRows = ModConfig.File.Bind("Create Server Screen", "NativeRows", true,
            "Show the common server settings (name, style, join message, mod network, autosave, ...) as rows on the game's Create Server screen.");

        CreateScreenRows.Add(NameRow.Create(10));
        CreateScreenRows.Add(CreateRow.Choice("colour", "Name Colour", 20, () => ModConfig.RoomNameStyle,
            RoomNames.Styles, Array.ConvertAll(RoomNames.Styles, s => s == "None" ? "PLAIN" : s)));
        CreateScreenRows.Add(new CreateRow
        {
            Id = "highlight",
            Label = "Name Highlight",
            Order = 30,
            Available = () => NameStyle.Highlight != null,
            Options = () => HighlightNames,
            Selected = () => Math.Max(0, Array.IndexOf(HighlightNames, NameStyle.Highlight.Value)),
            Choose = i => { if (i >= 0 && i < HighlightNames.Length) NameStyle.Highlight.Value = HighlightNames[i]; },
        });
        CreateScreenRows.Add(CreateRow.Toggle("joinmsg", "Join Message", 40, () => ModConfig.JoinMessageEnabled));
        CreateScreenRows.Add(CreateRow.Toggle("players", "Show Players in Listing", 50, () => ServerPlayersFeature.Publish));
        CreateScreenRows.Add(CreateRow.Toggle("modnet", "Mod Network", 60, () => ModNet.ModNet.Enabled));
        CreateScreenRows.Add(CreateRow.Toggle("modchat", "Mod Chat", 70, () => ChatFeature.Enabled));
        CreateScreenRows.Add(CreateRow.Toggle("smooth", "Smooth Joins", 80, () => CarPreloader.Enabled));
        CreateScreenRows.Add(AutosaveRow(90));

        Registry.OnTick(CreateScreenBuilder.Tick);
    }

    private static string[] _highlightNames;

    private static string[] HighlightNames => _highlightNames ??= Array.ConvertAll(NameStyle.Highlights, h => h.Name);

    // ---- autosave: OFF / 5 / 10 / 15 min (plus the configured interval if it's another one) ----

    private static readonly List<string> AutosaveLabels = new();
    private static readonly List<int> AutosaveMinutes = new();

    private static CreateRow AutosaveRow(int order) => new()
    {
        Id = "autosave",
        Label = "Autosave",
        Order = order,
        Available = () => AutosaveConfig.Enabled != null && AutosaveConfig.IntervalMinutes != null,
        Selected = () =>
        {
            BuildAutosave();
            if (!AutosaveConfig.Enabled.Value) return 0;
            int now = (int)Math.Round(AutosaveConfig.IntervalMinutes.Value);
            return Math.Max(0, AutosaveMinutes.IndexOf(now));
        },
        Options = () => AutosaveLabels,
        Choose = i =>
        {
            if (i <= 0 || i >= AutosaveMinutes.Count) { AutosaveConfig.Enabled.Value = false; return; }
            AutosaveConfig.Enabled.Value = true;
            AutosaveConfig.IntervalMinutes.Value = AutosaveMinutes[i];
        },
    };

    private static void BuildAutosave()
    {
        AutosaveLabels.Clear();
        AutosaveMinutes.Clear();
        AutosaveLabels.Add("OFF");
        AutosaveMinutes.Add(0);
        int current = (int)Math.Round(AutosaveConfig.IntervalMinutes.Value);
        var minutes = new List<int> { 5, 10, 15 };
        if (AutosaveConfig.Enabled.Value && current > 0 && !minutes.Contains(current)) minutes.Add(current);
        minutes.Sort();
        foreach (int m in minutes)
        {
            AutosaveLabels.Add(m + " MIN");
            AutosaveMinutes.Add(m);
        }
    }

    internal static void NameTyped(string text) => NameRow.Typed(text);
}

/// <summary>
/// Server Name row: DEFAULT (the game's "&lt;name&gt;'s room"), your typed name, a few ready-made names, and
/// TYPE A NAME… which opens the text field (as does Enter / click / A on the row).
/// </summary>
internal static class NameRow
{
    private static readonly string[] ReadyNames = { "Free Roam", "Chill Cruise", "Offroad Crew", "Convoy Night", "Newcomers Welcome", "Photo Tour" };
    private const string DefaultLabel = "DEFAULT";
    private const string TypeLabel = "TYPE A NAME…";

    private static string _typed; // the last typed (non-preset) name this game, kept while cycling through presets
    private static readonly List<string> Labels = new();

    public static CreateRow Create(int order) => new()
    {
        Id = CreateScreenFeature.NameRowId,
        Label = "Server Name",
        Order = order,
        Available = () => ModConfig.CustomRoomName != null,
        Selected = Selected,
        Options = Options,
        Choose = Choose,
        Submit = gamepad => CreateNameEditor.RequestFromSubmit(gamepad),
    };

    internal static void Typed(string text)
    {
        if (!string.IsNullOrEmpty(text) && Array.IndexOf(ReadyNames, text) < 0) _typed = text;
    }

    private static string Current => (ModConfig.CustomRoomName.Value ?? "").Trim();

    private static int Selected()
    {
        string v = Current;
        if (v.Length == 0) return 0;
        int p = Array.IndexOf(ReadyNames, v);
        if (p >= 0) return (_typed != null ? 2 : 1) + p;
        _typed = v; // set elsewhere (PLUS menu, config file)
        return 1;
    }

    private static IReadOnlyList<string> Options()
    {
        Labels.Clear();
        Labels.Add(DefaultLabel);
        if (_typed != null) Labels.Add(Shown(_typed));
        Labels.AddRange(ReadyNames);
        Labels.Add(TypeLabel);
        return Labels;
    }

    private static string Shown(string name)
    {
        string s = RoomNames.Escape(name);
        if (Filter.NameFilter.IsBlocked(s, out _)) return "(NOT ALLOWED)";
        return s.Length > 28 ? s.Substring(0, 27) + "…" : s;
    }

    private static void Choose(int i)
    {
        int typedSlot = _typed != null ? 1 : 0;
        if (i <= 0) { ModConfig.CustomRoomName.Value = ""; return; }
        if (typedSlot == 1 && i == 1) { ModConfig.CustomRoomName.Value = _typed; return; }
        int p = i - 1 - typedSlot;
        if (p >= 0 && p < ReadyNames.Length) { ModConfig.CustomRoomName.Value = ReadyNames[p]; return; }
        CreateNameEditor.Request(); // TYPE A NAME…
    }
}

// ---- hooks ----

[HarmonyPatch(typeof(ContextUICreateServer), nameof(ContextUICreateServer.Start))]
internal static class CreateScreenStartPatch
{
    // After the lobby-size setup and the PLUS button (normal priority), so both are in place.
    [HarmonyPriority(Priority.Low)]
    private static void Postfix(ContextUICreateServer __instance)
    {
        try { CreateScreenBuilder.Build(__instance); }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("[CreateScreen] Adding the server setting rows failed: " + e);
            try { CreateScreenBuilder.Clear(); } catch { }
        }
    }
}

[HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.CreateRoom))]
internal static class CreateRoomGuardPatch
{
    private static bool Prefix()
    {
        try
        {
            if (CreateScreenBuilder.ShouldBlockCreate()) return false;
            CreateScreenRows.RaiseServerCreated();
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[CreateScreen] Create guard: " + e.Message); }
        return true;
    }
}

[HarmonyPatch(typeof(UISelector), nameof(UISelector.OnSubmit))]
internal static class RowSubmitPatch
{
    private static void Prefix(UISelector __instance)
    {
        if (CreateScreenBuilder.SubmitRowPtr == IntPtr.Zero || __instance.Pointer != CreateScreenBuilder.SubmitRowPtr) return;
        try { CreateScreenBuilder.Submitted(__instance.Pointer, pointer: false); } catch { }
    }
}

[HarmonyPatch(typeof(UISelector), nameof(UISelector.OnPointerClick))]
internal static class RowClickPatch
{
    private static void Prefix(UISelector __instance, PointerEventData eventData)
    {
        if (CreateScreenBuilder.SubmitRowPtr == IntPtr.Zero || __instance.Pointer != CreateScreenBuilder.SubmitRowPtr) return;
        try
        {
            if (eventData != null && eventData.button != PointerEventData.InputButton.Left) return;
            CreateScreenBuilder.Submitted(__instance.Pointer, pointer: true);
        }
        catch { }
    }
}
