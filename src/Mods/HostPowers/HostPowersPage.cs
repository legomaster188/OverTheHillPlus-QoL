using System;
using System.Linq;
using OverTheHillPlus.Features;
using OverTheHillPlus.UI;
using UnityEngine;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.HostPowers;

/// <summary>
/// The Host tab of Host Powers' page (the old F7 menu drew it itself): everyone-at-once actions, weather, time,
/// tools and camping, the player list; and the name filter's mild-swears switch for your own server name and join
/// message. The features' own tabs (events, players, teleport, track builder) follow it.
/// </summary>
internal static class HostPowersPage
{
    private const int Plus = Registry.PlusMode;
    // Before every feature section of the same tab, as the old menu drew its own cards first.
    private const int BuiltInOrder = int.MinValue / 2;

    private static float _hostHour = 12f;
    private static float _hostTimeSpeed = 1f;

    /// <summary>Registers the sections (from the plugin's Load, before the features register theirs).</summary>
    internal static void RegisterSections()
    {
        string id = HostPowersPlugin.ModId;
        Registry.Section(Plus, "Host", DrawHost, BuiltInOrder, modId: id);
        Registry.Section(Plus, "Host", DrawNameFilter, BuiltInOrder + 1, modId: id);
    }

    private static void DrawNameFilter()
    {
        Card("Name filter", "Core's swear/slur filter for the text you publish when you host.", () =>
        {
            Switch(HostPowersConfig.AllowSwears, "Allow mild swears", "Your own server name and join message only; slurs stay blocked", true);
        });
    }

