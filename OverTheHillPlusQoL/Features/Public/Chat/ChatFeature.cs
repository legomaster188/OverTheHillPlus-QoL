using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Fusion;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Features.Public.Chat;

/// <summary>
/// Text chat for players who run this mod (see <see cref="ChatNet"/> for the messages and <see cref="ChatOverlay"/>
/// for the on-screen box). Settings and the PLUS > Chat tab.
/// </summary>
internal sealed class ChatFeature : IFeature
{
    internal const string Section = "Chat";
    internal const string Tab = "Chat";

    internal static ConfigEntry<bool> Enabled, ShowOverlay, RelayForGuests;
    internal static ConfigEntry<Key> ChatKey, ChatAltKey;
    internal static ConfigEntry<float> FadeSeconds, TextScale;
    internal static ConfigEntry<int> MaxLines, Position;
    internal static ConfigEntry<string> MutedNames;

    private static string _menuDraft = "";

    public void Register()
    {
        var cfg = ModConfig.File;
        Enabled = cfg.Bind(Section, "Enabled", true,
            "Text chat with other players who run this mod (needs the mod network on, and a host that runs it). Vanilla players never see it.");
        ChatKey = cfg.Bind(Section, "ChatKey", Key.T,
            "Opens the chat box while driving in a multiplayer session. Enter sends, Escape cancels. None = no key.");
        ChatAltKey = cfg.Bind(Section, "ChatAltKey", Key.None,
            "A second key that opens the chat box. Enter works, but the game also reads Enter as 'interact' while driving.");
        ShowOverlay = cfg.Bind(Section, "ShowOverlay", true, "Show new messages on screen (they fade out after a while).");
        FadeSeconds = cfg.Bind(Section, "FadeSeconds", 12f, "Seconds a message stays on screen before it fades out.");
        MaxLines = cfg.Bind(Section, "MaxLines", 6, "How many messages show on screen at once (twice as many while the chat box is open).");
        TextScale = cfg.Bind(Section, "TextScale", 1f, "Size of the on-screen chat.");
        Position = cfg.Bind(Section, "Position", (int)ChatCorner.BottomLeft, "Where the chat sits: 0 = bottom left, 1 = middle left, 2 = top left.");
        RelayForGuests = cfg.Bind(Section, "RelayForGuests", true,
            "When hosting with your own chat off: still pass your modded guests' messages between them (you don't see them).");
        MutedNames = cfg.Bind(Section, "MutedNames", "", "Players whose messages are hidden, by name, separated by |.");

        ChatNet.RegisterChannel();
        Registry.OnTick(ChatOverlay.Tick);
        Registry.OnGui(ChatOverlay.OnGui);
        Registry.Section(Registry.PlusMode, Tab, DrawChat, 0);
        Registry.Section(Registry.PlusMode, Tab, DrawOverlay, 10);
        Registry.Section(Registry.PlusMode, Tab, DrawMute, 20);
        Registry.Section(Registry.PlusMode, Tab, DrawRecent, 30);
    }

    // ---------------------------------------------------------------- menu

    private static void DrawChat()
    {
        Menu.Card("Chat", "Talk to players who run this mod, through the host. Vanilla players never see it, and every message must pass the word filter (a message that fails is not sent, nothing is censored).", () =>
        {
            Menu.Switch(Enabled, "Text chat", "Off: you neither see nor send messages", true);
            Menu.Switch(Net.Enabled, "Mod network", "Chat travels over it (Network tab). Hosts: start the server after enabling", true);
            Menu.KeyValue("Status", ChatNet.Describe());
            Menu.KeyValue("Open the chat box", KeysText());
            Menu.Switch(RelayForGuests, "Carry guests' chat while mine is off (host)", "Your modded guests can still talk to each other", Net.IsHost || !Net.IsGuest);

            _menuDraft = Menu.TextRow("Message", _menuDraft, ChatNet.MaxChars);
            bool canSend = ChatNet.CanSend(out string why);
            Menu.Chips(2,
                ("Send", () =>
                {
                    string r = ChatNet.Send(_menuDraft);
                    if (r == null) { _menuDraft = ""; Menu.SetStatus("Message sent"); }
                    else Menu.SetStatus(r);
                }, canSend && !string.IsNullOrWhiteSpace(_menuDraft)),
                ("Clear history", ChatNet.ClearHistory, ChatNet.History.Count > 0));
            if (!canSend && Enabled.Value) Menu.Small("Can't send right now: " + why + ".");
            if (ChatNet.SentCount + ChatNet.ReceivedCount + ChatNet.BlockedCount > 0)
                Menu.KeyValue("Messages", $"{ChatNet.SentCount} sent · {ChatNet.ReceivedCount} received"
                    + (ChatNet.BlockedCount > 0 ? $" · {ChatNet.BlockedCount} blocked by the filter" : "")
                    + (ChatNet.MutedCount > 0 ? $" · {ChatNet.MutedCount} muted" : ""));
        });
    }

