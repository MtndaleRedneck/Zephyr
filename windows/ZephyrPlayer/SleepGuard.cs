using System;
using System.Runtime.InteropServices;

namespace ZephyrPlayer
{
    /// <summary>
    /// Keeps the PC awake (never the screen) while a movie is open, like the VLC helper does. Holds
    /// ES_CONTINUOUS | ES_SYSTEM_REQUIRED only, never ES_DISPLAY_REQUIRED, so the display can still go dark.
    /// Execution state belongs to the calling thread, so every call must come from the UI thread; the window
    /// calls <see cref="Update"/> from its events and a short timer, which also retries after a failed call.
    /// </summary>
    public sealed class SleepGuard
    {
        internal const uint ES_CONTINUOUS = 0x80000000;
        internal const uint ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

        private readonly Func<uint, uint> _set;
        private int _owner = -1;

        public bool Held { get; private set; }

        public SleepGuard(Func<uint, uint>? setState = null) { _set = setState ?? SetThreadExecutionState; }

        /// <summary>Pure rule: awake only when the setting is on, a movie is playing or paused, and the window is not minimized.</summary>
        public static bool ShouldHold(bool settingOn, bool mediaOpen, bool minimized) => settingOn && mediaOpen && !minimized;

        /// <summary>For window close: the timer is stopped there, so a failed release is retried a few times right away.
        /// Windows also drops a thread's execution state when the thread ends, so this is belt and braces.</summary>
        public void ReleaseNow()
        {
            for (var i = 0; i < 3 && Held; i++) Update(false);
        }

        public void Update(bool want)
        {
            var thread = Environment.CurrentManagedThreadId;
            if (_owner != -1 && thread != _owner) return; // never release or take from another thread
            if (want == Held) return;
            try
            {
                var result = _set(want ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
                if (result == 0) return; // call failed: stay as we were so the next Update retries
                Held = want;
                _owner = want ? thread : -1;
            }
            catch { /* best effort; the next Update retries */ }
        }
    }
}
