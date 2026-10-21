using System;
using System.Security.Cryptography;
using System.Text;

namespace Nocturne.Core.Utils;

// DPAPI (current user) or nothing. The earlier fallback XORed with a fixed key whenever DPAPI threw or was
// unavailable, which looks protected and isn't, and Unprotect returned garbage instead of failing.
// Callers treat a throw as "no usable credential".
public static class CredentialProtection
{
    public static byte[] Protect(byte[] data)
    {
        if (data == null || data.Length == 0) return Array.Empty<byte>();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Credential storage requires Windows DPAPI.");
        return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    }

    public static byte[] Unprotect(byte[] data)
    {
        if (data == null || data.Length == 0) return Array.Empty<byte>();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Credential storage requires Windows DPAPI.");
        return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    }

    public static byte[] ProtectString(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return Array.Empty<byte>();
        return Protect(Encoding.UTF8.GetBytes(plaintext));
    }

    public static string? UnprotectString(byte[]? ciphertext)
    {
        if (ciphertext == null || ciphertext.Length == 0) return null;
        var bytes = Unprotect(ciphertext);
        return Encoding.UTF8.GetString(bytes);
    }
}

public static class PkceHelper
{
    public static string GenerateCodeVerifier()
    {
        var randomBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }
        return Base64UrlEncode(randomBytes);
    }

    public static string GenerateCodeChallenge(string verifier)
    {
        using var sha256 = SHA256.Create();
        var challengeBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(challengeBytes);
    }

    public static (string Verifier, string Challenge) Generate()
    {
        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        return (verifier, challenge);
    }

    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}
