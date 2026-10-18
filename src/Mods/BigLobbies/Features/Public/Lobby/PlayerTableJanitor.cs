using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using Fusion;
using HarmonyLib;
using Il2CppInterop.Runtime;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.UI;
using UnityEngine;
using Object = UnityEngine.Object;
using OverTheHillPlus.BigLobbies;

namespace OverTheHillPlus.Features.Public.Lobby;

/// <summary>
/// Keeps the game's networked player tables honest when people leave, reconnect or the host changes sessions,
/// which matters most above 4 players (a stale entry steals one of only 4 slots).
///
/// What the game really does (decoded with Cpp2IL):
/// - OverlandNetworkRunner.OnPlayerLeft (host only): despawns the car, forgets it, then calls
///   OverlandSessionPlayers.RemovePlayer. If anything before that throws, RemovePlayer never runs and the leaver
///   stays in the tables for the rest of the session.
/// - RemovePlayer: "left" pop-up, removes the name (key RawEncoded-1), supporter/dev/busy flags, compacts
///   OrderedPlayerRefs, clears the PlayerColors entry, destroys the lobby dummy, then
///   MapNetworkManager.Instance.RPC_DestroyPlayerOwnedObjects and finally (tail call) RPC_DisconnectedPlayer.
///   With no MapNetworkManager (lobby), it throws a NullReferenceException right before RPC_DisconnectedPlayer, so
///   the "player disconnected" event (camp ends, name tag cleanup) and our overflow promotion are skipped.
/// - Host migration is stubbed out in this build: OnHostMigration and PushHostMigrationSnapshot are empty and
///   playersConnectionTokenHashToNetworkObject is never filled, so the reconnection-token cleanup in Update and
///   WaitForReconnection (which only waits for the cloud connection) never touch the player tables.
///
/// What this adds (host only, all guarded):
/// - A periodic sweep: any table entry for a player Fusion no longer has in the session (after a grace period)
///   is removed the game's own way (RemovePlayer, with the duplicate "left" pop-up suppressed), then any leftovers
///   are cleared directly; duplicates and garbage entries are dropped; slotted players without a colour get one;
///   waiting overflow players are promoted into freed slots.
/// - Right before AddPlayer, if all 4 slots look taken, an immediate sweep, so a stale entry can't push a new
///   player into overflow ("lobby full" with only 2 people).
/// - Completes RemovePlayer when it stops at the missing MapNetworkManager (disconnect event + promotion).
/// - Optional: despawns cars left behind by players who are gone.
/// </summary>
internal sealed class PlayerTableJanitorFeature : IFeature
{
    public void Register()
    {
        var f = BigLobbiesConfig.File;
        const string s = "Player Tables";
        Janitor.AutoClean = f.Bind(s, "AutoClean", true,
            "Host: remove players who already left from the game's player tables (fixes 'lobby full' with free slots, wrong colours/names, invisible players).");
        Janitor.GraceSeconds = f.Bind(s, "GraceSeconds", 4f,
            "How long a player must be gone from the session before their leftover entries are cleaned (1-15 s).");
        Janitor.CompleteLeaves = f.Bind(s, "CompleteLeaves", true,
            "Host: when the game's leave handling stops early (in the lobby), still send the 'player disconnected' event and move waiting players into the freed slot.");
        Janitor.RemoveGhostCars = f.Bind(s, "RemoveGhostCars", false,
            "Host: despawn cars that belong to players who are no longer in the session.");

        Registry.OnTick(Janitor.Tick);
        Registry.Section(Registry.PlusMode, "Lobby", Janitor.DrawCard, 40);
    }
}

internal static class Janitor
{
    public static ConfigEntry<bool> AutoClean, CompleteLeaves, RemoveGhostCars;
    public static ConfigEntry<float> GraceSeconds;

    /// <summary>Highest PlayerRef raw value probed in the keyed tables: follows the highest player id seen (ids grow
    /// as players join and leave), never below 64.</summary>
    private static int ProbeMax => Math.Max(64, PlayerTables.PlayerTables.ScanMax);
    private const float ScanInterval = 1.5f;

