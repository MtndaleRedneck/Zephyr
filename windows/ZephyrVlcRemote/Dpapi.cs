using System.Security.Cryptography;
using System.Text;

namespace ZephyrVlcRemote;

/// <summary>Tokens and the pending VLC password are wrapped with DPAPI (CurrentUser) at
/// rest (round-5): the config file alone no longer hands a reader live credentials.
/// Migration is fail-closed and non-destructive: values written before this change stay
/// readable (plain fallback) and are wrapped the next time the config is saved; a value
/// that cannot be unwrapped is skipped (that one phone looks unpaired) and the FILE is
/// never altered on read.</summary>
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
