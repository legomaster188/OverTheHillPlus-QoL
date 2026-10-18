using System;
using Fusion;

namespace OverTheHillPlus;

/// <summary>
/// What players are doing that another mod knows about, told through core so mods don't reference each other. Used by
/// the host's AFK rule (Server Utils): a guest who spectates or types a message is still there.
///
/// - Local player: the mod that owns the activity adds a check (the spectate camera, the chat box); others read
///   <see cref="Spectating"/> / <see cref="Chatting"/>.
/// - Other players (host): a mod that sees a guest do something (a chat message) calls <see cref="MarkActive"/>; the
///   mods that care subscribe to <see cref="Active"/>.
/// </summary>
internal static class PlayerActivity
{
    private static readonly CheckList SpectatingChecks = new();
    private static readonly CheckList ChattingChecks = new();

    /// <summary>Adds a check that is true while the local player watches someone else (spectate camera).</summary>
    internal static void AddSpectatingCheck(Func<bool> spectating) => SpectatingChecks.Add(spectating);

    /// <summary>Adds a check that is true while the local player is typing a chat message.</summary>
    internal static void AddChattingCheck(Func<bool> chatting) => ChattingChecks.Add(chatting);

    /// <summary>True while any mod says the local player is spectating.</summary>
    internal static bool Spectating => SpectatingChecks.Any();

    /// <summary>True while any mod says the local player is typing a chat message.</summary>
    internal static bool Chatting => ChattingChecks.Any();

    /// <summary>Raised by <see cref="MarkActive"/>: another player did something only a mod can see.</summary>
    internal static event Action<PlayerRef> Active;

    /// <summary>
    /// Tells the other mods that player <paramref name="player"/> did something only this mod sees (e.g. the host
    /// received their chat message). Every subscriber runs on its own; one that throws doesn't stop the others.
    /// </summary>
    internal static void MarkActive(PlayerRef player)
    {
        var active = Active;
        if (active == null) return;
        foreach (var d in active.GetInvocationList())
        {
            try { ((Action<PlayerRef>)d)(player); }
            catch (Exception e) { DebugLog.Error("core", e, "PlayerActivity.MarkActive"); Core.CorePlugin.Logger?.LogWarning("PlayerActivity.Active: " + e.Message); }
        }
    }
}
