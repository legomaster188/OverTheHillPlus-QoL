using System;
using System.Linq;
using OverTheHillPlus.ServerBrowser;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>Server browser v2: menu cards in PLUS > Servers and a quick bar drawn over the game's browser.</summary>
internal sealed class ServerBrowserPlusFeature : IFeature
{
    private const float BarW = 600f, BarH = 74f;

    private static GUIStyle _center, _title;
    private static bool _dragging;
    private static Vector2 _dragOffset;
    private static bool _barHover;
    private static EventSystem _heldEventSystem;
    private static bool _loggedGuiError;
    private static int _shownFavs = 8;

    public void Register()
    {
        BrowserPlus.Bind(ModConfig.File);
        Registry.Section(Registry.PlusMode, "Servers", DrawMenu, -10);
        Registry.OnTick(Tick);
        Registry.OnGui(DrawBar);
    }

    private static bool MenuOpen => Time.frameCount - Menu.DrawnFrame <= 2;

    // ---------------------------------------------------------------- menu

    private static void DrawMenu()
    {
        Menu.Card("Browser v2", "Favourites pinned to the top, extra filters, age sort and quick join", () =>
        {
            bool open = BrowserPlus.BrowserAlive;
            Menu.Banner(open
                    ? $"Server browser open: {BrowserPlus.Shown} shown" + (BrowserPlus.Hidden > 0 ? $", {BrowserPlus.Hidden} hidden by PLUS filters" : "")
                    : "Open Play online > server browser to use quick join. Filters apply as soon as it opens.",
                open ? Theme.Good : Theme.Info);

            var sel = open ? BrowserPlus.SelectedSession() : null;
            string selName = sel == null ? null : Favourites.PlainName(Favourites.DisplayNameOf(sel));
            bool selFav = sel != null && Favourites.IsFavourite(sel);
            Menu.Chips(2,
                ("Quick join best server", (Action)(() => BrowserPlus.QuickJoin()), open),
                (sel == null ? "Star selected server" : (selFav ? "Unstar " : "Star ") + Short(selName, 22),
                    (Action)(() => BrowserPlus.ToggleFavourite(sel)), sel != null));
            Menu.Small($"Quick join picks an open public server on your game version with a free slot: favourites first, then the busiest. " +
                       $"In the browser, {BrowserPlus.FavouriteKey.Value} stars the selected server.");

            Menu.Switch(BrowserPlus.PinFavourites, "Pin favourites to the top", "Starred servers always lead the list", true);
            Menu.Switch(BrowserPlus.HideIncompatible, "Hide other game versions", "Servers you can't join anyway", true);
            Menu.Switch(BrowserPlus.OnlyFreeSlots, "Only servers with free slots", "Open and not full", true);
            Menu.Switch(BrowserPlus.OnlyBigLobbies, "Only big lobbies", "Modded rooms with more than 4 player slots", true);
            Menu.Switch(BrowserPlus.OnlyFavourites, "Only favourites", "Show just your starred servers", true);

            Menu.Small("Order by age (after favourites)");
            var age = BrowserPlus.SortByAge.Value;
            Menu.Chips(3,
                (Mark("Game order", age == AgeSort.Off), (Action)(() => BrowserPlus.SortByAge.Value = AgeSort.Off), true),
                (Mark("Newest first", age == AgeSort.NewestFirst), (Action)(() => BrowserPlus.SortByAge.Value = AgeSort.NewestFirst), true),
                (Mark("Oldest first", age == AgeSort.OldestFirst), (Action)(() => BrowserPlus.SortByAge.Value = AgeSort.OldestFirst), true));

            Menu.Switch(BrowserPlus.ShowFavTag, "FAV tag on favourites", null, true);
            Menu.Switch(BrowserPlus.ShowBigTag, "BIG tag on big lobbies", "Shows the lobby size, e.g. BIG 8", true);
            Menu.Switch(BrowserPlus.ShowBar, "Quick bar in the browser", "Filters, star and quick join on top of the game's browser (drag to move)", true);
            if (BrowserPlus.BarX.Value >= 0f || BrowserPlus.BarY.Value >= 0f)
                Menu.Chips(1, ("Reset quick bar position", (Action)(() => { BrowserPlus.BarX.Value = -1f; BrowserPlus.BarY.Value = -1f; }), true));
        });

        Menu.Card("Favourite servers", $"{Favourites.Count} starred · remembered by host/room name", () =>
        {
            var favs = Favourites.All().ToList();
            if (favs.Count == 0)
            {
                Menu.Small("No favourites yet. Star a server from the list below, the quick bar, or with the hotkey in the browser.");
            }
            else
            {
                foreach (var e in favs.Take(_shownFavs))
                {
                    var row = Menu.NextRow(24f);
                    GUI.Label(new Rect(row.x + 4f, row.y, row.width - 250f, 24f), Short(e.Name, 40), Theme.Label);
                    GUI.Label(new Rect(row.xMax - 240f, row.y, 160f, 24f), Favourites.LastSeenText(e), Theme.Small);
                    string name = e.Name;
                    if (Menu.ChipButton(new Rect(row.xMax - 74f, row.y, 74f, 24f), "Remove", true))
                    {
                        Favourites.Remove(name);
                        BrowserPlus.RequestRefresh();
                        Menu.SetStatus($"Removed {name} from favourites");
                    }
                }
                if (favs.Count > _shownFavs) Menu.Chips(1, ($"Show all {favs.Count}", (Action)(() => _shownFavs = 1000), true));
            }
        });

        Menu.Card("Servers online now", "Favourites, then busiest · star from here", () =>
        {
            var all = OnlineNow();
            if (all.Count == 0)
            {
                Menu.Small("No server list yet: open the game's server browser.");
                return;
            }
            foreach (var (info, name, fav, status) in all.Take(12))
            {
                var s = info.FusionSessionInfo;
                if (s == null) continue;
                var row = Menu.NextRow(24f);
                Theme.Fill(new Rect(row.x, row.y + 4f, 4f, 16f), RowStyler.ColorFor(status), 2);
                GUI.Label(new Rect(row.x + 12f, row.y, row.width - 230f, 24f), Short(name, 36), Theme.Label);
                string size = $"{s.PlayerCount}/{s.MaxPlayers}" + (BrowserPlus.IsBig(s) ? "  <color=#4FD8F7>BIG</color>" : "");
                GUI.Label(new Rect(row.xMax - 214f, row.y, 120f, 24f), size, Theme.Small);
                if (Menu.ChipButton(new Rect(row.xMax - 84f, row.y, 84f, 24f), fav ? "Unstar" : "Star", true))
                    BrowserPlus.ToggleFavourite(info);
            }
            if (all.Count > 12) Menu.Small($"+{all.Count - 12} more in the game's browser.");
        });
    }

