using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Fusion;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.PlayerColors;

/// <summary>Settings and the "Player colours" card (PLUS > Lobby) for the overflow-player palette.</summary>
internal sealed class PlayerColorsFeature : IFeature
{
    private const string SectionName = "Player Colours";

    internal static ConfigEntry<bool> Enabled, TintFlares;
    internal static ConfigEntry<float> Saturation, Brightness;

    private struct Row
    {
        public string Name;
        public int Table; // 0..3 = stock slot, -1 = extra player
        public Color Color;
        public bool Known;
        public bool Local;
    }

    private static readonly List<Row> Rows = new();
    private static float _nextRefresh;

    public void Register()
    {
        var f = ModConfig.File;
        Enabled = f.Bind(SectionName, "ExtraPlayerColours", true,
            "Players beyond the game's 4 colour slots get their own distinct colour (map, compass, names, join messages) instead of all sharing one. Players 1-4 keep the game's colours.");
        TintFlares = f.Bind(SectionName, "TintExtraFlares", true,
            "Recolour the pinpoint flare of players beyond the 4 slots (the game gives them all player 4's red flare).");
        Saturation = f.Bind(SectionName, "PaletteSaturation", 0.75f,
            new ConfigDescription("Saturation of the generated colours.", new AcceptableValueRange<float>(0.3f, 1f)));
        Brightness = f.Bind(SectionName, "PaletteBrightness", 0.95f,
            new ConfigDescription("Brightness of the generated colours.", new AcceptableValueRange<float>(0.5f, 1f)));

        Registry.Section(Registry.PlusMode, Menu.LobbyTab, DrawCard, 40);
    }

    private static void DrawCard()
    {
        Menu.Card("Player colours", "The game only has 4 player colours; everyone past that shared one. Extra players now get their own colour, the same on every modded client.", () =>
        {
            Menu.Switch(Enabled, "Colours for extra players", "Map, compass, pause menu, name tags, join messages, pinpoints", true);
            Menu.Switch(TintFlares, "Recolour extra players' flares", "Otherwise they all use player 4's red flare", Enabled.Value);
            Menu.SliderRow(Saturation, "Palette saturation", 0.3f, 1f, "0.00", "", Enabled.Value, percent: true);
            Menu.SliderRow(Brightness, "Palette brightness", 0.5f, 1f, "0.00", "", Enabled.Value, percent: true);

            DrawSwatches();
            DrawPlayers();
        });
    }

    private static void DrawSwatches()
    {
        Color[] palette;
        try { palette = PlayerPalette.Palette; }
        catch (Exception e) { PlayerPalette.LogOnce("palette", e); return; }

        GUI.Label(new Rect(Menu.Left + 4f, Menu.Y, 200f, 20f), "Extra player palette", Theme.Muted_);
        Menu.Y += 22f;
        var row = Menu.NextRow(22f);
        const float gap = 4f;
        float w = (row.width - gap * (palette.Length - 1)) / palette.Length;
        var prev = GUI.color;
        if (!Enabled.Value) GUI.color = new Color(1f, 1f, 1f, 0.35f);
        for (int i = 0; i < palette.Length; i++)
            Theme.Fill(new Rect(row.x + i * (w + gap), row.y, w, row.height), palette[i], 4);
        GUI.color = prev;
    }

    private static void DrawPlayers()
    {
        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + 1f;
            Refresh();
        }
        if (Rows.Count == 0)
        {
            Menu.Small("Join or host a session to see each player's colour here.");
            return;
        }

        foreach (var r in Rows)
        {
            var rect = Menu.NextRow(22f);
            if (r.Known) Theme.Fill(new Rect(rect.x + 4f, rect.y + 3f, 28f, 16f), r.Color, 4);
            else Theme.Fill(new Rect(rect.x + 4f, rect.y + 3f, 28f, 16f), Theme.Off, 4);
            string who = r.Local ? r.Name + "  (you)" : r.Name;
            GUI.Label(new Rect(rect.x + 42f, rect.y, rect.width - 170f, 22f), who, Theme.Label);
            string slot = r.Table >= 0 ? $"slot {r.Table + 1}" : "extra player";
            GUI.Label(new Rect(rect.xMax - 120f, rect.y, 120f, 22f), slot, Theme.Muted_);
        }
    }

    /// <summary>Lists connected players (probing player ids; ActivePlayers is an awkward Il2Cpp IEnumerable).</summary>
    private static void Refresh()
    {
        Rows.Clear();
        try
        {
            var runner = GameState.Runner;
            if (runner == null || !runner.IsRunning) return;
            var sp = OverlandSessionPlayers.Instance;
            int local = runner.LocalPlayer.RawEncoded;

            for (int raw = 1; raw <= 64 && Rows.Count < 32; raw++)
            {
                var p = PlayerRef.FromEncoded(raw);
                bool valid;
                try { valid = runner.IsPlayerValid(p); } catch { valid = false; }
                if (!valid) continue;

                var row = new Row { Name = NameOf(sp, p), Table = PlayerPalette.TableIndex(p), Local = raw == local };
                if (row.Table >= 0) row.Known = PlayerPalette.TryGetColor(p, out row.Color);
                else row.Known = PlayerPalette.TryGetOverflowColor(p, out row.Color);
                if (row.Table < -1) row.Table = -1;
                Rows.Add(row);
            }
        }
        catch (Exception e) { PlayerPalette.LogOnce("player list", e); }
    }

    private static string NameOf(OverlandSessionPlayers sp, PlayerRef p)
    {
        try
        {
            if (sp != null)
            {
                var names = sp.PlayerRefToNameDictionary;
                int key = p.RawEncoded - 1; // the game keys names by RawEncoded-1
                if (names.ContainsKey(key))
                {
                    string n = names.Get(key).Value;
                    if (!string.IsNullOrWhiteSpace(n)) return n;
                }
            }
        }
        catch { }
        return $"Player {p.PlayerId}";
    }
}
