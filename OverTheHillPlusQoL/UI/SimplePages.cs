using System;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public.Autosave;
using OverTheHillPlus.Features.Public.Chat;
using OverTheHillPlus.Features.Public.Fog;
using OverTheHillPlus.Features.Public.Gfx;
using OverTheHillPlus.Features.Public.GraphicsProfiles;
using OverTheHillPlus.Features.Public.GraphicsSky;
using OverTheHillPlus.Features.Public.JoinSmoothing;
using OverTheHillPlus.Features.Public.Rejoin;
using OverTheHillPlus.Features.Public.ServerBrowserPlus;
using OverTheHillPlus.Features.Public.Unstuck;
using OverTheHillPlus.Multiplayer;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;
using PostFx = OverTheHillPlus.Features.Public.Graphics.PostFxSettings;
using UnstuckCore = OverTheHillPlus.Features.Public.Unstuck.Unstuck;

namespace OverTheHillPlus.UI;

/// <summary>
/// The Simple view's built-in pages: Play, Servers, Hosting, Graphics. Only the settings most players use, with plain
/// labels; every row writes the same setting as its Advanced counterpart. Everything else stays in Advanced.
/// </summary>
internal static class SimplePages
{
    private const int Plus = Registry.PlusMode;

    internal static void Register()
    {
        SimpleMenu.Page(Plus, "Play", Play);
        SimpleMenu.Page(Plus, "Servers", Servers);
        SimpleMenu.Page(Plus, "Hosting", Hosting);
        SimpleMenu.Page(Plus, "Graphics", Graphics);
        // Buttons that open the menu on an Advanced tab land on the matching Simple page.
        SimpleMenu.Alias(Plus, "QoL", "Play");
        SimpleMenu.Alias(Plus, "Lobby", "Hosting");
        SimpleMenu.Alias(Plus, "Host", "Hosting");
        SimpleMenu.Alias(Plus, "Network", "Hosting");
        SimpleMenu.Alias(Plus, "Moderation", "Hosting");
        SimpleMenu.Alias(Plus, "Chat", "Hosting");
    }

    // ---------------------------------------------------------------- Play

    private static readonly int[] FpsSteps = { 0, 30, 60, 90, 120, 144, 165, 240, 360 };
    private static readonly float[] FovSteps = { 0f, 50f, 55f, 60f, 65f, 70f, 75f, 80f, 85f, 90f, 95f, 100f, 105f, 110f };

    private static void Play()
    {
        SimpleMenu.IntChoice("Frame-rate cap", "Goes higher than the game's own options. Setting a cap turns v-sync off.",
            ModConfig.FpsCap.Value, FpsSteps, v => v <= 0 ? "Game" : v + " fps", v => ModConfig.FpsCap.Value = v);
        SimpleMenu.FloatChoice("Field of view", "Driving camera field of view. Game = the game's own setting.",
            ModConfig.FovOverride.Value, FovSteps, v => v <= 0f ? "Game" : v.ToString("0"), v => ModConfig.FovOverride.Value = v);
        SimpleMenu.Toggle(ModConfig.ShowHud, "Driving HUD", $"Speed, RPM and gear on screen. {KeyName(ModConfig.HudKey.Value)} shows or hides it.");
        SimpleMenu.Toggle(ModConfig.AutoHeadlights, "Auto headlights", "Headlights on from 19:00 to 07:00, off in daylight.");
        SimpleMenu.Toggle(ModConfig.RunInBackground, "Keep running when alt-tabbed", "An alt-tabbed host otherwise freezes the session for everyone.");
        SimpleMenu.Toggle(ModConfig.SkipSplash, "Skip logo screens", "Straight to the main menu when the game starts.");
        SimpleMenu.Toggle(ModConfig.HideTutorialHints, "Hide tutorial hints", "No more reminder pop-ups.");

        float left = UnstuckCore.CooldownLeft;
        bool waiting = UnstuckCore.Waiting;
        var unstuckKey = UnstuckFeature.UnstuckKey;
        string key = unstuckKey == null || unstuckKey.Value == Key.None ? null : KeyName(unstuckKey.Value);
        SimpleMenu.Button("Unstuck my car", waiting ? "Asking the host" : left > 0f ? $"{UnityEngine.Mathf.CeilToInt(left)} s" : "Ready",
            "Puts your car back on its wheels (as a guest, a host with this mod does it)." + (key != null ? $" Hotkey: {key}." : ""),
            () => Menu.SetStatus(UnstuckCore.Use()), left <= 0f && !waiting);
    }

    // ---------------------------------------------------------------- Servers

    private static readonly float[] RefreshSteps = { 0f, 10f, 15f, 30f, 60f };

