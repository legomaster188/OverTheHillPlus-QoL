using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// "Players with the mod": who else has our mods running right now, shared between mods without them referencing each
/// other. The Player Market mod fills it from the market server (it owns the heartbeat and the list request); Server
/// Browser+ shows it in the server browser and joins a listed server through its own join path, which it offers here
/// as <see cref="Join"/> so the market's page can use it too. Main thread only.
/// </summary>
internal static class ModPlayers
{
    internal sealed class Player
    {
        /// <summary>Steam name, or "" for a player who isn't signed in to the market.</summary>
        public string Name = "";
        /// <summary>Photon region code ("eu", "us", ...), or "".</summary>
        public string Region = "";
        /// <summary>"menu", "solo", "hosting" or "server".</summary>
        public string Status = "";
        /// <summary>The server's name, its Fusion session name and its join code: only when its host shares them.</summary>
        public string Server = "", Session = "", Code = "";
        /// <summary>Seconds since the last heartbeat.</summary>
        public int Seen;

        public bool Joinable => Code.Length > 0 || Session.Length > 0;
    }

    private static List<Player> _list = new();

    /// <summary>The last list (empty until the first answer).</summary>
    public static IReadOnlyList<Player> List => _list;

    /// <summary>Everyone online with the mod, all regions (the list only names those it can show).</summary>
    public static int Online { get; private set; }

    /// <summary>Online per region.</summary>
    public static IReadOnlyDictionary<string, int> Regions { get; private set; } = new Dictionary<string, int>();

    /// <summary>When the list last came (Time.unscaledTime); -1: never.</summary>
    public static float UpdatedAt { get; private set; } = -1f;

    /// <summary>The region the list is for ("" = all).</summary>
    public static string Region { get; private set; } = "";

    /// <summary>Why there is no list ("offline", "switched off"), or null.</summary>
    public static string Problem { get; set; }

    /// <summary>Set by the mod that can fetch the list (the Player Market): asks for it, for a region ("" = all).</summary>
    public static Action<string> Refresh;

    /// <summary>Set by the mod that can join a listed server (Server Browser+): joins it, or returns why not.</summary>
    public static Func<Player, string> Join;

    /// <summary>A list fetcher is there (the Player Market mod is installed and its setting is on).</summary>
    public static bool Available => Refresh != null;

    public static void Ask(string region)
    {
        try { Refresh?.Invoke(region ?? ""); }
        catch (Exception e) { DebugLog.Error("core", e, "ModPlayers.Ask"); }
    }

    /// <summary>The fetcher: a fresh list came.</summary>
    public static void Publish(string region, int online, IReadOnlyDictionary<string, int> regions, List<Player> list, float now)
    {
        Region = region ?? "";
        Online = online;
        Regions = regions ?? new Dictionary<string, int>();
        _list = list ?? new List<Player>();
        UpdatedAt = now;
        Problem = null;
    }

    /// <summary>"Ana, hosting Muddy Sunday" / "Someone, in Muddy Sunday" / "Ana, playing solo".</summary>
    public static string Line(Player p)
    {
        string who = p.Name.Length > 0 ? p.Name : "Someone";
        return p.Status switch
        {
            "hosting" => who + (p.Server.Length > 0 ? ", hosting " + p.Server : ", hosting"),
            "server" => who + (p.Server.Length > 0 ? ", in " + p.Server : ", in a server"),
            "solo" => who + ", playing solo",
            _ => who + ", in the menus",
        };
    }
}
