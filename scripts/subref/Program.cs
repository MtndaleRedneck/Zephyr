using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LibVLCSharp.Shared;

// CI-only reference harness: SubRef <media> <x> <y> <width> <height> <stateFile> [libVLC options...]
// Shows a borderless window whose client area is exactly width x height at (x, y) and
// plays <media> through libVLC created with ONLY the options given (none = libVLC's
// defaults). Writes its playback state to <stateFile> every 250 ms; the driving script
// grabs the screen and kills it.
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var media = args[0];
        int x = int.Parse(args[1]), y = int.Parse(args[2]), w = int.Parse(args[3]), h = int.Parse(args[4]);
        var state = args[5];
        var opts = new List<string>();
        for (var i = 6; i < args.Length; i++) opts.Add(args[i]);
        Application.EnableVisualStyles();
        Core.Initialize();
        var vlc = new LibVLC(opts.ToArray());
        var mp = new MediaPlayer(vlc) { EnableMouseInput = false, EnableKeyInput = false };
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(x, y),
            ClientSize = new Size(w, h),
            BackColor = Color.Black,
            TopMost = true,
            Text = "SubRef libVLC",
        };
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        form.Controls.Add(panel);
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (s, e) =>
        {
            try
            {
                var o = new Dictionary<string, object>
                {
                    ["time_ms"] = mp.Time,
                    ["state"] = mp.State.ToString(),
                    ["libvlc_version"] = vlc.Version,
                    ["spu"] = mp.Spu,
                    ["client"] = new[] { panel.ClientSize.Width, panel.ClientSize.Height },
                    ["options"] = opts,
                };
                File.WriteAllText(state, System.Text.Json.JsonSerializer.Serialize(o));
            }
            catch { }
        };
        form.Shown += (s, e) =>
        {
            mp.Hwnd = panel.Handle;
            var m = new Media(vlc, new Uri(media));
            mp.Play(m);
            timer.Start();
        };
        Application.Run(form);
        return 0;
    }
}
