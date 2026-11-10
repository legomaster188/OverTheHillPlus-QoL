using System;

namespace OverTheHillPlus.Shared;

internal static class ModEvents
{
    /// <summary>Kind, value, and the map key it happened on ("" when it has no map).</summary>
    public static event Action<string, double, string> Happened;

    private static bool _logged;

    public static void Raise(string kind, double value, string map = "")
    {
        var h = Happened;
        if (h == null || string.IsNullOrEmpty(kind)) return;
        foreach (Action<string, double, string> d in h.GetInvocationList())
        {
            try { d(kind, value, map ?? ""); }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "ModEvents.Raise " + kind);
                if (_logged) continue;
                _logged = true;
                Core.CorePlugin.Logger?.LogWarning("[Events] a listener threw (further errors not logged): " + e.Message);
            }
        }
    }
}
