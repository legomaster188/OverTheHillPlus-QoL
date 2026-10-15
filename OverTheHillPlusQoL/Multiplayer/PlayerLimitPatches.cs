using System;
using Fusion;
using HarmonyLib;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// Makes the chosen lobby size reach the network session: the patches re-apply the size picked in Create Server
/// at each step on the way from that screen to the running session.
/// </summary>
internal static class PlayerLimit
{
    /// <summary>The size picked in Create Server for the session we're about to host (0 = none pending).</summary>
    public static int Pending;

    /// <summary>
    /// Fusion copies its project config when a runner starts, so raise the player capacity on every config
    /// object it might copy from (the global config and the config asset).
    /// </summary>
    public static void RaiseFusionCapacity(int players)
    {
        void Raise(string what, NetworkProjectConfig cfg)
        {
            var sim = cfg?.Simulation;
            if (sim == null || sim.PlayerCount >= players) return;
            sim.PlayerCount = players;
        }
        try { Raise("Fusion global config", NetworkProjectConfig.Global); } catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] global config: " + e.Message); }
        try { Raise("Fusion config asset", NetworkProjectConfigAsset.Global?.Config); } catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] config asset: " + e.Message); }
    }
}

[HarmonyPatch(typeof(ContextUICreateServer), nameof(ContextUICreateServer.SetGameSetupContextValues))]
internal static class SetupContextPatch
{
    private static void Postfix(ContextUICreateServer __instance, ref GameSetupContext gameSetupContext)
    {
        try
        {
            if (!ModConfig.ExpandLobbySize.Value || gameSetupContext == null) return;
            var options = __instance.maxPlayersOptionsRange;
            int index = __instance.maxPlayersOptionsSelectedIndex;
            if (options == null || index < 0 || index >= options.Count || !int.TryParse(options[index], out int chosen)) return;
            if (gameSetupContext.MaxPlayers != chosen) gameSetupContext.MaxPlayers = chosen;
            PlayerLimit.Pending = chosen;
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] SetGameSetupContextValues patch: " + e.Message); }
    }
}

[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.StartGame))]
internal static class OverlandStartGamePatch
{
    private static void Prefix(GameSetupContext gameSetupContext)
    {
        try
        {
            if (gameSetupContext == null) return;
            if (!ModConfig.ExpandLobbySize.Value || gameSetupContext.GameMode == GameMode.Client) return;

            if (PlayerLimit.Pending > 0 && gameSetupContext.MaxPlayers != PlayerLimit.Pending)
                gameSetupContext.MaxPlayers = PlayerLimit.Pending;
            int custom = ModConfig.CustomMaxPlayers.Value;
            if (custom >= 2) gameSetupContext.MaxPlayers = Math.Min(custom, RoomNames.MaxCustomPlayers);
            int want = gameSetupContext.MaxPlayers;
            if (want <= 0) return;
            PlayerLimit.Pending = want;
            PlayerLimit.RaiseFusionCapacity(want);
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] StartGame patch: " + e.Message); }
    }

    /// <summary>
    /// Room names look like "&lt;id&gt;_&lt;player&gt;'s room": the id before the first '_' keeps the session unique and
    /// backs join-by-code, so only the display part after it is replaced.
    /// </summary>
    internal static void RenameRoom(GameSetupContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.RoomName)) return;
        int cut = ctx.RoomName.IndexOf('_');
        string prefix = cut >= 0 ? ctx.RoomName.Substring(0, cut + 1) : "";
        string display = cut >= 0 ? ctx.RoomName.Substring(cut + 1) : ctx.RoomName;
        string styled = RoomNames.Style(RoomNames.DisplayName(display));
        if (styled == display) return;
        ctx.RoomName = prefix + styled;
    }
}

