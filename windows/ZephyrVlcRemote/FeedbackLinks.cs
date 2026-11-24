namespace ZephyrVlcRemote;

/// <summary>Where "Feedback & bugs" sends people: Zephyr's own GitHub issues and Zephyr's own
/// feedback form (the same form Zephyr Android and Zephyr Player use). The BingeMote form is
/// never used here. The form's "Reply email" field (entry.1538046022) is never prefilled.</summary>
internal static class FeedbackLinks
{
    internal const string RepoIssues = "https://github.com/MtndaleRedneck/Zephyr/issues/new";
    internal const string BugFormUrl = "https://docs.google.com/forms/d/e/1FAIpQLSdzwHtL4OAUFpffzp0WqLq99AfY0QIHrl-CPjFLTp4nL1ThuA/viewform";
    private const string EntryMessage = "entry.574867238";   // Your message
    private const string EntrySentFrom = "entry.2130527520"; // Sent from
    private const string EntrySource = "entry.764416810";    // Source selected
    private const string EntryAppVer = "entry.2049802033";   // App version

    internal static string FormUrl(string msg, string version, string os) =>
        BugFormUrl + "?usp=pp_url" +
        "&" + EntryMessage + "=" + Uri.EscapeDataString(msg) +
        "&" + EntrySentFrom + "=" + Uri.EscapeDataString("Windows PC") +
        "&" + EntrySource + "=" + Uri.EscapeDataString("Zephyr VLC Remote") +
        "&" + EntryAppVer + "=" + Uri.EscapeDataString(version + " (" + os + ")");

    internal static string IssueUrl(string msg, string version, string os)
    {
        var title = "Feedback: " + (msg.Length <= 60 ? msg : msg.Substring(0, 57) + "...");
        // Trim the message, never the body: the version lines always survive a long report.
        string Body(string m) => m + "\n\nZephyr VLC Remote PC version: " + version + "\nWindows version: " + os;
        var url = RepoIssues + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(Body(msg));
        while (url.Length > 8000 && msg.Length > 280)
        {
            msg = msg.Substring(0, msg.Length / 2).Trim();
            url = RepoIssues + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(Body(msg));
        }
        return url;
    }
}

