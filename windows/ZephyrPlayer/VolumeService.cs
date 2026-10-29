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

        private IAudioEndpointVolume? _endpoint;

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
            IMMDeviceEnumerator? enumerator = null; IMMDevice? device = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                var hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device);
                Marshal.ThrowExceptionForHR(hr);
                if (device == null) return;
                var iid = typeof(IAudioEndpointVolume).GUID;
                hr = device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
                Marshal.ThrowExceptionForHR(hr);
                _endpoint = (IAudioEndpointVolume)obj;
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Windows volume initialization failed: " + ex.GetType().Name + " HRESULT=0x" + ex.HResult.ToString("X8")); _endpoint = null; } // no audio device (headless CI) - fail soft
            finally { if (device != null) Marshal.ReleaseComObject(device); if (enumerator != null) Marshal.ReleaseComObject(enumerator); }
        }

        /// <summary>False when the machine has no usable audio endpoint.</summary>
        public bool Available => _endpoint != null;

        /// <summary>Windows master volume, 0-100; -1 when unavailable.</summary>
        public int GetMaster()
        {
            if (_endpoint == null) return -1;
            try
            {
                return _endpoint.GetMasterVolumeLevelScalar(out var level) == 0 ? (int)Math.Round(level * 100) : -1;
            }
            catch { return -1; }
        }

        public bool GetMute() => GetMuteOrNull() ?? false;

        public bool? GetMuteOrNull()
        {
            if (_endpoint == null) return null;
            try { return _endpoint.GetMute(out var m) == 0 ? m != 0 : null; }
            catch { return null; }
        }

        internal static bool MuteAfterPoll(bool current, bool? observed) => observed ?? current;

        public void SetMaster(int volume)
        {
            if (_endpoint == null) return;
            try { var context = Guid.Empty; Marshal.ThrowExceptionForHR(_endpoint.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0, 100) / 100f, ref context)); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Windows volume write failed: 0x" + ex.HResult.ToString("X8")); }
        }

        public void SetMute(bool mute)
        {
            if (_endpoint == null) return;
            try { var context = Guid.Empty; Marshal.ThrowExceptionForHR(_endpoint.SetMute(mute ? 1 : 0, ref context)); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Windows volume write failed: 0x" + ex.HResult.ToString("X8")); }
        }

        public void Dispose()
        {
            if (_endpoint != null) Marshal.ReleaseComObject(_endpoint);
            _endpoint = null;
        }
    }
}
