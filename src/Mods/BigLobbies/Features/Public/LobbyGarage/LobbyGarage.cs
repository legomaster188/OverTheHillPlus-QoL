using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Fusion;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;
using OverTheHillPlus.BigLobbies;

namespace OverTheHillPlus.Features.Public.LobbyGarage;

/// <summary>
/// The pre-game lobby with more than 4 players. What the game does there (decoded with Cpp2IL):
/// - Lobby.GetSpawnPoint(player) returns spawnPoints[index of the player in OrderedPlayerRefs], a 4-slot table, and
///   null for anyone else. Both the lobby car ("dummy") coroutine and the name labels use it, so extra players
///   got no car (the coroutine hits a null) or an unstable spot that changed between calls.
/// - StateLobbyMenu.UpdateCarsStatus walks the 7-entry name table and indexes the 4-entry LobbyPlayersStatus label
///   list with the spawn point index: index 4+ throws, which also skips the lobby fade-in (black screen) when it
///   runs from UpdateCarsStatusAndFadeIn, and players without a name entry (8+) never got a label.
/// Fixes: every extra player gets a stable spot of their own (kept until they leave), the label list grows with
/// the spots, the label/car pass is redone without the 4/7 limits and can no longer throw, and cars are kept on
/// their spot (also after an extra player moves into a freed slot).
/// </summary>
internal sealed class LobbyGarage : IFeature
{
    public const int MaxSpots = 32;

    public static ConfigEntry<bool> EnabledEntry;
    public static ConfigEntry<string> LayoutEntry;
    public static ConfigEntry<float> RowDepthEntry;

    public static bool Enabled => EnabledEntry == null || EnabledEntry.Value;
    public static string Layout => LayoutEntry?.Value ?? LobbyGarageLayout.Staggered;
    public static float RowDepth => RowDepthEntry?.Value ?? 6f;

    // stats for the menu
    public static int LabelsAdded, SpotsCloned, CarsMoved, Swallowed, LastShown;
    public static int BaseLabels = -1;

    private static readonly Dictionary<int, int> Spot = new(); // extra player (RawEncoded) -> spawn point index
    private static IntPtr _lobbyPtr;
    private static global::Lobby _lobby;
    private static StateLobbyMenu _menu;
    private static readonly HashSet<string> Logged = new();

    public void Register()
    {
        var f = BigLobbiesConfig.File;
        EnabledEntry = f.Bind("LobbyGarage", "Enabled", true,
            "Show players 5 and up properly in the pre-game lobby: their own spot and car, a name label, no black screen.");
        LayoutEntry = f.Bind("LobbyGarage", "Layout", LobbyGarageLayout.Staggered,
            new ConfigDescription("Where lobby cars 5+ stand.", new AcceptableValueList<string>(LobbyGarageLayout.Layouts)));
        RowDepthEntry = f.Bind("LobbyGarage", "RowDepth", 6f, "Metres between rows of extra lobby cars (Staggered rows layout).");

        Registry.Section(Registry.PlusMode, "Lobby", DrawMenu, 30);
    }

