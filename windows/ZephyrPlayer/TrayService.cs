using System;

namespace ZephyrPlayer
{
    /// <summary>
    /// System-tray presence (settled behavior: closing the window sends the
    /// player to the tray; reopening from the desktop icon, taskbar or tray
    /// always restores it). WinForms NotifyIcon hosted inside the WPF app -
    /// the same component the BingeMote helper uses, driven by the WPF
    /// dispatcher's message loop. The icon is visible for the whole app
    /// lifetime so the running player is always discoverable.
    /// </summary>
    public sealed class TrayService : IDisposable
    {
        private readonly System.Windows.Forms.NotifyIcon _icon;

        public event Action? OpenRequested;
        public event Action? QuitRequested;

        public TrayService()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var open = new System.Windows.Forms.ToolStripMenuItem("Open Zephyr Player");
            open.Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
            open.Click += (_, __) => OpenRequested?.Invoke();
            var quit = new System.Windows.Forms.ToolStripMenuItem("Quit Zephyr Player");
            quit.Click += (_, __) => QuitRequested?.Invoke();
            menu.Items.Add(open);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(quit);

            System.Drawing.Icon icon;
            var stream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/assets/icon.ico"))?.Stream;
            icon = stream != null ? new System.Drawing.Icon(stream) : System.Drawing.SystemIcons.Application;

            _icon = new System.Windows.Forms.NotifyIcon
            {
                Text = "Zephyr Player",
                Icon = icon,
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.DoubleClick += (_, __) => OpenRequested?.Invoke();
        }

        /// <summary>One-time "still running here" hint the first time a close hides to the tray.</summary>
        public void ShowStillRunningHint()
        {
            try { _icon.ShowBalloonTip(2500, "Zephyr Player", "Zephyr Player is still running here. Quit from this icon when you're done.", System.Windows.Forms.ToolTipIcon.Info); }
            catch { }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
