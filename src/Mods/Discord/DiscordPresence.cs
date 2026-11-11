using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverTheHillPlus.Discord;

/// <summary>What the game is doing right now, as read on the main thread (plain data, no game types).</summary>
internal sealed class PresenceState
{
    /// <summary>A map is loaded (false = main menu / loading).</summary>
    public bool InWorld;
    public string Map;
    public string Car;
    /// <summary><see cref="Car"/> is a stand-in phrase ("a car") rather than a car's name.</summary>
    public bool CarGeneric;
    /// <summary>The picture name of a modded car with a public name (see <see cref="DiscordPresence.CarImages"/>), else null.</summary>
    public string CarImage;
    /// <summary>In a multiplayer session (hosting or joined); false = solo.</summary>
    public bool InSession;
    public int Players, MaxPlayers;
    /// <summary>The room's display name (may hold rich-text tags). Only shown when the player allows it.</summary>
    public string ServerName;
    /// <summary>When this play session started (Unix seconds), for Discord's elapsed timer; 0 = none.</summary>
    public long StartUnix;
    /// <summary>You host the session (false = joined someone's).</summary>
    public bool Hosting;
    /// <summary>The session's join code (only read when the player allows it).</summary>
    public string RoomCode;
    /// <summary>How many of our mods are switched on (0 = unknown).</summary>
    public int ModCount;
    /// <summary>What you are doing besides driving ("In the lobby", "Taking photos", ...; see
    /// <see cref="DiscordPresence.ActivityFor"/>), null while driving.</summary>
    public string Activity;
    /// <summary>The same for everyone in the session (a hash of the session, never its name), so Discord groups the
    /// players of one server as a party; null = none.</summary>
    public string PartyId;
}

/// <summary>The player's presence settings.</summary>
internal sealed class PresenceOptions
{
    public bool ShowMap = true;
    public bool ShowCar = true;
    public bool ShowPlayerCount = true;
    /// <summary>Off by default: the server's name is shown on your Discord profile to everyone who can see it.</summary>
    public bool ShowServerName;
    public bool ShowElapsed = true;
    /// <summary>Off by default: anyone who can see your Discord profile could join with the code.</summary>
    public bool ShowRoomCode;
    /// <summary>"Hosting a server" instead of "In a server" when you are the host.</summary>
    public bool ShowHosting = true;
    /// <summary>The number of mods in the image's hover text.</summary>
    public bool ShowModCount = true;
    /// <summary>An art asset key uploaded to the Discord application, or an https image URL ("" = the
    /// <see cref="DiscordPresence.DefaultImage"/> logo, "none" = text only).</summary>
    public string LargeImageKey = "";
    public string LargeText = "over the hill";
    /// <summary>A "Get the mods" button under the presence (Discord shows it to others, not to yourself).</summary>
    public bool ShowButton = true;
}

internal enum DiscordReply { Other, Ready, Error }

/// <summary>
/// Discord Rich Presence messages (pure; the tests compile this file on its own): the handshake, SET_ACTIVITY with
/// the activity built from <see cref="PresenceState"/> and <see cref="PresenceOptions"/>, clearing it, and the
/// reconnect backoff. Discord shows "Playing &lt;application name&gt;" above these lines:
/// details = map (and car), state = "Solo" or "In a server (n/max)".
/// </summary>
internal static class DiscordPresence
{
    /// <summary>Discord's limit for details, state and the asset texts.</summary>
    public const int MaxText = 128;
    /// <summary>The mods' logo: an https image URL (Discord's media proxy takes any public image URL as large_image or
    /// small_image, see developers/events/gateway-events "Activity Asset Image"), from the public repo's assets/discord.</summary>
    public const string DefaultImage = ImageBase + "logo.png";
    /// <summary>Where the public repo serves the images (Liveries/discord in the private one).</summary>
    public const string ImageBase = "https://raw.githubusercontent.com/legomaster188/OverTheHillPlus-QoL/main/assets/discord/";
    /// <summary>The modded cars with a picture in the public repo (assets/discord/car_&lt;name&gt;.png, Liveries/discord in the
    /// private one); a car joins the list when its picture is published with the car.</summary>
    public static readonly string[] CarImages =
    { "vera", "tercel", "aro", "hilo", "bastion", "hilo22", "drifter", "pixie", "bento", "mule", "drover", "marmot", "ibex", "limber", "badger", "nomad", "gemse", "longhorn", "meerkat", "jackal", "marten", "ermine", "grayling", "stoat", "mastiff" };
    /// <summary>The button's link: the releases page, where every pack's installer is.</summary>
    public const string ModsUrl = "https://github.com/legomaster188/OverTheHillPlus-QoL/releases";
    public const string ButtonLabel = "Get the mods";
    /// <summary>Discord's limit for an image URL.</summary>
    public const int MaxImageUrl = 256;

