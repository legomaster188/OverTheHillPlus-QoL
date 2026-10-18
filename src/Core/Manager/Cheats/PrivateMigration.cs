using System.Collections.Generic;

namespace OverTheHillPlus.Core.Manager;

// Private build only: tools/export_qol.py skips every Cheats/ folder, so the public core has neither the private
// plugin's GUID nor the private mods' sections (its partial methods then compile to nothing).

internal sealed partial class SectionMap
{
    static partial void AddPrivate(SectionMap map)
    {
        var m = map;
        // cheats: ModConfig's [General] FlipKey and BlockAchievementsAfterCheating, [Assists], [Cheats] (the dev
        // console too), [Movement], [Vehicles], and the Tuning, Chassis, Fun and ESP features.
        m.Key("General", "FlipKey", "cheats")
         .Key("General", "BlockAchievementsAfterCheating", "cheats");
        m.Section("Assists", "cheats")
         .Section("Cheats", "cheats")
         .Section("Movement", "cheats")
         .Section("Vehicles", "cheats")
         .Section("Tuning", "cheats")
         .Section("Chassis Tuning", "cheats")
         .Section("Fun", "cheats")
         .Section("ESP.World", "cheats")
         .Section("ESP Players", "cheats");
        // Ruling R10: the server browser profiler (and its switch) moved into the cheats mod.
        m.Key("ServerBrowser", "Profiling", "cheats");

        // hostpowers: ModConfig's [Host] (god mode, weather lock), Events, TrackBuilder, Players, Teleport, Server Start.
        m.Section("Host", "hostpowers")
         .Section("Events", "hostpowers")
         .Section("TrackBuilder", "hostpowers")
         .Section("Players Teleport", "hostpowers")
         .Section("Teleport", "hostpowers")
         .Section("Server Start", "hostpowers");
        // Task 9: the host's "Allow mild swears" switch moved from core's [NameFilter] into hostpowers (same names).
        m.Key("NameFilter", "AllowSwears", "hostpowers");

        // convoy
        m.Section("Convoy AI", "convoy")
         .Section("Convoy", "convoy");

        // fullmap: the old plugin's [Map] section (explore-full-map options).
        m.Section("Map", "fullmap");
    }
}

internal static partial class LegacyDetector
{
    static partial void AddPrivate(List<string> guids) { }
}
