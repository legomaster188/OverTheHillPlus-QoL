using System.Collections.Generic;

namespace OverTheHillPlus.Api;

// Private build only: tools/export_qol.py skips every Cheats/ folder, so the public core doesn't name the private
// mods (its partial method then compiles to nothing, and those ids are free there).

internal static partial class ApiRules
{
    static partial void AddPrivate(List<string> ids)
    {
        ids.AddRange(new[] { "cheats", "hostpowers", "convoy", "unlockall", "fullmap", "bookmarks", "worldtools", "carscale", "traffic", "carspawner", "customcosmetics" });
    }
}
