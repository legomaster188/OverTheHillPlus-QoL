using OverTheHillPlus.Features.Public.Chat;
using OverTheHillPlus.UI;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Chat;

/// <summary>Chat's everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class ChatSimple
{
    internal static void Draw()
    {
        SimpleMenu.Heading("Chat");
        if (ChatFeature.Enabled != null)
            SimpleMenu.Toggle(ChatFeature.Enabled, "Text chat",
                ChatFeature.ChatKey == null ? "Chat with other players who run this mod." : $"Press {KeyName(ChatFeature.ChatKey.Value)} to chat with other players who run this mod.");
        // Core's setting: chat travels over the mod network.
        if (Net.Enabled != null)
            SimpleMenu.Toggle(Net.Enabled, "Mod network", "Chat travels over it. Hosts: start the server after switching it on.");
    }

    private static string KeyName(Key key) => key == Key.None ? "no key" : key.ToString();
}
