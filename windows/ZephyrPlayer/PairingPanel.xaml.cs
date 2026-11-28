using System;
using System.Collections.Generic;
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
    /// discovery findings. One instance, owned by App; the
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
        private static readonly SolidColorBrush OkGreen = new(Color.FromRgb(0x4C, 0xAF, 0x50));
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
            _storeChanged = (_, __) => Dispatcher.InvokeAsync(() => { if (_codeShowing && HasNewPhone()) OnPairSucceeded(); else RefreshPhones(); });
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
            if (PairButton != null)
                PairButton.Background = new SolidColorBrush(PhoneNameBox.Text.Trim().Length == 0
                    ? Color.FromRgb(0xD0, 0x76, 0x00) : Brand.Orange);
        }

        private string PendingName()
        {
            var n = PhoneNameBox.Text.Trim();
            return n;
        }

        /// <summary>The digits with a thin space between them (WPF has no letter spacing) and a word space in the
        /// middle of a six-digit code.</summary>
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

        private void Mint(bool userStarted = false)
        {
            var name = PhoneNameBox.Text.Trim();
            _pairStarted(name);
            PhoneNameBox.IsReadOnly = true;
            // The gate hands the code out only at mint time; mirror it for display.
            PairCodeText.Text = FormatCode(_gate.NewCode(userStarted));
        }

        // Window-side "a code is on screen" flag: the gate's HasActiveCode goes false at
        // expiry, so gating renewal on it meant the code NEVER renewed (review blocker 1).
        private bool _codeShowing;
        // Success is a new phone in the store, never just "the code went away" (a lock, expiry
        // or cancel also clears the code).
        private HashSet<string> _tokensAtShow = new();
        private bool HasNewPhone() => _store.Entries().Any(e => !_tokensAtShow.Contains(e.Token));

        private void OnPair(object sender, RoutedEventArgs e)
        {
            if (_store.IsBroken) return;
            if (PhoneNameBox.Text.Trim().Length == 0) { PhoneNameBox.Focus(); return; }
            // Never quietly swap a code the phone may already be typing (name/Pair flow):
            // mint only when no code is live; the name box edits the pending name live.
            if (!_gate.HasActiveCode || _gate.Locked) Mint(userStarted: true);
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
            if (_codeShowing && _gate.Locked)
            {
                // Too many wrong codes: take the code down and say so, instead of leaving a
                // code on screen that the phone can no longer use. Pair (a person) re-opens it.
                _gate.CancelCode();
                PhoneNameBox.IsReadOnly = false;
                _codeShowing = false;
                CodePanel.Visibility = Visibility.Collapsed;
                PairButton.Visibility = Visibility.Visible;
                RefreshStatus();
                return;
            }
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
                // A Pair session lasts one code lifetime (10 minutes): it closes by itself, so guessing is
                // never left open for hours. Pressing Pair again (a person) starts a new session.
                _gate.CancelCode();
                PhoneNameBox.IsReadOnly = false;
                _codeShowing = false;
                CodePanel.Visibility = Visibility.Collapsed;
                PairButton.Visibility = Visibility.Visible;
                RefreshStatus();
                return;
            }
            var t = left ?? TimeSpan.Zero;
            ExpiresText.Text = $"Code expires in {(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        private void ShowCode()
        {
            if (!_codeShowing) _tokensAtShow = _store.Entries().Select(e => e.Token).ToHashSet();
            _codeShowing = true;
            CodePanel.Visibility = Visibility.Visible;
            // The code is already live: a Pair button that only re-shows the same
            // panel reads as broken, so it hides until the code is gone (round-3 P5).
            PairButton.Visibility = Visibility.Collapsed;
            var left = _gate.CodeTimeLeft ?? TimeSpan.FromMinutes(10);
            ExpiresText.Text = $"Code expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
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
            var cards = entries.Select(e => new PhoneCard {
                Name = e.Name, Token = e.Token,
                Detail = PhoneStatus(_store.IsConnected(e.Token), _store.LastSeen(e.Token), DateTime.Now),
                Paired = e.PairedAt is DateTime date ? "Paired " + date.ToLocalTime().ToString("d") : "Previously paired",
                ForgetId = "Forget:" + e.Name,
                ForgetName = "Forget " + e.Name,
            }).ToList();
            // The panel's tick refreshes every second; replacing an unchanged list rebuilt every card each second, so UI
            // Automation (screen readers, CI's card lookups) kept losing the Forget buttons mid-lookup. Only swap the
            // list when a card changed. A class (reference equality), not an anonymous type: automation peers match
            // items by Equals, and an unchanged card matched its OLD peer whose container was gone.
            if (!(PhoneList.ItemsSource is System.Collections.Generic.List<PhoneCard> shown && shown.Select(c => c.Key).SequenceEqual(cards.Select(c => c.Key))))
                PhoneList.ItemsSource = cards;
            NoPhonesText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        internal sealed class PhoneCard
        {
            public string Name { get; init; } = "";
            public string Token { get; init; } = "";
            public string Detail { get; init; } = "";
            public string Paired { get; init; } = "";
            public string ForgetId { get; init; } = "";
            public string ForgetName { get; init; } = "";
            internal string Key => string.Join("\u0001", Name, Token, Detail, Paired);
        }

        /// <summary>"Connected now", "Last seen today, 3:41 PM", "Last seen yesterday", "Last seen Oct 28", or "Not connected this session".</summary>
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
            WarningPanel.Visibility = Visibility.Visible;
            var ip = ConnectionState.PrimaryIPv4();
            ThisPcText.Text = ip != null ? "This PC: " + ip : "This PC: address unavailable";
            ResetPairingsButton.Visibility = _store.CanResetUnreadable ? Visibility.Visible : Visibility.Collapsed;
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
            else if (_gate.Locked)
            {
                StatusDot.Fill = WarnOrange;
                StatusText.Text = "Too many wrong codes were entered. Click Pair to get a new code.";
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

        /// <summary>Test seam: no modal dialogs on test threads.</summary>
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