[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class FusionStartGamePatch
{
    /// <summary>Last chance to rename: the session name Fusion/Photon will publish ("&lt;id&gt;_&lt;name&gt;").</summary>
    private static void RenameSession(StartGameArgs args)
    {
        string name = args.SessionName;
        if (string.IsNullOrEmpty(name))
        {
            // The name may come from a generator callback instead: wrap it so its result gets styled.
            var original = args.SessionNameGenerator;
            if (original == null) return;
            args.SessionNameGenerator = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Func<string>>(
                new Func<string>(() =>
                {
                    string generated = original.Invoke();
                    string styled = RoomNamePatch.Restyle(generated);
                    return styled;
                }));
            return;
        }
        string renamed = RoomNamePatch.Restyle(name);
        if (renamed == name) return;
        args.SessionName = renamed;
    }

    private static void Prefix(StartGameArgs args)
    {
        try
        {
            if (args.GameMode != GameMode.Client) RenameSession(args);
            if (!ModConfig.ExpandLobbySize.Value || args.GameMode == GameMode.Client || PlayerLimit.Pending <= 0) return;
            PlayerLimit.RaiseFusionCapacity(PlayerLimit.Pending);
            // Required: without this the room opens at 4 even with the config raised (verified in-game). Reading the
            // value back through the interop wrapper shows 0, but the write does reach Fusion.
            args.PlayerCount = new Il2CppSystem.Nullable<int>(PlayerLimit.Pending);
            Plugin.Logger.LogInfo($"[Lobby] Fusion StartGame PlayerCount -> {PlayerLimit.Pending}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] Fusion StartGame patch: " + e.Message); }
        finally { PlayerLimit.Pending = 0; }
    }
}

/// <summary>Server-name text and colour styles (rich-text tags, which the game's server list renders).</summary>
internal static class RoomNames
{
    public const int MaxCustomPlayers = 100; // above ~32 is untested: Photon room limits and host bandwidth apply
    public static readonly string[] Styles = { "None", "Gold", "Cyan", "Pink", "Green", "Red", "Purple", "Rainbow", "Sunset", "Ocean" };

    private static readonly System.Collections.Generic.Dictionary<string, string> Solid = new()
    {
        ["Gold"] = "#F2B33D", ["Cyan"] = "#4FD8F7", ["Pink"] = "#FF6FB5", ["Green"] = "#5BE38A", ["Red"] = "#FF5A4E", ["Purple"] = "#B48CFF",
    };

    private static readonly System.Collections.Generic.Dictionary<string, (UnityEngine.Color a, UnityEngine.Color b)> Gradients = new()
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
        string custom = Escape(ModConfig.CustomRoomName.Value).Trim();
        if (custom.Length > 40) custom = custom.Substring(0, 40);
        if (custom.Length == 0) return gameDefault;
        if (Features.Public.Filter.NameFilter.IsBlocked(custom, out string why))
        {
            if (_refusedLogged != custom)
            {
                _refusedLogged = custom;
                Plugin.Logger.LogWarning($"[Lobby] Custom server name not used: {why}");
            }
            return gameDefault;
        }
        return custom;
    }

    private static string _refusedLogged;

    /// <summary>
    /// Neutralises markup the user typed: '&lt;' and '&gt;' become look-alike angle quotes, so "&lt;size=0&gt;" or
    /// "&lt;alpha=#00&gt;" can neither hide letters from the word filter nor restyle everyone's server list. Only the
    /// mod's own style tags (<see cref="Style"/>) are added afterwards.
    /// </summary>
    public static string Escape(string text) =>
        string.IsNullOrEmpty(text) ? (text ?? "") : text.Replace('<', '‹').Replace('>', '›');

    public static string Style(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        string style = ModConfig.RoomNameStyle.Value ?? "None";
        string result = name;
        if (Solid.TryGetValue(style, out var hex)) result = $"<color={hex}>{name}</color>";
        else if (style == "Rainbow") result = PerChar(name, i => UnityEngine.Color.HSVToRGB((i * 0.09f) % 1f, 0.7f, 1f));
        else if (Gradients.TryGetValue(style, out var g))
            result = PerChar(name, i => UnityEngine.Color.Lerp(g.a, g.b, name.Length <= 1 ? 0f : i / (float)(name.Length - 1)));
        if (ModConfig.RoomNameBold.Value) result = $"<b>{result}</b>";
        return result;
    }

    /// <summary>Colours the text in up to 8 runs, so a gradient costs ~150 characters instead of ~20 per letter.</summary>
    private static string PerChar(string text, System.Func<int, UnityEngine.Color> colorAt)
    {
        const int maxRuns = 8;
        int run = System.Math.Max(1, (int)System.Math.Ceiling(text.Length / (double)maxRuns));
        var sb = new System.Text.StringBuilder(text.Length + maxRuns * 24);
        for (int i = 0; i < text.Length; i += run)
        {
            var c = colorAt(i);
            string part = text.Substring(i, System.Math.Min(run, text.Length - i));
            sb.Append($"<color=#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}>{part}</color>");
        }
        return sb.ToString();
    }
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
        if (string.IsNullOrEmpty(full) || full.Contains("<color=") || full.Contains("<b>")) return full;
        int cut = full.IndexOf('_');
        string prefix = cut >= 0 ? full.Substring(0, cut + 1) : "";
        string display = cut >= 0 ? full.Substring(cut + 1) : full;
        return prefix + RoomNames.Style(RoomNames.DisplayName(display));
    }

    private static void Postfix(ref string __result)
    {
        try
        {
            string renamed = Restyle(__result);
            if (renamed == __result) return;
            __result = renamed;
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] Room rename failed: " + e.Message); }
    }
}
