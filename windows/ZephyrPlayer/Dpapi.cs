using System;
using System.Security.Cryptography;
using System.Text;

namespace ZephyrPlayer;

/// <summary>Protect pairing tokens at rest with Windows CurrentUser DPAPI.
/// PairedStore atomically migrates a readable legacy file. An unreadable wrapped
/// value fails the entire store closed, preserving the original file.</summary>
internal static class Dpapi
{
    private const string Prefix = "dpapi:";

    internal static string Wrap(string plain) => Prefix + Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    /// <summary>The plain value, or null when a wrapped value cannot be unwrapped.</summary>
    internal static string? Unwrap(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // legacy plain
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(stored[Prefix.Length..]), null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }
}
