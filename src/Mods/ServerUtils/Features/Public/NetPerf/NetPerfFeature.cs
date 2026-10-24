using BepInEx.Configuration;
using OverTheHillPlus.ServerUtils;

namespace OverTheHillPlus.Features.Public.NetPerf;

/// <summary>
/// Network performance for big (8 to 16 player) lobbies, host side only.
///
/// What the game does (from the decompiled code): Photon Fusion 2 in Host mode; the host simulates every car and
/// sends world snapshots to each guest. Interest management (Fusion.Addons.InterestManagement) decides which
/// objects each guest receives: every player has a camera "view cone" (PlayerCameraPositioning moves it to the
/// player's camera and sets its length from <c>cameraDistance</c>), and objects outside it stop updating for that
/// guest (RendererVisibilityByInterest hides them). The tick rate / snapshot rate come from the Fusion project
/// config and the host's values are what the session uses.
///
/// Tweaks (all opt-in, all off by default):
/// - Before hosting: snapshot send rate divisor, longer connection timeout
///   (written to the Fusion config right before StartGame; restored exactly when off or when joining as a guest).
/// - While hosting: interest (view) distance scale, keep player cars always replicated.
/// - Read-only per-player diagnostics: ping, direct/relayed, objects replicated, interest cells.
/// </summary>
internal sealed class NetPerfFeature : IFeature
{
    internal const string Section = "Network Performance";

    internal static ConfigEntry<int> SendDivisor;
    internal static ConfigEntry<bool> PatientTimeout;
    internal static ConfigEntry<float> InterestScale;
    internal static ConfigEntry<bool> KeepCarsVisible;

    public void Register()
    {
        var f = ServerUtilsConfig.File;
        SendDivisor = f.Bind(Section, "HostSnapshotDivisor", 1,
            new ConfigDescription("Host only, applied when you start hosting: send world snapshots every Nth tick (1 = game default, 2 = half rate). " +
                                  "Less upload with many players; remote cars interpolate a little further behind.",
                new AcceptableValueRange<int>(1, 4)));
        PatientTimeout = f.Bind(Section, "HostPatientTimeout", false,
            "Host only, applied when you start hosting: double the connection timeout (at least 20 s) so a guest with a short hitch isn't dropped.");
        InterestScale = f.Bind(Section, "HostInterestScale", 1f,
            new ConfigDescription("Host only, live: scales how far each guest's view cone reaches for replication (1 = game default). " +
                                  "Lower = less data per guest; far-away objects stop updating sooner.",
                new AcceptableValueRange<float>(0.4f, 2f)));
        KeepCarsVisible = f.Bind(Section, "HostKeepCarsVisible", false,
            "Host only, live: every player's car is always replicated to every guest, whatever the interest distance (pairs well with a lower interest scale).");

        Registry.Section(Registry.PlusMode, "Network", NetPerfMenu.Draw, 0);
        Registry.OnTick(NetPerfRuntime.Tick);
    }

    /// <summary>Any of the host tweaks is switched on.</summary>
    internal static bool AnyActive =>
        SendDivisor.Value > 1 || PatientTimeout.Value || KeepCarsVisible.Value ||
        System.Math.Abs(InterestScale.Value - 1f) > 0.01f;

    internal static void ResetAll()
    {
        SendDivisor.Value = 1;
        PatientTimeout.Value = false;
        InterestScale.Value = 1f;
        KeepCarsVisible.Value = false;
    }

    /// <summary>A sensible starting point for a 12 to 16 player lobby.</summary>
    internal static void ApplyBigLobbyPreset()
    {
        SendDivisor.Value = 2;
        PatientTimeout.Value = true;
        InterestScale.Value = 0.8f;
        KeepCarsVisible.Value = true;
    }
}
