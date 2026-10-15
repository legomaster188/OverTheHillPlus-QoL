using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace OverTheHillPlus.ServerBrowser;

internal enum ServerStatus { Public, Private, Full, Incompatible }

/// <summary>Colors each server row by status and appends the first-seen age to the name.</summary>
[HarmonyPatch(typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateViewsHolder))]
internal static class RowStyler
{
    private const string StripeName = "OTHPlus_Stripe";
    private const string TintName = "OTHPlus_Tint";
    private const string AgeMarker = "<size=80%><color=#9AA4AE>  · ";
    private static int _diagRows;
    private static bool _loggedError;

    public static Color ColorFor(ServerStatus s) => Parse(s switch
    {
        ServerStatus.Private => ModConfig.ColorPrivate.Value,
        ServerStatus.Full => ModConfig.ColorFull.Value,
        ServerStatus.Incompatible => ModConfig.ColorIncompatible.Value,
        _ => ModConfig.ColorPublic.Value,
    });

    // Own "#RRGGBB" parser: ColorUtility may be stripped from the IL2CPP build.
    private static Color Parse(string html)
    {
        var hex = (html ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Color.white;
        return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }

    private static void Postfix(SessionInfoListAdapter __instance, SessionInfoItemViewsHolder newOrRecycled)
    {
        try { Style(__instance, newOrRecycled); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            Plugin.Logger.LogError("Row styling failed (further errors suppressed): " + e);
        }
    }

    private static void Style(SessionInfoListAdapter adapter, SessionInfoItemViewsHolder vh)
    {
        var data = adapter.Data;
        int idx = vh.ItemIndex;
        if (data == null || idx < 0 || idx >= data.Count) return;
        var info = data[idx];
        var session = info?.FusionSessionInfo;
        if (session == null) return;

        var status = Classify(info, session);
        var root = vh.root;
        if (root != null)
        {
            bool on = ModConfig.ColorRows.Value;
            var color = ColorFor(status);
            var tint = GetOrCreate(root, TintName, stripe: false);
            var stripe = GetOrCreate(root, StripeName, stripe: true);
            tint.gameObject.SetActive(on);
            stripe.gameObject.SetActive(on);
            tint.color = new Color(color.r, color.g, color.b, 0.12f);
            stripe.color = color;
        }

        var label = vh.sessionListItem?.SessionNameLabel;
        if (label != null)
        {
            string text = label.text ?? "";
            int cut = text.IndexOf(AgeMarker, StringComparison.Ordinal);
            if (cut >= 0) text = text.Substring(0, cut);
            string age = ModConfig.ShowAge.Value ? FirstSeenTracker.AgeText(session.Name) : null;
            if (age != null)
            {
                label.richText = true;
                text += AgeMarker + age + "</color></size>";
            }
            if (text != label.text) label.text = text;
        }
    }

    public static ServerStatus Classify(OverlandSessionInfo info, Fusion.SessionInfo session)
    {
        var props = info.RoomProperties;
        string ver = props?.GameVersion;
        // Not OverlandSessionInfo.GameVersionsMatch: it throws on some inputs (Substring out of range).
        string local = LocalVersion();
        bool versionOk = VersionsMatch(info, ver, local);

        if (_diagRows < 5)
        {
            _diagRows++;
            Plugin.Logger.LogInfo($"[row] '{info.RoomDisplayName}' players={session.PlayerCount}/{session.MaxPlayers} open={session.IsOpen} " +
                                  $"private={props?.PrivateServer} mods={props?.AllowMods} ver='{ver}' local='{local}' match={versionOk}");
        }

        if (!versionOk) return ServerStatus.Incompatible;
        if (!session.IsOpen || (session.MaxPlayers > 0 && session.PlayerCount >= session.MaxPlayers)) return ServerStatus.Full;
        if (props != null && props.PrivateServer) return ServerStatus.Private;
        return ServerStatus.Public;
    }

    private static string _localVersion;

    /// <summary>The game's own network version (e.g. "26.10.14_0a7187a"); Application.version is a placeholder.</summary>
    private static string LocalVersion()
    {
        if (!string.IsNullOrEmpty(_localVersion)) return _localVersion;
        try { _localVersion = OverlandNetworkRunner.GameVersion; }
        catch { }
        return _localVersion;
    }

    private static bool VersionsMatch(OverlandSessionInfo info, string remote, string local)
    {
        if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(local)) return true; // unknown: don't grey it out
        // The game's own rule first; it throws on malformed input, so fall back to comparing the date part.
        try { return info.GameVersionsMatch(local); }
        catch { }
        return remote.Split('_')[0].Trim() == local.Split('_')[0].Trim();
    }

    private static Image GetOrCreate(RectTransform root, string name, bool stripe)
    {
        var existing = root.Find(name);
        if (existing != null) return existing.GetComponent<Image>();

        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        var img = go.AddComponent<Image>();
        img.raycastTarget = false;
        go.AddComponent<LayoutElement>().ignoreLayout = true;

        var rt = go.GetComponent<RectTransform>();
        if (stripe)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(6f, 0f);
        }
        else
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
        }
        rt.anchoredPosition = Vector2.zero;
        go.transform.SetAsLastSibling();
        return img;
    }
}