    /// <summary>True while the janitor itself calls RemovePlayer: hides the duplicate "left" pop-up.</summary>
    internal static bool SuppressLeftPopup;
    private static bool _inJanitorRemove;

    private static bool _sweepNow;
    private static float _nextScan;
    private static IntPtr _sessionPtr;
    private static readonly Dictionary<int, float> GoneSince = new();
    private static readonly HashSet<int> Joined = new(); // raws AddPlayer was called for this session
    private static readonly HashSet<string> Warned = new();

    public static int CleanedSlots, CleanedColors, CleanedNames, CleanedFlags, GameRemovals, Promoted, LeavesCompleted, GhostCars, Sweeps;

    // ---- snapshot for the menu (refreshed by the tick, read by OnGUI) ----
    private sealed class Row
    {
        public int Raw;
        public string Name;
        public bool Present;
        public bool HasColor;
    }

    private static readonly List<Row> Slots = new();
    private static readonly List<Row> Overflow = new();
    private static int _namesCount = -1, _namesCap = -1;
    private static string _validityNote = "";

    // ======================================================================================================
    // tick
    // ======================================================================================================

    public static void Tick()
    {
        float now = Time.unscaledTime;
        if (!_sweepNow && now < _nextScan) return;
        _nextScan = now + ScanInterval;

        var sp = OverlandSessionPlayers.Instance;
        if (sp == null)
        {
            ResetSession(IntPtr.Zero);
            return;
        }
        if (sp.Pointer != _sessionPtr) ResetSession(sp.Pointer);

        NetworkRunner runner;
        try
        {
            if (sp.Object == null) return; // not spawned yet: networked properties aren't valid
            runner = sp.Runner;
        }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.Tick"); return; }
        if (runner == null) return;

        var v = Validity.Build(runner);
        bool writer = CanWrite(sp, runner);
        TakeSnapshot(sp, v, writer);

        bool force = _sweepNow;
        _sweepNow = false;
        if (!force && !AutoClean.Value) return;
        if (!writer) return;
        Sweep(sp, runner, v, force ? 0f : Mathf.Clamp(GraceSeconds.Value, 1f, 15f), callGame: true);
    }

    private static void ResetSession(IntPtr ptr)
    {
        if (ptr == _sessionPtr) return;
        _sessionPtr = ptr;
        GoneSince.Clear();
        Joined.Clear();
        Slots.Clear();
        Overflow.Clear();
        _namesCount = _namesCap = -1;
    }

    private static bool CanWrite(OverlandSessionPlayers sp, NetworkRunner runner)
    {
        try { return runner.IsServer && !runner.IsSinglePlayer && sp.HasStateAuthority; }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.CanWrite"); return false; }
    }

    /// <summary>Menu button: clean now, ignoring the grace period. Runs on the next tick.</summary>
    public static string RequestCleanNow()
    {
        var sp = OverlandSessionPlayers.Instance;
        if (sp == null) return "Blocked: not in a session.";
        try { if (!CanWrite(sp, sp.Runner)) return "Blocked: only the host can fix the player tables."; }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.RequestCleanNow"); return "Blocked: session not ready."; }
        _sweepNow = true;
        return "Player tables: cleanup queued.";
    }

    // ======================================================================================================
    // who is really in the session
    // ======================================================================================================

    private sealed class Validity
    {
        public NetworkRunner Runner;
        public int Local;
        public bool UseApi;
        public HashSet<int> Active; // from runner.ActivePlayers (null when it couldn't be read)
        public bool Usable;

