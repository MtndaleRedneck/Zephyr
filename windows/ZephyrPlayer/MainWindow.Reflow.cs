using System;
using System.Windows;

namespace ZephyrPlayer
{
    /// <summary>2.0.2 narrow-width reflow, measured against the 3.0 bar: the volume group and the speed/sleep badges give
    /// way in steps (no % label, a shorter track, speaker only, no badges) so the side groups never run into the centre
    /// cluster and Play stays on the centre line. The bar geometry itself is the accepted 3.0 one.</summary>
    public partial class MainWindow
    {
        private int _reflowLevel;
        private bool _reflowing;
        internal int ReflowLevelNow => _reflowLevel;

        private void OnBarSizeChanged(object sender, SizeChangedEventArgs e) => Reflow();

        private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

        private void ApplyReflowLevel(int level)
        {
            VolumeLabel.Visibility = level >= 1 ? Visibility.Collapsed : Visibility.Visible;
            VolumeSlider.Width = level >= 2 ? 56 : 110;
            VolumeSlider.Visibility = level >= 3 ? Visibility.Collapsed : Visibility.Visible;
            var speedOn = !PlayerBarLogic.IsNormalRate(_rate) && level < 4;
            var sleepOn = _sleep.IsActive && level < 4;
            SpeedLabel.Visibility = speedOn ? Visibility.Visible : Visibility.Collapsed;
            SleepLabel.Visibility = sleepOn ? Visibility.Visible : Visibility.Collapsed;
        }

        internal void Reflow()
        {
            if (_reflowing || ControlBar == null || ControlBar.ActualWidth <= 0) return;
            _reflowing = true;
            try
            {
                var inner = ControlBar.ActualWidth - ControlBar.Padding.Left - ControlBar.Padding.Right - 2;
                CenterPlaybackControls.Measure(Unbounded);
                var center = CenterPlaybackControls.DesiredSize.Width;
                var side = (inner - center) / 2;
                var chosen = 5;
                for (var level = 0; level <= 4; level++)
                {
                    ApplyReflowLevel(level);
                    LeftGroup.Measure(Unbounded);
                    RightGroup.Measure(Unbounded);
                    if (LeftGroup.DesiredSize.Width <= side + 0.5 && RightGroup.DesiredSize.Width <= side + 0.5) { chosen = level; break; }
                }
                if (chosen == 5) ApplyReflowLevel(4);
                _reflowLevel = chosen;
            }
            finally { _reflowing = false; }
        }
    }
}
