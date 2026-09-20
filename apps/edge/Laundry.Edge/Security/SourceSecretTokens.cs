using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Laundry.Edge.Security;

internal static class SourceSecretTokens
{
    private const int TokenBytes = 32;

    public static (string Token, byte[] Digest) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return (WebEncoders.Base64UrlEncode(bytes), SHA256.HashData(bytes));
    }

    public static bool TryDigest(string token, out byte[] digest)
    {
        digest = [];
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token);
            if (bytes.Length != TokenBytes) return false;
            digest = SHA256.HashData(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
