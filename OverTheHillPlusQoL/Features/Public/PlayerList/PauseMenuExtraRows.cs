using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.PlayerList;

/// <summary>
/// Pause menu player list for big lobbies. Decoded from the game (Cpp2IL):
/// StatePauseMenu.SetupMenu loops i over OrderedPlayerRefs (4 entries) and fills ContextUIPauseMenu.PlayerInfos[i]
/// (colour dot, host crown, name, badges, car icon, kick/voice options); Open wires PlayerInfos[i]'s button to
/// OpenPlayerMenu(i), which indexes OrderedPlayerRefs. So players outside the 4 slots are never listed.
///
/// Here, after every SetupMenu, rows for the extra players are cloned from the last stock row and filled the same way.
/// The clones are kept in our own list, never added to PlayerInfos, so no game code ever indexes them (they are
/// display-only: no kick / voice options). If anything about that fails, a side panel overlay lists them instead.
/// </summary>
internal static class PauseMenuExtraRows
{
    internal static StatePauseMenu OpenMenu;
    internal static bool NativeFailed;
    internal static int RowsShown;
    internal static readonly List<PlayerListRoster.Entry> Entries = new();

    private static IntPtr _ctx;
    private static readonly List<ContextUIPlayerInfo> Rows = new();
    private static bool? _parentHasLayout;
    private static float _nextPoll;
    private static string _signature = "";

    // ---- open / close tracking ----

    public static void Opened(StatePauseMenu menu)
    {
        OpenMenu = menu;
        _nextPoll = 0f;
    }

    public static void Closed()
    {
        OpenMenu = null;
        Entries.Clear();
        RowsShown = 0;
    }

    private static bool MenuAlive(StatePauseMenu menu)
    {
        if (menu == null) return false;
        try
        {
            var ctx = menu.contextUIPauseMenu;
            return ctx != null && !ctx.WasCollected;
        }
        catch { return false; }
    }

    /// <summary>While the pause menu is open: catch players that joined/spawned without a player-data event.</summary>
    public static void Tick()
    {
        if (OpenMenu == null || Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + 1f;
        if (!MenuAlive(OpenMenu)) { Closed(); return; }
        var sig = Signature(Build());
        if (sig != _signature) Refresh(OpenMenu);
    }

    // ---- building ----

    private static List<PlayerListRoster.Entry> Build()
    {
        var list = new List<PlayerListRoster.Entry>();
        var sp = OverlandSessionPlayers.Instance;
        foreach (var p in PlayerListRoster.Extras()) list.Add(PlayerListRoster.Describe(sp, p));
        return list;
    }

    private static string Signature(List<PlayerListRoster.Entry> list)
    {
        var sb = new StringBuilder();
        foreach (var e in list)
            sb.Append(e.Ref.RawEncoded).Append(':').Append(e.Name).Append(':').Append(e.IsHost ? 'h' : '-')
              .Append(ColorUtility.ToHtmlStringRGB(e.Color)).Append('|');
        return sb.ToString();
    }

    /// <summary>Runs after the game's SetupMenu (menu opened, or player data changed while open).</summary>
    public static void Refresh(StatePauseMenu menu)
    {
        if (menu == null) return;
        OpenMenu ??= menu;
        var list = Build();
        _signature = Signature(list);
        Entries.Clear();
        Entries.AddRange(list);

        if (!PlayerListFeature.PauseRows.Value || NativeFailed)
        {
            HideAll();
            return;
        }
        try { ApplyRows(menu, list); }
        catch (Exception e)
        {
            NativeFailed = true;
            Plugin.Logger.LogWarning($"[PlayerList] Extra pause menu rows failed, using the side panel instead: {e}");
            HideAll();
        }
    }

    private static void ApplyRows(StatePauseMenu menu, List<PlayerListRoster.Entry> list)
    {
        var ctx = menu.contextUIPauseMenu;
        if (ctx == null) return;
        if (ctx.Pointer != _ctx)
        {
            Rows.Clear(); // the old rows were children of the old UI and went with it
            _ctx = ctx.Pointer;
            _parentHasLayout = null;
        }
        Rows.RemoveAll(r => r == null || r.WasCollected);

        var infos = ctx.PlayerInfos;
        if (infos == null || infos.Count == 0) return;
        var template = infos[infos.Count - 1];
        if (template == null) return;
        var previous = infos.Count >= 2 ? infos[infos.Count - 2] : null;

        while (Rows.Count < list.Count && Rows.Count < 32)
        {
            var row = CreateRow(template, Rows.Count);
            if (row == null) break;
            Rows.Add(row);
        }

        RowsShown = 0;
        for (int i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (i < list.Count)
            {
                Fill(row, list[i]);
                Place(row, i, template, previous);
                RowsShown++;
            }
            else row.gameObject.SetActive(false);
        }
    }

    private static ContextUIPlayerInfo CreateRow(ContextUIPlayerInfo template, int index)
    {
        var go = Object.Instantiate(template.gameObject, template.transform.parent);
        go.name = $"OTHPlus_ExtraPlayerRow{index + 5}";
        var row = go.GetComponent<ContextUIPlayerInfo>();
        if (row == null)
        {
            Object.Destroy(go);
            throw new InvalidOperationException("cloned row has no ContextUIPlayerInfo");
        }
        return row;
    }

    private static void Fill(ContextUIPlayerInfo row, PlayerListRoster.Entry e)
    {
        row.gameObject.SetActive(true);
        try { if (row.SmileImage != null) row.SmileImage.color = e.Color; } catch { }
        try { if (row.CrownImage != null) row.CrownImage.gameObject.SetActive(e.IsHost); } catch { }
        if (row.NameText != null) row.NameText.text = e.Name;
        try { row.SetPlayerBadges(e.Supporter, e.Dev); } catch { }
        try
        {
            Sprite icon = null;
            try { icon = OverlandNetworkRunner.Instance?.GetCarInfo(e.Ref)?.Icon; } catch { }
            if (row.VehicleImage != null)
            {
                row.VehicleImage.enabled = icon != null;
                if (icon != null) row.VehicleImage.sprite = icon;
            }
        }
        catch { }
        // display only: no kick / voice chat options (the game's option menu can't address these players)
        try { row.SetShowOptions(false, false, e.IsLocal, false); } catch { }
        try { row.SetPlayerOptionsOpen(false, true); } catch { }
        try { if (row.ExpandIcon != null) row.ExpandIcon.gameObject.SetActive(false); } catch { }
    }

    private static void Place(ContextUIPlayerInfo row, int index, ContextUIPlayerInfo template, ContextUIPlayerInfo previous)
    {
        var parent = template.transform.parent;
        if (_parentHasLayout == null)
        {
            bool layout = false;
            try { layout = parent != null && parent.GetComponent<UnityEngine.UI.LayoutGroup>() != null; } catch { }
            _parentHasLayout = layout;
        }

        if (_parentHasLayout.Value)
        {
            // a layout group stacks the rows: keep ours right after the game's last row
            row.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1 + index);
            return;
        }

        // manual layout: continue the spacing of the game's rows
        var last = template.transform.TryCast<RectTransform>();
        var rt = row.transform.TryCast<RectTransform>();
        if (last == null || rt == null) return;
        Vector2 step = Vector2.zero;
        var prev = previous != null ? previous.transform.TryCast<RectTransform>() : null;
        if (prev != null) step = last.anchoredPosition - prev.anchoredPosition;
        if (step.sqrMagnitude < 1f)
        {
            float h = 0f;
            try { h = template.PlayerInfoRowHeight; } catch { }
            if (h < 10f) h = Mathf.Max(30f, last.rect.height);
            step = new Vector2(0f, -h);
        }
        rt.anchoredPosition = last.anchoredPosition + step * (index + 1);
    }

