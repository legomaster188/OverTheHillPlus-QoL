using System;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public.Filter;
using UnityEngine;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.Discord;

/// <summary>
/// Reads what the game is doing every 2 s on the main thread (map, car, session, player count, and the server's name
/// only when allowed) and hands it to <see cref="DiscordClient"/>, which talks to Discord on its own thread.
/// </summary>
internal sealed class DiscordFeature : IFeature
{
    private static float _next;
    private static string _contextKey;
    private static long _startUnix;
    private static bool _loggedRead;

    public void Register()
    {
        Registry.OnTick(Tick);
        Registry.Section(Registry.PlusMode, "Discord", DrawAdvanced);
        CoreBehaviour.OnQuit += DiscordClient.Stop;
        DiscordClient.Start();
    }

    private static void Tick()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 2f;
        PresenceState state;
        try { state = Read(); }
        catch (Exception e)
        {
            DebugLog.Error("discord", e, "DiscordFeature.Tick");
            if (!_loggedRead) { _loggedRead = true; DiscordPlugin.Logger.LogInfo("[Discord] Reading the game state failed: " + e.Message); }
            state = new PresenceState();
        }
        // The elapsed timer restarts when you change map or go solo <-> session.
        string context = state.InWorld + "|" + state.Map + "|" + state.InSession;
        if (context != _contextKey)
        {
            _contextKey = context;
            _startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        state.StartUnix = _startUnix;
        DiscordClient.Publish(state, DiscordConfig.Options(), DiscordConfig.ShowPresence.Value, (DiscordConfig.ApplicationId.Value ?? "").Trim());
    }

    private static PresenceState Read()
    {
        var s = new PresenceState();
        var runner = GameState.Runner;
        AreaInfo area = null;
        try { area = SceneReferences.Instance?.AreaInfo; } catch (Exception ex) { DebugLog.Error("discord", ex, "DiscordFeature.Read"); }
        s.InWorld = runner != null && area != null;
        if (!s.InWorld) return s;
        s.Map = Localized(area.Name);
        try
        {
            foreach (var m in Core.Manager.ModManager.All)
                if (m != null && Core.Manager.ModManager.IsEnabled(m.Id)) s.ModCount++;
        }
        catch { }
        try
        {
            var me = GameState.LocalPlayer;
            var info = me == null ? null : OverlandNetworkRunner.Instance?.GetCarInfo(me.Value);
            // A car a stock game doesn't have (core's NonStockCars) shows the name its mod allows: the stock car it
            // stands in for, or "a car".
            if (info != null) s.Car = Core.Shared.NonStockCars.ShownName(info.ItemId, Localized(info.Name), out s.CarGeneric);
            // The framework's modded cars ("cars.<id>") with a public name get their picture; the others keep the logo.
            string owner = info == null || s.CarGeneric ? null : Core.Shared.NonStockCars.OwnerOf(info.ItemId);
            if (owner != null && owner.StartsWith("cars.", StringComparison.Ordinal)) s.CarImage = owner.Substring(5);
        }
        catch (Exception ex) { DebugLog.Error("discord", ex, "DiscordFeature.Read"); }
        try
        {
            s.InSession = runner.IsRunning && !runner.IsSinglePlayer;
            if (s.InSession)
            {
                var session = runner.SessionInfo;
                if (session != null && session.IsValid)
                {
                    s.Players = session.PlayerCount;
                    s.MaxPlayers = session.MaxPlayers;
                    if (DiscordConfig.ShowServerName.Value) s.ServerName = ServerName(session);
                }
                try { s.Hosting = runner.IsServer; } catch { }
                if (DiscordConfig.ShowRoomCode.Value)
                    try { s.RoomCode = OverlandNetworkRunner.Instance?.GameSetupContext?.RoomCode; } catch { }
            }
        }
        catch (Exception ex) { DebugLog.Error("discord", ex, "DiscordFeature.Read"); }
        return s;
    }

    /// <summary>The room's name as the browser shows it, or null when it fails the word filter.</summary>
    private static string ServerName(Fusion.SessionInfo session)
    {
        string name = null;
        try { name = new OverlandSessionInfo(session).RoomDisplayName; } catch (Exception ex) { DebugLog.Error("discord", ex, "DiscordFeature.ServerName"); }
        name = DiscordPresence.PlainName(name);
        if (name.Length == 0 || NameFilter.IsBlockedStrict(name, out _)) return null;
        return name;
    }

    private static string Localized(UnityEngine.Localization.LocalizedString s)
    {
        try
        {
            string v = s?.GetLocalizedString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch (Exception ex) { DebugLog.Error("discord", ex, "DiscordFeature.Localized"); return null; }
    }

    private static void DrawAdvanced()
    {
        Card("Discord application", "Discord shows its name as \"Playing ...\"", () =>
        {
            string id = TextRow("Application id", DiscordConfig.ApplicationId.Value, 24);
            if (id != DiscordConfig.ApplicationId.Value) DiscordConfig.ApplicationId.Value = id.Trim();
            Small(DiscordPresence.ValidClientId(DiscordConfig.ApplicationId.Value.Trim())
                ? "Default: the Over The Hill Mods application. Use your own from discord.com/developers to change the name."
                : "Not a valid id: 17-20 digits.");
            string key = TextRow("Large image key", DiscordConfig.LargeImageKey.Value, 64);
            if (key != DiscordConfig.LargeImageKey.Value) DiscordConfig.LargeImageKey.Value = key.Trim();
            string text = TextRow("Large image text", DiscordConfig.LargeText.Value, 64);
            if (text != DiscordConfig.LargeText.Value) DiscordConfig.LargeText.Value = text;
            Small("Empty = the mods' logo. An art asset key of the application or an https image URL works too; \"none\" = text only.");
        });
    }
}