    internal static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) BigLobbiesPlugin.Logger.LogWarning($"[LobbyGarage] {message} (logged once)");
    }

    // ---- spawn spots ----

    internal static void OnLobbyAwake(global::Lobby lobby)
    {
        Spot.Clear();
        _lobby = lobby;
        _lobbyPtr = lobby == null ? IntPtr.Zero : lobby.Pointer;
        if (!Enabled || lobby == null) return;
        try { LobbyGarageLayout.Apply(lobby, true); }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "LobbyGarage.OnLobbyAwake"); LogOnce("layout", "layout: " + e.Message); }
    }

    /// <summary>
    /// Spawn point for a player the game has no slot for (or one that must keep its spot). True = use idx/point.
    /// Players in one of the 4 slots keep the game's own answer.
    /// </summary>
    internal static bool Resolve(global::Lobby lobby, PlayerRef player, int gameIndex, SpawnPoint gamePoint, out int idx, out SpawnPoint point)
    {
        idx = -1;
        point = null;
        var points = lobby?.spawnPoints;
        if (points == null || points.Count == 0) return false;
        if (lobby.Pointer != _lobbyPtr)
        {
            Spot.Clear();
            _lobbyPtr = lobby.Pointer;
            _lobby = lobby;
        }
        int raw = player.RawEncoded;
        if (raw <= 0) return false;

        if (gamePoint != null && gameIndex >= 0)
        {
            Spot.Remove(raw); // has a slot now (moved up when someone left): back to the game's spot
            return false;
        }
        if (Spot.TryGetValue(raw, out int k) && k < points.Count && points[k] != null)
        {
            idx = k;
            point = points[k];
            return true;
        }

        int first = SlotCount();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            for (int i = first; i < MaxSpots; i++)
            {
                if (Spot.ContainsValue(i)) continue;
                while (points.Count <= i)
                {
                    if (!LobbyGarageLayout.AddPoint(lobby)) return false;
                    SpotsCloned++;
                }
                if (points[i] == null) continue;
                Spot[raw] = i;
                idx = i;
                point = points[i];
                BigLobbiesPlugin.Logger.LogInfo($"[LobbyGarage] Player {player.PlayerId} has no lobby slot: using spot {i}");
                return true;
            }
            if (attempt == 0 && !PruneLeftPlayers()) break;
        }
        return false;
    }

    internal static void Release(PlayerRef player)
    {
        try { Spot.Remove(player.RawEncoded); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "LobbyGarage.Release"); }
    }

    /// <summary>Size of the game's player slot table (4), which maps to the first spawn points.</summary>
    private static int SlotCount()
    {
        try
        {
            var sp = OverlandSessionPlayers.Instance;
            if (sp != null) return Math.Max(0, sp.OrderedPlayerRefs.Length);
        }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "LobbyGarage.SlotCount"); }
        return 4;
    }

    /// <summary>Frees spots of players no longer in the session. True if any was freed.</summary>
    private static bool PruneLeftPlayers()
    {
        var present = new HashSet<int>();
        try
        {
            var runner = OverlandNetworkRunner.Instance?.NetworkRunner ?? GameState.Runner;
            var e = runner?.ActivePlayers?.GetEnumerator();
            if (e != null)
            {
                var plain = e.Cast<Il2CppSystem.Collections.IEnumerator>();
                while (plain.MoveNext()) present.Add(e.Current.RawEncoded);
            }
        }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "LobbyGarage.PruneLeftPlayers"); LogOnce("active", "active players: " + ex.Message); }
        if (present.Count == 0)
        {
            try
            {
                foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FusionPlayer>()))
                {
                    var fp = o.TryCast<FusionPlayer>();
                    try { if (fp?.Object != null) present.Add(fp.Object.InputAuthority.RawEncoded); } catch (Exception ex2) { DebugLog.Error("biglobbies", ex2, "LobbyGarage.PruneLeftPlayers"); }
                }
            }
            catch (Exception ex2) { DebugLog.Error("biglobbies", ex2, "LobbyGarage.PruneLeftPlayers"); }
        }
        if (present.Count == 0) return false;
        int before = Spot.Count;
        foreach (var raw in new List<int>(Spot.Keys))
            if (!present.Contains(raw)) Spot.Remove(raw);
        return Spot.Count < before;
    }

    // ---- name labels + cars (replaces StateLobbyMenu.UpdateCarsStatus) ----

    /// <summary>
    /// Same steps as the game's UpdateCarsStatus: hide every label, then for each player put a label over their
    /// spot. Covers players past the 7-entry name table (anyone with a lobby car) and grows the label list as needed.
    /// </summary>
    internal static void UpdateCarsStatus(StateLobbyMenu menu)
    {
        _menu = menu;
        var ui = menu.lobbyMenuContextUI;
        if (ui == null) return; // the game does nothing either until the lobby UI exists
        var labels = ui.LobbyPlayersStatus;
        if (labels == null) return;
        if (BaseLabels < 0) BaseLabels = labels.Count;

        for (int i = 0; i < labels.Count; i++)
        {
            var s = labels[i];
            if (s != null) s.gameObject.SetActive(false);
        }
        LastShown = 0;
        if (menu.isSinglePlayer) return;
        var sp = OverlandSessionPlayers.Instance;
        var lobby = menu.lobby;
        if (sp == null || lobby == null) return;
        _lobby = lobby;

        var dummies = sp.playerRefToDummy;
        foreach (var player in KnownPlayers(sp, dummies))
        {
            try
            {
                var point = lobby.GetSpawnPoint(player, out int idx);
                if (point == null || idx < 0) continue;
                if (idx >= labels.Count && !GrowLabels(labels, idx + 1)) continue;
                var status = labels[idx];
                if (status == null) continue;
                Transform anchor = point.frontTransform != null ? point.frontTransform : point.transform;
                menu.SetPlayerNameLabel(player, status, anchor);
                LastShown++;
                KeepCarOnSpot(dummies, player, point);
            }
            catch (Exception e) { DebugLog.Error("biglobbies", e, "LobbyGarage.UpdateCarsStatus"); LogOnce("label", $"label for player {player.PlayerId}: {e.Message}"); }
        }
    }

    /// <summary>Everyone in the name table (keys are RawEncoded-1) plus everyone with a lobby car.</summary>
    private static List<PlayerRef> KnownPlayers(OverlandSessionPlayers sp, Il2CppSystem.Collections.Generic.Dictionary<PlayerRef, GameObject> dummies)
    {
        var raws = new SortedSet<int>();
        try
        {
            var names = sp.PlayerRefToNameDictionary;
            int count = names.Count, found = 0;
            for (int key = 0; key < 256 && found < count; key++)
            {
                if (!names.ContainsKey(key)) continue;
                found++;
                raws.Add(key + 1);
            }
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "LobbyGarage.KnownPlayers"); LogOnce("names", "name table: " + e.Message); }
        try
        {
            if (dummies != null)
                foreach (var kv in dummies)
                    if (kv.Key.RawEncoded > 0) raws.Add(kv.Key.RawEncoded);
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "LobbyGarage.KnownPlayers"); LogOnce("dummies", "lobby cars: " + e.Message); }
        var list = new List<PlayerRef>(raws.Count);
        foreach (int raw in raws) list.Add(PlayerRef.FromEncoded(raw));
        return list;
    }

    /// <summary>Clones the first label until the list has <paramref name="count"/> entries.</summary>
    private static bool GrowLabels(Il2CppSystem.Collections.Generic.List<ContextUILobbyPlayerStatus> labels, int count)
    {
        count = Math.Min(count, MaxSpots + 8);
        ContextUILobbyPlayerStatus template = null;
        for (int i = 0; i < labels.Count && template == null; i++)
            if (labels[i] != null) template = labels[i];
        if (template == null) return false;
        var parent = template.transform.parent;
        while (labels.Count < count)
        {
            var go = Object.Instantiate(template.gameObject, parent, false);
            go.name = $"{template.gameObject.name}_OTHPlus{labels.Count}";
            go.SetActive(false);
            var status = go.GetComponent<ContextUILobbyPlayerStatus>();
            if (status == null) { Object.Destroy(go); return false; }
            labels.Add(status);
            LabelsAdded++;
        }
        return true;
    }

    /// <summary>
    /// The lobby car is placed once when it's created; if the player's spot changed since (moved up into a freed
    /// slot, or the layout was re-applied), move it so the car and its label stay together. Local visual only.
    /// </summary>
    private static void KeepCarOnSpot(Il2CppSystem.Collections.Generic.Dictionary<PlayerRef, GameObject> dummies, PlayerRef player, SpawnPoint point)
    {
        if (dummies == null || !dummies.ContainsKey(player)) return;
        var car = dummies[player];
        if (car == null) return;
        var target = point.transform;
        if ((car.transform.position - target.position).sqrMagnitude < 0.25f) return;
        car.transform.SetPositionAndRotation(target.position, target.rotation);
        CarsMoved++;
    }

    // ---- menu ----

    private static bool InLobby => _lobby != null && _lobby.gameObject.activeInHierarchy;

    private static void ReapplyLayout()
    {
        try
        {
            var lobby = _lobby;
            if (lobby == null) { Menu.SetStatus("Failed: not in the lobby."); return; }
            int moved = LobbyGarageLayout.Apply(lobby, true);
            if (_menu != null) _menu.UpdateCarsStatus(); // moves the cars and labels onto the new spots
            Menu.SetStatus(Layout == LobbyGarageLayout.ModDefault
                ? "Layout set to Mod default: takes effect in the next lobby."
                : $"Lobby layout applied ({moved} extra spots).");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "LobbyGarage.ReapplyLayout"); Menu.SetStatus("Failed: " + e.Message); }
    }

    private static void DrawMenu()
    {
        Menu.Card("Lobby cars 5+", "The pre-game lobby only had room for 4: extra players get their own spot, car and name label.", () =>
        {
            Menu.Switch(EnabledEntry, "Show every player in the lobby", "Spots, cars and labels for players 5–32; no black screen", true);
            Menu.Small("Layout for the extra cars (your view only):");
            var items = new (string, Action, bool)[LobbyGarageLayout.Layouts.Length];
            for (int i = 0; i < items.Length; i++)
            {
                string name = LobbyGarageLayout.Layouts[i];
                items[i] = (name == Layout ? $"[ {name} ]" : name, () => { LayoutEntry.Value = name; if (InLobby) ReapplyLayout(); }, Enabled);
            }
            Menu.Chips(3, items);
            Menu.SliderRow(RowDepthEntry, "Row spacing", 4f, 14f, "0.0", " m", Enabled && Layout == LobbyGarageLayout.Staggered);
            Menu.KeyValue("Lobby", InLobby ? $"{_lobby.spawnPoints?.Count ?? 0} spots, {LastShown} labels shown (game had {(BaseLabels < 0 ? 4 : BaseLabels)})" : "not in a lobby");
            Menu.KeyValue("Extra players placed", $"{Spot.Count} now · {LabelsAdded} labels and {SpotsCloned} spots added · {CarsMoved} cars re-placed");
            if (Swallowed > 0) Menu.KeyValue("Errors caught", $"{Swallowed} (see the BepInEx log)");
            if (LobbyGarageLayout.LastLog.Length > 0) Menu.Small(LobbyGarageLayout.LastLog);
            Menu.Chips(1, ("Re-apply layout now", ReapplyLayout, Enabled && InLobby));
        });
    }
}
