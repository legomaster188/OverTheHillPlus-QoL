using System;
using System.IO;
using System.Text.Json;
using BepInEx;
using OverTheHillPlus.ServerBrowser;

namespace OverTheHillPlus.Features.Public.Rejoin;

/// <summary>
/// A server the player chose to join after a restart (the "this server needs mods" prompt: Install &amp; join, or
/// Switch on &amp; restart). Kept apart from <see cref="LastServerStore"/>, which stays the server you were really in.
/// Saved to BepInEx/config/OverTheHillPlus.pendingjoin.json; RejoinCore reads it once at the next start and deletes it.
/// </summary>
internal static class PendingJoinStore
{
    private sealed class Saved
    {
        public LastServerStore.Entry Server { get; set; }
        /// <summary>When it was saved (unix seconds).</summary>
        public long At { get; set; }
    }

    private static string FilePath => Path.Combine(Paths.ConfigPath, "OverTheHillPlus.pendingjoin.json");

    public static void Save(LastServerStore.Entry server)
    {
        if (server == null || string.IsNullOrEmpty(server.SessionName)) return;
        try
        {
            // tmp then replace: a crash while saving never leaves a cut file (it would be read as damaged and dropped).
            Core.Shared.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved { Server = server, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
        }
        catch (Exception e) { DebugLog.Error("serverbrowser", e, "PendingJoinStore.Save"); ServerBrowserPlugin.Logger.LogWarning("[Rejoin] could not save the server to join after the restart: " + e.Message); }
    }

    /// <summary>The saved server when it is at most <paramref name="maxAgeSeconds"/> old; an older or damaged file is deleted.</summary>
    public static LastServerStore.Entry Load(long maxAgeSeconds)
    {
        try
        {
            Core.Shared.AtomicFile.Recover(FilePath);
            if (!File.Exists(FilePath)) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (saved?.At ?? 0);
            if (saved?.Server != null && !string.IsNullOrEmpty(saved.Server.SessionName) && age >= 0 && age <= maxAgeSeconds)
                return saved.Server;
        }
        catch (Exception e) { DebugLog.Error("serverbrowser", e, "PendingJoinStore.Load"); ServerBrowserPlugin.Logger.LogWarning("[Rejoin] could not read the server to join after the restart: " + e.Message); }
        Clear();
        return null;
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception e) { DebugLog.Error("serverbrowser", e, "PendingJoinStore.Clear"); ServerBrowserPlugin.Logger.LogWarning("[Rejoin] could not delete the server to join after the restart: " + e.Message); }
    }
}
