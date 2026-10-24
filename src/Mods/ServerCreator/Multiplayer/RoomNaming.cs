using System;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.ServerCreator;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// Server-name text and colour styles (rich-text tags, which the game's server list renders). The extra looks
/// (highlight, size, italic, underline, spacing) and the markup budget live in Features/Public/ServerNameStyles.
/// </summary>
internal static class RoomNames
{
    public static readonly string[] Styles = { "None", "Gold", "Cyan", "Pink", "Green", "Red", "Purple", "Rainbow", "Sunset", "Ocean" };

    internal static readonly System.Collections.Generic.Dictionary<string, string> Solid = new()
    {
        ["Gold"] = "#F2B33D", ["Cyan"] = "#4FD8F7", ["Pink"] = "#FF6FB5", ["Green"] = "#5BE38A", ["Red"] = "#FF5A4E", ["Purple"] = "#B48CFF",
    };

    internal static readonly System.Collections.Generic.Dictionary<string, (UnityEngine.Color a, UnityEngine.Color b)> Gradients = new()
    {
        ["Sunset"] = (new UnityEngine.Color(1f, 0.85f, 0.25f), new UnityEngine.Color(1f, 0.25f, 0.45f)),
        ["Ocean"] = (new UnityEngine.Color(0.3f, 0.95f, 1f), new UnityEngine.Color(0.25f, 0.35f, 1f)),
    };

    /// <summary>
    /// The custom name if set and allowed, otherwise the game's own display name. Names with swears or slurs are
    /// refused (not censored); the menu shows why.
    /// </summary>
    public static string DisplayName(string gameDefault)
    {
        string custom = Escape(ServerCreatorConfig.CustomRoomName.Value).Trim();
        if (custom.Length > 40)
        {
            custom = custom.Substring(0, 40);
            // Never publish half an emoji: a lone high surrogate breaks Photon's UTF-8 and the style runs.
            if (char.IsHighSurrogate(custom[custom.Length - 1])) custom = custom.Substring(0, custom.Length - 1);
            custom = custom.TrimEnd();
        }
        if (custom.Length == 0) return gameDefault;
        if (Features.Public.Filter.NameFilter.IsBlocked(custom, out string why))
        {
            if (_refusedLogged != custom)
            {
                _refusedLogged = custom;
                ServerCreatorPlugin.Logger.LogWarning($"[Lobby] Custom server name not used: {why}");
            }
            return gameDefault;
        }
        return custom;
    }

    private static string _refusedLogged;

    /// <summary>Neutralises markup the user typed (see <see cref="HostNotice.Escape"/>): only the mod's own style
    /// tags (<see cref="Style"/>) are added afterwards.</summary>
    public static string Escape(string text) => HostNotice.Escape(text);

    /// <summary>
    /// The name with the chosen look (colour, bold, and the extra styles of
    /// <see cref="Features.Public.ServerNameStyles.NameStyle"/>), within the markup budget.
    /// </summary>
    public static string Style(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        string text;
        try { text = Features.Public.ServerNameStyles.NameStyle.Compose(name, Features.Public.ServerNameStyles.NameStyle.Current()).Text; }
        catch (Exception e)
        {
            DebugLog.Error("servercreator", e, "RoomNames.Style");
            if (!_styleErrorLogged)
            {
                _styleErrorLogged = true;
                ServerCreatorPlugin.Logger.LogWarning("[Lobby] Server name style failed, name left plain: " + e.Message);
            }
            text = name;
        }
        // Since the game's 10-16 update its list strips <tags>; escaped tags survive that and still render (checked on
        // this PC by Server Browser+), so every player sees the colours.
        try
        {
            if (text != name && ServerCreatorConfig.ColoursForEveryone != null && ServerCreatorConfig.ColoursForEveryone.Value
                && Core.Shared.ServerListCaps.EscapedTagsRender == true)
            {
                string escaped = Core.Shared.ServerListCaps.EscapeTags(text);
                if (escaped.Length <= 480) text = escaped;
            }
        }
        catch (Exception e) { DebugLog.Error("servercreator", e, "RoomNames.Style escape"); }
        if (Produced.Count > 64) Produced.Clear();
        if (!string.IsNullOrEmpty(text)) Produced.Add(text);
        return text;
    }

    private static bool _styleErrorLogged;

    // Every display name Style() returned. "Already styled" is decided by this, never by tag substrings: those can
    // come from the game-default name (a host's own name with "<b>" in it) and would skip the custom name.
    private static readonly System.Collections.Generic.HashSet<string> Produced = new();

