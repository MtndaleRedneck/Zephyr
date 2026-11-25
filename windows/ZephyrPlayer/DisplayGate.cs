using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ZephyrPlayer
{
    /// <summary>
    /// Wake-only first press (BingeMote helper 1.7.2 behavior, ported to the Player's own command
    /// paths). While the PC screen is dark the FIRST phone press only wakes the screen; the press's
    /// own action is swallowed so nothing fires blind. Discrete presses (keys, typing, playback
    /// commands, starting a movie) are gated on the dark check alone, so the next press after the
    /// wake works. Touchpad moves, scrolls and zooms have no gesture end in the protocol, so a
    /// swallowed gesture is eaten to its end: 400 ms of quiet hands control back. A mouse button
    /// press that was swallowed also swallows its matching release for the same phone and button
    /// (a lone right-up would open a context menu); every other release always passes.
    /// The state source is injected: production feeds it from the display power notification,
    /// tests feed it directly. Hardware behavior is only proven on a real PC.
    /// </summary>
    internal sealed class DisplayGate
    {
        private readonly Func<bool> _isDarkOrUnknown;
        private readonly Action _wake;
        private readonly Func<DateTime> _now;
        private readonly object _gate = new();
        private DateTime _swallowUntil = DateTime.MinValue;
        private readonly Dictionary<string, HashSet<string>> _swallowedDowns = new();

        internal static readonly TimeSpan GestureQuiet = TimeSpan.FromMilliseconds(400);

        internal DisplayGate(Func<bool> isDarkOrUnknown, Action wake, Func<DateTime>? now = null)
        {
            _isDarkOrUnknown = isDarkOrUnknown;
            _wake = wake;
            _now = now ?? (() => DateTime.UtcNow);
        }

        internal bool DarkOrUnknown => _isDarkOrUnknown();

        /// <summary>One-shot wake request from the phone (fire-and-forget).</summary>
        internal void Wake() { try { _wake(); } catch { } }

        /// <summary>Clicks-free presses: keys, typing, playback commands, starting a movie.
        /// True = consumed (screen woken), do not run the action.</summary>
        internal bool ShouldSwallowDiscrete()
        {
            lock (_gate) return DiscreteLocked();
        }

        private bool DiscreteLocked()
        {
            if (!_isDarkOrUnknown()) return false;
            Wake();
            return true;
        }

        /// <summary>Touchpad moves, scrolls, zooms. True = frame consumed.</summary>
        internal bool ShouldSwallowContinuous()
        {
            lock (_gate)
            {
                var now = _now();
                if (_isDarkOrUnknown())
                {
                    Wake();
                    _swallowUntil = now + GestureQuiet;
                    return true;
                }
                if (now < _swallowUntil)
                {
                    _swallowUntil = now + GestureQuiet;
                    return true;
                }
                return false;
            }
        }

        private static string Key(string? button) => button == "right" ? "right" : "left";

        /// <summary>A mouse button frame from one phone. True = do not inject it.</summary>
        internal bool ShouldSwallowButton(string owner, string? button, bool down)
        {
            var key = Key(button);
            lock (_gate)
            {
                if (down)
                {
                    if (DiscreteLocked())
                    {
                        if (!_swallowedDowns.TryGetValue(owner, out var set)) _swallowedDowns[owner] = set = new HashSet<string>();
                        set.Add(key);
                        return true;
                    }
                    Forget(owner, key); // a press that passes drops older memory so nothing stale eats a later release
                    return false;
                }
                return Forget(owner, key);
            }
        }

        private bool Forget(string owner, string key)
        {
            if (!_swallowedDowns.TryGetValue(owner, out var set) || !set.Remove(key)) return false;
            if (set.Count == 0) _swallowedDowns.Remove(owner);
            return true;
        }

        internal int SwallowedButtonCountForTests { get { lock (_gate) { var n = 0; foreach (var s in _swallowedDowns.Values) n += s.Count; return n; } } }
    }

    /// <summary>
    /// Production display-state source: GUID_CONSOLE_DISPLAY_STATE power notifications on a
    /// message-only window. Only a real notification marks the display On (a wake call never does),
    /// and "no notification yet" counts as dark (fail-safe: an uncertain press wakes, never fires
    /// blind). Windows reports the current state when the registration is made. If the registration
    /// itself fails the gate is disabled instead (Available = false), because a remote that can
    /// never leave "unknown" would swallow every press forever; that is logged by the caller.
    /// </summary>
    internal sealed class DisplayWatch : IDisposable
    {
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private static readonly Guid GuidConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

        internal enum MonitorState { Unknown, Off, On, Dimmed }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);
        [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint esFlags);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PowerBroadcastSetting { public Guid PowerSetting; public uint DataLength; public byte Data; }

        private HwndSource? _source;
        private IntPtr _registration;
        private volatile MonitorState _state = MonitorState.Unknown;

        /// <summary>False when the notification could not be registered (gate disabled).</summary>
        internal bool Available { get; private set; }

        internal MonitorState State => _state;
        internal bool DarkOrUnknown => _state != MonitorState.On;

        /// <summary>Raw GUID_CONSOLE_DISPLAY_STATE value to state: 0 off, 1 on, 2 dimmed.</summary>
        internal static MonitorState Map(uint raw) => raw switch { 0 => MonitorState.Off, 1 => MonitorState.On, 2 => MonitorState.Dimmed, _ => MonitorState.Unknown };

        internal void NoteRaw(uint raw) => _state = Map(raw);

        /// <summary>Must be called on the UI thread (creates the message-only window).</summary>
        internal void Start()
        {
            try
            {
                var p = new HwndSourceParameters("ZephyrDisplayWatch") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0 };
                _source = new HwndSource(p);
                _source.AddHook(Hook);
                var g = GuidConsoleDisplayState;
                _registration = RegisterPowerSettingNotification(_source.Handle, ref g, DEVICE_NOTIFY_WINDOW_HANDLE);
                Available = _registration != IntPtr.Zero;
            }
            catch { Available = false; }
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
            {
                try
                {
                    var s = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                    if (s.PowerSetting == GuidConsoleDisplayState && s.DataLength >= 4)
                        NoteRaw((uint)Marshal.ReadInt32(lParam, Marshal.OffsetOf<PowerBroadcastSetting>(nameof(PowerBroadcastSetting.Data)).ToInt32()));
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        /// <summary>One-shot: brings the screen back if its idle timer shut it off. Without
        /// ES_CONTINUOUS it resets the idle timers only; it never marks the display On.</summary>
        internal static void WakeDisplay()
        {
            try { SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED); } catch { }
        }

        /// <summary>Build the production gate, or null when the notification is unavailable.</summary>
        internal DisplayGate? CreateGate() => Available ? new DisplayGate(() => DarkOrUnknown, WakeDisplay) : null;

        public void Dispose()
        {
            try { if (_registration != IntPtr.Zero) UnregisterPowerSettingNotification(_registration); } catch { }
            try { _source?.RemoveHook(Hook); _source?.Dispose(); } catch { }
            _registration = IntPtr.Zero;
        }
    }
}
