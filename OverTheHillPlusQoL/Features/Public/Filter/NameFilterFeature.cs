using System;

namespace OverTheHillPlus.Features.Public.Filter;

/// <summary>Lobby tab card for the swear/slur filter: verdicts for your server name and join message, a tester, a self-check.</summary>
internal sealed class NameFilterFeature : IFeature
{
    private static string _tryText = "";

    public void Register()
    {
        try
        {
            string result = NameFilter.SelfCheck();
            if (result.StartsWith("Failed", StringComparison.Ordinal)) Plugin.Logger.LogWarning("[NameFilter] " + result);
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[NameFilter] start-up self-check: " + e.Message); }

        Registry.Section(Registry.PlusMode, "Lobby", Draw, 50);
    }

    private static void Draw()
    {
        Menu.Card("Name filter", "Server names and join messages with swears or slurs are refused, not censored: the game's default name (or no greeting) is used instead.", () =>
        {
            Verdict("Server name", Multiplayer.RoomNames.Escape(ModConfig.CustomRoomName.Value), "empty (game default)");
            Verdict("Join message", Multiplayer.RoomNames.Escape(ModConfig.JoinMessage.Value), "empty");
            _tryText = Menu.TextRow("Try a name", _tryText, 60);
            if (!string.IsNullOrWhiteSpace(_tryText)) Verdict("Result", _tryText, "");
            Menu.Small($"Sees through l33t spelling, repeated letters, spaced or dotted letters and look-alike characters. {NameFilter.WordCount} words; ordinary words like Scunthorpe, class or cockpit are allowed.");
            Menu.Chips(1, ("Run self-check", () => Menu.SetStatus(NameFilter.SelfCheck()), true));
        });
    }

    private static void Verdict(string label, string text, string emptyText)
    {
        if (string.IsNullOrWhiteSpace(text)) { Menu.KeyValue(label, emptyText); return; }
        Menu.KeyValue(label, NameFilter.IsBlocked(text, out string why)
            ? $"<color=#E5534B>blocked: {why}</color>"
            : "<color=#4CC38A>ok</color>");
    }
}
