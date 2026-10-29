using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ZephyrPlayer;

internal static class PlaybackSourcePolicy
{
    internal static bool Allowed(string source, string library, IPAddress peer, bool subtitle = false)
    {
        if(peer.IsIPv4MappedToIPv6)peer=peer.MapToIPv4();
        if (source.Length > 8192 || source.Any(char.IsControl) || !Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0) return false;
        if (uri.IsFile)
        {
            if (uri.Host.Length != 0 || uri.IsUnc || library.Length == 0) return false;
            var ext = Path.GetExtension(uri.LocalPath).ToLowerInvariant();
            if (!(subtitle ? new[]{".srt",".vtt",".ass",".ssa"} : new[]{".mkv",".mp4",".m4v",".avi",".mov",".wmv",".ts",".mpg",".mpeg",".webm",".flv",".m2ts"}).Contains(ext)) return false;
            try
            {
                var path = Path.GetFullPath(uri.LocalPath);
                var root = Path.GetFullPath(library).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
                if (OperatingSystem.IsWindows())
                {
                    if (!Regex.IsMatch(path, @"^[A-Za-z]:[\\/]")) return false;
                    var drive = new DriveInfo(Path.GetPathRoot(path)!);
                    if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) return false;
                }
                // Resolve every junction in both the chosen root and child path.
                // Cloud-placeholder reparse points without a link target remain local.
                var resolvedRoot = ResolvePath(Path.GetFullPath(library)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var resolvedFile = ResolvePath(path);
                if (!resolvedFile.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase) || resolvedFile.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                if (OperatingSystem.IsWindows())
                {
                    if (path.Substring(2).Contains(':') || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\")) return false;
                    var drive = new DriveInfo(Path.GetPathRoot(resolvedFile)!);
                    if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) return false;
                }
                return true;
            }
            catch { return false; }
        }
        if (uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out var host)
            || host.AddressFamily != AddressFamily.InterNetwork || !PlaybackServer.IsPrivateAddress(host)
            || !host.Equals(peer) || uri.Port < 1024) return false;
        return Regex.IsMatch(uri.AbsolutePath, subtitle ? @"^/s/[a-zA-Z0-9_-]{1,128}/[0-9]+/sub/[0-9]+$" : @"^/s/[a-zA-Z0-9_-]{1,128}/[0-9]+$");
    }

    private static string ResolvePath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach(var segment in path.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget != null)
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unresolved link");
        }
        return Path.GetFullPath(current);
    }

    internal static bool AllowedSet(string source, string[] subtitles, string library, IPAddress peer) =>
        subtitles.Length <= 16 && Allowed(source, library, peer) && subtitles.All(s => Allowed(s, library, peer, subtitle: true));
}