        public static Validity Build(NetworkRunner runner)
        {
            var v = new Validity { Runner = runner };
            try { v.Local = runner.LocalPlayer.RawEncoded; } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Validity.Build"); v.Local = 0; }

            try
            {
                var set = new HashSet<int>();
                var en = runner.ActivePlayers?.GetEnumerator();
                var mover = en?.TryCast<Il2CppSystem.Collections.IEnumerator>();
                if (mover != null)
                {
                    int guard = 0;
                    while (mover.MoveNext() && guard++ < 256) set.Add(en.Current.RawEncoded);
                    if (set.Count > 0) v.Active = set;
                }
            }
            catch (Exception e) { DebugLog.Error("biglobbies", e, "Validity.Build"); WarnOnce("active", "[Lobby] Player tables: can't read ActivePlayers: " + e.Message); }

            try { v.UseApi = v.Local > 0 && runner.IsPlayerValid(PlayerRef.FromEncoded(v.Local)); }
            catch (Exception e) { DebugLog.Error("biglobbies", e, "Validity.Build"); WarnOnce("api", "[Lobby] Player tables: IsPlayerValid failed: " + e.Message); }

            // Never judge players without a trustworthy source: wrongly "gone" would wipe real players.
            v.Usable = v.UseApi || v.Active != null;
            _validityNote = v.UseApi && v.Active != null ? "Fusion session list (2 sources)"
                : v.UseApi ? "Fusion IsPlayerValid"
                : v.Active != null ? "Fusion ActivePlayers"
                : "unavailable: cleanup paused";
            return v;
        }

        public bool Present(int raw)
        {
            if (raw <= 0) return false;
            if (raw == Local) return true;
            if (Active != null && Active.Contains(raw)) return true;
            if (!UseApi) return false;
            try { return Runner.IsPlayerValid(PlayerRef.FromEncoded(raw)); }
            catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Validity.Present"); return true; } // unknown: keep
        }
    }

    /// <summary>True once a player has been gone for at least <paramref name="grace"/> seconds.</summary>
    private static bool GoneLongEnough(Validity v, int raw, float grace)
    {
        if (raw <= 0) return false;
        if (v.Present(raw))
        {
            GoneSince.Remove(raw);
            return false;
        }
        float now = Time.unscaledTime;
        if (!GoneSince.TryGetValue(raw, out float since))
        {
            since = now;
            GoneSince[raw] = now;
        }
        return now - since >= grace;
    }

    // ======================================================================================================
    // the sweep
    // ======================================================================================================

