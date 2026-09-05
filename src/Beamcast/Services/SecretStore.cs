using System.Security.Cryptography;
using System.Text;

namespace Beamcast;

/// <summary>
/// Keeps small secrets (remembered room passwords, owner tokens) readable only by this Windows
/// account on this machine, through the Data Protection API. Nothing here is a substitute for the
/// room's own end-to-end encryption; it only protects the settings file at rest.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("beamcast/secrets/v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
            return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}
