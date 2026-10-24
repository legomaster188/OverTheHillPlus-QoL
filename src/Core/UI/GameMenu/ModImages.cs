using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.GameMenu;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The cards' images: a mod's own image is the "card.png" resource embedded in the DLL that registered it (16:9,
/// 640 × 360; every project with an Assets/card.png embeds it, see src/Directory.Build.props), decoded once into a
/// texture and a sprite that live for the session. A mod without one, a plugin of another creator and a mod that
/// isn't installed get a generated card (<see cref="CardArt"/>). A broken image is logged once and the mod gets the
/// generated card: one bad file never breaks the grid.
/// </summary>
internal static class ModImages
{
    /// <summary>The embedded resource's name (LogicalName) in a mod's DLL.</summary>
    internal const string Resource = "card.png";

    private const int MaxBytes = 8 * 1024 * 1024;
    private const int ArtW = 320, ArtH = 180;

    private static readonly Dictionary<string, Sprite> Cache = new();
    private static readonly HashSet<string> Failed = new();
    private static readonly HashSet<string> Logged = new();

    /// <summary>The sprite of <paramref name="card"/> (made on first use, then cached), or null when even the
    /// generated card could not be made.</summary>
    internal static Sprite For(CardInfo card)
    {
        if (card == null) return null;
        string key = card.ImageKey;
        if (Cache.TryGetValue(key, out var cached))
        {
            if (cached != null) return cached;
            Cache.Remove(key); // destroyed (should not happen: kept for the session)
        }
        if (Failed.Contains(key)) return null;
        Sprite made = null;
        if (!string.IsNullOrEmpty(card.ModId))
        {
            try
            {
                var png = Embedded(card.ModId);
                if (png != null)
                {
                    OverTheHillPlus.CrashTrail.Mark("MODS cards: decoding the image of " + card.ModId);
                    made = Make(png, "OTH_Card_" + card.ModId);
                    if (made == null) Warn(key, $"the card image of {card.ModId} is not a readable PNG; it gets a generated card");
                }
            }
            catch (Exception e) { Warn(key, $"the card image of {card.ModId}: {e.Message}; it gets a generated card"); }
        }
        if (made == null)
        {
            try
            {
                OverTheHillPlus.CrashTrail.Mark("MODS cards: generating a card for " + (card.Title ?? card.Seed));
                made = Make(CardArt.Png(string.IsNullOrEmpty(card.Seed) ? card.Title : card.Seed, card.Title, ArtW, ArtH), "OTH_CardArt");
            }
            catch (Exception e) { Warn("art " + key, $"generating the card of {card.Title}: {e.Message}"); }
        }
        if (made == null)
        {
            Failed.Add(key);
            return null;
        }
        Cache[key] = made;
        return made;
    }

    /// <summary>The card.png bytes of the DLL that registered mod <paramref name="id"/>, or null.</summary>
    private static byte[] Embedded(string id)
    {
        var asm = ModManager.SourceOf(id);
        if (asm == null) return null;
        using var s = asm.GetManifestResourceStream(Resource);
        if (s == null) return null;
        if (s.Length > MaxBytes) throw new InvalidDataException($"{Resource} is {s.Length / 1024} KB (at most {MaxBytes / 1024 / 1024} MB)");
        var bytes = new byte[s.Length];
        int got = 0;
        while (got < bytes.Length)
        {
            int n = s.Read(bytes, got, bytes.Length - got);
            if (n <= 0) break;
            got += n;
        }
        return got == bytes.Length ? bytes : null;
    }

    private static Sprite Make(byte[] png, string name)
    {
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = name };
        // Non-readable: the pixels live on the GPU only (nothing reads them back).
        if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(png), true))
        {
            Object.Destroy(t);
            return null;
        }
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Trilinear;
        t.hideFlags = HideFlags.HideAndDontSave;
        var sprite = Sprite.Create(t, new Rect(0f, 0f, t.width, t.height), new Vector2(0.5f, 0.5f), 100f);
        sprite.name = name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static void Warn(string key, string message)
    {
        if (Logged.Add(key)) Core.CorePlugin.Logger?.LogWarning("[Core] MODS cards: " + message);
    }
}
