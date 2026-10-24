using System;
using System.Security.Cryptography;

namespace OverTheHillPlus.Core.Install;

/// <summary>
/// The release signing key (ECDSA P-256, SHA-256) and the check of a mods.json against its mods.json.sig. Only the
/// mod author holds the private key, so nobody else can publish a mod list this accepts. Pure logic so it can be unit tested.
/// </summary>
internal static class ReleaseKey
{
    /// <summary>Public key, big-endian coordinates (hex).</summary>
    public const string X = "7899edbd54e007a6612a2b2c1fd9c8718d686c1f4ca2e8ee597e9ab77c985dc8";
    public const string Y = "83f55936171f02333f54e588395d8ec416603749d8a82c9eb8d61623148da29e";

    /// <summary>What the player sees when the list fails the check.</summary>
    public const string NotSignedMessage = "The mod list from GitHub isn't signed by the mod author, so nothing was installed.";

    /// <summary>True when <paramref name="sigBase64"/> (base64 of the 64-byte r||s) is a valid signature of <paramref name="data"/> by the release key.</summary>
    public static bool Verify(byte[] data, string sigBase64) => Verify(data, sigBase64, X, Y);

    public static bool Verify(byte[] data, string sigBase64, string xHex, string yHex)
    {
        if (data == null || string.IsNullOrWhiteSpace(sigBase64)) return false;
        try
        {
            var sig = Convert.FromBase64String(sigBase64.Trim());
            if (sig.Length != 64) return false;
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = { X = Hex(xHex), Y = Hex(yHex) },
            });
            return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256);
        }
        catch (Exception) { return false; } // bad base64, bad key, platform error: not verified
    }

    private static byte[] Hex(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }
}
