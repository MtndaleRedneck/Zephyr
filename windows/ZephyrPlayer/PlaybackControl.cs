// Playback control seam (protocol.md Control): the paired phone drives play /
// pause / seek / volume / captions / fullscreen over HTTP. Production dispatches
// to the real libVLC MediaPlayer on the UI thread; tests inject a recorder or
// drive a real headless libVLC (media-open/decode/control integration - video
// output itself stays unverified on CI).
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using LibVLCSharp.Shared;

namespace ZephyrPlayer
{
    /// <summary>Production control: every player touch on the window's dispatcher.</summary>
    public sealed class MainWindowPlaybackControl : IPlaybackControl
    {
        private readonly MainWindow _window;
        private readonly MediaPlayer _player;

        public MainWindowPlaybackControl(MainWindow window, MediaPlayer player)
        {
            _window = window;
            _player = player;
        }

        private void OnUi(Action a) => _window.Dispatcher.Invoke(a);
        private T OnUi<T>(Func<T> f) => _window.Dispatcher.Invoke(f);

        public void Play(string url, long? positionSeconds, string[] subtitles) =>
            OnUi(() => _window.PlayFromUrl(url, positionSeconds, subtitles));
        public void Pause() => OnUi(() => { _player.SetPause(true); _window.ShowBarOnce(); });
        public void Resume() => OnUi(() => { _player.SetPause(false); _window.ShowBarOnce(); });
        public void Seek(long positionSeconds) => OnUi(() => { if (_player.Length > 0) _player.Time = positionSeconds * 1000; });
        public void SetVolume(int volume) => OnUi(() => _window.SetVolumeTied(volume));
        public void Mute(bool mute) => OnUi(() => _window.SetMuteTied(mute));
        public void SetSpu(int trackId) => OnUi(() => _player.SetSpu(trackId));
        public void SetFullscreen(bool on) => OnUi(() => _window.SetFullscreen(on));

        public string StatusJson() => OnUi(() => JsonSerializer.Serialize(new
        {
            state = _player.State.ToString(),
            time = _player.Time / 1000,
            length = _player.Length / 1000,
            volume = _player.Volume,
            muted = _player.Mute,
            spu = _player.Spu,
            // The phone's subtitle sheet needs the track LIST, not just the
            // current id (teardown C2): id + name per track.
            spuTracks = (_player.SpuDescription ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>())
                .Select(t => new { id = t.Id, name = t.Name }).ToArray(),
            fullscreen = _window.IsFullscreen,
            update_available = UpdateState.AvailableVersion,
            update_check = UpdateState.Outcome,
        }));
    }
}
