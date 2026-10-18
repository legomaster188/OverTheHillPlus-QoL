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
    /// <summary>Logs the stock name label's layout once (before any styling), so the server-name styles know what
    /// fits: a name taller than the row is cut away whole (a size above 100% left the name blank for every player).</summary>
    private static System.Collections.Generic.List<string> LogLabelLayout(TMPro.TMP_Text label)
    {
        var names = new System.Collections.Generic.List<string>();
        try
        {
            var rect = label.rectTransform.rect;
            ServerBrowserPlugin.Logger.LogInfo($"[Browser+] server-name label layout: font '{(label.font != null ? label.font.name : "?")}' size {label.fontSize:0.#}" +
                $" (auto-size {label.enableAutoSizing}, min {label.fontSizeMin:0.#}, max {label.fontSizeMax:0.#}), box {rect.width:0}x{rect.height:0}," +
                $" overflow {label.overflowMode}, wrapping {label.textWrappingMode}, preferred height {label.preferredHeight:0.#}");
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMPro.TMP_FontAsset>()))
            {
                var f = o.TryCast<TMPro.TMP_FontAsset>();
                if (f != null && !names.Contains(f.name)) names.Add(f.name);
            }
            ServerBrowserPlugin.Logger.LogInfo($"[Browser+] TextMeshPro fonts loaded ({names.Count}): " + string.Join(", ", names));
        }
        catch (Exception e) { DebugLog.Error("serverbrowser", e, "RowStyler.LogLabelLayout"); }
        return names;
    }

    /// <summary>Which &lt;font="..."&gt; tags the stock label resolves (one character parsed = the tag was read; an
    /// unknown font leaves the tag as visible text), and whether &lt;sprite=0&gt; finds a sprite asset. Parsed on an
    /// inactive copy of the label, so the real row never changes.</summary>
    private static (System.Collections.Generic.List<string> Fonts, bool? Sprites) ProbeTags(TMPro.TMP_Text label, System.Collections.Generic.List<string> fonts)
    {
        var ok = new System.Collections.Generic.List<string>();
        bool? sprites = null;
        GameObject copy = null;
        try
        {
            copy = UnityEngine.Object.Instantiate(label.gameObject, label.transform.parent);
            copy.SetActive(false);
            copy.name = "OTH_TagProbe";
            var t = copy.GetComponent<TMPro.TMP_Text>();
            if (t == null) return (ok, null);
            t.richText = true;
            int Parsed(string text)
            {
                t.text = text;
                t.ForceMeshUpdate(true, true);
                return t.textInfo.characterCount;
            }
            foreach (var f in fonts)
                if (Parsed($"<font=\"{f}\">a</font>") == 1) ok.Add(f);
            if (Parsed("<sprite=0>") == 1)
                sprites = t.textInfo.characterInfo[0].elementType == TMPro.TMP_TextElementType.Sprite;
            else sprites = false;
        }
        catch (Exception e) { DebugLog.Error("serverbrowser", e, "RowStyler.ProbeTags"); }
        finally { if (copy != null) UnityEngine.Object.Destroy(copy); }
        return (ok, sprites);
    }

    private const string StripeName = "OTHPlus_Stripe";
    private const string TintName = "OTHPlus_Tint";
    private const string AgeMarker = "<size=80%><color=#9AA4AE>  · ";
    /// <summary>Optional rich-text tags (e.g. "FAV", "BIG 8") shown before the age; set by feature files.</summary>
    internal static Func<OverlandSessionInfo, Fusion.SessionInfo, string> ExtraTags;
    private static int _diagRows;
    private static bool _loggedError, _capsChecked;

    public static Color ColorFor(ServerStatus s) => Parse(s switch
    {
        ServerStatus.Private => ServerBrowserConfig.ColorPrivate.Value,
        ServerStatus.Full => ServerBrowserConfig.ColorFull.Value,
        ServerStatus.Incompatible => ServerBrowserConfig.ColorIncompatible.Value,
        _ => ServerBrowserConfig.ColorPublic.Value,
    });

    // Rows restyle on every scroll/recycle: remember the last parse per status colour string instead of
    // trimming/parsing (and allocating) for each row.
    private static readonly System.Collections.Generic.Dictionary<string, Color> Parsed = new();

    private static Color Parse(string html)
    {
        if (html == null) return Color.white;
        if (Parsed.TryGetValue(html, out var c)) return c;
        if (Parsed.Count > 32) Parsed.Clear(); // only grows if the colours are edited a lot
        return Parsed[html] = ParseHex(html);
    }

    // Own "#RRGGBB" parser: ColorUtility may be stripped from the IL2CPP build.
    private static Color ParseHex(string html)
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
            ServerBrowserPlugin.Logger.LogError("Row styling failed (further errors suppressed): " + e);
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
            bool on = ServerBrowserConfig.ColorRows.Value;
            var color = ColorFor(status);
            var tint = GetOrCreate(root, TintName, stripe: false);
            var stripe = GetOrCreate(root, StripeName, stripe: true);
            if (tint != null && stripe != null)
            {
                tint.gameObject.SetActive(on);
                stripe.gameObject.SetActive(on);
                tint.color = new Color(color.r, color.g, color.b, 0.12f);
                stripe.color = color;
            }
        }

        var label = vh.sessionListItem?.SessionNameLabel;
        if (label != null && !_capsChecked)
        {
            _capsChecked = true;
            try
            {
                bool rich = label.richText, ctrl = label.parseCtrlCharacters;
                var fonts = LogLabelLayout(label);
                var (renderable, sprites) = ProbeTags(label, fonts);
                Core.Shared.ServerListCaps.Save(rich && ctrl, $"name label: richText {rich}, parseCtrlCharacters {ctrl}", renderable, sprites);
                ServerBrowserPlugin.Logger.LogInfo($"[Browser+] the game's server-name labels: rich text {rich}, escape sequences {ctrl} -> " +
                    (rich && ctrl ? "escaped colours show for every player" : "names can't carry colours for players without mods"));
                ServerBrowserPlugin.Logger.LogInfo($"[Browser+] font tags the labels resolve ({renderable.Count} of {fonts.Count}): " +
                    (renderable.Count == 0 ? "none (an unknown font shows the tag as text)" : string.Join(", ", renderable)) +
                    $"; sprite tags: {(sprites == null ? "unknown" : sprites.Value ? "a sprite asset exists" : "no sprite asset")}");
            }
            catch (Exception e) { DebugLog.Error("serverbrowser", e, "RowStyler caps"); }
        }
        if (label != null)
        {
            string original = label.text ?? ""; // read once: each read marshals a new string across interop
            string text = original;
            int cut = text.IndexOf(AgeMarker, StringComparison.Ordinal);
            if (cut >= 0) text = text.Substring(0, cut);
            // The game strips colours from server names since its 10-16 update: show the host's styled name again.
            if (ServerBrowserConfig.RestoreNameColours != null && ServerBrowserConfig.RestoreNameColours.Value)
            {
                string rich = null;
                try { rich = RichName.FromSessionName(session.Name); } catch { }
                if (!string.IsNullOrEmpty(rich)) { text = rich; label.richText = true; }
            }
            string age = ServerBrowserConfig.ShowAge.Value ? FirstSeenTracker.AgeText(session.Name) : null;
            string tags = null;
            try { tags = ExtraTags?.Invoke(info, session); }
            catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "RowStyler.Style"); /* a feature's tags must never break row styling */ }
            if (age != null || !string.IsNullOrEmpty(tags))
            {
                label.richText = true;
                text += AgeMarker + (tags ?? "") + (age ?? "") + "</color></size>";
            }
            if (text != original) label.text = text;
        }
    }

    public static ServerStatus Classify(OverlandSessionInfo info, Fusion.SessionInfo session)
    {
        var props = info.RoomProperties;
        string ver = props?.GameVersion;
        // Not OverlandSessionInfo.GameVersionsMatch: it throws on some inputs (Substring out of range).
        string local = LocalVersion();
        bool versionOk = VersionsMatch(info, ver, local);

        if (DevDetails.On && _diagRows < 5)
        {
            _diagRows++;
            ServerBrowserPlugin.Logger.LogInfo($"[row] '{info.RoomDisplayName}' players={session.PlayerCount}/{session.MaxPlayers} open={session.IsOpen} " +
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
        catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "RowStyler.LocalVersion"); }
        return _localVersion;
    }

    private static bool VersionsMatch(OverlandSessionInfo info, string remote, string local)
    {
        if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(local)) return true; // unknown: don't grey it out
        // The game's own rule first; it throws on malformed input, so fall back to comparing the date part.
        try { return info.GameVersionsMatch(local); }
        catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "RowStyler.VersionsMatch"); }
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
