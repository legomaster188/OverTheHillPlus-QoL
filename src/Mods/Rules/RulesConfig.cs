using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Rules;

/// <summary>Server Rules & MOTD's settings in BepInEx/config/oth.rules.cfg.</summary>
internal static class RulesConfig
{
    public const int RuleSlots = 6, AnnouncementSlots = 6;

    public static ConfigEntry<bool> WelcomeEnabled;
    public static ConfigEntry<string> WelcomeMessage;
    public static ConfigEntry<string> WelcomeColor;
    public static ConfigEntry<float> WelcomeDelay;

    public static ConfigEntry<bool> RulesOnJoin;
    public static ConfigEntry<bool> RulesAsNotices;
    public static ConfigEntry<string> RulesTitle;
    public static readonly ConfigEntry<string>[] RuleLines = new ConfigEntry<string>[RuleSlots];
    public static ConfigEntry<float> PopupSeconds;

    public static ConfigEntry<bool> AnnounceEnabled;
    public static ConfigEntry<int> AnnounceMinutes;
    public static ConfigEntry<string> AnnounceColor;
    public static readonly ConfigEntry<string>[] Announcements = new ConfigEntry<string>[AnnouncementSlots];

    public static ConfigEntry<bool> VoteKickEnabled;
    public static ConfigEntry<bool> HostVotes;
    public static ConfigEntry<int> MinYes;
    public static ConfigEntry<Key> VoteYesKey;
    public static ConfigEntry<Key> VoteNoKey;

    public static ConfigFile File;

    private static readonly string[] DefaultRules = { "Be kind to everyone.", "No ramming or blocking other players.", "Have fun!", "", "", "" };

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        WelcomeEnabled = cfg.Bind("Welcome", "Enabled", true, "When hosting, greet each player who joins (everyone in the session sees it, players without mods too).");
        WelcomeMessage = cfg.Bind("Welcome", "Message", "Welcome to {host}'s server, {name}!", "Welcome message. {name} = the player who joined, {host} = you.");
        WelcomeColor = cfg.Bind("Welcome", "Color", "Gold", "Pop-up colour: Gold, Cyan, Pink, Green, Red, Purple, White.");
        WelcomeDelay = cfg.Bind("Welcome", "DelaySeconds", 8f, "Seconds after joining before the welcome and the rules show (the joiner's game is still loading before that).");

        RulesOnJoin = cfg.Bind("Rules", "ShowOnJoin", true, "Show the rules to each player who joins.");
        RulesAsNotices = cfg.Bind("Rules", "NoticesForPlayersWithoutMod", false,
            "Players without Server Rules get the rules as pop-up notices, which everyone in the session sees (at most once every 3 minutes; joins in between share one). Players with it get a rules window only they see.");
        RulesTitle = cfg.Bind("Rules", "Title", "Server rules", "Title of the rules window.");
        for (int i = 0; i < RuleSlots; i++)
            RuleLines[i] = cfg.Bind("Rules", "Rule" + (i + 1), DefaultRules[i], $"Rule {i + 1} (empty = not used).");
        PopupSeconds = cfg.Bind("Rules", "WindowSeconds", 20f, "How long the rules window stays on screen.");

        AnnounceEnabled = cfg.Bind("Announcements", "Enabled", false, "When hosting, show one of the messages below to everyone every few minutes, in turn.");
        AnnounceMinutes = cfg.Bind("Announcements", "IntervalMinutes", 15, "Minutes between announcements.");
        AnnounceColor = cfg.Bind("Announcements", "Color", "Cyan", "Pop-up colour: Gold, Cyan, Pink, Green, Red, Purple, White.");
        for (int i = 0; i < AnnouncementSlots; i++)
            Announcements[i] = cfg.Bind("Announcements", "Message" + (i + 1), "", $"Announcement {i + 1} (empty = not used).");

        VoteKickEnabled = cfg.Bind("VoteKick", "Enabled", true,
            "When hosting, players with Server Rules can vote to remove a player. A strict majority of them is needed; you can veto any vote, and you can never be voted out.");
        HostVotes = cfg.Bind("VoteKick", "HostVotes", true, "Your own vote counts too when hosting.");
        MinYes = cfg.Bind("VoteKick", "MinimumYesVotes", 2, "A vote never passes with fewer yes votes than this (at least 2).");
        // Insert / Delete: no other mod and none of the game's own bindings use them by default.
        VoteYesKey = cfg.Bind("VoteKick", "VoteYesKey", Key.Insert, "Key to vote yes while a vote is on screen (again: hides the card).");
        VoteNoKey = cfg.Bind("VoteKick", "VoteNoKey", Key.Delete, "Key to vote no while a vote is on screen (again: hides the card).");
    }
}
