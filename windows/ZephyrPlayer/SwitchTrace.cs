using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ZephyrPlayer
{
    /// <summary>Ordering trace for the player -> library switch (CP2 round 5, Instinct's
    /// review of run 41095411723: one all-black client frame at 438 ms of Back). Off by
    /// default and free when off (one null check per mark). When on it records, with a
    /// QueryPerformanceCounter time stamp (the same clock an outside screen sampler on the
    /// machine reads), each step the shell takes on Back and, from a 1 ms watcher thread,
    /// every change of the native windows involved: the video host's visibility and
    /// parent, the libVLC video window(s) inside it, and LibVLCSharp's overlay window.
    /// Tests turn it on in-process (Begin/Snapshot) to assert the order; the CI capture
    /// build can also write it to a file (StartFromEnvironment, compiled only into the
    /// hooked build).</summary>
    internal static class SwitchTrace
    {
        internal readonly record struct Entry(long Qpc, string Event, string Detail, int Thread)
        {
            public override string ToString() =>
                $"{Qpc} {Qpc * 1000.0 / Stopwatch.Frequency:0.000}ms t{Thread} {Event}{(Detail.Length > 0 ? " " + Detail : "")}";
        }

        private static readonly object Gate = new();
        private static List<Entry>? _entries;
        private static Thread? _watcher;
        private static volatile bool _watch;
        private static IntPtr _shell;

        internal static bool On => _entries != null;

        /// <summary>Turns the trace on (clearing any earlier entries).</summary>
        internal static void Begin()
        {
            lock (Gate) _entries = new List<Entry>();
            StartWatcher();
        }

        /// <summary>Turns the trace off and returns what it holds.</summary>
        internal static List<Entry> End()
        {
            _watch = false;
            _watcher?.Join(500);
            _watcher = null;
            lock (Gate) { var e = _entries ?? new List<Entry>(); _entries = null; return e; }
        }

        internal static List<Entry> Snapshot()
        {
            lock (Gate) return _entries == null ? new List<Entry>() : new List<Entry>(_entries);
        }

        internal static void Mark(string ev, string detail = "")
        {
            if (_entries == null) return;
            var e = new Entry(Stopwatch.GetTimestamp(), ev, detail, Environment.CurrentManagedThreadId);
            lock (Gate) _entries?.Add(e);
        }

        /// <summary>The shell whose native children the watcher follows.</summary>
        internal static void WatchShell(IntPtr shell)
        {
            _shell = shell;
            if (On) StartWatcher();
        }

        // ---------- the native watcher (only while the trace is on) ----------

        private static void StartWatcher()
        {
            if (_watcher != null || _shell == IntPtr.Zero) return;
            _watch = true;
            _watcher = new Thread(WatchLoop) { IsBackground = true, Name = "switch-trace watcher" };
            _watcher.Start();
        }

        private static void WatchLoop()
        {
            string last = "";
            IntPtr host = IntPtr.Zero;
            timeBeginPeriod(1);
            try
            {
                while (_watch && _entries != null)
                {
                    try
                    {
                        var found = FindHost(_shell);
                        if (found != IntPtr.Zero) host = found;
                        var state = Describe(_shell, host);
                        if (state != last) { Mark("native", state); last = state; }
                    }
                    catch (Exception ex) { Mark("native.error", ex.GetType().Name); }
                    Thread.Sleep(1);
                }
            }
            finally { timeEndPeriod(1); }
        }

        /// <summary>The video host = the shell's direct "Static" child (LibVLCSharp's
        /// VideoView host); once seen it is followed even if reparented.</summary>
        internal static IntPtr FindHost(IntPtr shell)
        {
            var host = IntPtr.Zero;
            if (shell == IntPtr.Zero) return host;
            EnumChildWindows(shell, (h, _) =>
            {
                if (ClassOf(h).Equals("Static", StringComparison.OrdinalIgnoreCase) && GetParent(h) == shell) { host = h; return false; }
                return true;
            }, IntPtr.Zero);
            return host;
        }

        /// <summary>Count of visible windows inside the host (libVLC's video output).</summary>
        internal static int VisibleVideoChildren(IntPtr host)
        {
            if (host == IntPtr.Zero || !IsWindow(host)) return 0;
            int n = 0;
            EnumChildWindows(host, (h, _) => { if (IsWindowVisible(h)) n++; return true; }, IntPtr.Zero);
            return n;
        }

        private static string Describe(IntPtr shell, IntPtr host)
        {
            var sb = new StringBuilder();
            if (host == IntPtr.Zero) sb.Append("host=none");
            else if (!IsWindow(host)) sb.Append("host=destroyed");
            else
            {
                sb.Append("host=").Append(IsWindowVisible(host) ? "visible" : "HIDDEN");
                sb.Append(GetParent(host) == shell ? " parent=shell" : " parent=other");
                int kids = 0, vis = 0; var classes = new List<string>();
                EnumChildWindows(host, (h, _) => { kids++; if (IsWindowVisible(h)) vis++; if (classes.Count < 3) classes.Add(ClassOf(h)); return true; }, IntPtr.Zero);
                sb.Append(" video-windows=").Append(kids).Append(" visible=").Append(vis);
                if (classes.Count > 0) sb.Append(" [").Append(string.Join(",", classes)).Append(']');
            }
            sb.Append(" overlay=").Append(OverlayVisible() ? "visible" : "hidden");
            return sb.ToString();
        }

        private static bool OverlayVisible()
        {
            uint me = (uint)Environment.ProcessId; bool vis = false;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                var t = new StringBuilder(64); GetWindowTextW(h, t, 64);
                if (t.ToString() == "LibVLCSharp.WPF") { vis = true; return false; }
                return true;
            }, IntPtr.Zero);
            return vis;
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64); GetClassNameW(h, sb, 64); return sb.ToString();
        }

#if CI_CAPTURE_HOOK
        /// <summary>CI capture build only: ZEPHYR_SWITCH_TRACE names a file; the trace is
        /// on for the process's life and flushed to that file about every 250 ms.</summary>
        internal static void StartFromEnvironment()
        {
            var path = Environment.GetEnvironmentVariable("ZEPHYR_SWITCH_TRACE");
            if (string.IsNullOrEmpty(path)) return;
            Begin();
            Mark("trace.start", $"qpc-frequency={Stopwatch.Frequency}");
            var t = new Thread(() =>
            {
                int written = 0;
                while (true)
                {
                    Thread.Sleep(250);
                    try
                    {
                        var all = Snapshot();
                        if (all.Count > written)
                        {
                            var sb = new StringBuilder();
                            for (int i = written; i < all.Count; i++) sb.Append(all[i].ToString()).Append('\n');
                            System.IO.File.AppendAllText(path, sb.ToString());
                            written = all.Count;
                        }
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "switch-trace writer" };
            t.Start();
        }
#endif

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    }
}
