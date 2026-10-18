using OverTheHillPlus.Features;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.MapHud;

/// <summary>
/// The cards the old F7 menu drew itself that belong here: the driving HUD switch (QoL tab) and "This session"
/// (players in the session, Lobby tab).
/// </summary>
internal sealed class MapHudFeature : IFeature
{
    // Before the feature cards of the same tab, as the old menu drew its own cards first.
    private const int CardOrder = int.MinValue / 2;

    public void Register()
    {
        Registry.Section(Registry.PlusMode, "QoL", DrawHud, CardOrder);
        Registry.Section(Registry.PlusMode, LobbyTab, DrawLobby, CardOrder);
    }

    private static void DrawHud()
    {
        Card("Driving HUD", null, () =>
        {
            Switch(MapHudConfig.ShowHud, $"Driving HUD ({MapHudConfig.HudKey.Value})", "Speed, RPM, gear, power, torque, slip", true);
        });
    }

    private static void DrawLobby()
    {
        Card("This session", null, () =>
        {
            var r = GameState.Runner;
            if (r == null) { Small("Not in a session."); return; }
            int players = 0, max = 0;
            try { players = OverlandSessionPlayers.Instance != null ? OverlandSessionPlayers.Instance.GetPlayerCount() : 0; } catch (System.Exception ex) { DebugLog.Error("maphud", ex, "MapHudFeature.DrawLobby"); }
            try { max = r.SessionInfo != null ? r.SessionInfo.MaxPlayers : 0; } catch (System.Exception ex) { DebugLog.Error("maphud", ex, "MapHudFeature.DrawLobby"); }
            KeyValue("Players", max > 0 ? $"{players} / {max}" : players.ToString());
            KeyValue("You are", GameState.Badge.ToLowerInvariant());
        });
    }
}
