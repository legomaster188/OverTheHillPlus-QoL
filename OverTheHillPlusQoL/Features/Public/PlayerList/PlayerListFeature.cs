using BepInEx.Configuration;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.PlayerList;

/// <summary>
/// Big lobbies, part "see everyone": players 5+ in the pause menu's player list and name tags above every car.
/// </summary>
internal sealed class PlayerListFeature : IFeature
{
    private const string Section = "Big lobby - player list";

    public static ConfigEntry<bool> PauseRows, PauseOverlay, NameTags, HostNames;
    public static ConfigEntry<float> OverlayX;

    private static float _extrasAt;
    private static int _extrasCount;

    public void Register()
    {
        var f = ModConfig.File;
        PauseRows = f.Bind(Section, "PauseMenuRows", true,
            "Add rows for players beyond the game's 4 player slots to the pause menu's player list (display only).");
        PauseOverlay = f.Bind(Section, "PauseMenuSidePanel", false,
            "Also list players beyond the 4 slots in a side panel while the pause menu is open (used automatically if the rows fail).");
        OverlayX = f.Bind(Section, "SidePanelPosition", 1f,
            new ConfigDescription("Side panel position: 0 = left edge, 1 = right edge.", new AcceptableValueRange<float>(0f, 1f)));
        NameTags = f.Bind(Section, "KeepNameTags", true,
            "Keep the name tags above other players' cars up to date in big lobbies, and restore a tag the game failed to create.");
        HostNames = f.Bind(Section, "HostRealNames", true,
            "Host: show the real name of players past the game's 7-name table instead of 'Player N'.");

        PauseRows.SettingChanged += OnRowsSettingChanged;

        Registry.OnTick(() =>
        {
            PauseMenuExtraRows.Tick();
            NameTagKeeper.Tick();
        });
        Registry.OnGui(PauseMenuExtraRows.OnGui);
        Registry.Section(Registry.PlusMode, "Lobby", DrawCard, 40);
    }

    private static void OnRowsSettingChanged(object sender, System.EventArgs e)
    {
        try { PauseMenuExtraRows.Reapply(); }
        catch (System.Exception ex) { PlayerListRoster.LogOnce("reapply", "re-applying pause menu rows: " + ex.Message); }
    }

    private static void DrawCard()
    {
        Menu.Card("Everyone in the list", "Players 5+ in the pause menu and named above their cars", () =>
        {
            if (PauseMenuExtraRows.NativeFailed)
                Menu.Banner("The extra pause menu rows failed this session; the side panel is used instead (see the log).", UI.Theme.Warn);

            Menu.Switch(PauseRows, "Players 5+ in the pause menu",
                "Rows below the game's four, same look. Display only: no kick or voice options for them.", true);
            Menu.Switch(PauseOverlay, "Side panel list",
                "A separate list of players 5+ next to the pause menu.", true);
            if (PauseOverlay.Value || PauseMenuExtraRows.NativeFailed)
                Menu.SliderRow(OverlayX, "Side panel left to right", 0f, 1f, "0.00", "", true, true);
            Menu.Switch(NameTags, "Name tags for everyone",
                "Refreshes tags when a name or colour arrives late, restores a missing tag.", true);
            Menu.Switch(HostNames, "Real names past 7 players",
                "Host only: the game's name table holds 7; you still know the rest.", true);

            if (PlayerListRoster.InMultiplayerSession)
            {
                if (Time.unscaledTime >= _extrasAt)
                {
                    _extrasAt = Time.unscaledTime + 1f;
                    _extrasCount = PlayerListRoster.Extras().Count;
                }
                Menu.KeyValue("Beyond the 4 slots", _extrasCount == 0 ? "nobody" : _extrasCount.ToString());
                Menu.KeyValue("Name tags restored / refreshed", $"{NameTagKeeper.Restored} / {NameTagKeeper.Refreshed}");
            }
            else Menu.Small("Join or host an online session to see who is beyond the 4 slots.");
        });
    }
}
