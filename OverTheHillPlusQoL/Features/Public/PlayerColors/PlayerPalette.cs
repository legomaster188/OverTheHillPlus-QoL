using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.PlayerColors;

/// <summary>
/// Colours for players beyond the game's 4-entry PlayerColors table.
///
/// What the game does (from the Cpp2IL dump):
/// - UIManager.GetPlayerColor(p) looks p up in OverlandSessionPlayers.PlayerColors (4 entries) and returns
///   Player1Color..Player4Color for index 0..3; any other index (a player not in the table) gets one fixed
///   fallback colour, so every extra player in a big lobby looks the same.
/// - WorldSpacePinpoint.GetPlayerFlare(p) does the same lookup and loads Flare_Yellow / Flare_Purple / Flare_Green
///   for index 0..2 and Flare_Red for everything else, so extra players share player 4's red flare.
///
/// Here, every player that is not in the table gets a colour from a generated palette instead. The palette is
/// deterministic (golden-ratio hue steps, skipping the 4 stock hues), and a player's palette slot comes from their
/// PlayerId, so every modded client shows the same colour for the same player. Players in the table are untouched.
/// </summary>
internal static class PlayerPalette
{
    public const int Size = 16;

    // Hues of the stock colours, used until UIManager exists: the flares are yellow, purple, green, red.
    private static readonly float[] StockHues = { 0.14f, 0.78f, 0.33f, 0.0f };
    private const float AvoidRadius = 0.035f; // hue distance (0..0.5) kept from every stock colour

    private static Color[] _palette;
    private static bool _provisional;          // built before UIManager existed: rebuild once it does
    private static float _builtSat = -1f, _builtVal = -1f;
    private static float _nextUiCheck;

    // rawEncoded -> palette slot, for the current session
    private static readonly Dictionary<int, int> Slots = new();
    private static IntPtr _slotsRunner;

    private static bool _loggedError;

    public static bool Enabled => PlayerColorsFeature.Enabled?.Value ?? true;

    /// <summary>The generated palette (built on first use).</summary>
    public static Color[] Palette
    {
        get
        {
            EnsurePalette();
            return _palette;
        }
    }

    /// <summary>
    /// The colour a player shows as everywhere the game asks UIManager.GetPlayerColor: the stock colour for
    /// players 1 to 4 (in the PlayerColors table), the generated one for anyone else. False if unknown.
    /// </summary>
    public static bool TryGetColor(PlayerRef player, out Color color)
    {
        color = Color.white;
        try
        {
            if (!player.IsRealPlayer) return false;
            int index = TableIndex(player);
            if (index >= 0)
            {
                var ui = UIManager.Instance;
                if (ui == null) return false;
                var data = index switch { 0 => ui.Player1Color, 1 => ui.Player2Color, 2 => ui.Player3Color, _ => ui.Player4Color };
                if (data == null) return false;
                color = data.color;
                return true;
            }
            return TryGetOverflowColor(player, out color);
        }
        catch (Exception e)
        {
            LogOnce("TryGetColor", e);
            return false;
        }
    }

    /// <summary>The generated colour for a player that is not in the 4-entry table (or false if they are).</summary>
    public static bool TryGetOverflowColor(PlayerRef player, out Color color)
    {
        color = Color.white;
        if (!Enabled || !player.IsRealPlayer) return false;
        EnsurePalette();
        color = _palette[SlotFor(player)];
        return true;
    }

    /// <summary>Index in OverlandSessionPlayers.PlayerColors (0..3), -1 if not in it, -2 if the table is unavailable.</summary>
    public static int TableIndex(PlayerRef player)
    {
        var sp = OverlandSessionPlayers.Instance;
        if (sp == null) return -2;
        var colors = sp.PlayerColors;
        int raw = player.RawEncoded;
        for (int i = 0; i < colors.Length; i++)
            if (colors.Get(i).RawEncoded == raw) return i;
        return -1;
    }