    /// <summary>
    /// Finds entries for players who are gone and removes them. <paramref name="callGame"/> lets the game's own
    /// RemovePlayer do the full cleanup first (lobby dummy, owned objects, disconnect event); it is off when we're
    /// already inside a game call (AddPlayer).
    /// </summary>
    private static int Sweep(OverlandSessionPlayers sp, NetworkRunner runner, Validity v, float grace, bool callGame)
    {
        if (!v.Usable)
        {
            WarnOnce("unusable", "[Lobby] Player tables: no reliable player list from Fusion, cleanup skipped.");
            return 0;
        }

        int changes = 0;
        try
        {
            // 1. collect everyone the tables mention
            var mentioned = new HashSet<int>();
            var order = sp.OrderedPlayerRefs;
            for (int i = 0; i < order.Length; i++) mentioned.Add(order.Get(i).RawEncoded);
            foreach (int raw in mentioned) PlayerTables.PlayerTables.NoteSeen(raw);
            var colors = sp.PlayerColors;
            for (int i = 0; i < colors.Length; i++) mentioned.Add(colors.Get(i).RawEncoded);
            var names = sp.PlayerRefToNameDictionary;
            var supporter = sp.PlayerRefToSupporterEditionOwnership;
            var dev = sp.PlayerRefToDevTeamMembership;
            var busy = sp.BusyPlayerRefToBusyReason;
            int probeMax = ProbeMax;
            for (int raw = 1; raw <= probeMax; raw++)
            {
                if (mentioned.Contains(raw)) continue;
                var p = PlayerRef.FromEncoded(raw);
                if (SafeContains(names, raw - 1) || SafeContains(supporter, p) || SafeContains(dev, p) || SafeContains(busy, p))
                    mentioned.Add(raw);
            }

            var gone = new HashSet<int>();
            foreach (int raw in mentioned)
                if (raw > 0 && GoneLongEnough(v, raw, grace)) gone.Add(raw);
            // forget timers for players nobody mentions any more
            if (GoneSince.Count > 0)
                foreach (var raw in new List<int>(GoneSince.Keys))
                    if (!mentioned.Contains(raw)) GoneSince.Remove(raw);

            // 2. let the game remove them properly (pop-up suppressed: they already "left")
            if (callGame)
            {
                foreach (int raw in gone)
                {
                    if (!InOrder(sp, raw) && !SafeContains(sp.PlayerRefToNameDictionary, raw - 1)) continue;
                    _inJanitorRemove = true;
                    SuppressLeftPopup = true;
                    try { sp.RemovePlayer(PlayerRef.FromEncoded(raw)); GameRemovals++; }
                    catch (Exception e) { DebugLog.Error("biglobbies", e, "Janitor.Sweep"); WarnOnce("remove", "[Lobby] Player tables: RemovePlayer failed: " + e.Message); }
                    finally
                    {
                        SuppressLeftPopup = false;
                        _inJanitorRemove = false;
                    }
                    Joined.Remove(raw);
                    BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Player tables: player {raw - 1} had left; removed the game's way");
                }
            }

            // 3. whatever is still there: clear it directly
            changes += CompactOrder(sp, gone);
            changes += FixColors(sp, gone);
            foreach (int raw in gone)
            {
                var p = PlayerRef.FromEncoded(raw);
                if (SafeRemove(sp.PlayerRefToNameDictionary, raw - 1)) { CleanedNames++; changes++; }
                if (SafeRemove(sp.PlayerRefToSupporterEditionOwnership, p)) { CleanedFlags++; changes++; }
                if (SafeRemove(sp.PlayerRefToDevTeamMembership, p)) { CleanedFlags++; changes++; }
                if (SafeRemove(sp.BusyPlayerRefToBusyReason, p)) { CleanedFlags++; changes++; }
                Joined.Remove(raw);
            }

            // 4. waiting players into free slots
            changes += PromoteWaiting(sp, v);
            if (callGame && BigLobbiesConfig.LobbyGuards.Value && HasFreeSlot(sp)) BigLobby.PromoteOverflow(sp);

            // 5. optional: cars of players who are gone
            if (callGame && RemoveGhostCars.Value) changes += DespawnGhostCars(runner, v, grace);

            if (changes > 0)
            {
                Sweeps++;
                DebugLog.Info(BigLobbiesPlugin.ModId, $"player tables swept: {changes} fix(es) ({Describe(sp)}); {GameRemovals} game removals, {CleanedNames} names and {CleanedFlags} flags cleaned so far");
                try { OverlandSessionPlayers.InvokePlayerDataUpdated(); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.Sweep"); }
                BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Player tables: {changes} fix(es) applied ({Describe(sp)})");
            }
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "Janitor.Sweep"); WarnOnce("sweep", "[Lobby] Player tables sweep failed: " + e); }
        return changes;
    }

    /// <summary>Drops gone, duplicate and garbage entries from the slot order, keeping everyone else in order.</summary>
    private static int CompactOrder(OverlandSessionPlayers sp, HashSet<int> gone)
    {
        var order = sp.OrderedPlayerRefs;
        int n = order.Length;
        var keep = new List<int>(n);
        var seen = new HashSet<int>();
        int dropped = 0;
        for (int i = 0; i < n; i++)
        {
            int raw = order.Get(i).RawEncoded;
            if (raw == 0) continue;
            if (raw < 0 || gone.Contains(raw) || !seen.Add(raw)) { dropped++; continue; }
            keep.Add(raw);
        }
        if (dropped == 0)
        {
            // also close gaps (the game expects players packed at the front)
            bool gap = false, sawEmpty = false;
            for (int i = 0; i < n; i++)
            {
                int raw = order.Get(i).RawEncoded;
                if (raw == 0) sawEmpty = true;
                else if (sawEmpty) gap = true;
            }
            if (!gap) return 0;
        }
        for (int i = 0; i < n; i++)
        {
            int want = i < keep.Count ? keep[i] : 0;
            if (order.Get(i).RawEncoded != want) order.Set(i, PlayerRef.FromEncoded(want));
        }
        CleanedSlots += dropped;
        return Math.Max(dropped, 1);
    }