    private static void Servers()
    {
        SimpleMenu.Heading("Server browser");
        SimpleMenu.Toggle(BrowserPlus.PinFavourites, "Favourites at the top",
            BrowserPlus.FavouriteKey == null ? null : $"Star a server in the browser with {KeyName(BrowserPlus.FavouriteKey.Value)}.");
        SimpleMenu.Toggle(BrowserFriends.Highlight, "Highlight friends' servers", "Servers where a Steam friend plays stand out.");
        SimpleMenu.Toggle(BrowserPlus.OnlyFreeSlots, "Only servers with free slots", "Hides full and closed servers.");
        SimpleMenu.Toggle(BrowserPlus.HideIncompatible, "Hide other game versions", "Servers you can't join anyway.");
        if (BrowserRefresh.AutoRefresh != null && BrowserRefresh.AutoRefreshSeconds != null)
        {
            SimpleMenu.FloatChoice("Auto refresh", "Refreshes the server list by itself while it is open.",
                BrowserRefresh.AutoRefresh.Value ? BrowserRefresh.AutoRefreshSeconds.Value : 0f, RefreshSteps,
                v => v <= 0f ? "Off" : v.ToString("0") + " s",
                v =>
                {
                    BrowserRefresh.AutoRefresh.Value = v > 0f;
                    if (v > 0f) BrowserRefresh.AutoRefreshSeconds.Value = v;
                });
        }

        SimpleMenu.Heading("Rejoin");
        var last = LastServerStore.Current;
        if (RejoinCore.Active)
            SimpleMenu.Button("Cancel rejoin", RejoinCore.StatusText, "Stops the rejoin that is running.", RejoinCore.RequestCancel);
        else
            SimpleMenu.Button("Rejoin last server", last == null ? "None yet" : RejoinCore.Name(last),
                last == null ? "The next server you join as a guest is remembered here."
                : RejoinCore.InAnySession ? "Leave the current session first (works from the main menu or the server browser)."
                : "Joins the server you were last in.",
                RejoinCore.RequestManual, last != null && !RejoinCore.InAnySession);
        SimpleMenu.Toggle(RejoinFeature.AutoRejoin, "Auto-rejoin after a disconnect", "Counts down, then rejoins. Never after a kick, a ban or quitting.");
    }

    // ---------------------------------------------------------------- Hosting

    private static readonly int[] SizeSteps = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
    private static readonly float[] AutosaveSteps = { 0f, 5f, 10f, 15f, 30f };

