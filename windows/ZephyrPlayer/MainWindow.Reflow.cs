using System;
using System.Windows;

namespace ZephyrPlayer
{
    /// <summary>Midnight Slate reflow, measured against the real content: level 0 (full volume track and
    /// spacing) is kept only while the three groups actually fit side by side; level 1 (compact: shorter
    /// volume track, tighter play margins) is measured the same way. Below compact fit the row's DownOnly
    /// viewbox scales it uniformly. Every control and readout stays visible at every width. The play
    /// cluster stays exactly centered: the side groups are overlay-aligned to the bar's edges, not
    /// column-bound.</summary>
    public partial class MainWindow
    {
        private int _reflowLevel;
        private bool _reflowing;
        internal int ReflowLevelNow => _reflowLevel;

        private void OnBarSizeChanged(object sender, SizeChangedEventArgs e) => Reflow();

        private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

        private void ApplyReflowLevel(int level)
        {
            VolumeSlider.Width = level >= 1 ? 56 : 92;
            PlayPauseButton.Margin = new Thickness(level >= 1 ? 4 : 10, 0, level >= 1 ? 4 : 10, 0);
        }

        /// <summary>The play cluster is centered, so each side group gets (inner - center)/2. The row
        /// fits when the WIDER side group fits its half: 2*max(left, right) + center &lt;= inner. Summing
        /// all three under-measures and would let the wide side overlap the cluster.</summary>
        private double MeasureRow()
        {
            LeftGroup.Measure(Unbounded);
            CenterPlaybackControls.Measure(Unbounded);
            RightGroup.Measure(Unbounded);
            var side = Math.Max(LeftGroup.DesiredSize.Width, RightGroup.DesiredSize.Width);
            return 2 * side + CenterPlaybackControls.DesiredSize.Width;
        }

        internal void Reflow()
        {
            if (_reflowing || ControlBar == null || ControlBar.ActualWidth <= 0) return;
            _reflowing = true;
            try
            {
                var inner = ControlBar.ActualWidth - ControlBar.Padding.Left - ControlBar.Padding.Right - 2;
                ApplyReflowLevel(0);
                var needed = MeasureRow();
                var level = PlayerBarLogic.ReflowLevel(inner, needed);
                if (level != 0)
                {
                    ApplyReflowLevel(level);
                    needed = MeasureRow();
                }
                _reflowLevel = level;
                // Wider than the content: the groups sit flush and the cluster centers. Narrower
                // window than the content: the viewbox scales the whole row down as one.
                ControlsRow.Width = Math.Max(inner, needed);
            }
            finally { _reflowing = false; }
        }
    }
}
