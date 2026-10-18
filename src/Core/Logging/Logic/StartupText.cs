using System;
using System.Collections.Generic;
using System.Text;

namespace OverTheHillPlus.Logging;

/// <summary>The one-line texts of the startup self-checks (patches per mod, types that did not load). Pure.</summary>
internal static class StartupText
{
    /// <summary>
    /// "camera: 5 patch classes applied, 1 failed (FooPatch: Method not found; BarPatch: ...)". The failures are listed
    /// (at most three) so the line says what is missing from the game.
    /// </summary>
    public static string PatchLine(string modId, int ok, int failed, IReadOnlyList<string> failures)
    {
        var sb = new StringBuilder();
        sb.Append(modId ?? "?").Append(": ").Append(ok).Append(ok == 1 ? " patch class applied" : " patch classes applied");
        if (failed <= 0) return sb.ToString();
        sb.Append(", ").Append(failed).Append(" failed");
        if (failures != null && failures.Count > 0)
        {
            sb.Append(" (");
            for (int i = 0; i < failures.Count && i < 3; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(failures[i]);
            }
            if (failures.Count > 3) sb.Append("; ... ").Append(failures.Count - 3).Append(" more");
            sb.Append(')');
        }
        return sb.ToString();
    }

    /// <summary>"camera: 2 of its 40 types could not load (game update?): Foo (Could not load type 'X'); Bar (...)".</summary>
    public static string TypeLoadLine(string modId, int failedTypes, int totalTypes, IReadOnlyList<string> messages)
    {
        var sb = new StringBuilder();
        sb.Append(modId ?? "?").Append(": ").Append(failedTypes).Append(" of its ").Append(totalTypes)
          .Append(" types could not load (the game changed under the mod?)");
        if (messages != null)
            for (int i = 0; i < messages.Count && i < 3; i++) sb.Append(i == 0 ? ": " : "; ").Append(messages[i]);
        return sb.ToString();
    }

    /// <summary>"Wrote 12 settings files, 1 could not be read: oth.cfg (...)" style: the file and what is wrong.</summary>
    public static string ConfigLine(string file, string problem) => (file ?? "?") + ": " + problem;
}
