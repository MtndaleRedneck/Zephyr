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
            // Dark menu to match the app: the stock WinForms strip renders light even in
            // Windows 11 dark mode (static-review finding, verified in source).
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
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

        /// <summary>Dark chrome for the tray strip (background, border, item states).</summary>
        private sealed class DarkMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColors()) { }
            protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = System.Drawing.ColorTranslator.FromHtml("#F5F5F7");
                base.OnRenderItemText(e);
            }
        }

        private sealed class DarkColors : System.Windows.Forms.ProfessionalColorTable
        {
            private static System.Drawing.Color Bg => System.Drawing.ColorTranslator.FromHtml("#26262C");
            private static System.Drawing.Color Border => System.Drawing.ColorTranslator.FromHtml("#3A3A42");
            private static System.Drawing.Color Hover => System.Drawing.ColorTranslator.FromHtml("#3A3A42");
            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color MenuBorder => Border;
            public override System.Drawing.Color MenuItemBorder => System.Drawing.ColorTranslator.FromHtml("#FF9500");
            public override System.Drawing.Color MenuItemSelected => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
            public override System.Drawing.Color MenuItemPressedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemPressedGradientEnd => Hover;
            public override System.Drawing.Color ImageMarginGradientBegin => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd => Bg;
            public override System.Drawing.Color SeparatorDark => Border;
            public override System.Drawing.Color SeparatorLight => Border;
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
