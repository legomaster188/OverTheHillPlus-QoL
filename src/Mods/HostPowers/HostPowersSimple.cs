using OverTheHillPlus.Features.Cheats.Players;
using OverTheHillPlus.Features.Cheats.Teleport;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.HostPowers;

/// <summary>
/// Host Powers' everyday rows, first on its MODS page (the old CHEATS menu's Players page): everyone to you or to your
/// map pin, you to your pin, and Go to / Bring for each player. The rest is in its Advanced fold.
/// </summary>
internal static class HostPowersSimple
{
    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (PlayersFeature.SummonIncludesMe == null) return;
        SimpleMenu.TitledPage("Players", PlayersPage, true);
    }

    private static void PlayersPage()
    {
        string blocked = PlayerMoves.NotAllowedReason;
        bool host = blocked == null;
        bool busy = PlayerMoves.Busy;
        if (!host) SimpleMenu.Note("Moving other players works solo or when you host.");
        else if (busy) SimpleMenu.Note($"Moving players: {PlayerMoves.BusyLabel} ({PlayerMoves.BusyLeft} left)...");

        bool pin = TeleportCore.TryGetPinCached(out _);
        SimpleMenu.Button("Everyone to me", null, "Lines every player up behind you, on safe ground.",
            () => PlayersFeature.Report(PlayerMoves.EveryoneToMe()), host && !busy);
        SimpleMenu.Button("Everyone to my map pin", pin ? null : "No pin", "Place a pin on the map first.",
            () => PlayersFeature.Report(PlayerMoves.SummonAllToPin(PlayersFeature.SummonIncludesMe.Value)), host && !busy && pin);
        SimpleMenu.Button("Me to my map pin", pin ? null : "No pin", "Teleports your car to the pin on your map.",
            () => Menu.SetStatus(TeleportCore.TeleportToPin()), pin && TeleportCore.NotAllowedReason == null);

        var players = HostTools.Players;
        int others = 0;
        foreach (var p in players) if (!p.IsMe) others++;
        if (others == 0) return;
        SimpleMenu.Heading("Players");
        bool can = HostTools.Available;
        foreach (var p in players)
        {
            if (p.IsMe) continue;
            var target = p;
            SimpleMenu.Actions(p.Name,
                ("Go to", () => Menu.SetStatus(HostTools.GoTo(target)), can),
                ("Bring", () => Menu.SetStatus(HostTools.BringToMe(target)), can));
        }
    }
}
