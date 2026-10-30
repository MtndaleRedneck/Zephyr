using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;

namespace ZephyrPlayer
{
    /// <summary>
    /// Feedback, matching the phone's flow (android Feedback.kt): the user types,
    /// sees EXACTLY what would be sent, and submits in the browser via the same
    /// pre-filled Google Form or a pre-filled GitHub issue. The report carries only
    /// what he typed plus app version + Windows version - no PC name, Wi-Fi names,
    /// paired phones or file paths.
    /// </summary>
    public partial class FeedbackPanel : System.Windows.Controls.UserControl
    {
        private const string RepoIssues = "https://github.com/MtndaleRedneck/Zephyr/issues";
        // The dedicated Zephyr feedback form (created + verified public by the
        // coordinator, Oct 26). The shared BingeMote form is NEVER used. The
        // "Reply email" field (entry.1538046022) is never prefilled - the user
        // types it only if they want an answer. Phone-only fields (Android
        // version / Phone model / Display) stay empty on PC; the Windows build
        // rides in App version.
        private const string? BugFormUrl = "https://docs.google.com/forms/d/e/1FAIpQLSdzwHtL4OAUFpffzp0WqLq99AfY0QIHrl-CPjFLTp4nL1ThuA/viewform";
        private const string EntryMessage = "entry.574867238";   // Your message (required)
        private const string EntrySentFrom = "entry.2130527520"; // Sent from
        private const string EntrySource = "entry.764416810";    // Source selected
        private const string EntryPaired = "entry.1337995316";   // Paired
        private const string EntryAppVer = "entry.2049802033";   // App version
        private const int MaxText = 3000;
        private const int MaxEncoded = 4500;

        private readonly int _pairedCount;

        /// <param name="pairedCount">Current paired-phone count, prefilled into the
        /// form's "Paired" field (the phone flow does the same).</param>
        public FeedbackPanel(int pairedCount = 0)
        {
            InitializeComponent();
            _pairedCount = pairedCount;
            FormButton.IsEnabled = BugFormUrl != null;
            FormNote.Visibility = BugFormUrl == null ? Visibility.Visible : Visibility.Collapsed;
            UpdatePreview();
        }

        private static string Version() =>
            "Zephyr Player " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?") + " (Windows)";

        private static string OsVersion() => "Windows " + Environment.OSVersion.Version;

        private static string Clean(string text)
        {
            var t = text.Trim();
            if (t.Length > MaxText) t = t.Substring(0, MaxText);
            while (t.Length > 0 && Enc(t).Length > MaxEncoded)
                t = t.Substring(0, t.Length - 50).TrimEnd();
            return t;
        }

        private string Preview(string text) =>
            "Message:\n" + (Clean(text).Length > 0 ? Clean(text) : "(nothing written yet)")
            + "\n\nSent from: Windows PC\nSource selected: Zephyr Player\nPaired: "
            + (_pairedCount > 0 ? _pairedCount + (_pairedCount == 1 ? " phone" : " phones") : "Not paired")
            + "\nApp version: " + Version() + " (" + OsVersion() + ")";

        private string FormUrl()
        {
            var t = Clean(MessageTextBox.Text);
            return BugFormUrl! + "?usp=pp_url"
                + "&" + EntryMessage + "=" + Enc(t)
                + "&" + EntrySentFrom + "=" + Enc("Windows PC")
                + "&" + EntrySource + "=" + Enc("Zephyr Player")
                + "&" + EntryPaired + "=" + Enc(_pairedCount > 0 ? _pairedCount + (_pairedCount == 1 ? " phone" : " phones") : "Not paired")
                + "&" + EntryAppVer + "=" + Enc(Version() + " (" + OsVersion() + ")");
        }

        private string IssueUrl()
        {
            var t = Clean(MessageTextBox.Text);
            var nl = t.IndexOf('\n');
            var title = "Feedback: " + (nl >= 0 ? t.Substring(0, nl) : t);
            if (title.Length > 67) title = title.Substring(0, 67);
            var body = t + "\n\n---\nApp version: " + Version() + "\nOS: " + OsVersion();
            return RepoIssues + "/new?title=" + Enc(title) + "&body=" + Enc(body);
        }

        private static string Enc(string s) => Uri.EscapeDataString(s);

        private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

        private void UpdatePreview() => PreviewText.Text = Preview(MessageTextBox.Text);

        private void OnForm(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo(FormUrl()) { UseShellExecute = true });

        private void OnIssue(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo(IssueUrl()) { UseShellExecute = true });
    }
}
