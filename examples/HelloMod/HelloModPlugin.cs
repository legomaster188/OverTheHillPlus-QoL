using System;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Api;
using OverTheHillPlus.Core.Manager; // ModInfo
using UnityEngine;

namespace HelloMod;

/// <summary>
/// A tiny mod for the OTH Mod Manager's public API (docs/modding-api.md): its own MODS page with a few settings, a
/// greeting drawn on screen, and a "wave" other players who run it see.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency("com.oth.core")] // load after the Mod Manager
public class HelloModPlugin : BasePlugin
{
    public const string Guid = "com.example.hellomod";
    public const string Name = "Hello Mod";
    public const string Version = "1.0.0";

    /// <summary>Names the config file (BepInEx/config/oth.hellomod.cfg) and the MODS page's switch.</summary>
    private const string ModId = "hellomod";

    /// <summary>Our network channel: anything from 1000 to 65000, kept forever once released.</summary>
    private const int WaveChannel = 41420;
    private const byte WaveMessage = 1;

    private static readonly string[] Corners = { "Top left", "Top right", "Bottom left", "Bottom right" };

    private static ConfigEntry<bool> _show;
    private static ConfigEntry<string> _greeting;
    private static ConfigEntry<float> _size;
    private static ConfigEntry<int> _corner;
    private static ConfigEntry<string> _waveText;

    private static string _lastWave;
    private static float _waveShownFor;
    private static int _waves;
    private static GUIStyle _style;

    public override void Load()
    {
        // 1. Register first, so the mod stays on the MODS page (and can be switched back on) while it is off.
        Mods.Register(new ModInfo
        {
            Id = ModId,
            Name = Name,
            Description = "Says hello in a corner of the screen, and waves to other players who run it.",
            Version = Version,
            DrawSimple = DrawPage,
        });
        // 2. Switched off on the MODS page: do nothing at all this session.
        if (!Mods.IsEnabled(ModId))
        {
            Log.LogInfo($"{Name} is switched off on the MODS page.");
            return;
        }
        try
        {
            // 3. Settings in our own file, BepInEx/config/oth.hellomod.cfg (saved for us).
            var cfg = Mods.ConfigFor(ModId);
            _show = cfg.Bind("Greeting", "Show", true, "Show the greeting on screen.");
            _greeting = cfg.Bind("Greeting", "Text", "Hello from Hello Mod!", "What the greeting says.");
            _size = cfg.Bind("Greeting", "Size", 18f, "Text size of the greeting.");
            _corner = cfg.Bind("Greeting", "Corner", 0, "0 top left, 1 top right, 2 bottom left, 3 bottom right.");
            _waveText = cfg.Bind("Wave", "Text", "o/", "What your wave says to other players.");

            // 4. More settings in the Advanced fold of our page.
            Page.Section(ModId, "Greeting look", DrawLook);

            // 5. Per-frame work and an overlay.
            Hooks.OnTick(ModId, () => _waveShownFor = Math.Max(0f, _waveShownFor - Time.unscaledDeltaTime));
            Hooks.OnGui(ModId, DrawGreeting);

            // 6. A network channel: guests reach everyone through the host (AllowRelay).
            Net.Register(ModId, WaveChannel, new Net.ChannelOptions
            {
                Name = "wave",
                MaxPayload = 64,
                RatePerSecond = 1f,
                Burst = 3,
                AllowRelay = true,
                Active = () => _show.Value,
            }, OnWave);

            // A mod that gives the player something the game wouldn't (moved cars, items...) would also call
            // Advantage.MarkUsed() at that moment, so achievements stay locked. Saying hello isn't that.
            Log.LogInfo($"{Name} {Version} loaded.");
        }
        catch (Exception e)
        {
            // 7. The MODS page says "Failed to start"; the game and the other mods keep running.
            Mods.MarkFailed(ModId, e.Message);
            Log.LogError($"{Name} failed to start: {e}");
        }
    }

    /// <summary>Our MODS page (everyday settings).</summary>
    private static void DrawPage()
    {
        Page.Row.Toggle(_show, "Show greeting", "Shows the greeting on screen.");
        string text = Page.Row.Text("Greeting", _greeting.Value, 40, "What the greeting says.");
        if (text != _greeting.Value) _greeting.Value = text;

        Page.Heading("Wave");
        string wave = Page.Row.Text("Wave text", _waveText.Value, 24, "What your wave says to other players.");
        if (wave != _waveText.Value) _waveText.Value = wave;
        bool online = Net.IsHost || Net.IsGuest;
        Page.Row.Button("Wave", online ? "to everyone" : "join a session first", "Players who run Hello Mod see your wave.", SendWave, online);
        Page.Note(_waves == 0 ? "No waves received yet." : $"{_waves} wave(s) received. Last one: {_lastWave}");
    }

    /// <summary>The Advanced fold of our page.</summary>
    private static void DrawLook()
    {
        Page.Card("Greeting look", "Where and how big the greeting is.", () =>
        {
            Page.Row.Slider(_size, "Text size", 10f, 40f, "0", " px");
            Page.Row.Choice("Corner", "Which corner of the screen.", Math.Clamp(_corner.Value, 0, 3), Corners, i => _corner.Value = i);
        });
    }

    private static void DrawGreeting()
    {
        if (!_show.Value) return;
        _style ??= new GUIStyle(GUI.skin.label);
        _style.fontSize = Mathf.RoundToInt(_size.Value);
        string text = _waveShownFor > 0f ? $"{_greeting.Value}   (wave: {_lastWave})" : _greeting.Value;
        const float w = 700f, h = 60f, pad = 20f;
        float x = _corner.Value % 2 == 0 ? pad : Screen.width - w - pad;
        float y = _corner.Value < 2 ? pad : Screen.height - h - pad;
        _style.alignment = _corner.Value % 2 == 0 ? TextAnchor.UpperLeft : TextAnchor.UpperRight;
        GUI.Label(new Rect(x, y, w, h), text, _style);
    }

    private static void SendWave()
    {
        string text = _waveText.Value ?? "";
        // Text a player typed goes through the word filter before other players see it.
        if (TextFilter.IsBlocked(text, out _)) text = "o/";
        Net.Broadcast(WaveChannel, WaveMessage, Encoding.UTF8.GetBytes(text));
    }

    private static void OnWave(Net.Message m)
    {
        if (m.Type != WaveMessage) return;
        // Payloads are untrusted: check them like any input.
        string text = Encoding.UTF8.GetString(m.Payload ?? Array.Empty<byte>());
        if (text.Length == 0 || text.Length > 24 || TextFilter.IsBlocked(text, out _)) text = "o/";
        _lastWave = text;
        _waves++;
        _waveShownFor = 5f;
    }
}