    private static void DrawHost()
    {
        if (!HostTools.Available)
        {
            Banner(GameState.Runner == null
                ? "Host tools work once you're hosting a session (or in single-player)."
                : "You're a guest. Only the host's game can run these, since it owns the session.", Theme.Info);
        }
        bool host = HostTools.Available;
        Card("Everyone", "Works on players without the mod too: your game runs the session.", () =>
        {
            Switch(HostPowersConfig.LobbyGodMode, "Nobody takes damage", "Lobby-wide god mode", host);
            Chips(3,
                ("Repair all", () => SetStatus(HostTools.ForEveryone(HostTools.Repair, "Repaired")), host),
                ("Wash all", () => SetStatus(HostTools.ForEveryone(HostTools.Clean, "Washed")), host),
                ("Refill all tools", () => SetStatus(HostTools.ForEveryone(HostTools.Refill, "Refilled")), host));
            Chips(3,
                ("Bring all to me", () => SetStatus(HostTools.ForEveryone(p => p.IsMe ? "Failed" : HostTools.BringToMe(p), "Brought")), host),
                ("Flip all upright", () => SetStatus(HostTools.ForEveryone(HostTools.Flip, "Flipped")), host),
                ("Launch all", () => SetStatus(HostTools.ForEveryone(p => p.IsMe ? "Failed" : HostTools.Launch(p), "Launched")), host));
        });
        Card("Weather", null, () =>
        {
            var weathers = HostWorld.Weathers;
            int current = HostWorld.CurrentWeatherId;
            KeyValue("Now", current < 0 ? "—" : HostWorld.WeatherName(current));
            if (weathers.Count == 0) { Small("Load into a world to list weather types."); return; }
            var items = weathers.Select(w => (w.id == current ? $"• {w.name}" : w.name, (Action)(() =>
            {
                SetStatus(HostWorld.SetWeather(w.id));
                if (HostPowersConfig.LockWeather.Value) HostPowersConfig.LockedWeatherId.Value = w.id;
            }), host)).ToArray();
            Chips(3, items);
            Switch(HostPowersConfig.LockWeather, "Lock weather", HostPowersConfig.LockedWeatherId.Value >= 0
                ? $"Keeps {HostWorld.WeatherName(HostPowersConfig.LockedWeatherId.Value)}" : "Pick a weather above to lock it", host);
            if (HostPowersConfig.LockWeather.Value && HostPowersConfig.LockedWeatherId.Value < 0 && current >= 0) HostPowersConfig.LockedWeatherId.Value = current;
            float wind = HostWorld.Wind;
            float newWind = RawSlider("Wind", wind, 0f, 1f, $"{wind:0.00}", host);
            if (Math.Abs(newWind - wind) > 0.01f) SetStatus(HostWorld.SetWind(Mathf.Round(newWind * 100f) / 100f));
            Chips(2, ("Aurora tonight", () => SetStatus(HostWorld.Aurora(true)), host), ("No aurora", () => SetStatus(HostWorld.Aurora(false)), host));
        });
        Card("Time", null, () =>
        {
            int hh = (int)_hostHour, mm = (int)((_hostHour - hh) * 60f);
            _hostHour = RawSlider("Time of day", _hostHour, 0f, 23.99f, $"{hh:00}:{mm:00}", host);
            Chips(4,
                ($"Set {hh:00}:{mm:00}", () => SetStatus(HostWorld.SetTime(hh, mm)), host),
                ("+6 hours", () => SetStatus(HostWorld.SkipHours(6)), host),
                ("Pause", () => SetStatus(HostWorld.PauseTime(true)), host),
                ("Resume", () => SetStatus(HostWorld.PauseTime(false)), host));
            _hostTimeSpeed = RawSlider("Time speed", _hostTimeSpeed, 0.1f, 20f, $"{_hostTimeSpeed:0.0}×", host);
            Chips(2, ($"Apply {_hostTimeSpeed:0.0}×", () => SetStatus(HostWorld.TimeSpeed(_hostTimeSpeed)), host),
                ("Normal speed", () => { _hostTimeSpeed = 1f; SetStatus(HostWorld.TimeSpeed(1f)); }, host));
        });
        Card("Tools & camping", null, () =>
        {
            Chips(1, ("Give every player all tools", () => SetStatus(HostWorld.GiveAllTools()), host));
            bool supporter = HostWorld.OwnsSupporterEdition;
            Chips(2,
                ("Pitch tent", () => SetStatus(HostWorld.SpawnTent(false)), host),
                ("Light campfire", () => SetStatus(HostWorld.SpawnCampfire(false)), host));
            Chips(3,
                ("Supporter tent", () => SetStatus(HostWorld.SpawnTent(true)), host && supporter),
                ("Supporter fire", () => SetStatus(HostWorld.SpawnCampfire(true)), host && supporter),
                ("End camping", () => SetStatus(HostWorld.EndCamping()), host));
            if (!supporter) Small("Supporter Edition styles need the Supporter Edition DLC.");
        });

        var players = HostTools.Players;
        Card("Players", players.Count == 0 ? "No players listed yet." : $"{players.Count} in session", () =>
        {
            foreach (var p in players)
            {
                var row = new Rect(Inset - 6f, Y, IW + 12f, 30f);
                Theme.Fill(row, Theme.CardHover, 6);
                Label(new Rect(Inset + 4f, Y + 2f, IW - 20f, 26f),
                    $"<b>{p.Name}</b>{(p.IsMe ? "  <color=#8C96A6>(you)</color>" : "")}{(p.Car == null ? "  <color=#8C96A6>no car</color>" : "")}", Theme.Label);
                Y += 34f;
                var target = p;
                Chips(4,
                    ("Go to", () => SetStatus(HostTools.GoTo(target)), host && !p.IsMe),
                    ("Bring", () => SetStatus(HostTools.BringToMe(target)), host && !p.IsMe),
                    ("Repair", () => SetStatus(HostTools.Repair(target)), host),
                    ("Wash", () => SetStatus(HostTools.Clean(target)), host));
                Chips(4,
                    ("Flip", () => SetStatus(HostTools.Flip(target)), host),
                    ("Refill", () => SetStatus(HostTools.Refill(target)), host),
                    ("Launch", () => SetStatus(HostTools.Launch(target)), host && !p.IsMe),
                    ("Kick", () => SetStatus(HostTools.Kick(target)), host && !p.IsMe));
                Y += 4f;
            }
        });
    }
}
