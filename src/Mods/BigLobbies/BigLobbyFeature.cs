using System;
using OverTheHillPlus.BigLobbies;
using OverTheHillPlus.Features;
using OverTheHillPlus.UI;
using UnityEngine;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// The lobby size and stability settings of <see cref="BigLobby"/> / <see cref="LobbyRules"/> on the mod's page
/// (the old F7 menu's Lobby tab drew these cards itself), and the fall rescue tick.
/// </summary>
internal sealed class BigLobbyFeature : IFeature
{
    // Before the feature cards of the same tab, as the old menu drew its own cards first.
    private const int CardOrder = -100;

    public void Register()
    {
        Registry.OnTick(BigLobby.Tick);
        Registry.Section(Registry.PlusMode, Menu.LobbyTab, DrawLobbySize, CardOrder);
        Registry.Section(Registry.PlusMode, Menu.LobbyTab, DrawStability, CardOrder + 1);
    }

    // The text typed in "Exact size" and the setting value it stands for: when the setting changes elsewhere (the
    // Simple row, a settings import) the field shows the new value again.
    private static string _customSizeText;
    private static int _customSizeFor;

    private static void DrawLobbySize()
    {
        Card("Lobby size", "Bigger lobbies in the game's Create Server screen. Everyone in a lobby above 4 players should run this mod.", () =>
        {
            Switch(BigLobbiesConfig.ExpandLobbySize, "Up to 16 players", "Adds 2–16 to the Max Players option", true);
            int stored = BigLobbiesConfig.CustomMaxPlayers.Value;
            if (_customSizeText == null || stored != _customSizeFor)
            {
                _customSizeText = stored >= 2 ? stored.ToString() : "";
                _customSizeFor = stored;
            }
            int shown = int.TryParse(_customSizeText, out int sv) && sv >= 2 ? Math.Min(sv, LobbyRules.MaxCustomPlayers) : 0;
            _customSizeText = UI.SimpleMenu.Text("Exact size (typed)", _customSizeText, 3,
                shown >= 2 ? $"Hosting uses {shown} (max {LobbyRules.MaxCustomPlayers}{(shown > 32 ? ", untested above 32" : "")})." : "Empty: the Create Server choice.");
            int typed = int.TryParse(_customSizeText, out int ts) ? ts : 0;
            int wanted = typed >= 2 ? Math.Min(typed, LobbyRules.MaxCustomPlayers) : 0;
            if (wanted != stored) BigLobbiesConfig.CustomMaxPlayers.Value = wanted;
            _customSizeFor = wanted;
            KeyValue("Exact size in use", wanted >= 2 ? $"{wanted}{(wanted > 32 ? " (untested above 32)" : "")}" : "the Create Server choice");
            float v = RawSlider("Preselected size", BigLobbiesConfig.DefaultMaxPlayers.Value, 2f, LobbyRules.MaxPlayers,
                LobbyRules.OptionLabel(BigLobbiesConfig.DefaultMaxPlayers.Value), BigLobbiesConfig.ExpandLobbySize.Value);
            int n = Mathf.RoundToInt(v);
            if (n != BigLobbiesConfig.DefaultMaxPlayers.Value) BigLobbiesConfig.DefaultMaxPlayers.Value = n;
            KeyValue("Network capacity", BigLobbiesConfig.MeasuredCapacity.Value > 0
                ? $"{BigLobbiesConfig.MeasuredCapacity.Value} players (measured)"
                : "not measured yet — join or host any session once");
            Small("Above the network capacity, extra players get fallback spawn spots and placeholder names instead of crashing the lobby.");
        });
    }

    private static void DrawStability()
    {
        Card("Stability", null, () =>
        {
            Switch(BigLobbiesConfig.LobbyGuards, "Lobby crash guards", "Smart spawn points, safe join/leave, finish registering extra players", true);
            Switch(BigLobbiesConfig.FallRescue, "Fall rescue", "Host/solo: cars under the ground or off the map are put back", true);
            KeyValue("Cars rescued", BigLobby.Rescued.ToString());
            if (DevDetails.On)
            {
                KeyValue("Players beyond 4 slots", BigLobby.Overflowed.ToString());
                KeyValue("Moved into free slots", BigLobby.Promoted.ToString());
                KeyValue("Errors absorbed", LobbyRules.GuardedErrors.ToString());
                KeyValue("Spawn fallbacks", LobbyRules.SpawnFallbacks.ToString());
            }
        });
    }
}
