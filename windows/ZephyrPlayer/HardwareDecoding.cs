namespace ZephyrPlayer
{
    /// <summary>
    /// The LibVLC start-up options for the Hardware decoding setting. Auto adds nothing (VLC picks the
    /// graphics card when it can); Off passes VLC 3's own switch for turning hardware decoding off.
    /// </summary>
    public static class HardwareDecoding
    {
        public static string[] Options(string setting) =>
            setting == "off" ? new[] { "--avcodec-hw=none" } : System.Array.Empty<string>();
    }
}
