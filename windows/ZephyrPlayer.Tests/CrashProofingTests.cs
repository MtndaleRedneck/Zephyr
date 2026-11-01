using System;
using System.Threading;
using System.Windows;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>2.0.2 crash-proofing: the UI-thread guard (which exceptions the app survives,
    /// and the burst limit) and the Feedback link on a PC with no browser.</summary>
    public class CrashProofingTests
    {
        [Fact]
        public void Guard_SurvivesOrdinaryHandlerExceptions_NotCorruptingOnes()
        {
            CrashGuard.ResetForTest();
            var now = DateTime.UtcNow;
            Assert.True(CrashGuard.ShouldHandle(new InvalidOperationException("handler bug"), now));
            Assert.True(CrashGuard.ShouldHandle(new System.IO.IOException("disk"), now));
            Assert.False(CrashGuard.ShouldHandle(new OutOfMemoryException(), now));
            Assert.False(CrashGuard.ShouldHandle(new AccessViolationException(), now));
            Assert.False(CrashGuard.ShouldHandle(new InvalidOperationException("wrapped", new OutOfMemoryException()), now));
            CrashGuard.ResetForTest();
        }

        [Fact]
        public void Guard_ABurstOfExceptions_StopsBeingHandled_ThenRecovers()
        {
            CrashGuard.ResetForTest();
            var t0 = DateTime.UtcNow;
            for (var i = 0; i < CrashGuard.BurstLimit; i++)
                Assert.True(CrashGuard.ShouldHandle(new InvalidOperationException(), t0.AddSeconds(i)));
            // A handler throwing again and again (every frame) is not swallowed forever.
            Assert.False(CrashGuard.ShouldHandle(new InvalidOperationException(), t0.AddSeconds(CrashGuard.BurstLimit)));
            // Once the window has passed, a lone exception is handled again.
            Assert.True(CrashGuard.ShouldHandle(new InvalidOperationException(), t0 + CrashGuard.BurstWindow + TimeSpan.FromSeconds(CrashGuard.BurstLimit + 1)));
            CrashGuard.ResetForTest();
        }

        [Fact]
        public void Feedback_NoBrowser_DoesNotThrow_CopiesTheLink_AndSaysSo()
        {
            Exception? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    string? note = null;
                    FeedbackPanel.LaunchOverride = _ => throw new System.ComponentModel.Win32Exception(1155, "No application is associated with the specified file for this operation");
                    FeedbackPanel.NoteOverride = s => note = s;
                    try
                    {
                        const string url = "https://github.com/MtndaleRedneck/Zephyr/issues/new?title=t";
                        var opened = FeedbackPanel.OpenInBrowser(url);
                        Assert.False(opened);
                        Assert.NotNull(note);
                        Assert.Contains("No web browser could open the feedback page", note);
                        if (note!.Contains("copied")) Assert.Equal(url, Clipboard.GetText());
                    }
                    finally { FeedbackPanel.LaunchOverride = null; FeedbackPanel.NoteOverride = null; }
                    // A working launch still opens the link and shows nothing.
                    string? launched = null; string? note2 = null;
                    FeedbackPanel.LaunchOverride = u => launched = u;
                    FeedbackPanel.NoteOverride = s => note2 = s;
                    try { Assert.True(FeedbackPanel.OpenInBrowser("https://example.invalid/")); }
                    finally { FeedbackPanel.LaunchOverride = null; FeedbackPanel.NoteOverride = null; }
                    Assert.Equal("https://example.invalid/", launched);
                    Assert.Null(note2);
                }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(30000));
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
