using OverTheHillPlus.UI;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Bookmarks;

/// <summary>Teleport Bookmarks' MODS page: save the spot you're on, then jump, move, rename, reorder or delete.</summary>
internal static class BookmarksSimple
{
    private static string _name = "";

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (BookmarksConfig.Clearance == null) return;
        var store = BookmarkJump.Store;
        string map = BookmarkJump.MapKey;
        string blocked = BookmarkJump.BlockReason;
        bool inWorld = GameState.Runner != null;

        SimpleMenu.Heading("Save");
        _name = SimpleMenu.Text("Name", _name, BookmarkStore.MaxNameLength,
            "Name for the next bookmark (empty = Spot 1, Spot 2...). Rename also uses this name.");
        string key = KeyName(BookmarksConfig.SaveKey.Value);
        SimpleMenu.Button("Save this spot", inWorld ? BookmarkJump.MapName : "",
            "Saves where your car is now, on this map." + (key != null ? $" Hotkey: {key}." : ""),
            () =>
            {
                Menu.SetStatus(BookmarkJump.SaveHere(_name));
                _name = "";
            }, inWorld && GameState.LocalCar != null);

        var list = store.For(map);
        SimpleMenu.Heading(inWorld ? $"Bookmarks on {BookmarkJump.MapName} ({list.Count})" : "Bookmarks");
        if (blocked != null) SimpleMenu.Note(blocked);
        if (inWorld && list.Count == 0) SimpleMenu.Note("No bookmarks on this map yet.");
        bool canJump = blocked == null;
        // A copy: a button may change the list while it is drawn.
        var items = new BookmarkStore.Bookmark[list.Count];
        for (int i = 0; i < items.Length; i++) items[i] = list[i];
        for (int i = 0; i < items.Length; i++)
        {
            var b = items[i];
            string dist = BookmarkJump.DistanceTo(b);
            string label = dist != null ? $"{b.Name}   ·   {dist}" : b.Name;
            bool first = i == 0;
            SimpleMenu.Actions(label,
                ("Go", () => Menu.SetStatus(BookmarkJump.Jump(b)), canJump),
                ("Move here", () => Menu.SetStatus(BookmarkJump.MoveHere(b)), inWorld && GameState.LocalCar != null),
                ("Rename", () => Rename(map, b), BookmarkStore.CleanName(_name).Length > 0),
                ("Up", () => store.Move(map, b, -1), !first),
                ("Delete", () => { if (store.Remove(map, b)) Menu.SetStatus($"Deleted \"{b.Name}\"."); }, true));
        }

        SimpleMenu.Heading("Travel");
        string backKey = KeyName(BookmarksConfig.BackKey.Value);
        SimpleMenu.Button("Back to where I was", BookmarkJump.HistoryCount > 0 ? $"{BookmarkJump.HistoryCount} step(s)" : "",
            "Before the last jump, on this map." + (backKey != null ? $" Hotkey: {backKey}." : ""),
            () => Menu.SetStatus(BookmarkJump.Back()), canJump && BookmarkJump.HistoryCount > 0);
        int elsewhere = store.CountOtherMaps(map);
        if (elsewhere > 0) SimpleMenu.Note($"{elsewhere} more bookmark(s) on other maps.");
        SimpleMenu.Note("Jumping works solo or as the host. Achievements stay locked for the session after a jump.");
    }

    private static void Rename(string map, BookmarkStore.Bookmark b)
    {
        string old = b.Name;
        if (BookmarkJump.Store.Rename(map, b, _name))
        {
            Menu.SetStatus($"Renamed \"{old}\" to \"{b.Name}\".");
            _name = "";
        }
    }

    private static string KeyName(Key key) => key == Key.None ? null : key.ToString();

    /// <summary>The Advanced fold: how a jump puts the car down.</summary>
    internal static void DrawAdvanced()
    {
        if (BookmarksConfig.Clearance == null) return;
        Menu.Card("Jumping", "How the car is put down at a bookmark", () =>
        {
            Menu.SliderRow(BookmarksConfig.Clearance, "Drop height", 0.3f, 5f, "0.0", " m", true);
            Menu.Switch(BookmarksConfig.StopCar, "Stop the car on arrival", "Zero speed and spin after a jump", true);
            Menu.Small("Hotkeys (SaveKey, BackKey) are set in BepInEx/config/oth.bookmarks.cfg.");
        });
    }
}
