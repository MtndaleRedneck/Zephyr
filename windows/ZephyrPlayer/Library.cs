// Library: the paired phone browses the assigned movies folder and picks it through
// folder-only browsing - the same contract as the BingeMote helper's Movies.cs
// (jailed paths, natural sort, video-extension filter, local drives only in browse).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZephyrPlayer
{
    internal static class Library
    {
        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
            { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".mpg", ".mpeg", ".webm", ".flv", ".m2ts" };

        // File Explorer's ordering: "Episode 2" before "Episode 10".
        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);
        private static readonly Comparer<string> Natural = Comparer<string>.Create((a, b) =>
            // Headless CI host runs on Linux; the P/Invoke is Windows-only.
            OperatingSystem.IsWindows() ? StrCmpLogicalW(a, b) : string.CompareOrdinal(a, b));

        // "$RECYCLE.BIN" and "System Volume Information" are not movies folders.
        private static bool VisibleDir(string d) =>
            (new DirectoryInfo(d).Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

        /// <summary>Resolve a folder-relative path, refusing anything outside the library folder.</summary>
        private static string? Jail(string root, string rel)
        {
            if (root.Length == 0) return null;
            try
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(Path.Combine(fullRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return null;
                var resolvedRoot = PlaybackSourcePolicy.ResolvePath(Path.GetFullPath(root)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var resolved = PlaybackSourcePolicy.ResolvePath(full);
                if ((!resolved.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(resolved.TrimEnd(Path.DirectorySeparatorChar), resolvedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) || resolved.StartsWith(@"\\")) return null;
                return full;
            }
            catch { return null; }
        }

        /// <summary>List one level of the library folder (folders first, then video files).</summary>
        public static (int status, string json) List(string root, string rel)
        {
            if (root.Length == 0) return Error(400, "no library folder", "Pick a movies folder first: Settings > Assigned Movies folder on the phone.");
            var dir = Jail(root, rel);
            if (dir == null || !Directory.Exists(dir)) return Error(404, "not found", "That folder does not exist on the PC.");
            try
            {
                var entries = new List<object>();
                foreach (var d in Directory.GetDirectories(dir).Where(VisibleDir).OrderBy(d => Path.GetFileName(d), Natural))
                    entries.Add(new { name = Path.GetFileName(d), dir = true });
                foreach (var f in Directory.GetFiles(dir).OrderBy(f => Path.GetFileName(f), Natural))
                    if (VideoExt.Contains(Path.GetExtension(f)))
                        entries.Add(new { name = Path.GetFileName(f), dir = false });
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var atRoot = string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    fullRoot, StringComparison.OrdinalIgnoreCase);
                return (200, JsonSerializer.Serialize(new { path = rel, atRoot, entries }));
            }
            catch (UnauthorizedAccessException) { return Error(404, "not found", "Windows won't let Zephyr look in that folder."); }
            catch (Exception) { return Error(404, "not found", "That folder does not exist on the PC."); }
        }

        /// <summary>List subfolders of any folder (drives when path is empty) so the phone can
        /// pick the library folder. Folder names only, never files, read-only.</summary>
        public static (int status, string json) Browse(string path)
        {
            try
            {
                if (path.Length == 0)
                {
                    var drives = new List<object>();
                    foreach (var d in DriveInfo.GetDrives())
                        if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                            drives.Add(new { name = d.Name, dir = true });
                    return (200, JsonSerializer.Serialize(new { path = "", entries = drives }));
                }
                // A bare drive letter ("C:") is drive-relative on Windows, not the root; mean the root.
                if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':') path += "\\";
                // Local drives only (fixed or a removable USB stick), drive-rooted ("C:\..."):
                // no UNC shares (a token holder must not reach the network) and no relative paths.
                string full;
                if (OperatingSystem.IsWindows())
                {
                    if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
                        return Error(404, "not found", "That folder does not exist on the PC.");
                    var letter = char.ToUpperInvariant(path[0]);
                    var localDrive = false;
                    foreach (var d in DriveInfo.GetDrives())
                        if ((d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)
                            && char.ToUpperInvariant(d.Name[0]) == letter) { localDrive = true; break; }
                    if (!localDrive) return Error(404, "not found", "That folder does not exist on the PC.");
                    full = Path.GetFullPath(path);
                }
                else
                {
                    // Headless/Linux CI host: absolute local paths only - same
                    // intent as the drive check (no relative paths, no roaming).
                    if (!path.StartsWith('/')) return Error(404, "not found", "That folder does not exist on the PC.");
                    full = Path.GetFullPath(path);
                }
                var resolved = PlaybackSourcePolicy.ResolvePath(full);
                if (resolved.StartsWith(@"\\")) return Error(404, "not found", "Network folders are not supported.");
                if (OperatingSystem.IsWindows()) { var drive = new DriveInfo(Path.GetPathRoot(resolved)!); if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) return Error(404, "not found", "Local folders only."); }
                if (!Directory.Exists(full)) return Error(404, "not found", "That folder does not exist on the PC.");
                var entries = new List<object>();
                foreach (var d in Directory.GetDirectories(full).Where(VisibleDir).OrderBy(d => Path.GetFileName(d), Natural))
                {
                    var name = Path.GetFileName(d);
                    entries.Add(new { name = name.Length == 0 ? d : name, dir = true });
                }
                return (200, JsonSerializer.Serialize(new { path = full, entries }));
            }
            catch (UnauthorizedAccessException) { return Error(404, "not found", "Windows won't let Zephyr look in that folder."); }
            catch (Exception) { return Error(404, "not found", "That folder does not exist on the PC."); }
        }

        private static (int, string) Error(int code, string error, string detail) =>
            (code, JsonSerializer.Serialize(new { code, error, detail }));
    }
}
