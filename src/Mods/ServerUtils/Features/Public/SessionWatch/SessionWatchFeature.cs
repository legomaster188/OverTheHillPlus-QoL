using System;
using System.Text;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.ServerUtils;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.SessionWatch;

/// <summary>
/// Diagnostics for "my server is not in the public list": what a hosted session is started with (mode, name, region
/// setting, visibility, every session property and its size) and, while hosting, a status line every 30 s and on
/// every change: the region Photon put it in, listed / joinable, whether the game's own browser accepts it
/// (OverlandSessionInfo.IsSessionValid: it needs AreaInfoName, GameVersion and RoomProperties), players and the
/// property keys. Everything goes to the BepInEx log (and so the debug log); nothing changes the session.
/// </summary>
internal sealed class SessionWatchFeature : IFeature
{
    private static float _next;
    private static string _last;
    private static IntPtr _runner;

    public void Register()
    {
        Registry.OnTick(Tick);
    }

    private static void Tick()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 2f;
        try
        {
            var r = GameState.Runner;
            bool hosting = r != null && r.IsRunning && r.IsServer && !r.IsSinglePlayer;
            if (!hosting) { if (_runner != IntPtr.Zero) { Log("stopped hosting"); _runner = IntPtr.Zero; _last = null; } return; }
            var info = r.SessionInfo;
            if (info == null || !info.IsValid) return;
            string line = Describe(info);
            bool changed = line != _last || r.Pointer != _runner;
            if (changed || Time.unscaledTime >= _nextForce)
            {
                _nextForce = Time.unscaledTime + 30f;
                Log((changed ? "hosted session now: " : "hosted session (30 s check): ") + line);
                _last = line;
                _runner = r.Pointer;
            }
        }
        catch (Exception e) { DebugLog.Error(ServerUtilsPlugin.ModId, e, "SessionWatch.Tick"); }
    }

    private static float _nextForce;

    internal static string Describe(SessionInfo info)
    {
        bool valid = false;
        try { valid = OverlandSessionInfo.IsSessionValid(info); } catch { }
        var sb = new StringBuilder();
        sb.Append('\'').Append(Short(info.Name)).Append("' region ").Append(info.Region ?? "?")
          .Append(", ").Append(info.PlayerCount).Append('/').Append(info.MaxPlayers)
          .Append(", listed ").Append(info.IsVisible ? "yes" : "NO")
          .Append(", joinable ").Append(info.IsOpen ? "yes" : "NO")
          .Append(", the game's browser accepts it ").Append(valid ? "yes" : "NO (a required property is missing or the version differs)")
          .Append(", properties: ").Append(Keys(info));
        return sb.ToString();
    }

    internal static string Keys(SessionInfo info)
    {
        var props = info.Properties;
        if (props == null) return "none";
        var sb = new StringBuilder();
        try
        {
            // Known keys only: enumerating Fusion's property dictionary through IL2CPP crashed the runtime.
            foreach (var k in Core.Manager.SessionSlots.KnownKeys)
            {
                if (!props.TryGetValue(k, out var v)) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(k);
                try { sb.Append('(').Append(v?.ToString()?.Length ?? 0).Append(" ch)"); } catch { }
            }
        }
        catch (Exception e) { return "? " + e.Message; }
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    private static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > 70 ? s.Substring(0, 70) + "..." : s;
    }

    internal static void Log(string text)
    {
        ServerUtilsPlugin.Logger.LogInfo("[SessionWatch] " + text);
    }
}

/// <summary>Logs what a hosted game is started with (after every mod's prefix has added its properties).</summary>
[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class SessionWatchStartGamePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(StartGameArgs args)
    {
        try
        {
            if (args == null) return;
            var mode = args.GameMode;
            var sb = new StringBuilder();
            sb.Append("starting ").Append(mode).Append(": session '").Append(args.SessionName ?? "(auto)").Append('\'');
            try { sb.Append(", lobby '").Append(args.CustomLobbyName ?? "(default)").Append('\''); } catch { }
            try { sb.Append(", players ").Append(args.PlayerCount?.ToString() ?? "(default)"); } catch { }
            try { sb.Append(", listed ").Append(args.IsVisible?.ToString() ?? "(default yes)"); } catch { }
            try { sb.Append(", joinable ").Append(args.IsOpen?.ToString() ?? "(default yes)"); } catch { }
            try
            {
                string fixedRegion = Fusion.Photon.Realtime.PhotonAppSettings.Global?.AppSettings?.FixedRegion;
                sb.Append(", region setting ").Append(string.IsNullOrEmpty(fixedRegion) ? "best region (auto)" : fixedRegion);
            }
            catch (Exception e) { sb.Append(", region setting ?(").Append(e.Message).Append(')'); }
            try
            {
                var props = args.SessionProperties;
                if (props == null) sb.Append(", no session properties");
                else
                {
                    sb.Append(", ").Append(props.Count).Append(" session properties: ");
                    foreach (var k in Core.Manager.SessionSlots.KnownKeys)
                        if (props.ContainsKey(k)) sb.Append(k).Append(' ');
                }
            }
            catch (Exception e) { sb.Append(", properties ?(").Append(e.Message).Append(')'); }
            SessionWatchFeature.Log(sb.ToString());
        }
        catch (Exception e) { DebugLog.Error(ServerUtilsPlugin.ModId, e, "SessionWatch.StartGame"); }
    }
}
