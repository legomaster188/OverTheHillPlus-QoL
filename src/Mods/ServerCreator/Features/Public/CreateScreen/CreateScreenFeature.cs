using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using OverTheHillPlus.Features.Public.ServerNameStyles;
using OverTheHillPlus.Features.Public.ServerPlayers;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.ServerCreator;
using UnityEngine.EventSystems;

namespace OverTheHillPlus.Features.Public.CreateScreen;

/// <summary>
/// The common server settings as native rows on the game's Create Server screen (between Server Region and Create),
/// so they don't need the MODS page. This mod's own rows (server name, name colour and highlight, join message, names
/// in the listing) are added here; other mods add theirs (mod network, chat, smooth joins, autosave, ...) through
/// <see cref="ServerSettings.AddRow"/>, and <see cref="CreateScreenBuilder"/> draws them all. The PLUS LOBBY SETTINGS
/// button stays last for everything else.
/// </summary>
internal sealed class CreateScreenFeature : IFeature
{
    internal const string NameRowId = "name";
    internal static ConfigEntry<bool> NativeRows;

    public void Register()
    {
        NativeRows = ServerCreatorConfig.File.Bind("Create Server Screen", "NativeRows", true,
            "Show the common server settings (name, style, join message, mod network, autosave, ...) as rows on the game's Create Server screen.");

        const string mod = ServerCreatorPlugin.ModId;
        ServerSettings.AddRow(NameRow.Create(10));
        ServerSettings.AddRow(ServerSettingRow.Choice("colour", "Name Colour", 20, mod, () => ServerCreatorConfig.RoomNameStyle,
            RoomNames.Styles, Array.ConvertAll(RoomNames.Styles, s => s == "None" ? "PLAIN" : s)));
        ServerSettings.AddRow(new ServerSettingRow
        {
            Id = "highlight",
            Label = "Name Highlight",
            Order = 30,
            ModId = mod,
            Available = () => NameStyle.Highlight != null,
            Options = () => HighlightNames,
            Selected = () => Math.Max(0, Array.IndexOf(HighlightNames, NameStyle.Highlight.Value)),
            Choose = i => { if (i >= 0 && i < HighlightNames.Length) NameStyle.Highlight.Value = HighlightNames[i]; },
        });
        ServerSettings.AddRow(ServerSettingRow.Toggle("joinmsg", "Join Message", 40, mod, () => ServerCreatorConfig.JoinMessageEnabled));
        ServerSettings.AddRow(ServerSettingRow.Toggle("players", "Show Players in Listing", 50, mod, () => ServerPlayersFeature.Publish));

        Registry.OnTick(CreateScreenBuilder.Tick);
    }

    private static string[] _highlightNames;

    private static string[] HighlightNames => _highlightNames ??= Array.ConvertAll(NameStyle.Highlights, h => h.Name);

    internal static void NameTyped(string text) => NameRow.Typed(text);
}

/// <summary>
/// Server Name row: DEFAULT (the game's "&lt;name&gt;'s room"), your typed name, a few ready-made names, and
/// TYPE A NAME… which opens the text field (as does Enter / click / A on the row).
/// </summary>
internal static class NameRow
{
    private static readonly string[] ReadyNames = { "Free Roam", "Chill Cruise", "Offroad Crew", "Road Trip Night", "Newcomers Welcome", "Photo Tour" };
    private const string DefaultLabel = "DEFAULT";
    private const string TypeLabel = "TYPE A NAME…";

    private static string _typed; // the last typed (non-preset) name this game, kept while cycling through presets
    private static readonly List<string> Labels = new();

    public static ServerSettingRow Create(int order) => new()
    {
        Id = CreateScreenFeature.NameRowId,
        Label = "Server Name",
        Order = order,
        ModId = ServerCreatorPlugin.ModId,
        Available = () => ServerCreatorConfig.CustomRoomName != null,
        Selected = Selected,
        Options = Options,
        Choose = Choose,
        Submit = gamepad => CreateNameEditor.RequestFromSubmit(gamepad),
    };

    internal static void Typed(string text)
    {
        if (!string.IsNullOrEmpty(text) && Array.IndexOf(ReadyNames, text) < 0) _typed = text;
    }

    private static string Current => (ServerCreatorConfig.CustomRoomName.Value ?? "").Trim();

    private static int Selected()
    {
        string v = Current;
        if (v.Length == 0) return 0;
        int p = Array.IndexOf(ReadyNames, v);
        if (p >= 0) return (_typed != null ? 2 : 1) + p;
        _typed = v; // set elsewhere (MODS page, config file)
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
        if (i <= 0) { ServerCreatorConfig.CustomRoomName.Value = ""; return; }
        if (typedSlot == 1 && i == 1) { ServerCreatorConfig.CustomRoomName.Value = _typed; return; }
        int p = i - 1 - typedSlot;
        if (p >= 0 && p < ReadyNames.Length) { ServerCreatorConfig.CustomRoomName.Value = ReadyNames[p]; return; }
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
            DebugLog.Error("servercreator", e, "CreateScreenStartPatch.Postfix");
            ServerCreatorPlugin.Logger.LogWarning("[CreateScreen] Adding the server setting rows failed: " + e);
            try { CreateScreenBuilder.Clear(); } catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateScreenStartPatch.Postfix"); }
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
            ServerSettings.RaiseServerCreated(m => ServerCreatorPlugin.Logger.LogWarning("[CreateScreen] " + m));
        }
        catch (Exception e) { DebugLog.Error("servercreator", e, "CreateRoomGuardPatch.Prefix"); ServerCreatorPlugin.Logger.LogWarning("[CreateScreen] Create guard: " + e.Message); }
        return true;
    }
}

[HarmonyPatch(typeof(UISelector), nameof(UISelector.OnSubmit))]
internal static class RowSubmitPatch
{
    private static void Prefix(UISelector __instance)
    {
        if (CreateScreenBuilder.SubmitRowPtr == IntPtr.Zero || __instance.Pointer != CreateScreenBuilder.SubmitRowPtr) return;
        try { CreateScreenBuilder.Submitted(__instance.Pointer, pointer: false); } catch (Exception ex) { DebugLog.Error("servercreator", ex, "RowSubmitPatch.Prefix"); }
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
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "RowClickPatch.Prefix"); }
    }
}
