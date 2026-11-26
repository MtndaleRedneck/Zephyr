using System;
using System.IO;
using System.Linq;
using LibVLCSharp.Shared;

namespace ZephyrPlayer.Tests;

/// <summary>
/// One serialized entry to the native libvlc load for every test. Core.Initialize is not safe to call from
/// several xUnit collections at the same moment, and a failed load used to say only "Failed to load required
/// native libraries". On failure this adds what is actually on disk, so a missing-file cause and a load race
/// can be told apart from the log.
/// </summary>
internal static class NativeVlc
{
    private static readonly object Gate = new();
    private static bool _ready;

    public static void Init()
    {
        lock (Gate)
        {
            if (_ready) return;
            try
            {
                Core.Initialize();
                _ready = true;
            }
            catch (Exception e)
            {
                // Best effort: this describes the failure, so it must never replace it with a second exception.
                var dir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
                string listing = "(unavailable)", size = "unknown";
                try
                {
                    listing = Directory.Exists(dir)
                        ? string.Join(", ", Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Take(40))
                        : "(directory missing)";
                    var dll = Path.Combine(dir, "libvlc.dll");
                    size = File.Exists(dll) ? new FileInfo(dll).Length + " bytes" : "missing";
                }
                catch { }
                throw new InvalidOperationException(
                    $"libvlc load failed. dir={dir}  libvlc.dll={size} entries=[{listing}] x64process={Environment.Is64BitProcess}", e);
            }
        }
    }
}