    private static readonly Regex ClientIdPattern = new("^[0-9]{17,20}$", RegexOptions.CultureInvariant);
    private static readonly Regex AssetKeyPattern = new("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new("<[^<>]{0,64}>", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly char[] UrlBreakers = { '"', '\\', ' ', '<', '>' };

    /// <summary>A Discord application id: a snowflake of 17-20 digits (anything else never reaches the pipe).</summary>
    public static bool ValidClientId(string id) => !string.IsNullOrEmpty(id) && ClientIdPattern.IsMatch(id);

    public static string HandshakeJson(string clientId) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("v", 1);
        w.WriteString("client_id", clientId ?? "");
        w.WriteEndObject();
    });

    public static string SetActivityJson(int pid, PresenceState state, PresenceOptions options, string nonce) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("cmd", "SET_ACTIVITY");
        w.WriteStartObject("args");
        w.WriteNumber("pid", pid);
        w.WritePropertyName("activity");
        WriteActivity(w, state ?? new PresenceState(), options ?? new PresenceOptions());
        w.WriteEndObject();
        w.WriteString("nonce", nonce ?? "0");
        w.WriteEndObject();
    });

    /// <summary>SET_ACTIVITY with a null activity: Discord removes the presence.</summary>
    public static string ClearActivityJson(int pid, string nonce) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("cmd", "SET_ACTIVITY");
        w.WriteStartObject("args");
        w.WriteNumber("pid", pid);
        w.WriteNull("activity");
        w.WriteEndObject();
        w.WriteString("nonce", nonce ?? "0");
        w.WriteEndObject();
    });

    /// <summary>The two visible lines; null for a line that is left out.</summary>
    public static (string details, string state) Lines(PresenceState s, PresenceOptions o)
    {
        if (!s.InWorld) return ("In the menus", null);
        string map = o.ShowMap ? Field(s.Map) : null;
        string car = o.ShowCar ? Field(s.Car) : null;
        string activity = Field(s.Activity);
        string details = activity != null ? (map != null ? map + " · " + activity : activity)
            : map != null && car != null ? map + " · " + car
            : map ?? (car != null ? (s.CarGeneric ? "Driving " : "Driving the ") + car : "Driving");
        string state;
        if (!s.InSession) state = "Solo";
        else
        {
            string name = o.ShowServerName ? Field(PlainName(s.ServerName)) : null;
            bool host = o.ShowHosting && s.Hosting;
            state = name != null ? (host ? "Hosting " : "In ") + name : host ? "Hosting a server" : "In a server";
            // With a party Discord writes the count itself ("(3 of 8)") next to this line.
            if (o.ShowPlayerCount && s.Players > 0 && s.MaxPlayers > 0 && Party(s, o) == null) state += $" ({s.Players}/{s.MaxPlayers})";
            string code = o.ShowRoomCode ? RoomCodeText(s.RoomCode) : null;
            if (code != null) state += " · Code " + code;
        }
        return (Clip(details, MaxText), Field(Clip(state, MaxText)));
    }

    /// <summary>Changes whenever what Discord would show changes (the elapsed start included).</summary>
    public static string ActivityKey(PresenceState s, PresenceOptions o)
    {
        var (details, state) = Lines(s, o);
        var party = Party(s, o);
        return details + "\n" + state + "\n" + (o.ShowElapsed ? s.StartUnix : 0) + "\n" + ImageKey(o) + "\n" + HoverText(s, o) + "\n" +
               CarImageKey(s, o) + "\n" + (party == null ? "" : party.Value.id + "/" + party.Value.size + "/" + party.Value.max) + "\n" + o.ShowButton;
    }

    /// <summary>The image's hover text: the player's text, plus the number of mods when allowed.</summary>
    public static string HoverText(PresenceState s, PresenceOptions o)
    {
        string text = Field(o.LargeText);
        if (o.ShowModCount && s.ModCount > 0)
        {
            string mods = s.ModCount == 1 ? "1 mod" : s.ModCount + " mods";
            text = text != null ? text + " · " + mods : "Modded · " + mods;
        }
        return Field(Clip(text, MaxText));
    }

    /// <summary>A join code as shown: letters and digits only (dashes and spaces dropped), upper case, 3-12 long;
    /// null for anything else.</summary>
    public static string RoomCodeText(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var sb = new StringBuilder(code.Length);
        foreach (char c in code.Trim())
        {
            if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            else if (c != '-' && c != ' ') return null;
        }
        return sb.Length is >= 3 and <= 12 ? sb.ToString() : null;
    }

    private static void WriteActivity(Utf8JsonWriter w, PresenceState s, PresenceOptions o)
    {
        var (details, state) = Lines(s, o);
        w.WriteStartObject();
        if (details != null) w.WriteString("details", details);
        if (state != null) w.WriteString("state", state);
        if (o.ShowElapsed && s.StartUnix > 0)
        {
            w.WriteStartObject("timestamps");
            w.WriteNumber("start", s.StartUnix);
            w.WriteEndObject();
        }
        string image = ImageKey(o), small = null;
        string text = HoverText(s, o);
        string car = CarImageKey(s, o);
        if (car != null) { small = image; image = car; }   // the car's picture is the big one, the logo moves to the corner
        if (image != null || text != null)
        {
            w.WriteStartObject("assets");
            if (image != null) w.WriteString("large_image", image);
            if (text != null) w.WriteString("large_text", text);
            if (small != null) w.WriteString("small_image", small);
            if (small != null && car != null) w.WriteString("small_text", "Over The Hill Mods");
            w.WriteEndObject();
        }
        var party = Party(s, o);
        if (party != null)
        {
            w.WriteStartObject("party");
            w.WriteString("id", party.Value.id);
            w.WriteStartArray("size");
            w.WriteNumberValue(party.Value.size);
            w.WriteNumberValue(party.Value.max);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        if (o.ShowButton)
        {
            w.WriteStartArray("buttons");
            w.WriteStartObject();
            w.WriteString("label", ButtonLabel);
            w.WriteString("url", ModsUrl);
            w.WriteEndObject();
            w.WriteEndArray();
        }
        w.WriteBoolean("instance", false);
        w.WriteEndObject();
    }

    /// <summary>The session as a Discord party (id and "n of max"), when the count is shown and known; else null.</summary>
    public static (string id, int size, int max)? Party(PresenceState s, PresenceOptions o)
    {
        if (s == null || o == null || !o.ShowPlayerCount || !s.InWorld || !s.InSession) return null;
        if (s.Players <= 0 || s.MaxPlayers <= 0 || s.Players > s.MaxPlayers) return null;
        string id = (s.PartyId ?? "").Trim();
        if (id.Length < 2 || id.Length > 128) return null;
        return (id, s.Players, s.MaxPlayers);
    }

    /// <summary>
    /// What you are doing besides driving, from the class name of the game's top state ("StateLobbyMenu", ...): a short
    /// phrase for the presence, or null (driving, paused, anything else).
    /// </summary>
    public static string ActivityFor(string topState)
    {
        if (string.IsNullOrEmpty(topState)) return null;
        if (topState == "StateLobbyMenu") return "In the lobby";
        if (topState.StartsWith("StatePhotoMode", StringComparison.Ordinal)) return "Taking photos";
        if (topState == "StateMapScreen") return "Checking the map";
        if (topState.IndexOf("Garage", StringComparison.Ordinal) >= 0 || topState.IndexOf("Customi", StringComparison.Ordinal) >= 0)
            return "In the garage";
        if (topState.IndexOf("Camp", StringComparison.Ordinal) >= 0) return "Camping";
        return null;
    }

    /// <summary>A party id for a session: a short hash of its name (the name itself never leaves the game).</summary>
    public static string PartyIdFor(string sessionName)
    {
        if (string.IsNullOrEmpty(sessionName)) return null;
        ulong h = 1469598103934665603UL;
        foreach (char c in sessionName) { h ^= c; h *= 1099511628211UL; }
        return "oth-" + h.ToString("x16");
    }

    /// <summary>The picture URL of the modded car being driven, when the player shows the car and keeps the default
    /// image; null otherwise.</summary>
    private static string CarImageKey(PresenceState s, PresenceOptions o)
    {
        if (!o.ShowCar || s == null || !s.InWorld || string.IsNullOrEmpty(s.CarImage) || !string.IsNullOrWhiteSpace(o.LargeImageKey)) return null;
        return Array.IndexOf(CarImages, s.CarImage) >= 0 ? ImageBase + "car_" + s.CarImage + ".png" : null;
    }

    private static string ImageKey(PresenceOptions o)
    {
        string k = (o.LargeImageKey ?? "").Trim();
        if (k.Length == 0) return DefaultImage;
        if (string.Equals(k, "none", StringComparison.OrdinalIgnoreCase)) return null;
        if (k.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return k.Length <= MaxImageUrl && k.IndexOfAny(UrlBreakers) < 0 ? k : null;
        return AssetKeyPattern.IsMatch(k) ? k : null;
    }

    /// <summary>Text for a field, or null when Discord would refuse it (under 2 characters).</summary>
    private static string Field(string s)
    {
        if (s == null) return null;
        s = s.Trim();
        return s.Length >= 2 ? s : null;
    }

    /// <summary>A room name as plain text: rich-text tags removed, whitespace collapsed.</summary>
    public static string PlainName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s = name.IndexOf('<') >= 0 ? Tags.Replace(name, "") : name;
        return Spaces.Replace(s, " ").Trim();
    }

    /// <summary>At most <paramref name="max"/> UTF-16 units, never cutting an emoji in half.</summary>
    public static string Clip(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        int n = max;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s.Substring(0, n);
    }

    /// <summary>Seconds to wait before reconnect attempt number <paramref name="failures"/> + 1: 5, 10, 20, 40, then 60.</summary>
    public static int BackoffSeconds(int failures)
    {
        if (failures <= 0) return 5;
        if (failures >= 4) return 60;
        return 5 << failures;
    }

    /// <summary>What a reply frame is: the READY dispatch after the handshake, an error, or anything else.</summary>
    public static DiscordReply Classify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return DiscordReply.Other;
            if (!doc.RootElement.TryGetProperty("evt", out var evt) || evt.ValueKind != JsonValueKind.String) return DiscordReply.Other;
            return evt.GetString() switch
            {
                "READY" => DiscordReply.Ready,
                "ERROR" => DiscordReply.Error,
                _ => DiscordReply.Other,
            };
        }
        catch (JsonException ex) { DebugLog.Error("discord", ex, "DiscordPresence.Classify"); return DiscordReply.Other; }
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) body(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
