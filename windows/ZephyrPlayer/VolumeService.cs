// Windows master-volume tie. One shared
// 0-100 number: the bar slider is Windows' master volume; the player adds the
// Settings loudness boost on top through libVLC gain. The player's remembered
// volume is restored on open. External Windows volume changes are picked up by
// polling (500 ms clock tick) so both places keep showing the same number.
// Fails soft where no audio endpoint exists (headless CI): Available=false and
// every call is a no-op.
using System;
using System.Runtime.InteropServices;

namespace ZephyrPlayer
{
    public sealed class VolumeService : IDisposable
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        }

        // Partial vtable: declared in order, only these are called.
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out int channels);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(int channel, float levelDb, ref Guid eventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid eventContext);
            [PreserveSig] int GetChannelVolumeLevel(int channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
            [PreserveSig] int SetMute(int mute, ref Guid eventContext);
            [PreserveSig] int GetMute(out int mute);
        }

        // No cached endpoint: the DEFAULT output device is resolved again for every
        // call, so plugging headphones or switching Windows' output is followed at
        // once instead of writes landing on the stale device from app start.
        private int _disposed;
        private DateTime _lastActivationFailLog = DateTime.MinValue;

        // Read-only ABI probe for ordinary Windows CI. No volume setting is changed.
        internal static (int enumerateHr, bool collection, int defaultHr, bool device) ProbeComSlots()
        {
            IMMDeviceEnumerator? enumerator = null; IMMDevice? device = null; IntPtr collection = IntPtr.Zero;
            try {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                var enumHr = enumerator.EnumAudioEndpoints(0, 15, out collection);
                var defaultHr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                return (enumHr, collection != IntPtr.Zero, defaultHr, device != null);
            } finally { if (collection != IntPtr.Zero) Marshal.Release(collection); if (device != null) Marshal.ReleaseComObject(device); if (enumerator != null) Marshal.ReleaseComObject(enumerator); }
        }

        public VolumeService()
        {
            // Kept parameterless for callers and tests; resolution is per call.
        }

        /// <summary>Activate the CURRENT default render endpoint's volume control,
        /// or null when the machine has no usable audio endpoint (headless CI).</summary>
        private IAudioEndpointVolume? ActivateDefault()
        {
            if (_disposed != 0) return null;
            IMMDeviceEnumerator? enumerator = null; IMMDevice? device = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                var hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device);
                Marshal.ThrowExceptionForHR(hr);
                if (device == null) return null;
                var iid = typeof(IAudioEndpointVolume).GUID;
                hr = device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
                Marshal.ThrowExceptionForHR(hr);
                return (IAudioEndpointVolume)obj;
            }
            catch (Exception ex)
            {
                // Fail soft (headless CI has no endpoint), but keep the failure
                // observable: exception type + HRESULT only, throttled so the
                // per-call activation path cannot spam the log. No secrets.
                var now = DateTime.UtcNow;
                if (now - _lastActivationFailLog >= TimeSpan.FromSeconds(30))
                {
                    _lastActivationFailLog = now;
                    System.Diagnostics.Trace.TraceWarning("Windows audio endpoint activation failed: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                }
                return null;
            }
            finally { if (device != null) Marshal.ReleaseComObject(device); if (enumerator != null) Marshal.ReleaseComObject(enumerator); }
        }

        /// <summary>False when the machine has no usable audio endpoint right now.</summary>
        public bool Available
        {
            get
            {
                var ep = ActivateDefault();
                if (ep == null) return false;
                Marshal.ReleaseComObject(ep);
                return true;
            }
        }

        /// <summary>Windows master volume, 0-100; -1 when unavailable.</summary>
        public int GetMaster()
        {
            var ep = ActivateDefault();
            if (ep == null) return -1;
            try
            {
                return ep.GetMasterVolumeLevelScalar(out var level) == 0 ? (int)Math.Round(level * 100) : -1;
            }
            catch { return -1; }
            finally { Marshal.ReleaseComObject(ep); }
        }

        /// <summary>Maps one endpoint mute read to a tri-state: null = the read
        /// FAILED (unknown), never "unmuted". Callers must keep their previous
        /// state on null. Pinned by VolumeMuteTests.</summary>
        internal static bool? MuteFromRead(int hr, int mute) => hr == 0 ? (bool?)(mute != 0) : null;

        /// <summary>Test seam (InternalsVisibleTo): forces TryGetMute's outcome
        /// without a real endpoint so call-site behavior on a failed read is
        /// testable headless. None = normal COM path.</summary>
        internal enum ForcedMute { None, True, False, Unknown }
        internal ForcedMute ForcedMuteOverride = ForcedMute.None;

        /// <summary>Windows master mute, or null when the state is UNKNOWN (no
        /// endpoint, failed HRESULT, or exception). A failed read must never be
        /// read as "unmuted".</summary>
        public bool? TryGetMute()
        {
            if (ForcedMuteOverride == ForcedMute.True) return true;
            if (ForcedMuteOverride == ForcedMute.False) return false;
            if (ForcedMuteOverride == ForcedMute.Unknown) return null;
            var ep = ActivateDefault();
            if (ep == null) return null;
            try { return ep.GetMute(out var m) == 0 ? MuteFromRead(0, m) : null; }
            catch { return null; }
            finally { Marshal.ReleaseComObject(ep); }
        }

        /// <summary>Legacy convenience: false when unknown. New callers that
        /// carry their own state must use TryGetMute so a failed read does not
        /// silently unmute.</summary>
        public bool GetMute() => TryGetMute() ?? false;

        public void SetMaster(int volume)
        {
            var ep = ActivateDefault();
            if (ep == null) return;
            try
            {
                var context = Guid.Empty;
                // A volume write must never silently unmute. Some endpoint paths
                // drop mute when the level changes; preserve the user's mute.
                var wasMuted = ep.GetMute(out var before) == 0 && before != 0;
                Marshal.ThrowExceptionForHR(ep.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0, 100) / 100f, ref context));
                if (wasMuted && ep.GetMute(out var after) == 0 && after == 0)
                    ep.SetMute(1, ref context);
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Windows volume write failed: 0x" + ex.HResult.ToString("X8")); }
            finally { Marshal.ReleaseComObject(ep); }
        }

        public void SetMute(bool mute)
        {
            var ep = ActivateDefault();
            if (ep == null) return;
            try { var context = Guid.Empty; Marshal.ThrowExceptionForHR(ep.SetMute(mute ? 1 : 0, ref context)); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Windows volume write failed: 0x" + ex.HResult.ToString("X8")); }
            finally { Marshal.ReleaseComObject(ep); }
        }

        public void Dispose()
        {
            _disposed = 1; // nothing cached to release; per-call activation owns its COM objects
        }
    }
}