    /// <summary>True if <paramref name="display"/> is exactly a name this mod already styled.</summary>
    public static bool IsOwnOutput(string display) => !string.IsNullOrEmpty(display) && Produced.Contains(display);
}

/// <summary>
/// Renames and styles the server where the game builds the room name ("&lt;id&gt;_&lt;player&gt;'s room"). The id before the
/// first '_' keeps the session unique and backs join-by-code, so only the part after it changes.
/// </summary>
[HarmonyPatch(typeof(OverlandNetworkUtils), nameof(OverlandNetworkUtils.GenerateRoomName))]
internal static class RoomNamePatch
{
    /// <summary>Applies the custom name/style to the part after the id. Idempotent: styled names are left alone.</summary>
    public static string Restyle(string full)
    {
        if (string.IsNullOrEmpty(full)) return full;
        int cut = full.IndexOf('_');
        string prefix = cut >= 0 ? full.Substring(0, cut + 1) : "";
        string display = cut >= 0 ? full.Substring(cut + 1) : full;
        if (RoomNames.IsOwnOutput(display)) return full; // already styled by us (e.g. GenerateRoomName, then StartGame)
        // The game default (the host's own name) is escaped too, so markup in it can't mix with the mod's style tags.
        string renamed = prefix + RoomNames.Style(RoomNames.DisplayName(RoomNames.Escape(display)));
        // Every player's browser cuts names at fixed positions: a short name or a changed prefix would break their list.
        string safe = Core.Shared.SessionNameGuard.Pick(full, renamed, out string why);
        if (why != null && _guardLogged != why)
        {
            _guardLogged = why;
            ServerCreatorPlugin.Logger.LogWarning($"[Lobby] Custom server name not used ({why}); the game's name is kept so every player's server list stays readable.");
        }
        return safe;
    }

    private static string _guardLogged;

    private static void Postfix(ref string __result)
    {
        try
        {
            string renamed = Restyle(__result);
            if (renamed == __result) return;
            if (DevDetails.On) ServerCreatorPlugin.Logger.LogInfo($"[Lobby] Room name '{__result}' -> '{renamed}' ({renamed.Length} chars)");
            __result = renamed;
        }
        catch (Exception e) { DebugLog.Error("servercreator", e, "RoomNamePatch.Postfix"); ServerCreatorPlugin.Logger.LogWarning("[Lobby] Room rename failed: " + e.Message); }
    }
}

/// <summary>
/// Styles the session name Fusion/Photon publishes, when hosting. (The session's player count is set by Lobby+'
/// own prefix on the same method.)
/// </summary>
[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class FusionSessionNamePatch
{
    /// <summary>Last chance to rename: the session name Fusion/Photon will publish ("&lt;id&gt;_&lt;name&gt;").</summary>
    private static void RenameSession(StartGameArgs args)
    {
        string name = args.SessionName;
        if (string.IsNullOrEmpty(name))
        {
            // The name may come from a generator callback instead: wrap it so its result gets styled.
            var original = args.SessionNameGenerator;
            if (DevDetails.On) ServerCreatorPlugin.Logger.LogInfo($"[Lobby] No session name set; generator present: {original != null}");
            if (original == null) return;
            args.SessionNameGenerator = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Func<string>>(
                new Func<string>(() =>
                {
                    string generated = original.Invoke();
                    // Runs inside Fusion's StartGame, outside the prefix's try: a styling error must not stop hosting.
                    string styled;
                    try { styled = RoomNamePatch.Restyle(generated); }
                    catch (Exception e)
                    {
                        DebugLog.Error("servercreator", e, "FusionSessionNamePatch.RenameSession");
                        ServerCreatorPlugin.Logger.LogWarning("[Lobby] Session name style failed, name left as generated: " + e.Message);
                        return generated;
                    }
                    if (DevDetails.On) ServerCreatorPlugin.Logger.LogInfo($"[Lobby] Generated session name '{generated}' -> '{styled}'");
                    return styled;
                }));
            return;
        }
        string renamed = RoomNamePatch.Restyle(name);
        if (renamed == name) return;
        args.SessionName = renamed;
        if (DevDetails.On) ServerCreatorPlugin.Logger.LogInfo($"[Lobby] Session name '{name}' -> '{renamed}' (reads back '{args.SessionName}')");
    }

    private static void Prefix(StartGameArgs args)
    {
        try
        {
            if (args.GameMode != GameMode.Client) RenameSession(args);
        }
        catch (Exception e) { DebugLog.Error("servercreator", e, "FusionSessionNamePatch.Prefix"); ServerCreatorPlugin.Logger.LogWarning("[Lobby] Fusion StartGame rename: " + e.Message); }
    }
}
