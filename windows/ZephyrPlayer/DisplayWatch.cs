using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ZephyrPlayer
{
    /// <summary>
    /// Production display-state source: GUID_CONSOLE_DISPLAY_STATE power notifications on a
    /// message-only window. Only a real notification marks the display On (a wake call never does),
    /// and "no notification yet" counts as NOT dark (fail-open, same as 3.0.0; a remote that never
    /// leaves unknown must not swallow every press). Windows reports the current state when registered. If the registration
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
        /// <summary>Dark means the display is reported Off or Dimmed. No notification yet (Unknown) is NOT dark:
        /// the remote then behaves exactly like shipped 3.0.0 instead of swallowing every press.</summary>
        internal bool DarkOrUnknown => _state == MonitorState.Off || _state == MonitorState.Dimmed;

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
