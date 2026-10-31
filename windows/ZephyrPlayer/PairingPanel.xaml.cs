using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZephyrPlayer
{
    /// <summary>
    /// The pairing surface (pairing layout): "Pair new phone" with a live
    /// code + countdown that renews before expiry (the pre-1.2 code silently
    /// dies after 10 minutes and Settings clobbers it), "This PC:" address for
    /// manual entry, the paired-phones list with Remove, and a status row for
    /// discovery / public-Wi-Fi findings. One instance, owned by App; the
    /// Settings "Android remote" section opens it too, so every place shows the
    /// SAME live gate - no more per-window codes clobbering each other.
    /// </summary>
    public partial class PairingPanel : UserControl
    {
        private readonly PairingGate _gate;
        private readonly PairedStore _store;
        private readonly Action<string> _pairStarted;
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Action _connChanged;
        private readonly EventHandler _storeChanged;
        private static readonly SolidColorBrush OkGreen = new(Color.FromRgb(0x3F, 0xB9, 0x50));
        private static readonly SolidColorBrush WarnOrange = new(Brand.Orange);

        static PairingPanel() { OkGreen.Freeze(); WarnOrange.Freeze(); }

        /// <param name="pairStarted">Called with the phone name at every code mint;
        /// App attaches it to the token when that code pairs.</param>
        public PairingPanel(PairingGate gate, PairedStore store, Action<string> pairStarted)
        {
            InitializeComponent();
            _gate = gate;
            _store = store;
            _pairStarted = pairStarted;
            _storeChanged = (_, __) => Dispatcher.InvokeAsync(() => { if (_codeShowing && _gate.CodeTimeLeft == null) OnPairSucceeded(); else RefreshPhones(); });
            _tick.Tick += (_, __) => { TickCountdown(); RefreshPhones(); };
            Loaded += (_, __) =>
            {
                // Name-first: opening this surface never creates an unseen code.
                CodePanel.Visibility = Visibility.Collapsed;
                PairButton.IsEnabled = !_store.IsBroken;
                RefreshPhones();
                RefreshStatus();
                _tick.Start();
            };
            _connChanged = () => Dispatcher.InvokeAsync(RefreshStatus);
            Loaded += (_, __) => { ConnectionState.Changed += _connChanged; _store.Changed += _storeChanged; };
            Unloaded += (_, __) => { _tick.Stop(); _gate.CancelCode(); _codeShowing = false; PhoneNameBox.IsReadOnly = false; PairButton.Visibility = Visibility.Visible; ConnectionState.Changed -= _connChanged; _store.Changed -= _storeChanged; };
        }

        private void OnPhoneNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            PhoneNameHint.Visibility = PhoneNameBox.Text.Length == 0
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            // A softer Pair until a name is typed (the 1.4 hint, in the 2.0 button style).
            if (PairButton != null) PairButton.Opacity = PhoneNameBox.Text.Trim().Length == 0 ? 0.6 : 1.0;
        }

        private string PendingName()
        {
            var n = PhoneNameBox.Text.Trim();
            return n;
        }

        private void Mint()
        {
            var name = PhoneNameBox.Text.Trim();
            _pairStarted(name);
            PhoneNameBox.IsReadOnly = true;
            // The gate hands the code out only at mint time; mirror it for display.
            PairCodeText.Text = FormatCode(_gate.NewCode());
        }

        /// <summary>Render 05's "482 913": the digits with a thin space between them (WPF has
        /// no letter spacing) and a word space in the middle of a six-digit code.</summary>
        internal static string FormatCode(string code)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < code.Length; i++)
            {
                if (i > 0) sb.Append(code.Length == 6 && i == 3 ? ' ' : '\u2009');
                sb.Append(code[i]);
            }
            return sb.ToString();
        }

        // Window-side "a code is on screen" flag: the gate's HasActiveCode goes false at
        // expiry, so gating renewal on it meant the code NEVER renewed (review blocker 1).
        private bool _codeShowing;

        private void OnPair(object sender, RoutedEventArgs e)
        {
            if (_store.IsBroken) return;
            if (PhoneNameBox.Text.Trim().Length == 0) { PhoneNameBox.Focus(); return; }
            // Never quietly swap a code the phone may already be typing (name/Pair flow):
            // mint only when no code is live; the name box edits the pending name live.
            if (!_gate.HasActiveCode) Mint();
            ShowCode();
        }

        private void OnCancelCode(object sender, RoutedEventArgs e)
        {
            _gate.CancelCode();
            PhoneNameBox.IsReadOnly = false;
            _codeShowing = false;
            CodePanel.Visibility = Visibility.Collapsed;
            PairButton.Visibility = Visibility.Visible;
        }

        private void TickCountdown()
        {
            if (!_codeShowing) return; // cancelled / paired: stay in the name + Pair state
            var left = _gate.CodeTimeLeft;
            if (left == null)
            {
                // Spent by a successful pair or cancelled: never mint a hidden code (N4b).
                PhoneNameBox.IsReadOnly = false;
            _codeShowing = false;
                CodePanel.Visibility = System.Windows.Visibility.Collapsed;
                PairButton.Visibility = Visibility.Visible;
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
            // The code is already live: a Pair button that only re-shows the same
            // panel reads as broken, so it hides until the code is gone (round-3 P5).
            PairButton.Visibility = Visibility.Collapsed;
            var left = _gate.CodeTimeLeft ?? TimeSpan.FromMinutes(10);
            ExpiresText.Text = $"New code in {(int)left.TotalMinutes}:{left.Seconds:00}";
            RefreshStatus();
        }

        /// <summary>Called by App (UI thread) when a phone completes pairing.</summary>
        public void OnPairSucceeded()
        {
            PhoneNameBox.IsReadOnly = false;
            _codeShowing = false;
            CodePanel.Visibility = Visibility.Collapsed;
            PairButton.Visibility = Visibility.Visible;
            RefreshPhones();
            RefreshStatus();
        }

        public void RefreshPhones()
        {
            var entries = _store.Entries();
            PhoneList.ItemsSource = entries.Select(e => new {
                e.Name, e.Token,
                Detail = PhoneStatus(_store.IsConnected(e.Token), _store.LastSeen(e.Token), DateTime.Now),
                DotVisibility = _store.IsConnected(e.Token) ? Visibility.Visible : Visibility.Collapsed,
                Paired = e.PairedAt is DateTime date ? "Paired " + date.ToLocalTime().ToString("d") : "Previously paired",
                ForgetId = "Forget:" + e.Name,
                ForgetName = "Forget " + e.Name,
            }).ToList();
            NoPhonesText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Render 05's card line: "Connected now", "Last seen today, 3:41 PM",
        /// "Last seen yesterday", "Last seen Oct 28", or "Not connected this session".</summary>
        internal static string PhoneStatus(bool connected, DateTime? lastSeenUtc, DateTime nowLocal)
        {
            if (connected) return "Connected now";
            if (lastSeenUtc is not DateTime seen) return "Not connected this session";
            var local = seen.ToLocalTime();
            if (local.Date == nowLocal.Date) return "Last seen today, " + local.ToString("t");
            if (local.Date == nowLocal.Date.AddDays(-1)) return "Last seen yesterday";
            return "Last seen " + local.ToString(local.Year == nowLocal.Year ? "MMM d" : "MMM d, yyyy");
        }

        private void RefreshStatus()
        {
            var ip = ConnectionState.PrimaryIPv4();
            ThisPcText.Text = ip != null ? "This PC: " + ip : "This PC: address unavailable";
            ResetPairingsButton.Visibility = _store.CanResetUnreadable ? Visibility.Visible : Visibility.Collapsed;
            // The public Wi-Fi notice is its own banner (render 05); it is shown exactly when
            // the 1.4 check (NetworkCheck) reports a public network, and says only that.
            WifiBanner.Visibility = ConnectionState.PublicWifiName != null ? Visibility.Visible : Visibility.Collapsed;
            WarningPanel.Visibility = Visibility.Visible;
            if (_store.IsBroken)
            {
                if(!_store.CanResetUnreadable){StatusDot.Fill=WarnOrange;StatusText.Text="Saved pairings are temporarily unavailable or in use. Close the other app and reopen this screen. Nothing was changed.";return;}
                // N5 fail-closed surfaced plainly: the pairings file could not be read,
                // nothing was deleted, and new pairing stays off until it is fixed.
                StatusDot.Fill = WarnOrange;
                StatusText.Text = "Saved pairings could not be read. Pairing is paused and nothing was deleted. You can reset pairings below; every phone will need to pair again.";
            }
            else if (ip == null)
            {
                StatusDot.Fill = WarnOrange;
                StatusText.Text = "No network address found. Connect this PC to the same Wi-Fi as your phone.";
            }
            else if (ConnectionState.DiscoveryOff)
            {
                StatusDot.Fill = WarnOrange;
                StatusText.Text = "Find my PC is off: another app is using it. Pair with the code above instead.";
            }
            else
            {
                StatusDot.Fill = OkGreen;
                StatusText.Text = "";
                WarningPanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>The banner's button runs the EXISTING firewall check (read only: the same
        /// verification the app runs at every start) and says what it found. It never changes
        /// a firewall rule, the network profile or any other Windows setting.</summary>
        private void OnFirewallCheck(object sender, RoutedEventArgs e)
        {
            var current = FirewallSetup.SetupIsCurrent();
            var text = FirewallSetup.LastReadFailed
                ? "Windows Firewall settings could not be read on this PC, so Zephyr cannot check its setup right now. A restart of Windows usually fixes the read."
                : current
                    ? "Zephyr's firewall setup is in place for home (Private) networks. Windows is treating this Wi-Fi as public, and Zephyr does not change that. If this is your home Wi-Fi, you can set it to Private in Windows Settings, under Network & internet."
                    : "Zephyr's firewall setup is not complete. Quit Zephyr from the tray icon and open it again: it will offer the setup.";
            FirewallCheckResult?.Invoke(text);
            var dlg = FirewallSetup.NoteWindow(text, "Firewall setup");
            var owner = Window.GetWindow(this);
            if (owner != null && owner.IsVisible) { dlg.Owner = owner; dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            if (SuppressDialogsForTest) return;
            dlg.ShowDialog();
        }

        /// <summary>Test seams: the firewall check's text, and no modal dialogs on test threads.</summary>
        internal static event Action<string>? FirewallCheckResult;
        internal static bool SuppressDialogsForTest;

        private void OnResetPairings(object sender,RoutedEventArgs e)
        {
            if(!_store.CanResetUnreadable)return;
            // The app's own dark confirm (CP4: no stock light MessageBox); No is the default.
            if(!SuppressDialogsForTest&&!FirewallSetup.Confirm(Window.GetWindow(this),"Reset all saved pairings? Every paired phone will be removed and must pair again. The unreadable file will be kept privately for recovery.","Reset pairings","Reset","Cancel"))return;
            try{_store.ResetUnreadablePairings();PairButton.IsEnabled=true;RefreshPhones();RefreshStatus();}
            catch{StatusText.Text="Pairings could not be reset. Nothing was changed; try closing and reopening the Player.";}
        }

        private void OnRemovePhone(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string token)
            {
                try
                {
                    _store.Remove(token);
                }
                catch (InvalidOperationException)
                {
                    // Broken/refused store: nothing changed; say so instead of
                    // letting the exception reach the unhandled handler (round-3 P4).
                    WarningPanel.Visibility = Visibility.Visible; StatusDot.Fill = WarnOrange;
                    StatusText.Text = "That change could not be saved - nothing was changed. Close and reopen the Player; if it still fails, send feedback from Settings and we will help.";
                    return;
                }
                RefreshPhones();
                RefreshStatus();
            }
        }
    }
}
