using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZephyrPlayer
{
    /// <summary>
    /// The pairing surface (owner-approved look): "Pair new phone" with a live
    /// code + countdown that renews before expiry (the pre-1.2 code silently
    /// dies after 10 minutes and Settings clobbers it), "This PC:" address for
    /// manual entry, the paired-phones list with Remove, and a status row for
    /// discovery / public-Wi-Fi findings. One instance, owned by App; the
    /// Settings "Android remote" section opens it too, so every place shows the
    /// SAME live gate - no more per-window codes clobbering each other.
    /// </summary>
    public partial class PairingWindow : Window
    {
        private readonly PairingGate _gate;
        private readonly PairedStore _store;
        private readonly Action<string> _pairStarted;
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Action _connChanged;
        private static readonly SolidColorBrush OkGreen = new(Color.FromRgb(0x4C, 0xAF, 0x50));
        private static readonly SolidColorBrush WarnOrange = new(Brand.Orange);

        /// <param name="pairStarted">Called with the phone name at every code mint;
        /// App attaches it to the token when that code pairs.</param>
        public PairingWindow(PairingGate gate, PairedStore store, Action<string> pairStarted)
        {
            InitializeComponent();
            WindowChrome.ApplyDarkTitleBar(this);
            MaxHeight = SystemParameters.WorkArea.Height;
            _gate = gate;
            _store = store;
            _pairStarted = pairStarted;
            _tick.Tick += (_, __) => TickCountdown();
            Loaded += (_, __) =>
            {
                // Always a fresh code on open (review N2): no blank code with a running
                // countdown, and closing cancels the code so none sits unseen (Closed below).
                Mint();
                ShowCode();
                RefreshPhones();
                RefreshStatus();
                _tick.Start();
            };
            _connChanged = () => Dispatcher.InvokeAsync(RefreshStatus);
            ConnectionState.Changed += _connChanged;
            Closed += (_, __) => { _tick.Stop(); _gate.CancelCode(); ConnectionState.Changed -= _connChanged; };
        }

        private void OnPhoneNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            PhoneNameHint.Visibility = PhoneNameBox.Text.Length == 0
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            _pairStarted(PendingName());
        }

        private string PendingName()
        {
            var n = PhoneNameBox.Text.Trim();
            return n.Length > 0 ? n : "My phone";
        }

        private void Mint()
        {
            var name = PhoneNameBox.Text.Trim();
            _pairStarted(name.Length > 0 ? name : "My phone");
            // The gate hands the code out only at mint time; mirror it for display.
            PairCodeText.Text = string.Join(" ", _gate.NewCode().ToCharArray());
        }

        // Window-side "a code is on screen" flag: the gate's HasActiveCode goes false at
        // expiry, so gating renewal on it meant the code NEVER renewed (review blocker 1).
        private bool _codeShowing;

        private void OnPair(object sender, RoutedEventArgs e)
        {
            // Never quietly swap a code the phone may already be typing (name/Pair flow):
            // mint only when no code is live; the name box edits the pending name live.
            if (!_gate.HasActiveCode) Mint();
            ShowCode();
        }

        private void OnCancelCode(object sender, RoutedEventArgs e)
        {
            _gate.CancelCode();
            _codeShowing = false;
            CodePanel.Visibility = Visibility.Collapsed;
        }

        private void TickCountdown()
        {
            if (!_codeShowing) return; // cancelled / paired: stay in the name + Pair state
            var left = _gate.CodeTimeLeft;
            if (left == null)
            {
                // Spent by a successful pair or cancelled: never mint a hidden code (N4b).
                _codeShowing = false;
                CodePanel.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            if (left.Value <= TimeSpan.FromSeconds(2))
            {
                // Renew just BEFORE the old one dies, so the phone never meets a dead code.
                Mint();
                left = _gate.CodeTimeLeft;
            }
            var t = left ?? TimeSpan.Zero;
            ExpiresText.Text = $"New code in {(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        private void ShowCode()
        {
            _codeShowing = true;
            CodePanel.Visibility = Visibility.Visible;
            var left = _gate.CodeTimeLeft ?? TimeSpan.FromMinutes(10);
            ExpiresText.Text = $"New code in {(int)left.TotalMinutes}:{left.Seconds:00}";
            RefreshStatus();
        }

        /// <summary>Called by App (UI thread) when a phone completes pairing.</summary>
        public void OnPairSucceeded()
        {
            _codeShowing = false;
            CodePanel.Visibility = Visibility.Collapsed;
            RefreshPhones();
            RefreshStatus();
        }

        public void RefreshPhones()
        {
            var entries = _store.Entries();
            PhoneList.ItemsSource = entries;
            NoPhonesText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshStatus()
        {
            var ip = ConnectionState.PrimaryIPv4();
            ThisPcText.Text = ip != null ? "This PC: " + ip : "This PC: address unavailable";
            if (_store.IsBroken)
            {
                // N5 fail-closed surfaced plainly: the pairings file could not be read,
                // nothing was deleted, and new pairing stays off until it is fixed.
                StatusDot.Fill = WarnOrange;
                StatusText.Text = "Your saved pairings could not be read, so pairing is paused. Nothing was deleted - restart the app or repair the pairings file to bring them back.";
            }
            else if (ConnectionState.DiscoveryOff)
            {
                StatusDot.Fill = WarnOrange;
                StatusText.Text = "Find my PC is off: another app is using it. Pair with the code above instead.";
            }
            else if (ConnectionState.PublicWifiName != null)
            {
                StatusDot.Fill = WarnOrange;
                StatusText.Text = "Windows is treating this Wi-Fi as public, which can block your phone from finding this PC.";
            }
            else
            {
                StatusDot.Fill = OkGreen;
                StatusText.Text = "Ready for your phone";
            }
        }

        private void OnRemovePhone(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string token)
            {
                _store.Remove(token);
                RefreshPhones();
                RefreshStatus();
            }
        }
    }
}