    /// <summary>Clears colour entries of gone/duplicate players and gives every slotted player a colour.</summary>
    private static int FixColors(OverlandSessionPlayers sp, HashSet<int> gone)
    {
        int changes = 0;
        var colors = sp.PlayerColors;
        var seen = new HashSet<int>();
        for (int i = 0; i < colors.Length; i++)
        {
            int raw = colors.Get(i).RawEncoded;
            if (raw == 0) continue;
            if (raw < 0 || gone.Contains(raw) || !seen.Add(raw))
            {
                colors.Set(i, PlayerRef.None);
                CleanedColors++;
                changes++;
            }
        }
        var order = sp.OrderedPlayerRefs;
        for (int i = 0; i < order.Length; i++)
        {
            int raw = order.Get(i).RawEncoded;
            if (raw <= 0 || seen.Contains(raw)) continue;
            for (int c = 0; c < colors.Length; c++)
            {
                if (colors.Get(c).RawEncoded != 0) continue;
                colors.Set(c, PlayerRef.FromEncoded(raw));
                seen.Add(raw);
                changes++;
                break;
            }
        }
        return changes;
    }

    /// <summary>Players AddPlayer saw who are still here but have no slot: give them freed slots, oldest first.</summary>
    private static int PromoteWaiting(OverlandSessionPlayers sp, Validity v)
    {
        if (Joined.Count == 0 || !HasFreeSlot(sp)) return 0;
        int changes = 0;
        var waiting = new List<int>();
        foreach (int raw in Joined)
            if (!InOrder(sp, raw) && v.Present(raw)) waiting.Add(raw);
        waiting.Sort();
        foreach (int raw in waiting)
        {
            if (!TakeSlot(sp, raw)) break;
            Promoted++;
            changes++;
            BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Player tables: player {raw - 1} moved into a free slot");
        }
        return changes;
    }

    private static bool TakeSlot(OverlandSessionPlayers sp, int raw)
    {
        var order = sp.OrderedPlayerRefs;
        int free = -1;
        for (int i = 0; i < order.Length; i++)
        {
            int r = order.Get(i).RawEncoded;
            if (r == raw) return true;
            if (free < 0 && r == 0) free = i;
        }
        if (free < 0) return false;
        order.Set(free, PlayerRef.FromEncoded(raw));
        var colors = sp.PlayerColors;
        for (int c = 0; c < colors.Length; c++)
            if (colors.Get(c).RawEncoded == raw) return true;
        for (int c = 0; c < colors.Length; c++)
        {
            if (colors.Get(c).RawEncoded != 0) continue;
            colors.Set(c, PlayerRef.FromEncoded(raw));
            break;
        }
        return true;
    }

