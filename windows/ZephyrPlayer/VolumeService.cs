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

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        }

        // Partial vtable: declared in order, only these are called.
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr notify);
            int UnregisterControlChangeNotify(IntPtr notify);
            int GetChannelCount(out int channels);
            int SetMasterVolumeLevel(float levelDb, Guid eventContext);
            int SetMasterVolumeLevelScalar(float level, Guid eventContext);
            int GetMasterVolumeLevel(out float levelDb);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(int channel, float levelDb, Guid eventContext);
            int SetChannelVolumeLevelScalar(int channel, float level, Guid eventContext);
            int GetChannelVolumeLevel(int channel, out float levelDb);
            int GetChannelVolumeLevelScalar(int channel, out float level);
            int SetMute(int mute, Guid eventContext);
            int GetMute(out int mute);
        }

        private IAudioEndpointVolume? _endpoint;

        public VolumeService()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                var hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out var device);
                if (hr != 0 || device == null) return;
                var iid = typeof(IAudioEndpointVolume).GUID;
                hr = device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
                if (hr == 0) _endpoint = (IAudioEndpointVolume)obj;
            }
            catch { _endpoint = null; } // no audio device (headless CI) - fail soft
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

        public bool GetMute()
        {
            if (_endpoint == null) return false;
            try { return _endpoint.GetMute(out var m) == 0 && m != 0; }
            catch { return false; }
        }

        public void SetMaster(int volume)
        {
            if (_endpoint == null) return;
            try { _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0, 100) / 100f, Guid.Empty); }
            catch { }
        }

        public void SetMute(bool mute)
        {
            if (_endpoint == null) return;
            try { _endpoint.SetMute(mute ? 1 : 0, Guid.Empty); }
            catch { }
        }

        public void Dispose()
        {
            if (_endpoint != null) Marshal.ReleaseComObject(_endpoint);
            _endpoint = null;
        }
    }
}
