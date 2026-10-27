using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace ZephyrPlayer
{
    /// <summary>
    /// The notification-area icon and its menu. The menu is a native WPF
    /// ContextMenu - dark, rounded, shadowed, with a rounded hover fill and no
    /// outline - the same modern Fluent language as the rest of the app. (The
    /// previous GDI-drawn strip rendered square corners and a hard orange hover
    /// outline, which was the tray blocker.) Single or double left-click restores
    /// the player; right-click opens the menu; it dismisses when it loses focus.
    /// </summary>
    internal sealed class TrayService : IDisposable
    {
        private readonly TaskbarIcon _icon;

        public event Action? OpenRequested;
        public event Action? QuitRequested;

        public TrayService()
        {
            _icon = new TaskbarIcon
            {
                IconSource = new BitmapImage(new Uri("pack://application:,,,/assets/icon.ico")),
                ToolTipText = "Zephyr Player",
                ContextMenu = BuildMenu(),
            };
            _icon.TrayLeftMouseDown += (_, __) => OpenRequested?.Invoke();
            _icon.TrayMouseDoubleClick += (_, __) => OpenRequested?.Invoke();
            // Built in code, so the icon is never in a visual tree and its Loaded
            // handler never fires - without ForceCreate the tray icon would never
            // exist at all (round-3 P1). enablesEfficiencyMode stays false: the
            // library default (true) would put the whole player into Windows 11
            // Efficiency Mode, which is wrong for video playback.
            try { _icon.ForceCreate(false); }
            catch
            {
                // The shell is not ready (e.g. before Explorer starts at sign-in):
                // keep the TaskbarIcon alive - its TaskbarCreated handler adds the
                // icon once Explorer is up.
            }
#if CI_CAPTURE_HOOK
            // Round-4 NEW-C2: when CI asks (ZEPHYR_TRAY_DIAG=<path>), record
            // whether the icon REALLY exists in the notification area - a corner
            // pixel crop cannot tell a hidden-overflow icon from a missing one.
            // Compiled out of shipped builds like the other hooks (round-12): an env
            // pointing anywhere must not make the shipped exe write files.
            try
            {
                var diag = Environment.GetEnvironmentVariable("ZEPHYR_TRAY_DIAG");
                if (!string.IsNullOrEmpty(diag))
                    System.IO.File.WriteAllText(diag, "IsCreated=" + _icon.IsCreated);
            }
            catch { }
#endif
        }

        private ContextMenu BuildMenu()
        {
            var open = new MenuItem { Header = "Open Zephyr Player", FontWeight = FontWeights.SemiBold, Style = ItemStyle() };
            open.Click += (_, __) => OpenRequested?.Invoke();
            var quit = new MenuItem { Header = "Quit Zephyr Player", Style = ItemStyle() };
            quit.Click += (_, __) => QuitRequested?.Invoke();
            var menu = new ContextMenu { Style = MenuStyle() };
            menu.Items.Add(open);
            menu.Items.Add(new Separator { Style = SepStyle() });
            menu.Items.Add(quit);
            return menu;
        }

        /// <summary>Rounded, shadowed dark chrome for the menu itself.</summary>
        private static Style MenuStyle()
        {
            var style = new Style(typeof(ContextMenu));
            var template = new ControlTemplate(typeof(ContextMenu));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)));
            border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new Thickness(4));
            // Round-3 P9: room for the shadow inside the popup so it is not
            // clipped flush at the edge.
            border.SetValue(Border.MarginProperty, new Thickness(12));
            border.SetValue(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect
                { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black });
            border.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Seal();
            return style;
        }

        /// <summary>Items: Segoe UI Variable on near-white, rounded hover fill, no outline.</summary>
        private static Style ItemStyle()
        {
            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA))));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Segoe UI")));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
            var template = new ControlTemplate(typeof(MenuItem));
            var bg = new FrameworkElementFactory(typeof(Border));
            bg.Name = "Bg";
            bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bg.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bg.AppendChild(content);
            template.VisualTree = bg;
            var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x44)), "Bg"));
            template.Triggers.Add(hover);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73))));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Seal();
            return style;
        }

        private static Style SepStyle()
        {
            var style = new Style(typeof(Separator));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 1.0));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46))));
            style.Seal();
            return style;
        }

        /// <summary>CI-only: opens the real menu at the capture script's fixed screen
        /// point (it grabs 40,40 + 360x170); the popup is topmost so it draws over any
        /// window. Never called in normal use.</summary>
        public void ShowMenuForCapture()
        {
            var menu = _icon.ContextMenu;
            if (menu == null) return;
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.PlacementRectangle = new Rect(40, 40, 0, 0);
            menu.IsOpen = true;
        }

        public void Dispose() => _icon.Dispose();
    }
}