    private static System.Collections.Generic.List<(OverlandSessionInfo info, string name, bool fav, ServerStatus status)> _online = new();
    private static float _onlineAt = -10f;
    private static int _onlineFavVersion = -1;

    /// <summary>The session list for the menu, rebuilt at most once a second (or when favourites change).</summary>
    private static System.Collections.Generic.List<(OverlandSessionInfo info, string name, bool fav, ServerStatus status)> OnlineNow()
    {
        if (Time.unscaledTime - _onlineAt < 1f && _onlineFavVersion == Favourites.Version) return _online;
        _onlineAt = Time.unscaledTime;
        _onlineFavVersion = Favourites.Version;
        _online = BrowserPlus.AllSessions().Select(info =>
            {
                ServerStatus status;
                try { status = RowStyler.Classify(info, info.FusionSessionInfo); } catch { status = ServerStatus.Public; }
                return (info, Favourites.PlainName(Favourites.DisplayNameOf(info)), Favourites.IsFavourite(info), status);
            })
            .OrderByDescending(t => t.Item3)
            .ThenByDescending(t => t.info.FusionSessionInfo.PlayerCount)
            .ToList();
        return _online;
    }

    private static string Mark(string label, bool on) => on ? $"[{label}]" : label;

    private static string Short(string s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    // ---------------------------------------------------------------- per frame

    private static void Tick()
    {
        BrowserPlus.Tick();

        // While the pointer is over the quick bar, pause the game's UI input so a click doesn't also hit the
        // browser underneath. Never while the mod menu is open: the menu manages the EventSystem itself.
        bool want = _barHover && BarVisible && !MenuOpen;
        if (want && _heldEventSystem == null)
        {
            var es = EventSystem.current;
            if (es != null && es.enabled) { es.enabled = false; _heldEventSystem = es; }
        }
        else if (!want && _heldEventSystem != null && !MenuOpen)
        {
            try { if (_heldEventSystem != null) _heldEventSystem.enabled = true; } catch { }
            _heldEventSystem = null;
        }
    }

    private static bool BarVisible => BrowserPlus.ShowBar.Value && BrowserPlus.BrowserAlive && !MenuOpen;

    // ---------------------------------------------------------------- quick bar overlay

    private static void DrawBar()
    {
        if (!BarVisible) { _barHover = false; _dragging = false; return; }
        var prevMatrix = GUI.matrix;
        try
        {
            Theme.Ensure();
            EnsureStyles();
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            float sw = Screen.width / scale, sh = Screen.height / scale;

            float x = BrowserPlus.BarX.Value >= 0f ? BrowserPlus.BarX.Value : (sw - BarW) / 2f;
            float y = BrowserPlus.BarY.Value >= 0f ? BrowserPlus.BarY.Value : 12f;
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, sw - BarW));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, sh - BarH));
            var r = new Rect(x, y, BarW, BarH);

            var ev = Event.current;
            if (ev.type == EventType.Repaint) _barHover = r.Contains(ev.mousePosition) || _dragging;
            HandleDrag(new Rect(r.x, r.y, 136f, 40f)); // title column is the drag handle

            Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.92f), 10);
            GUI.Label(new Rect(r.x + 12f, r.y + 6f, 120f, 18f), "PLUS SERVERS", _title);
            string counts = BrowserPlus.Hidden > 0 ? $"{BrowserPlus.Shown} shown · {BrowserPlus.Hidden} hidden" : $"{BrowserPlus.Shown} shown";
            GUI.Label(new Rect(r.x + 12f, r.y + 24f, 130f, 16f), counts, Theme.Small);

            var sel = BrowserPlus.SelectedSession();
            bool selFav = sel != null && Favourites.IsFavourite(sel);
            float bx = r.x + 140f, top = r.y + 6f, gap = 6f;
            float w = (BarW - 140f - 12f - gap * 3f) / 4f;
            if (Menu.ChipButton(new Rect(bx, top, w * 2f + gap, 28f), "Quick join best", true)) BrowserPlus.QuickJoin();
            if (Menu.ChipButton(new Rect(bx + (w + gap) * 2f, top, w * 2f + gap, 28f),
                    selFav ? "Unstar selected" : "Star selected", sel != null))
                BrowserPlus.ToggleFavourite(sel);

            float y2 = r.y + 40f;
            Toggle(new Rect(bx, y2, w, 28f), "Compatible", BrowserPlus.HideIncompatible);
            Toggle(new Rect(bx + (w + gap), y2, w, 28f), "Free slots", BrowserPlus.OnlyFreeSlots);
            Toggle(new Rect(bx + (w + gap) * 2f, y2, w, 28f), "Big only", BrowserPlus.OnlyBigLobbies);
            Toggle(new Rect(bx + (w + gap) * 3f, y2, w, 28f), "Favourites", BrowserPlus.OnlyFavourites);

            // Age sort cycles Off -> Newest -> Oldest from a small chip in the title column.
            var age = BrowserPlus.SortByAge.Value;
            string ageLabel = age == AgeSort.Off ? "Age: off" : age == AgeSort.NewestFirst ? "Age: newest" : "Age: oldest";
            var ageRect = new Rect(r.x + 12f, r.y + 42f, 120f, 26f);
            if (DrawChip(ageRect, ageLabel, age != AgeSort.Off))
                BrowserPlus.SortByAge.Value = (AgeSort)(((int)age + 1) % 3);
        }
        catch (Exception e)
        {
            if (!_loggedGuiError)
            {
                _loggedGuiError = true;
                Plugin.Logger.LogError("[Browser+] quick bar failed (further errors suppressed): " + e);
            }
        }
        finally { GUI.matrix = prevMatrix; }
    }

    private static void Toggle(Rect r, string label, BepInEx.Configuration.ConfigEntry<bool> entry)
    {
        if (DrawChip(r, label, entry.Value)) entry.Value = !entry.Value;
    }

    /// <summary>A pill that shows on/off state (filled with the accent colour when on).</summary>
    private static bool DrawChip(Rect r, string label, bool on)
    {
        bool hover = r.Contains(Event.current.mousePosition);
        Theme.Fill(r, on ? Theme.AccentDim : hover ? Theme.Hex(0x2E3946) : Theme.Hex(0x222B36), 6);
        if (on) Theme.Fill(new Rect(r.x, r.yMax - 3f, r.width, 3f), Theme.Accent, 1);
        _center.normal.textColor = on ? Theme.Text : Theme.Muted;
        GUI.Label(r, label, _center);
        return GUI.Button(r, GUIContent.none, Theme.Invisible);
    }

    private static void HandleDrag(Rect handle)
    {
        var ev = Event.current;
        switch (ev.type)
        {
            case EventType.MouseDown when ev.button == 0 && handle.Contains(ev.mousePosition):
                _dragging = true;
                _dragOffset = ev.mousePosition - new Vector2(handle.x, handle.y);
                ev.Use();
                break;
            case EventType.MouseDrag when _dragging:
                BrowserPlus.BarX.Value = Mathf.Round(ev.mousePosition.x - _dragOffset.x);
                BrowserPlus.BarY.Value = Mathf.Round(ev.mousePosition.y - _dragOffset.y);
                ev.Use();
                break;
            case EventType.MouseUp when _dragging:
                _dragging = false;
                ev.Use();
                break;
        }
    }

    private static void EnsureStyles()
    {
        if (_center != null && _title != null) return;
        _center = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, fontSize = 12, richText = true };
        _title = new GUIStyle(Theme.Section) { richText = true };
        _title.normal.textColor = Theme.Accent;
    }
}