    /// <summary>Stable palette slot for a player: PlayerId based, probing past slots held by other present players.</summary>
    public static int SlotFor(PlayerRef player)
    {
        var runner = GameState.Runner;
        IntPtr rp = runner != null ? runner.Pointer : IntPtr.Zero;
        if (rp != _slotsRunner)
        {
            Slots.Clear();
            _slotsRunner = rp;
        }

        int raw = player.RawEncoded;
        if (Slots.TryGetValue(raw, out int slot)) return slot;

        int preferred = ((player.PlayerId % Size) + Size) % Size;
        if (SlotTaken(preferred, raw)) PurgeLeft(runner);
        slot = preferred;
        for (int i = 0; i < Size; i++)
        {
            int s = (preferred + i) % Size;
            if (!SlotTaken(s, raw)) { slot = s; break; }
        }
        Slots[raw] = slot;
        return slot;
    }

    private static bool SlotTaken(int slot, int raw)
    {
        foreach (var kv in Slots)
            if (kv.Value == slot && kv.Key != raw) return true;
        return false;
    }

    /// <summary>Frees the slots of players who left (only needed when a slot is contested).</summary>
    private static void PurgeLeft(NetworkRunner runner)
    {
        if (runner == null) return;
        try
        {
            var gone = new List<int>();
            foreach (var kv in Slots)
                if (!runner.IsPlayerValid(PlayerRef.FromEncoded(kv.Key))) gone.Add(kv.Key);
            foreach (int k in gone) Slots.Remove(k);
        }
        catch (Exception e) { LogOnce("PurgeLeft", e); }
    }

    private static void EnsurePalette()
    {
        float sat = PlayerColorsFeature.Saturation?.Value ?? 0.75f;
        float val = PlayerColorsFeature.Brightness?.Value ?? 0.95f;

        if (_palette != null && _provisional && Time.unscaledTime >= _nextUiCheck)
        {
            _nextUiCheck = Time.unscaledTime + 1f;
            if (UIManager.Instance != null) _palette = null;
        }
        if (_palette != null && Mathf.Approximately(sat, _builtSat) && Mathf.Approximately(val, _builtVal)) return;

        var avoid = new List<float>(StockHues);
        _provisional = true;
        try
        {
            var ui = UIManager.Instance;
            if (ui != null)
            {
                _provisional = false;
                foreach (var d in new[] { ui.Player1Color, ui.Player2Color, ui.Player3Color, ui.Player4Color })
                {
                    if (d == null) continue;
                    Color.RGBToHSV(d.color, out float h, out float s, out float v);
                    if (s > 0.2f && v > 0.15f) avoid.Add(h);
                }
            }
        }
        catch (Exception e) { LogOnce("stock colours", e); }

        _palette = Build(sat, val, avoid);
        _builtSat = sat;
        _builtVal = val;
    }

    /// <summary>Deterministic palette: golden-ratio hue steps, three saturation/brightness tiers, stock hues skipped.</summary>
    internal static Color[] Build(float sat, float val, List<float> avoidHues)
    {
        sat = Mathf.Clamp(sat, 0.2f, 1f);
        val = Mathf.Clamp(val, 0.35f, 1f);
        var result = new Color[Size];
        int n = 0;
        for (int k = 0; n < Size && k < 2000; k++)
        {
            float h = Frac(0.05f + k * 0.618034f);
            bool clash = false;
            foreach (float a in avoidHues)
                if (HueDistance(h, a) < AvoidRadius) { clash = true; break; }
            if (clash) continue;

            int tier = n % 3;
            float s = tier == 1 ? sat * 0.6f : sat;
            float v = tier == 2 ? val * 0.72f : val;
            result[n++] = Color.HSVToRGB(h, Mathf.Clamp01(s), Mathf.Clamp01(v));
        }
        for (; n < Size; n++) result[n] = Color.HSVToRGB(Frac(n * 0.618034f), sat, val); // unreachable in practice
        return result;
    }

    internal static float HueDistance(float a, float b)
    {
        float d = Mathf.Abs(Frac(a) - Frac(b));
        return Mathf.Min(d, 1f - d);
    }

    private static float Frac(float x) => x - Mathf.Floor(x);

    internal static void LogOnce(string where, Exception e)
    {
        if (_loggedError) return;
        _loggedError = true;
        Plugin.Logger.LogWarning($"[PlayerColors] {where}: {e.GetType().Name}: {e.Message} (further errors are not logged)");
    }
}
