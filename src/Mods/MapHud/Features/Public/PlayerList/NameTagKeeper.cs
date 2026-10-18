using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using Object = UnityEngine.Object;
using OverTheHillPlus.MapHud;

namespace OverTheHillPlus.Features.Public.PlayerList;

/// <summary>
/// Name tags above other players' cars in big lobbies. Decoded from the game (Cpp2IL):
/// - FusionPlayer.Spawned creates the tag ("Prefabs/UI/PlayerNameCanvas") for every remote car with an input authority,
///   but only after invoking the static PlayerSpawned event; if anything in there throws for a player beyond the
///   4 slots, the tag is never created. Here a missing tag is restored by cloning another car's tag.
/// - PlayerNameBillboard.RefreshPlayerInfo (name from the 7-entry name table, colour from the 4-entry colour table) only
///   runs when the car spawns. Names that arrive later, or a colour slot gained when an extra player is promoted into
///   a free slot, never reach the tag. Here the tag is refreshed when that player's name or colour slot changes.
/// Purely local UI; works for host and guests.
/// </summary>
internal static class NameTagKeeper
{
    public static int Restored, Refreshed;

    private const float Interval = 1.5f;
    private const float MissingGrace = 4f;

    private static float _next;
    private static readonly Dictionary<IntPtr, float> MissingSince = new();
    private static readonly Dictionary<IntPtr, string> LastSignature = new();
    private static readonly Dictionary<IntPtr, GameObject> RestoredRoots = new(); // car -> the tag we created

    public static void Tick()
    {
        if (!PlayerListFeature.NameTags.Value || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + Interval;

        if (!PlayerListRoster.InMultiplayerSession)
        {
            MissingSince.Clear();
            LastSignature.Clear();
            RestoredRoots.Clear();
            return;
        }

        var sp = OverlandSessionPlayers.Instance;
        if (sp == null) return;
        var cars = PlayerListRoster.FusionPlayers();

        PlayerNameBillboard template = null;
        var alive = new HashSet<IntPtr>();
        foreach (var fp in cars)
        {
            try
            {
                if (fp == null || fp.WasCollected) continue;
                alive.Add(fp.Pointer);
                var b = fp.playerNameBillboard;
                if (template == null && b != null && !b.WasCollected) template = b;
            }
            catch (Exception ex) { DebugLog.Error("maphud", ex, "NameTagKeeper.Tick"); }
        }

        foreach (var fp in cars)
        {
            PlayerRef owner;
            try
            {
                if (fp == null || fp.WasCollected || fp.Object == null) continue;
                if (fp.HasInputAuthority) continue; // our own car never has a tag
                owner = fp.Object.InputAuthority;
            }
            catch (Exception ex) { DebugLog.Error("maphud", ex, "NameTagKeeper.Tick"); continue; }
            if (!owner.IsRealPlayer) continue;

            PlayerNameBillboard tag = null;
            try { tag = fp.playerNameBillboard; } catch (Exception ex) { DebugLog.Error("maphud", ex, "NameTagKeeper.Tick"); }

            if (tag == null || tag.WasCollected) Restore(fp, owner, template);
            else
            {
                MissingSince.Remove(fp.Pointer);
                KeepFresh(sp, tag, owner);
            }
        }

        CleanUp(alive);
    }

    private static void KeepFresh(OverlandSessionPlayers sp, PlayerNameBillboard tag, PlayerRef owner)
    {
        string expected = PlayerListRoster.NameOf(sp, owner);
        string signature = expected + "|" + PlayerListRoster.ColorSlot(sp, owner);
        bool seen = LastSignature.TryGetValue(tag.Pointer, out var old);
        if (seen && old == signature) return; // nothing changed for this player since the last look
        LastSignature[tag.Pointer] = signature;

        string shown = null;
        try { shown = tag.playerNameText != null ? tag.playerNameText.text : null; } catch (Exception ex) { DebugLog.Error("maphud", ex, "NameTagKeeper.KeepFresh"); }
        if (!seen && shown == expected) return; // first look and the game's tag is already right

        try
        {
            tag.RefreshPlayerInfo();
            Refreshed++;
        }
        catch (Exception e) { DebugLog.Error("maphud", e, "NameTagKeeper.KeepFresh"); PlayerListRoster.LogOnce("refresh", "refreshing a name tag: " + e.Message); }
    }

    private static void Restore(FusionPlayer fp, PlayerRef owner, PlayerNameBillboard template)
    {
        float now = Time.unscaledTime;
        if (!MissingSince.TryGetValue(fp.Pointer, out var since))
        {
            MissingSince[fp.Pointer] = now; // give the game's own Spawned a moment first
            return;
        }
        if (now - since < MissingGrace) return;
        if (template == null)
        {
            PlayerListRoster.LogOnce("notemplate", "a car has no name tag and there is no other tag to copy yet");
            return;
        }

        try
        {
            var root = TagRoot(template);
            var copy = Object.Instantiate(root, root.transform.parent);
            copy.name = "PlayerNameCanvas_OTHPlus";
            var tag = copy.GetComponentInChildren<PlayerNameBillboard>(true);
            if (tag == null)
            {
                Object.Destroy(copy);
                PlayerListRoster.LogOnce("notag", "copied name tag has no PlayerNameBillboard");
                return;
            }
            tag.Set(owner, fp.transform); // before its Start(), which tracks the car and fills name/colour
            fp.playerNameBillboard = tag;
            copy.SetActive(true);
            RestoredRoots[fp.Pointer] = copy;
            MissingSince.Remove(fp.Pointer);
            Restored++;
            MapHudPlugin.Logger.LogInfo($"[PlayerList] Restored the missing name tag of player {owner.PlayerId}");
        }
        catch (Exception e)
        {
            DebugLog.Error("maphud", e, "NameTagKeeper.Restore");
            MissingSince[fp.Pointer] = now + 30f; // don't retry every pass
            PlayerListRoster.LogOnce("restore", "restoring a name tag: " + e.Message);
        }
    }

    /// <summary>The instantiated "PlayerNameCanvas" object the tag lives in (the tag itself if that can't be told).</summary>
    private static GameObject TagRoot(PlayerNameBillboard tag)
    {
        var t = tag.transform;
        Transform found = null;
        for (var cur = t; cur != null; cur = cur.parent)
        {
            string n = cur.gameObject.name ?? "";
            if (n.StartsWith("PlayerNameCanvas", StringComparison.Ordinal)) found = cur;
        }
        return (found ?? t).gameObject;
    }

    private static void CleanUp(HashSet<IntPtr> aliveCars)
    {
        if (RestoredRoots.Count > 0)
        {
            var gone = new List<IntPtr>();
            foreach (var kv in RestoredRoots)
                if (!aliveCars.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var k in gone)
            {
                try
                {
                    var go = RestoredRoots[k];
                    if (go != null && !go.WasCollected) Object.Destroy(go);
                }
                catch (Exception ex) { DebugLog.Error("maphud", ex, "NameTagKeeper.CleanUp"); }
                RestoredRoots.Remove(k);
            }
        }
        if (MissingSince.Count > 64) MissingSince.Clear();
        if (LastSignature.Count > 128) LastSignature.Clear();
    }
}
