using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Fusion;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Rules;

/// <summary>Rules+'s MODS page: welcome, rules, announcements and vote kick.</summary>
internal static class RulesSimple
{
    private static readonly int[] Minutes = { 5, 10, 15, 20, 30, 45, 60 };
    private static List<(PlayerRef player, string name)> _targets = new();
    private static float _targetsAt = -100f;

    internal static void Draw()
    {
        if (RulesConfig.WelcomeEnabled == null) return;
        bool hosting = RulesHost.Hosting;

        DrawVote(hosting);

        SimpleMenu.Heading("Welcome");
        SimpleMenu.Toggle(RulesConfig.WelcomeEnabled, "Welcome message", "When hosting: everyone in the session sees it when a player joins, players without mods too.");
        TextEntry(RulesConfig.WelcomeMessage, "Message", 120, "{name} = the player who joined, {host} = you. A message with a blocked word is not sent.");
        ColorChoice(RulesConfig.WelcomeColor, "Colour");
        var dup = CoreConfig.Find("Multiplayer", "JoinMessageEnabled") as ConfigEntry<bool>;
        if (dup != null && dup.Value && RulesConfig.WelcomeEnabled.Value)
            SimpleMenu.Note("Server Creator+'s join message is on too: joining players get both. Switch one of them off.");

        SimpleMenu.Heading("Rules");
        SimpleMenu.Toggle(RulesConfig.RulesOnJoin, "Show the rules on join", "Players with Rules+ get a window only they see.");
        SimpleMenu.Toggle(RulesConfig.RulesAsNotices, "Notices for players without it",
            "Players without Rules+ get the rules as pop-up notices instead, which everyone in the session sees: " +
            "at most once every 3 minutes, joins in between share one.");
        TextEntry(RulesConfig.RulesTitle, "Title", RulesWire.MaxTitleChars, "Title of the rules window.");
        for (int i = 0; i < RulesConfig.RuleSlots; i++)
            TextEntry(RulesConfig.RuleLines[i], "Rule " + (i + 1), RulesWire.MaxRuleChars, "Empty = not used. A rule with a blocked word is left out.");
        SimpleMenu.Button("Show the rules", null, hosting ? "Shows your rules window here, as players with the mod see it."
            : "Asks the host for the rules window again.", () => Menu.SetStatus(RulesClient.AskForRules()));
        if (hosting)
            SimpleMenu.Button("Send the rules to everyone now", null, "Notices for everyone, and the window for players with Rules+.",
                () => Menu.SetStatus(RulesHost.SendRulesToEveryone()));

        SimpleMenu.Heading("Announcements");
        SimpleMenu.Toggle(RulesConfig.AnnounceEnabled, "Timed announcements", "When hosting: one message every few minutes, in turn, for everyone.");
        SimpleMenu.IntChoice("Every", "Minutes between announcements.", RulesConfig.AnnounceMinutes.Value, Minutes,
            v => v + " min", v => RulesConfig.AnnounceMinutes.Value = v);
        ColorChoice(RulesConfig.AnnounceColor, "Colour");
        for (int i = 0; i < RulesConfig.AnnouncementSlots; i++)
            TextEntry(RulesConfig.Announcements[i], "Message " + (i + 1), RulesWire.MaxWelcomeChars, "Empty = not used. A message with a blocked word is not sent.");

        SimpleMenu.Heading("Vote kick (host settings)");
        SimpleMenu.Toggle(RulesConfig.VoteKickEnabled, "Vote kick",
            "Players with Rules+ can vote to remove a player: a strict majority of them, at least the minimum below. You can veto any vote and are never voted out.");
        SimpleMenu.Toggle(RulesConfig.HostVotes, "Your vote counts", "Count your own vote when hosting.");
        SimpleMenu.IntChoice("Minimum yes votes", "A vote never passes with fewer yes votes.", RulesConfig.MinYes.Value, new[] { 2, 3, 4, 5 },
            v => v.ToString(), v => RulesConfig.MinYes.Value = v);
    }

    private static void DrawVote(bool hosting)
    {
        SimpleMenu.Heading("Vote kick");
        var v = RulesClient.VoteVisible ? RulesClient.Vote : null;
        if (v == null && RulesClient.VoteLive) v = RulesClient.Vote; // the card was hidden: the page still shows it
        if (v != null && v.Outcome == VoteOutcome.Passing)
        {
            SimpleMenu.Note($"Vote passed: {v.TargetName} is removed in {Mathf.CeilToInt(RulesClient.VoteSecondsLeft)} s.");
            if (hosting) SimpleMenu.Button("Veto", null, "Stop the removal: nobody is removed.", () => Menu.SetStatus(RulesHost.Veto()));
            return;
        }
        if (v != null && v.Outcome == VoteOutcome.Open)
        {
            SimpleMenu.Note($"Vote to remove {v.TargetName}: {v.Yes} yes, {v.No} no, {v.Needed} of {v.Eligible} needed, " +
                            $"{Mathf.CeilToInt(RulesClient.VoteSecondsLeft)} s left.");
            if (RulesClient.CanVote)
            {
                SimpleMenu.Button("Vote yes", RulesClient.MyBallot == true ? "your vote" : null, $"Remove {v.TargetName}.", () => Menu.SetStatus(RulesClient.Cast(true)));
                SimpleMenu.Button("Vote no", RulesClient.MyBallot == false ? "your vote" : null, $"Keep {v.TargetName}.", () => Menu.SetStatus(RulesClient.Cast(false)));
            }
            if (hosting) SimpleMenu.Button("Veto", null, "Stop this vote: nobody is removed.", () => Menu.SetStatus(RulesHost.Veto()));
            if (RulesClient.VoteVisible)
                SimpleMenu.Button("Hide the vote card", null, "Takes the card off your screen; you can still vote here.", RulesClient.HideVoteCard);
            return;
        }
        bool inSession = hosting || Features.Public.ModNet.ModNet.IsGuest;
        if (!inSession) { SimpleMenu.Note("Join or host a multiplayer session to vote."); return; }
        if (hosting && !RulesConfig.VoteKickEnabled.Value) { SimpleMenu.Note("Vote kick is off (host settings below)."); return; }
        if (!hosting && GameState.IsPublicGuest)
        {
            SimpleMenu.Note("In public lobbies only the host can start a vote; you can still vote in one.");
            return;
        }
        if (Time.unscaledTime - _targetsAt > 1f)
        {
            _targetsAt = Time.unscaledTime;
            try { _targets = RulesClient.Targets(); } catch (Exception ex) { DebugLog.Error("rules", ex, "RulesSimple.DrawVote"); _targets = new(); }
        }
        if (_targets.Count == 0) { SimpleMenu.Note("Nobody else to vote on."); return; }
        foreach (var (player, name) in _targets)
        {
            var p = player;
            SimpleMenu.Button(name, "start vote", $"Ask the players with Rules+ to vote on removing {name}.",
                () => Menu.SetStatus(RulesClient.StartVote(p)));
        }
    }

    private static void TextEntry(ConfigEntry<string> entry, string label, int max, string hint)
    {
        if (entry == null) return;
        string v = SimpleMenu.Text(label, entry.Value, max, hint);
        if (v != entry.Value) entry.Value = v;
    }

    private static void ColorChoice(ConfigEntry<string> entry, string label)
    {
        int index = Array.IndexOf(HostNotice.Colors, entry.Value);
        SimpleMenu.Choice(label, "Colour of the pop-up's smiley.", index < 0 ? 0 : index, HostNotice.Colors, i => entry.Value = HostNotice.Colors[i]);
    }
}