    private static void Hosting()
    {
        SimpleMenu.Heading("Your server");
        string name = SimpleMenu.Text("Server name", ModConfig.CustomRoomName.Value, 40, "Used for servers you host. Empty = the game's default name.");
        if (name != ModConfig.CustomRoomName.Value) ModConfig.CustomRoomName.Value = name;
        if (Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ModConfig.CustomRoomName.Value), out string why))
            SimpleMenu.Note($"<color=#E5534B>Name not used: {why}.</color>");

        var styles = RoomNames.Styles;
        SimpleMenu.Choice("Name colour", "How your server's name shows in everyone's server browser.",
            Array.IndexOf(styles, ModConfig.RoomNameStyle.Value), styles, i => ModConfig.RoomNameStyle.Value = styles[i]);

        int custom = ModConfig.CustomMaxPlayers.Value;
        SimpleMenu.IntChoice("Max players", "Picked for you in Create Server. Above 4 players, everyone should run this mod.",
            custom >= 2 ? custom : ModConfig.DefaultMaxPlayers.Value, SizeSteps, v => v.ToString(), SetLobbySize);

        SimpleMenu.Toggle(ModConfig.JoinMessageEnabled, "Greet players who join",
            "A pop-up for everyone when a player joins. Edit the text in Advanced > Hosting.");

        SimpleMenu.Heading("Smooth hosting");
        SimpleMenu.Toggle(CarPreloader.PreloadEnabled, "Smooth joins", "Preloads every car so players joining don't freeze your game.");
        if (AutosaveConfig.Enabled != null && AutosaveConfig.IntervalMinutes != null)
        {
            SimpleMenu.FloatChoice("Autosave", "Saves the game every few minutes while you play solo or host.",
                AutosaveConfig.Enabled.Value ? AutosaveConfig.IntervalMinutes.Value : 0f, AutosaveSteps,
                v => v <= 0f ? "Off" : v.ToString("0") + " min",
                v =>
                {
                    AutosaveConfig.Enabled.Value = v > 0f;
                    if (v > 0f) AutosaveConfig.IntervalMinutes.Value = v;
                });
        }
        SimpleMenu.Toggle(Net.Enabled, "Mod network", "Lets players with this mod share chat, unstuck requests and more.");
        SimpleMenu.Toggle(ChatFeature.Enabled, "Text chat",
            ChatFeature.ChatKey == null ? "Chat with other players who run this mod." : $"Press {KeyName(ChatFeature.ChatKey.Value)} to chat with other players who run this mod.");
    }

    private static void SetLobbySize(int n)
    {
        n = UnityEngine.Mathf.Clamp(n, 2, LobbyRules.MaxPlayers);
        ModConfig.DefaultMaxPlayers.Value = n;
        if (n > LobbyRules.StockMaxPlayers) ModConfig.ExpandLobbySize.Value = true;
        // An exact size typed in Advanced overrides the Create Server choice: keep it in step with this row.
        if (ModConfig.CustomMaxPlayers.Value >= 2) ModConfig.CustomMaxPlayers.Value = n;
        Menu.ForgetTypedText();
    }

    // ---------------------------------------------------------------- Graphics

    private static readonly float[] ScaleSteps = { 0.5f, 0.67f, 0.75f, 0.85f, 1f, 1.25f, 1.5f, 2f };
    private static readonly string[] ColourLooks = { "Game", "Natural", "Vivid", "Cinematic", "Retro" };
    private static string[] _skyLooks;

    private static void Graphics()
    {
        // Changed since the last tier: shown as "Balanced*", and the arrows step from that tier (not from an end).
        int tier = GraphicsProfilesFeature.CurrentTier(), last = GraphicsProfilesFeature.LastTier();
        var tiers = GraphicsTiers.Names;
        SimpleMenu.Choice("Quality", "All graphics options at once: Beauty, Balanced (the defaults), Performance or Potato.",
            tier, tiers, GraphicsProfilesFeature.ApplyTierAt, wrap: false,
            shown: tier >= 0 ? null : last >= 0 ? tiers[last] + "*" : "Custom", from: last >= 0 ? last : 1);

        if (GraphicsLighting.RenderScaleOn != null && GraphicsLighting.RenderScale != null)
        {
            bool upscaler = LightingScene.TndActive;
            SimpleMenu.FloatChoice("Render scale",
                upscaler ? "The game's upscaler is on and picks the resolution itself (game Settings > Graphics)."
                    : "Below 100% is faster and softer, above it is sharper and slower.",
                GraphicsLighting.RenderScaleOn.Value ? GraphicsLighting.RenderScale.Value : 1f, ScaleSteps,
                v => Math.Abs(v - 1f) < 0.001f ? "100%" : UnityEngine.Mathf.RoundToInt(v * 100f) + "%",
                v =>
                {
                    bool custom = Math.Abs(v - 1f) > 0.001f;
                    GraphicsLighting.RenderScaleOn.Value = custom;
                    if (custom) GraphicsLighting.RenderScale.Value = v;
                }, !upscaler);
        }

        if (PostFx.Enabled != null)
        {
            int colour = 0;
            if (PostFx.Enabled.Value)
            {
                colour = Array.IndexOf(ColourLooks, PostFx.Preset.Value);
                if (colour == 0) colour = -1; // "Game" is not a preset name: on with an unknown preset = custom
            }
            SimpleMenu.Choice("Colour look", "A colour grade on top of the game's picture. Game = the original look.",
                colour, ColourLooks, i => PostFx.ApplyPreset(i == 0 ? "Off" : ColourLooks[i]));
        }

        if (SkySettings.Enabled != null)
        {
            var looks = SkySettings.Looks;
            if (_skyLooks == null)
            {
                _skyLooks = new string[looks.Length + 1];
                _skyLooks[0] = "Game";
                for (int i = 0; i < looks.Length; i++) _skyLooks[i + 1] = looks[i].Name;
            }
            SimpleMenu.Choice("Sky", "Clouds and sky colours. Only you see it; Game = the game's own sky.",
                CurrentSky(looks), _skyLooks, i =>
                {
                    if (i == 0) SkySettings.Enabled.Value = false; // keeps a custom sky for Advanced to switch back on
                    else SkySettings.ApplyLook(looks[i - 1]);
                });
        }

        SimpleMenu.Choice("Fog", "Only changes what you see. Game = the game's own fog.",
            FogAtmosphere.CurrentSimpleLook(), FogAtmosphere.SimpleLooks, FogAtmosphere.ApplySimpleLook);
    }

    private static int CurrentSky(SkySettings.Look[] looks)
    {
        if (!SkySettings.Enabled.Value) return 0;
        for (int i = 0; i < looks.Length; i++)
        {
            var l = looks[i];
            if (Math.Abs(SkySettings.CloudAmount.Value - l.Amount) < 0.005f && Math.Abs(SkySettings.SkyTint.Value - l.SkyT) < 0.005f
                && string.Equals(SkySettings.SkyColor.Value, l.Sky, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return -1;
    }

    // ---------------------------------------------------------------- helpers

    internal static string KeyName(Key key) => key == Key.None ? "no key" : key.ToString();
}
