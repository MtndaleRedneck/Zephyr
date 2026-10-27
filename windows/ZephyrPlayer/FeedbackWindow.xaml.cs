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
    public partial class FeedbackWindow : Window
    {
        private const string RepoIssues = "https://github.com/MtndaleRedneck/Zephyr-Dev/issues";
        // TODO(feedback-form): wire the NEW Zephyr feedback form here - the
        // /viewform URL and its entry IDs arrive via Main once the form is
        // created. The shared BingeMote form must NOT be used (not even as a
        // fallback): until the URL lands, the form button stays disabled and
        // the GitHub issue path carries feedback. Entry IDs below follow the
        // settled schema (message / sentfrom / source / paired / appver /
        // android / model / display); PC fills message + source + appver + os.
        private const string? BugFormUrl = null;
        private const string EntryMessage = "";
        private const string EntrySource = "";
        private const string EntryOs = "";
        private const string EntryAppVer = "";
        private const int MaxText = 3000;
        private const int MaxEncoded = 4500;

        public FeedbackWindow()
        {
            InitializeComponent();
            WindowChrome.ApplyDarkTitleBar(this);
            // The new Zephyr form URL is pending (see BugFormUrl above): until it
            // is wired, the form button stays off rather than pointing anywhere
            // else. The GitHub issue path works today.
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

        private static string Preview(string text) =>
            "Message:\n" + (Clean(text).Length > 0 ? Clean(text) : "(nothing written yet)")
            + "\n\nApp version: " + Version() + "\nOS: " + OsVersion();

        private string FormUrl()
        {
            var t = Clean(MessageBox.Text);
            return BugFormUrl! + "?usp=pp_url"
                + "&" + EntryMessage + "=" + Enc(t)
                + "&" + EntrySource + "=" + Enc("Windows PC")
                + "&" + EntryOs + "=" + Enc(OsVersion())
                + "&" + EntryAppVer + "=" + Enc(Version());
        }

        private string IssueUrl()
        {
            var t = Clean(MessageBox.Text);
            var nl = t.IndexOf('\n');
            var title = "Feedback: " + (nl >= 0 ? t.Substring(0, nl) : t);
            if (title.Length > 67) title = title.Substring(0, 67);
            var body = t + "\n\n---\nApp version: " + Version() + "\nOS: " + OsVersion();
            return RepoIssues + "/new?title=" + Enc(title) + "&body=" + Enc(body);
        }

        private static string Enc(string s) => Uri.EscapeDataString(s);

        private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

        private void UpdatePreview() => PreviewText.Text = Preview(MessageBox.Text);

        private void OnForm(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo(FormUrl()) { UseShellExecute = true });

        private void OnIssue(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo(IssueUrl()) { UseShellExecute = true });
    }
}