    private static int DespawnGhostCars(NetworkRunner runner, Validity v, float grace)
    {
        int removed = 0;
        try
        {
            var onr = OverlandNetworkRunner.Instance;
            var spawned = onr?.SpawnedCharacters;
            if (spawned != null)
            {
                var stale = new List<PlayerRef>();
                foreach (var kv in spawned)
                    if (GoneLongEnough(v, kv.Key.RawEncoded, grace)) stale.Add(kv.Key);
                foreach (var p in stale)
                {
                    NetworkObject obj = null;
                    try { obj = spawned[p]; } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.DespawnGhostCars"); }
                    spawned.Remove(p);
                    if (TryDespawn(runner, obj)) removed++;
                    BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Player tables: removed the car of departed player {p.PlayerId}");
                }
            }

            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FusionPlayer>()))
            {
                var fp = o.TryCast<FusionPlayer>();
                var no = fp?.Object;
                if (no == null) continue;
                int raw;
                try { raw = no.InputAuthority.RawEncoded; } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.DespawnGhostCars"); continue; }
                if (raw <= 0 || !GoneLongEnough(v, raw, grace)) continue;
                if (TryDespawn(runner, no))
                {
                    removed++;
                    BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Player tables: removed a leftover car of departed player {raw - 1}");
                }
            }
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "Janitor.DespawnGhostCars"); WarnOnce("ghost", "[Lobby] Player tables: ghost car cleanup failed: " + e.Message); }
        GhostCars += removed;
        return removed;
    }

    private static bool TryDespawn(NetworkRunner runner, NetworkObject obj)
    {
        if (obj == null) return false;
        try
        {
            if (!obj.IsValid || !obj.HasStateAuthority) return false;
            runner.Despawn(obj);
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("biglobbies", e, "Janitor.TryDespawn");
            WarnOnce("despawn", "[Lobby] Player tables: despawn failed: " + e.Message);
            return false;
        }
    }

    // ======================================================================================================
    // hooks called from the Harmony patches below
    // ======================================================================================================

    /// <summary>Before AddPlayer: if every slot looks taken, drop entries of players who are gone right now.</summary>
    internal static void BeforeAddPlayer(OverlandSessionPlayers sp, PlayerRef player)
    {
        if (!AutoClean.Value || sp == null) return;
        try
        {
            var runner = sp.Runner;
            if (runner == null || !CanWrite(sp, runner)) return;
            if (InOrder(sp, player.RawEncoded) || HasFreeSlot(sp)) return;
            var v = Validity.Build(runner);
            int fixes = Sweep(sp, runner, v, 0f, callGame: false);
            if (fixes > 0) BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Player tables: freed slot(s) for joining player {player.PlayerId}");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "Janitor.BeforeAddPlayer"); WarnOnce("beforeAdd", "[Lobby] Player tables: pre-join cleanup failed: " + e.Message); }
    }

    internal static void AfterAddPlayer(PlayerRef player)
    {
        DebugLog.Verbose(BigLobbiesPlugin.ModId, () => $"player {player.PlayerId} added to the session tables");
        if (player.RawEncoded > 0) Joined.Add(player.RawEncoded);
    }

    /// <summary>
    /// After RemovePlayer. The game's method stops with a NullReferenceException when there's no MapNetworkManager
    /// (lobby): the tables are already clean then, only the disconnect event and our promotion are missing.
    /// </summary>
    internal static void AfterRemovePlayer(OverlandSessionPlayers sp, PlayerRef player, Exception error)
    {
        Joined.Remove(player.RawEncoded);
        if (error == null || _inJanitorRemove || sp == null) return;
        try
        {
            bool mapMissing = MapNetworkManager.Instance == null;
            bool nullRef = error is NullReferenceException || (error.ToString() ?? "").Contains("NullReferenceException");
            if (mapMissing && nullRef && !InOrder(sp, player.RawEncoded))
            {
                if (!CompleteLeaves.Value || !sp.HasStateAuthority) return;
                try { sp.RPC_DisconnectedPlayer(player); }
                catch (Exception e) { DebugLog.Error("biglobbies", e, "Janitor.AfterRemovePlayer"); WarnOnce("disc", "[Lobby] Player tables: disconnect event failed: " + e.Message); }
                if (BigLobbiesConfig.LobbyGuards.Value) BigLobby.PromoteOverflow(sp);
                LeavesCompleted++;
                _sweepNow = true; // promote from our own list too
                return;
            }
            // Stopped somewhere earlier: let the next tick clean up whatever was left behind.
            _sweepNow = true;
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "Janitor.AfterRemovePlayer"); WarnOnce("afterRemove", "[Lobby] Player tables: leave repair failed: " + e.Message); }
    }

    internal static void AfterPlayerLeftFailed(PlayerRef player, Exception error)
    {
        WarnOnce("left", $"[Lobby] The game's player-left handling failed for player {player.PlayerId}: {error?.Message} (cleanup scheduled)");
        _sweepNow = true;
    }

    // ======================================================================================================
    // helpers
    // ======================================================================================================

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

    private static bool SafeContains<TK, TV>(NetworkDictionary<TK, TV> d, TK key)
    {
        try { return d.ContainsKey(key); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.SafeContains"); return false; }
    }

    private static bool SafeRemove<TK, TV>(NetworkDictionary<TK, TV> d, TK key)
    {
        try { return d.ContainsKey(key) && d.Remove(key); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.SafeRemove"); return false; }
    }

    private static void WarnOnce(string key, string message)
    {
        if (Warned.Add(key)) BigLobbiesPlugin.Logger.LogWarning(message + " (further ones are not logged)");
    }

    private static string Describe(OverlandSessionPlayers sp)
    {
        var sb = new StringBuilder("slots:");
        try
        {
            var order = sp.OrderedPlayerRefs;
            for (int i = 0; i < order.Length; i++) sb.Append(' ').Append(order.Get(i).RawEncoded - 1);
            sb.Append(" colours:");
            var colors = sp.PlayerColors;
            for (int i = 0; i < colors.Length; i++) sb.Append(' ').Append(colors.Get(i).RawEncoded - 1);
            sb.Append($" names: {sp.PlayerRefToNameDictionary.Count}/{sp.PlayerRefToNameDictionary.Capacity}");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "Janitor.Describe"); sb.Append(" ? ").Append(e.Message); }
        return sb.ToString();
    }

    // ======================================================================================================
    // menu
    // ======================================================================================================

    private static void TakeSnapshot(OverlandSessionPlayers sp, Validity v, bool host)
    {
        Slots.Clear();
        Overflow.Clear();
        try
        {
            var colors = sp.PlayerColors;
            var colored = new HashSet<int>();
            for (int i = 0; i < colors.Length; i++) colored.Add(colors.Get(i).RawEncoded);
            var order = sp.OrderedPlayerRefs;
            var inSlots = new HashSet<int>();
            for (int i = 0; i < order.Length; i++)
            {
                int raw = order.Get(i).RawEncoded;
                inSlots.Add(raw);
                Slots.Add(new Row
                {
                    Raw = raw,
                    Name = raw > 0 ? NameOf(sp, raw) : "",
                    // Only the host's view of who is connected is authoritative; guests just see the slots.
                    Present = host && v.Usable ? v.Present(raw) : raw > 0,
                    HasColor = colored.Contains(raw),
                });
            }
            var extra = new List<int>();
            if (host && v.Active != null) extra.AddRange(v.Active);
            foreach (int raw in Joined) if (!extra.Contains(raw)) extra.Add(raw);
            extra.Sort();
            foreach (int raw in extra)
            {
                if (raw <= 0 || inSlots.Contains(raw) || (host && !v.Present(raw))) continue;
                Overflow.Add(new Row { Raw = raw, Name = NameOf(sp, raw), Present = true });
            }
            _namesCount = sp.PlayerRefToNameDictionary.Count;
            _namesCap = sp.PlayerRefToNameDictionary.Capacity;
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "Janitor.TakeSnapshot"); WarnOnce("snapshot", "[Lobby] Player tables snapshot failed: " + e.Message); }
    }

    private static string NameOf(OverlandSessionPlayers sp, int raw)
    {
        string name;
        try { name = sp.GetPlayerName(PlayerRef.FromEncoded(raw)); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.NameOf"); name = null; }
        if (string.IsNullOrEmpty(name)) name = $"Player {raw - 1}";
        name = name.Replace("<", "‹").Replace(">", "›"); // keep player-chosen tags out of our rich text
        return name.Length > 28 ? name.Substring(0, 28) + "…" : name;
    }

    private static string Hex(Color c) => $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";

    public static void DrawCard()
    {
        Menu.Card("Player slots", "The game keeps players in 4 networked slots (colour, map, compass, pause list). " +
                                  "When you host, entries left behind by players who disconnected are cleared and waiting players move up.", () =>
        {
            bool host = false, inSession = OverlandSessionPlayers.Instance != null;
            try { host = inSession && GameState.Runner != null && GameState.IsAuthority && !GameState.Runner.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "Janitor.DrawCard"); }

            Menu.Switch(AutoClean, "Clean up after leavers", "Host: remove players who already left from the slots", true);
            Menu.Switch(CompleteLeaves, "Finish lobby leaves", "Send the game's 'disconnected' event it skips in the lobby", true);
            Menu.Switch(RemoveGhostCars, "Remove leftover cars", "Host: despawn cars of players who are gone", true);
            Menu.SliderRow(GraceSeconds, "Wait before cleaning", 1f, 15f, "0", " s", AutoClean.Value);

            if (!DevDetails.On)
            {
                if (inSession) Menu.Chips(1, ("Clean up now", () => Menu.SetStatus(RequestCleanNow()), host));
                return;
            }
            if (!inSession)
            {
                Menu.Small("Not in a session. Host or join one to see the slots.");
                return;
            }

            for (int i = 0; i < Slots.Count; i++)
            {
                var r = Slots[i];
                string text;
                if (r.Raw == 0) text = $"<color={Hex(Theme.Muted)}>empty</color>";
                else if (r.Raw < 0) text = $"<color={Hex(Theme.Bad)}>invalid entry ({r.Raw})</color>";
                else if (!r.Present) text = $"<color={Hex(Theme.Warn)}>{r.Name}  (id {r.Raw - 1}, left the session)</color>";
                else text = $"{r.Name}  <color={Hex(Theme.Muted)}>id {r.Raw - 1}{(r.HasColor ? "" : ", no colour")}</color>";
                Menu.KeyValue($"Slot {i + 1}", text);
            }
            if (Overflow.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var r in Overflow) sb.Append(sb.Length > 0 ? ", " : "").Append(r.Name);
                Menu.KeyValue("Without a slot", $"<color={Hex(Theme.Info)}>{sb}</color>");
            }
            if (_namesCap > 0)
                Menu.Bar("Stored names", _namesCount / (float)_namesCap, _namesCount >= _namesCap ? Theme.Warn : Theme.Good, $"{_namesCount}/{_namesCap}");
            Menu.KeyValue("Player list source", _validityNote);
            Menu.KeyValue("Fixed so far", $"{CleanedSlots} slots, {CleanedColors} colours, {CleanedNames} names, {CleanedFlags} flags, " +
                                          $"{GameRemovals} removals, {Promoted} moved up, {LeavesCompleted} leaves, {GhostCars} cars");

            Menu.Chips(2,
                ("Clean up now", () => Menu.SetStatus(RequestCleanNow()), host),
                ("Write tables to log", () =>
                {
                    var sp = OverlandSessionPlayers.Instance;
                    if (sp == null) { Menu.SetStatus("Failed: not in a session."); return; }
                    BigLobbiesPlugin.Logger.LogInfo("[Lobby] Player tables: " + Describe(sp));
                    Menu.SetStatus("Player tables written to the BepInEx log.");
                }, true));
            if (!host) Menu.Small("Only the host can change the slots; you can still see them here.");
        });
    }
}

