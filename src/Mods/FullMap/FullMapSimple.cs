using OverTheHillPlus.UI;

namespace OverTheHillPlus.FullMap;

/// <summary>Explore Full Map's MODS page: the one-shot reveal and the keep-revealed switch.</summary>
internal static class FullMapSimple
{
    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (FullMapConfig.KeepRevealed == null) return;
        bool ready = MapReveal.MapReady;
        SimpleMenu.Heading("Map");
        SimpleMenu.Button("Reveal whole map", ready ? "Ready" : "No map yet",
            "Clears the fog from the whole map, with the game's own reveal. Only your map: other players keep theirs.",
            () => Menu.SetStatus(MapReveal.RevealNow()), ready);
        SimpleMenu.Toggle(FullMapConfig.KeepRevealed, "Keep map revealed",
            "Reveals the whole map again each time a world loads, also when you join someone else's game.");
        SimpleMenu.Note("The game saves the revealed map with your own map progress. Places still need a visit to count as discovered.");
    }
}
