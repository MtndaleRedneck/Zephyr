using System;
using System.Windows;

namespace ZephyrPlayer
{
    /// <summary>Midnight Slate reflow: the bar is one fixed-width pill, so the only squeeze is a
    /// narrow window. Two levels, and no control ever hides: 0 = full volume group and spacing,
    /// 1 = compact (shorter volume track, no % label, tighter play margins) below the compact
    /// threshold. Speed/sleep state readouts are owned by RefreshSpeedUi/RefreshSleepUi.</summary>
    public partial class MainWindow
    {
        private int _reflowLevel;
        private bool _reflowing;
        internal int ReflowLevelNow => _reflowLevel;

        private void OnBarSizeChanged(object sender, SizeChangedEventArgs e) => Reflow();

        private void ApplyReflowLevel(int level)
        {
            var compact = level >= 1;
            VolumeLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            VolumeSlider.Width = compact ? 56 : 110;
            PlayPauseButton.Margin = new Thickness(compact ? 4 : 10, 0, compact ? 4 : 10, 0);
        }

        internal void Reflow()
        {
            if (_reflowing || ControlBar == null || ControlBar.ActualWidth <= 0) return;
            _reflowing = true;
            try
            {
                var inner = ControlBar.ActualWidth - ControlBar.Padding.Left - ControlBar.Padding.Right - 2;
                var chosen = PlayerBarLogic.ReflowLevel(inner);
                ApplyReflowLevel(chosen);
                _reflowLevel = chosen;
            }
            finally { _reflowing = false; }
        }
    }
}
