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
    /// <summary>Production control (CP4 item 90): every phone command lands on the player
    /// VIEW inside the one shell, on its dispatcher. The phone protocol is unchanged.</summary>
    public sealed class PlayerViewPlaybackControl : IPlaybackControl, IAsyncPlaybackControl
    {
        private readonly PlayerView _view;
        private readonly MediaPlayer _player;
        private readonly Action? _showForPlayback;

        /// <summary>[showForPlayback] puts the shell on the player view - the same
        /// handoff /play does. Without it, /control/play would start playback while
        /// the app keeps showing Home (the video lands in a detached output window):
        /// protocol.md documents /control/play with the same payload as /play, so any
        /// caller that starts a URL expects to SEE it.</summary>
        public PlayerViewPlaybackControl(PlayerView view, MediaPlayer player, Action? showForPlayback = null)
        {
            _view = view;
            _player = player;
            _showForPlayback = showForPlayback;
        }

        private void OnUi(Action a) => _view.Dispatcher.Invoke(a);
        private T OnUi<T>(Func<T> f) => _view.Dispatcher.Invoke(f);

        public void Play(string url, long? positionSeconds, string[] subtitles) =>
            OnUi(() =>
            {
                _showForPlayback?.Invoke();
                _view.PlayFromUrl(url, positionSeconds, subtitles);
            });
        public Task PlayAsync(string url,long? positionSeconds,string[] subtitles) => _view.Dispatcher.InvokeAsync(() => {
            _showForPlayback?.Invoke();return _view.PlayFromUrlAsync(url,positionSeconds,subtitles);
        }).Task.Unwrap();
        public void Pause() => OnUi(() => { _view.PauseFromPhone(true); _view.ShowBarOnce(); });
        public void Resume() => OnUi(() => { _view.PauseFromPhone(false); _view.ShowBarOnce(); });
        public void Seek(long positionSeconds) => OnUi(() => { _view.SeekFromPhone(positionSeconds); _view.ShowBarOnce(); });
        public void SetVolume(int volume) => OnUi(() => _view.SetVolumeTied(volume));
        public void Mute(bool mute) => OnUi(() => _view.SetMuteTied(mute));
        public void SetSpu(int trackId) => OnUi(() => _view.SelectSubtitle(trackId));
        public void SetFullscreen(bool on) => OnUi(() => _view.SetFullscreen(on));

        public string StatusJson() => OnUi(() =>
        {
            var node = JsonSerializer.SerializeToNode(StatusObject())!.AsObject();
#if CI_CAPTURE_HOOK
            // Capture build only: what the bar draws, for the desktop proof script.
            node["ui"] = JsonSerializer.SerializeToNode(_view.UiState());
#endif
            return node.ToJsonString();
        });

        private object StatusObject() => new
        {
            state = _player.State.ToString(),
            time = _player.Time / 1000,
            length = _player.Length / 1000,
            volume = _view.SharedVolume,
            decoder_gain = _player.Volume,
            title = _view.CurrentMovieTitle,
            muted = _view.SharedMute,
            spu = _player.Spu,
            // The phone's subtitle sheet needs the track LIST, not just the
            // current id (teardown C2): id + name per track.
            // libVLC's id -1 "Disable" entry is Off, not a track (P0-3).
            spuTracks = PlayerView.RealTracks(_player.SpuDescription)
                .Select(t => new { id = t.Id, name = t.Name }).ToArray(),
            fullscreen = _view.IsFullscreen,
            update_available = UpdateState.AvailableVersion,
            update_check = UpdateState.Outcome,
            rate = _view.CurrentRate,
        };
    }
}
