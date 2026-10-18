namespace OverTheHillPlus;

/// <summary>
/// Extra troubleshooting detail for developers: internal counters and tables on mod pages, test buttons and verbose
/// log lines. Off for players; a developer's own troubleshooting mod switches it on when it loads. Mods check it where
/// they would otherwise show only what a player needs.
/// </summary>
internal static class DevDetails
{
    public static bool On { get; set; }
}