// ==========================================================================================================
// Harmony hooks
// ==========================================================================================================

/// <summary>Runs before BigLobby's AddPlayer replacement (and the original): frees slots held by players who left.</summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.AddPlayer))]
internal static class JanitorAddPlayerPatch
{
    [HarmonyPriority(Priority.First + 100)]
    private static void Prefix(OverlandSessionPlayers __instance, PlayerRef playerRef) => Janitor.BeforeAddPlayer(__instance, playerRef);

    // Postfixes are skipped when a prefix returns false (BigLobby's replacement does), so record in a finalizer.
    private static Exception Finalizer(Exception __exception, PlayerRef playerRef)
    {
        try { Janitor.AfterAddPlayer(playerRef); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "JanitorAddPlayerPatch.Finalizer"); }
        return __exception;
    }
}

/// <summary>Sees RemovePlayer's exception before the lobby guard swallows it.</summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RemovePlayer))]
internal static class JanitorRemovePlayerPatch
{
    [HarmonyPriority(Priority.First)]
    private static Exception Finalizer(Exception __exception, OverlandSessionPlayers __instance, PlayerRef playerRef)
    {
        try { Janitor.AfterRemovePlayer(__instance, playerRef, __exception); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "JanitorRemovePlayerPatch.Finalizer"); }
        return __exception;
    }
}

/// <summary>The janitor removes players who already got their "left" pop-up: don't show it twice.</summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RPC_ShowPlayerLeftNotification))]
internal static class JanitorLeftPopupPatch
{
    private static bool Prefix() => !Janitor.SuppressLeftPopup;
}

/// <summary>If the game's player-left callback throws, its RemovePlayer may never have run: schedule a cleanup.</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.Fusion_INetworkRunnerCallbacks_OnPlayerLeft))]
internal static class JanitorPlayerLeftPatch
{
    private static Exception Finalizer(Exception __exception, PlayerRef player)
    {
        if (__exception != null)
        {
            try { Janitor.AfterPlayerLeftFailed(player, __exception); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "JanitorPlayerLeftPatch.Finalizer"); }
        }
        return __exception;
    }
}
