using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;
using OverTheHillPlus.BigLobbies;

namespace OverTheHillPlus.Multiplayer;

/// <summary>Registers <see cref="SlotKeeper"/>'s settings, tick and menu card.</summary>
internal sealed class SlotKeeperFeature : IFeature
{
    public void Register()
    {
        var f = BigLobbiesConfig.File;
        SlotKeeper.Enabled = f.Bind("Multiplayer", "SlotKeeper", true,
            "Host, lobbies above 4: only players with one of the game's 4 slots get an entry in the shared name table. " +
            "A name without a slot makes the lobby screen of players without this mod crash before it fades in " +
            "(stuck on the loading screen).");
        SlotKeeper.LendSlots = f.Bind("Multiplayer", "LendSlotsToJoiners", true,
            "Host: when all 4 slots are taken, a guest who runs this mod and has been in for a while gives their slot " +
            "to the player who is joining, so the joining player loads in like in a 4-player lobby.");
        Registry.OnTick(SlotKeeper.Tick);
        Registry.Section(Registry.PlusMode, "Lobby", SlotKeeper.DrawCard, 35);
    }
}

/// <summary>
/// Players 5 and up stuck on the loading screen (host log: AddPlayer throws IndexOutOfRange from the 5th player,
/// joiners rejoin again and again). What the game does with a player who has no slot (decoded with Cpp2IL, see
/// LobbyGarage): StateLobbyMenu.UpdateCarsStatus walks the name table and indexes the 4-entry lobby label list with
/// the player's slot, so one name without a slot throws there, and UpdateCarsStatusAndFadeIn never reaches FadeIn.
/// The mod patches that on modded machines only; players without the mod stay black.
///
/// Two host-side rules fix it for everyone, without touching the game on the joining player's side:
/// 1. A name only with a slot: the game's AddPlayer writes the name (and dev flag) before it looks for a slot, so a
///    player without a slot is left with a name. That entry (and the dev flag, which would otherwise fill its
///    7-entry table) is removed again right after AddPlayer and on every tick. Modded players still get the name
///    from OverflowNames; the game shows "Player N" where it has none.
/// 2. Slots go to players who are loading: right before AddPlayer, if all 4 slots are taken, a settled guest who
///    runs this mod (they draw extra players themselves) gives up their slot, and the game's own AddPlayer then
///    runs exactly like in a 4-player lobby (name, slot, colour, pop-up, connected event, no exception).
/// </summary>
internal static class SlotKeeper
{
    public static ConfigEntry<bool> Enabled, LendSlots;

    /// <summary>A guest must have been in this long before giving up their slot (still loading otherwise).</summary>
    private const float SettleSeconds = 30f;
    private const float TickInterval = 1f;

    public static int Lent, NamesHidden, NoLender;

    private static IntPtr _sessionPtr;
    private static float _nextTick;
    private static readonly Dictionary<int, float> JoinedAt = new(); // RawEncoded -> unscaled time AddPlayer ran
    private static readonly HashSet<string> Warned = new();

    private static bool On => Enabled == null || Enabled.Value;

    private static bool IsHost(OverlandSessionPlayers sp)
    {
        try
        {
            var r = sp.Runner;
            return r != null && r.IsServer && !r.IsSinglePlayer && sp.HasStateAuthority;
        }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SlotKeeper.IsHost"); return false; }
    }

    private static void ResetIfNewSession(OverlandSessionPlayers sp)
    {
        if (sp.Pointer == _sessionPtr) return;
        _sessionPtr = sp.Pointer;
        JoinedAt.Clear();
    }

    // ---- 2. slots for joining players ----

