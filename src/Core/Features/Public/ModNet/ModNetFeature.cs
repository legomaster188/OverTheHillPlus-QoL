using System;
using System.Linq;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features.Public.PlayerList;

namespace OverTheHillPlus.Features.Public.ModNet;

/// <summary>Settings, per-frame pump, join/leave hooks and the status card of <see cref="ModNet"/>.</summary>
internal sealed class ModNetFeature : IFeature
{
    internal const string Section = "Mod Network";

    public void Register()
    {
        // Off by default until verified in-game: while a modded session runs it hooks the game's network receive
        // handler (which every join message passes through).
        ModNet.Enabled = CoreConfig.File.Bind(Section, "Enabled", false,
            "Let players who run this mod exchange small messages (unstuck requests, big-lobby names) through the host. " +
            "Vanilla players are never sent anything. As host it applies to servers started after enabling. Experimental.");
        Registry.OnTick(ModNet.Tick);
        Registry.Section(Registry.PlusMode, "Network", DrawCard, 50);
        ServerSettings.AddRow(ServerSettingRow.Toggle("modnet", "Mod Network", 60, "core", () => ModNet.Enabled));
        PatchJoinLeave();
    }

    /// <summary>Patched by hand, each in its own try/catch: if a callback was renamed the rest still works.</summary>
    private static void PatchJoinLeave()
    {
        var h = new Harmony(Core.CorePlugin.Guid + ".modnet.players");
        void Try(string method, string patch)
        {
            try
            {
                var target = AccessTools.Method(typeof(OverlandNetworkRunner), method);
                if (target == null) { Core.CorePlugin.Logger?.LogWarning($"[ModNet] {method} not found; relying on the periodic check"); return; }
                h.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ModNetFeature), patch)));
            }
            catch (Exception e) { DebugLog.Error("core", e, "ModNetFeature.PatchJoinLeave"); Core.CorePlugin.Logger?.LogWarning($"[ModNet] Could not patch {method}: {e.Message}"); }
        }
        Try("Fusion_INetworkRunnerCallbacks_OnPlayerJoined", nameof(JoinedPrefix));
        Try("Fusion_INetworkRunnerCallbacks_OnPlayerLeft", nameof(LeftPrefix));
    }

    private static void JoinedPrefix(PlayerRef player) => ModNet.PlayerJoinedOrLeft(player, true);
    private static void LeftPrefix(PlayerRef player) => ModNet.PlayerJoinedOrLeft(player, false);

    private static void DrawCard()
    {
        Menu.Card("Mod network", "Players who run this mod can talk to each other through the host: unstuck requests, big-lobby names, chat, ping notices. Vanilla players are never sent anything.", () =>
        {
            Menu.Switch(ModNet.Enabled, "Mod network (experimental)", "Host: applies to servers started after enabling. Guest: talks to modded hosts only", true);
            Menu.KeyValue("Status", ModNet.Describe());
            if (ModNet.Enabled.Value && ModNet.IsHost && ModNet.PeerCount > 0)
            {
                var sp = SafeSessionPlayers();
                int shown = 0;
                foreach (var p in ModNet.Peers.OrderBy(p => p.Ref.PlayerId))
                {
                    if (shown++ >= 8) { Menu.Small($"+{ModNet.PeerCount - 8} more"); break; }
                    string name;
                    try { name = PlayerListRoster.NameOf(sp, p.Ref); } catch (Exception ex) { DebugLog.Error("core", ex, "ModNetFeature.DrawCard"); name = "Player " + p.Ref.PlayerId; }
                    // Only the channels this game runs, by name: a channel it doesn't know is counted, never shown by number.
                    string channels = ModNetAdvert.ChannelSummary(p.Channels.Keys, ModNet.KnownChannelName);
                    Menu.KeyValue(Trim(name, 22), $"mod {p.ModVersion} · {channels}" + (p.Dropped > 0 ? $" · {p.Dropped} dropped" : ""));
                }
            }
            int dropped = 0;
            foreach (ModNet.DropReason r in Enum.GetValues(typeof(ModNet.DropReason))) dropped += ModNet.Dropped(r);
            if (ModNet.Sent + ModNet.Received > 0 || dropped > 0)
                Menu.KeyValue("Messages", $"{ModNet.Sent} sent · {ModNet.Received} received" + (dropped > 0 ? $" · {dropped} dropped" : ""));
            if (ModNetTransport.LastError != null) Menu.KeyValue("Problem", ModNetTransport.LastError);
        });
    }

    private static OverlandSessionPlayers SafeSessionPlayers()
    {
        try { return OverlandSessionPlayers.Instance; } catch (Exception ex) { DebugLog.Error("core", ex, "ModNetFeature.SafeSessionPlayers"); return null; }
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}

/// <summary>Adds the mod network's session property when hosting (runs alongside the other StartGame prefixes).</summary>
[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class ModNetStartGamePatch
{
    private static void Prefix(StartGameArgs args) => ModNet.OnStartGame(args);
}
