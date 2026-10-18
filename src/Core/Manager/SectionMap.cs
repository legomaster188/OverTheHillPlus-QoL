using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Core.Manager;

/// <summary>
/// Where each setting of the old single config file (OverTheHillPlus) goes: which mod's oth.&lt;id&gt;.cfg, and under
/// which section and key. Most sections belong to one mod as a whole; a section shared by several mods is split key by
/// key, and a key that was renamed carries its new section and key. Anything not listed goes to core. Pure logic with
/// no BepInEx types, so it can be unit tested.
/// Built from every Bind(section, key, ...) of the code that moved into the mods: when a mod changes a section or key
/// name, add the old name here so existing settings still find their way.
/// </summary>
internal sealed partial class SectionMap
{
    /// <summary>Where one old setting goes.</summary>
    public readonly struct Target
    {
        public Target(string mod, string section, string key)
        {
            Mod = mod;
            Section = section;
            Key = key;
        }

        /// <summary>The mod id whose config file gets the setting.</summary>
        public string Mod { get; }
        /// <summary>Section name in that file.</summary>
        public string Section { get; }
        /// <summary>Key name in that file.</summary>
        public string Key { get; }
    }

    public const string Fallback = "core";

    // Section and key names are case-sensitive in BepInEx config files.
    private readonly Dictionary<string, string> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), Target> _keys = new();

    public SectionMap() { }

    /// <summary>A map of whole sections only (section → mod id).</summary>
    public SectionMap(IReadOnlyDictionary<string, string> sectionToMod)
    {
        if (sectionToMod == null) return;
        foreach (var kv in sectionToMod) Section(kv.Key, kv.Value);
    }

    /// <summary>Sends a whole section to a mod (the default for its keys that have no <see cref="Key"/> rule).</summary>
    public SectionMap Section(string section, string mod)
    {
        _sections[section] = mod;
        return this;
    }

    /// <summary>
    /// Sends one key to a mod, optionally under a new section and/or key name. Rules for the same section and key
    /// replace each other, so a setting never ends up in two files.
    /// </summary>
    public SectionMap Key(string section, string key, string mod, string newSection = null, string newKey = null)
    {
        _keys[(section, key)] = new Target(mod, newSection ?? section, newKey ?? key);
        return this;
    }

    /// <summary>Where the old setting [section] key goes: its key rule, else its section's mod, else core.</summary>
    public Target Resolve(string section, string key)
    {
        if (_keys.TryGetValue((section, key), out var t)) return t;
        return new Target(_sections.TryGetValue(section, out var mod) ? mod : Fallback, section, key);
    }

    /// <summary>The map of the settings that existed in OverTheHillPlus.</summary>
    public static SectionMap Default => _default ??= Build();

    private static SectionMap _default;

    private static SectionMap Build()
    {
        var m = new SectionMap();

        // core: the MODS page, menu look and UX, the mod network, the name filter.
        m.Section("Core", "core")
         .Section("General", "core") // split below
         .Section("MenuStyle", "core")
         .Section("MenuUx", "core")
         .Section("Mod Network", "core")
         .Section("NameFilter", "core");
        m.Key("General", "MenuKey", "core", "Core", "OpenKey") // renamed
         .Key("General", "AdvancedMenu", "core")
         .Key("General", "MainMenuButton", "core", "Core", "MainMenuButton"); // the main menu's MODS button is core's

        // maphud: the driving HUD keys were in [General].
        m.Key("General", "HudKey", "maphud")
         .Key("General", "ShowHud", "maphud");
        m.Section("Big lobby - player list", "maphud")
         .Section("ExtraPlayersHud", "maphud")
         .Section("MapExtraPlayers", "maphud");

        // [Multiplayer] is split between Big Lobbies (lobby size, crash guards) and Server Creator+ (name, join message).
        m.Section("Multiplayer", "biglobbies");
        foreach (var k in new[] { "ExpandLobbySize", "DefaultMaxPlayers", "CustomMaxPlayers", "FallRescue", "LobbyGuards",
                                  "MeasuredCapacity", "SlotKeeper", "LendSlotsToJoiners" })
            m.Key("Multiplayer", k, "biglobbies");
        foreach (var k in new[] { "CustomRoomName", "RoomNameStyle", "RoomNameBold", "JoinMessageEnabled", "JoinMessage",
                                  "JoinMessageColor" })
            m.Key("Multiplayer", k, "servercreator");
        m.Section("Big Lobby Names", "biglobbies")
         .Section("LobbyGarage", "biglobbies")
         .Section("Player Colours", "biglobbies")
         .Section("Player Tables", "biglobbies")
         .Section("PlayerTables", "biglobbies");

        m.Section("Create Server Screen", "servercreator")
         .Section("Server Name Style", "servercreator")
         .Section("ServerPlayers", "servercreator");

        m.Section("ServerBrowser", "serverbrowser")
         .Section("ServerBrowserAllRegions", "serverbrowser")
         .Section("ServerBrowserPlus", "serverbrowser")
         .Section("BrowserFriends", "serverbrowser")
         .Section("BrowserRefresh", "serverbrowser")
         .Section("Rejoin", "serverbrowser");

        m.Section("Autosave", "serverutils")
         .Section("Host Governor", "serverutils")
         .Section("JoinSmoothing", "serverutils")
         .Section("Moderation", "serverutils")
         .Section("Network Performance", "serverutils")
         .Section("Ping Monitor", "serverutils")
         .Section("SettingsShare", "serverutils");

        m.Section("Chat", "chat");

        m.Section("Graphics.Detail", "graphics")
         .Section("Graphics.Fog", "graphics")
         .Section("Graphics.Lighting", "graphics")
         .Section("Graphics.Post", "graphics")
         .Section("Graphics.Profiles", "graphics")
         .Section("Graphics.Sky", "graphics")
         .Section("Graphics.Water", "graphics");

        m.Section("Photo", "photo")
         .Section("Spectate", "photo");

        m.Section("QoL", "drivingqol")
         .Section("Unstuck", "drivingqol");

        AddPrivate(m);
        return m;
    }

    /// <summary>The sections of the private mods (in a folder the public export leaves out).</summary>
    static partial void AddPrivate(SectionMap map);
}