    /// <summary>Before the game's AddPlayer (after the table janitor's own pre-join cleanup).</summary>
    internal static void BeforeAddPlayer(OverlandSessionPlayers sp, PlayerRef joining)
    {
        if (!On || sp == null || !IsHost(sp)) return;
        try
        {
            ResetIfNewSession(sp);
            int raw = joining.RawEncoded;
            if (raw <= 0) return;
            JoinedAt[raw] = Time.unscaledTime;
            if (InOrder(sp, raw) || HasFreeSlot(sp)) return;
            if (LendSlots == null || !LendSlots.Value) return;

            int lender = PickLender(sp, raw);
            if (lender == 0)
            {
                NoLender++;
                WarnOnce("nolender", $"[Lobby] Slots: all 4 slots are taken and no settled guest with this mod can give one up: " +
                                     $"player {joining.PlayerId} joins without a slot (players without this mod may not load into big lobbies)");
                return;
            }
            Release(sp, lender);
            Lent++;
            BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Slots: player {lender - 1} (runs this mod) gave their slot to joining player {joining.PlayerId}");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SlotKeeper.BeforeAddPlayer"); WarnOnce("before", "[Lobby] Slots: pre-join step failed: " + e.Message); }
    }

    /// <summary>
    /// A slotted guest who runs this mod and has been in for a while, the longest-present first. Never the host,
    /// the joining player, or a guest whose car isn't there (still loading or leaving).
    /// </summary>
    private static int PickLender(OverlandSessionPlayers sp, int joiningRaw)
    {
        int local = 0;
        try { local = sp.Runner.LocalPlayer.RawEncoded; } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SlotKeeper.PickLender"); }
        var withCar = PlayersWithCars();
        float now = Time.unscaledTime;
        int best = 0;
        float bestSince = float.MaxValue;
        var order = sp.OrderedPlayerRefs;
        for (int i = 0; i < order.Length; i++)
        {
            var p = order.Get(i);
            int raw = p.RawEncoded;
            if (raw <= 0 || raw == joiningRaw || raw == local) continue;
            if (!withCar.Contains(raw)) continue;
            bool modded;
            try { modded = Net.IsPeer(p); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SlotKeeper.PickLender"); modded = false; }
            if (!modded) continue;
            // Unknown join time = in since before we started tracking: settled.
            float since = JoinedAt.TryGetValue(raw, out float t) ? t : float.MinValue;
            if (now - since < SettleSeconds) continue;
            if (since < bestSince)
            {
                bestSince = since;
                best = raw;
            }
        }
        return best;
    }

    /// <summary>Takes a player out of the slot and colour tables, and out of the name and dev tables (rule 1).</summary>
    private static void Release(OverlandSessionPlayers sp, int raw)
    {
        var order = sp.OrderedPlayerRefs;
        for (int i = 0; i < order.Length; i++)
            if (order.Get(i).RawEncoded == raw) order.Set(i, PlayerRef.None);
        var colors = sp.PlayerColors;
        for (int i = 0; i < colors.Length; i++)
            if (colors.Get(i).RawEncoded == raw) colors.Set(i, PlayerRef.None);
        HideName(sp, raw);
    }

    // ---- 1. a name only with a slot ----

    /// <summary>After the game's AddPlayer, also when it threw at the full slot table.</summary>
    internal static void AfterAddPlayer(OverlandSessionPlayers sp, PlayerRef joined)
    {
        if (!On || sp == null || !IsHost(sp)) return;
        try
        {
            int raw = joined.RawEncoded;
            if (raw <= 0 || InOrder(sp, raw)) return;
            if (HideName(sp, raw))
                BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Slots: player {joined.PlayerId} has no slot; their name is kept out of the game's " +
                                      "name table so players without this mod still load in");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SlotKeeper.AfterAddPlayer"); WarnOnce("after", "[Lobby] Slots: post-join step failed: " + e.Message); }
    }

    /// <summary>Removes the name and dev-flag entries of a player. True if anything was removed.</summary>
    private static bool HideName(OverlandSessionPlayers sp, int raw)
    {
        bool any = false;
        try
        {
            var names = sp.PlayerRefToNameDictionary;
            if (names.ContainsKey(raw - 1) && names.Remove(raw - 1)) any = true;
        }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SlotKeeper.HideName"); }
        try
        {
            var dev = sp.PlayerRefToDevTeamMembership;
            var p = PlayerRef.FromEncoded(raw);
            if (dev.ContainsKey(p) && dev.Remove(p)) any = true;
        }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SlotKeeper.HideName"); }
        if (any) NamesHidden++;
        return any;
    }

    /// <summary>Host: every second, drop name/dev entries of players without a slot (e.g. after a slot was lent).</summary>
    public static void Tick()
    {
        if (!On || Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + TickInterval;
        try
        {
            var sp = OverlandSessionPlayers.Instance;
            if (sp == null || !IsHost(sp)) return;
            ResetIfNewSession(sp);
            var order = sp.OrderedPlayerRefs;
            var slotted = new HashSet<int>();
            for (int i = 0; i < order.Length; i++) slotted.Add(order.Get(i).RawEncoded);

            var names = sp.PlayerRefToNameDictionary;
            var dev = sp.PlayerRefToDevTeamMembership;
            int max = Features.Public.PlayerTables.PlayerTables.ScanMax;
            int hidden = 0;
            for (int raw = 1; raw <= max; raw++)
            {
                if (slotted.Contains(raw)) continue;
                bool hasName = false, hasDev = false;
                try { hasName = names.ContainsKey(raw - 1); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SlotKeeper.Tick"); }
                try { hasDev = dev.ContainsKey(PlayerRef.FromEncoded(raw)); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SlotKeeper.Tick"); }
                if ((hasName || hasDev) && HideName(sp, raw)) hidden++;
            }
            if (hidden > 0)
            {
                BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Slots: removed {hidden} name entr{(hidden == 1 ? "y" : "ies")} of players without a slot");
                try { OverlandSessionPlayers.InvokePlayerDataUpdated(); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SlotKeeper.Tick"); }
            }
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SlotKeeper.Tick"); WarnOnce("tick", "[Lobby] Slots: check failed: " + e.Message); }
    }

    // ---- helpers ----

    private static bool InOrder(OverlandSessionPlayers sp, int raw)
    {
        var order = sp.OrderedPlayerRefs;
        for (int i = 0; i < order.Length; i++)
            if (order.Get(i).RawEncoded == raw) return true;
        return false;
    }

    private static bool HasFreeSlot(OverlandSessionPlayers sp)
    {
        var order = sp.OrderedPlayerRefs;
        for (int i = 0; i < order.Length; i++)
            if (order.Get(i).RawEncoded == 0) return true;
        return false;
    }

    private static HashSet<int> PlayersWithCars()
    {
        var set = new HashSet<int>();
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                var p = fp.Object.InputAuthority;
                if (p.IsRealPlayer) set.Add(p.RawEncoded);
            }
            catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SlotKeeper.PlayersWithCars"); }
        }
        return set;
    }

    private static void WarnOnce(string key, string message)
    {
        if (Warned.Add(key)) BigLobbiesPlugin.Logger.LogWarning(message + " (logged once)");
    }

    internal static void DrawCard()
    {
        Menu.Card("Loading into big lobbies", "Host: keeps players 5 and up from getting stuck on the loading screen.", () =>
        {
            Menu.Switch(Enabled, "Names only with a slot", "Players without this mod can load into lobbies above 4", true);
            Menu.Switch(LendSlots, "Joining players get a slot", "A guest with this mod gives theirs up while someone loads in", On);
            Menu.KeyValue("Slots given to joiners", Lent.ToString());
            Menu.KeyValue("Name entries kept out", NamesHidden.ToString());
        });
    }
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.AddPlayer))]
internal static class SlotKeeperAddPlayerPatch
{
    // After the janitor's pre-join cleanup (Priority.First + 100), so only a slot that is really taken gets lent.
    [HarmonyPriority(Priority.Low)]
    private static void Prefix(OverlandSessionPlayers __instance, PlayerRef playerRef) => SlotKeeper.BeforeAddPlayer(__instance, playerRef);

    // Observes only (void): AddPlayerGuard decides what happens to the exception.
    [HarmonyPriority(Priority.Low)]
    private static void Finalizer(OverlandSessionPlayers __instance, PlayerRef playerRef) => SlotKeeper.AfterAddPlayer(__instance, playerRef);
}