    private static string KeysText()
    {
        var a = ChatKey.Value;
        var b = ChatAltKey.Value;
        if (a == Key.None && b == Key.None) return "no key (set one under Keybinds)";
        string s = a != Key.None ? KeybindEditor.KeyName(a) : "";
        if (b != Key.None) s += (s.Length > 0 ? " or " : "") + KeybindEditor.KeyName(b);
        return s + " while driving";
    }

    private static readonly string[] PositionNames = { "Bottom left", "Middle left", "Top left" };

    private static void DrawOverlay()
    {
        Menu.Card("Chat on screen", "New messages show for a while, then fade; all recent ones show again while the chat box is open.", () =>
        {
            Menu.Switch(ShowOverlay, "Show messages on screen", null, Enabled.Value);
            Menu.SliderRow(FadeSeconds, "Fade after", 3f, 60f, "0", " s", Enabled.Value && ShowOverlay.Value);
            float lines = Menu.RawSlider("Lines on screen", MaxLines.Value, 1f, 12f, MaxLines.Value.ToString(), Enabled.Value && ShowOverlay.Value,
                MaxLines.Description?.Description, MaxLines.Description?.Description);
            int li = Mathf.Clamp(Mathf.RoundToInt(lines), 1, 12);
            if (li != MaxLines.Value) MaxLines.Value = li;
            Menu.SliderRow(TextScale, "Text size", 0.7f, 1.6f, "0.00", "", Enabled.Value, percent: true);
            int pos = Mathf.Clamp(Position.Value, 0, PositionNames.Length - 1);
            var chips = new (string, Action, bool)[PositionNames.Length];
            for (int i = 0; i < PositionNames.Length; i++)
            {
                int k = i;
                chips[i] = ((k == pos ? "• " : "") + PositionNames[k], () => Position.Value = k, k != pos);
            }
            Menu.Chips(PositionNames.Length, chips);
        });
    }

    private static void DrawMute()
    {
        Menu.Card("Mute", "Hide a player's messages (only for you; they are not told). Saved by name.", () =>
        {
            var others = OtherPlayers();
            if (others.Count == 0) Menu.Small("Nobody else is in the session.");
            else
            {
                var chips = new List<(string, Action, bool)>();
                foreach (var name in others.Take(16))
                {
                    string n = name;
                    bool muted = ChatNet.IsMuted(n);
                    chips.Add(((muted ? "Unmute " : "Mute ") + Trim(ChatNet.NoTags(n), 20), () =>
                    {
                        ChatNet.SetMuted(n, !muted);
                        Menu.SetStatus((muted ? "Unmuted " : "Muted ") + n);
                    }, true));
                }
                Menu.Chips(2, chips.ToArray());
                if (others.Count > 16) Menu.Small($"+{others.Count - 16} more players not listed.");
            }
            int count = ChatNet.MutedNamesCount;
            if (count > 0)
            {
                Menu.KeyValue("Muted", Trim(ChatNet.NoTags(string.Join(", ", ChatNet.MutedNames)), 80));
                Menu.Chips(1, ($"Unmute all ({count})", () => { ChatNet.UnmuteAll(); Menu.SetStatus("Everyone unmuted"); }, true));
            }
        });
    }

    private static void DrawRecent()
    {
        Menu.Card("Recent messages", null, () =>
        {
            var h = ChatNet.History;
            if (h.Count == 0) { Menu.Small("No messages yet."); return; }
            int from = Math.Max(0, h.Count - 10);
            for (int i = from; i < h.Count; i++)
            {
                var l = h[i];
                string who = l.Kind == ChatNet.LineKind.System ? "(chat)" : Trim(ChatNet.NoTags(l.Name), 22);
                Menu.KeyValue(who, ChatNet.NoTags(l.Text));
            }
        });
    }

    private static List<string> _others = new();
    private static float _othersAt = -100f;

    /// <summary>Names of everyone in the session but us (filtered names show as "Player N"), refreshed once a second.</summary>
    private static List<string> OtherPlayers()
    {
        if (Time.unscaledTime - _othersAt < 1f) return _others;
        _othersAt = Time.unscaledTime;
        var list = new List<string>();
        _others = list;
        try
        {
            if (!PlayerListRoster.InMultiplayerSession) return list;
            var sp = ChatNet.SafeSessionPlayers();
            int local = 0;
            try { var r = GameState.Runner; if (r != null) local = r.LocalPlayer.RawEncoded; } catch { }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PlayerRef p in PlayerListRoster.AllPlayers())
            {
                if (p.RawEncoded == local) continue;
                string n = ChatNet.NameOf(sp, p);
                if (seen.Add(n)) list.Add(n);
            }
        }
        catch (Exception e) { ChatNet.LogOnce("players", "[Chat] player list: " + e.Message); }
        return list;
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max - 1) + "…";
}