    private static void HideAll()
    {
        RowsShown = 0;
        foreach (var r in Rows)
        {
            try { if (r != null && !r.WasCollected) r.gameObject.SetActive(false); } catch { }
        }
    }

    /// <summary>Settings changed from the mod menu: re-apply to the open pause menu.</summary>
    public static void Reapply()
    {
        if (OpenMenu != null && MenuAlive(OpenMenu)) Refresh(OpenMenu);
        else HideAll();
    }

    // ---- side panel overlay (optional, and the fallback) ----

    public static void OnGui()
    {
        if (OpenMenu == null || Entries.Count == 0) return;
        if (!(PlayerListFeature.PauseOverlay.Value || NativeFailed)) return;

        UI.Theme.Ensure();
        var prevMatrix = GUI.matrix;
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        try
        {
            const float w = 300f, rowH = 26f;
            int n = Mathf.Min(Entries.Count, 20);
            float h = 42f + n * rowH + 10f;
            float sw = Screen.width / scale, sh = Screen.height / scale;
            float x = Mathf.Lerp(24f, sw - w - 24f, Mathf.Clamp01(PlayerListFeature.OverlayX.Value));
            var r = new Rect(x, Mathf.Max(24f, (sh - h) * 0.5f), w, h);

            var bg = UI.Theme.Bg;
            UI.Theme.Fill(r, new Color(bg.r, bg.g, bg.b, 0.88f), 10);
            GUI.Label(new Rect(r.x + 14f, r.y + 12f, w - 28f, 18f), $"MORE PLAYERS  ·  {Entries.Count}", UI.Theme.Section);

            float y = r.y + 40f;
            for (int i = 0; i < n; i++)
            {
                var e = Entries[i];
                UI.Theme.Fill(new Rect(r.x + 14f, y + 7f, 12f, 12f), e.Color, 6);
                string name = (e.Name ?? "").Replace("<", "‹");
                string tags = (e.IsHost ? "  <color=#F2B33D>host</color>" : "") + (e.IsLocal ? "  <color=#8C96A6>you</color>" : "");
                GUI.Label(new Rect(r.x + 34f, y + 3f, w - 48f, 20f), name + tags, UI.Theme.Label);
                y += rowH;
            }
        }
        finally { GUI.matrix = prevMatrix; }
    }
}

[HarmonyPatch(typeof(StatePauseMenu), nameof(StatePauseMenu.SetupMenu))]
internal static class PauseMenuSetupExtraRowsPatch
{
    private static void Postfix(StatePauseMenu __instance)
    {
        try { PauseMenuExtraRows.Refresh(__instance); }
        catch (Exception e) { PlayerListRoster.LogOnce("setup", "pause menu extra players: " + e.Message); }
    }
}

[HarmonyPatch(typeof(StatePauseMenu), nameof(StatePauseMenu.Open))]
internal static class PauseMenuOpenTrackPatch
{
    private static void Postfix(StatePauseMenu __instance)
    {
        try { PauseMenuExtraRows.Opened(__instance); } catch { }
    }
}

[HarmonyPatch(typeof(StatePauseMenu), nameof(StatePauseMenu.Close))]
internal static class PauseMenuCloseTrackPatch
{
    private static void Prefix()
    {
        try { PauseMenuExtraRows.Closed(); } catch { }
    }
}
