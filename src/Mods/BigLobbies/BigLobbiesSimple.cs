using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.BigLobbies;

/// <summary>Lobby+'s everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class BigLobbiesSimple
{
    private static readonly int[] SizeSteps = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

    internal static void Draw()
    {
        int custom = BigLobbiesConfig.CustomMaxPlayers.Value;
        SimpleMenu.IntChoice("Max players", "Picked for you in Create Server. Above 4 players, everyone should run this mod.",
            custom >= 2 ? custom : BigLobbiesConfig.DefaultMaxPlayers.Value, SizeSteps, v => v.ToString(), SetLobbySize);
        if (OverTheHillPlus.Features.Public.Lobby.PublicListing.Enabled != null)
            SimpleMenu.Toggle(OverTheHillPlus.Features.Public.Lobby.PublicListing.Enabled, "Show to everyone under 4 players",
                "The game hides servers above 4 slots. With this on, your big lobby is listed as a 4-slot room (visible and joinable for everyone, also without mods) until the 4th player joins.");
    }

    private static void SetLobbySize(int n)
    {
        n = UnityEngine.Mathf.Clamp(n, 2, LobbyRules.MaxPlayers);
        BigLobbiesConfig.DefaultMaxPlayers.Value = n;
        if (n > LobbyRules.StockMaxPlayers) BigLobbiesConfig.ExpandLobbySize.Value = true;
        // An exact size typed in Advanced overrides the Create Server choice: keep it in step with this row.
        // (The typed field re-reads the setting by itself when it changes, see BigLobbyFeature.)
        if (BigLobbiesConfig.CustomMaxPlayers.Value >= 2) BigLobbiesConfig.CustomMaxPlayers.Value = n;
    }
}
