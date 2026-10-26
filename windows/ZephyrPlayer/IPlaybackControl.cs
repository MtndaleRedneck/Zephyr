namespace ZephyrPlayer
{
    public interface IPlaybackControl
    {
        void Play(string url, long? positionSeconds, string[] subtitles);
        void Pause();
        void Resume();
        void Seek(long positionSeconds);
        void SetVolume(int volume); // 0-100
        void Mute(bool mute);
        void SetSpu(int trackId); // -1 = off
        void SetFullscreen(bool on);
        string StatusJson();
    }

}
