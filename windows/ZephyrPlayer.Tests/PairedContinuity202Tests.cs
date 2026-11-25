using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Paired-phone continuity from the 2.0.2 file shape, with the REAL CurrentUser DPAPI (Dpapi.Wrap),
    /// synthetic tokens and names. Two labelled proofs, neither is an actually installed 2.0.2:
    ///  - "HandWritten202Shape_RealDpapi": the file below is written by this test in the shape 2.0.2's
    ///    PairedStore writes (JSON array of {token: "dpapi:...", name, pairedAt}); the tokens are wrapped by the real Dpapi class.
    ///  - "Legacy110Shape_RealDpapi": the 1.1.0 bare-string array, migrated to the wrapped shape.
    /// PairedStore.cs and Dpapi.cs are byte-identical to the player-v2.0.2 tag (blob ids in the review packet).
    /// </summary>
    public class PairedContinuity202Tests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "zp-pc202-" + Guid.NewGuid().ToString("N"));
        public PairedContinuity202Tests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
        private string FilePath => Path.Combine(_dir, "paired.json");

        [Fact]
        public void HandWritten202Shape_RealDpapi_LoadsKeepsNamesAndIsNotRewritten()
        {
            var json = "[{\"token\":\"" + Dpapi.Wrap("synthetic-token-a") + "\",\"name\":\"Kitchen phone\",\"pairedAt\":\"2026-10-01T12:00:00Z\"}," +
                       "{\"token\":\"" + Dpapi.Wrap("synthetic-token-b") + "\",\"name\":\"Den phone\",\"pairedAt\":null}]";
            File.WriteAllText(FilePath, json);
            var before = File.ReadAllBytes(FilePath);
            var store = new PairedStore(FilePath);
            Assert.False(store.IsBroken);
            Assert.True(store.IsPaired("synthetic-token-a"));
            Assert.True(store.IsPaired("synthetic-token-b"));
            Assert.False(store.IsPaired("synthetic-token-c"));
            Assert.Equal(new[] { "Den phone", "Kitchen phone" }, store.Entries().Select(e => e.Name).OrderBy(n => n).ToArray());
            // Already protected: reading must not rewrite the file (no needless churn on first 3.0 start).
            Assert.Equal(before, File.ReadAllBytes(FilePath));
            // The plain tokens never appear in the file.
            Assert.DoesNotContain("synthetic-token-a", File.ReadAllText(FilePath));
            // A write after the load keeps both old phones and adds the new one, re-readable from disk.
            store.Add("synthetic-token-c", "New phone");
            var reread = new PairedStore(FilePath);
            Assert.True(reread.IsPaired("synthetic-token-a"));
            Assert.True(reread.IsPaired("synthetic-token-b"));
            Assert.True(reread.IsPaired("synthetic-token-c"));
        }

        [Fact]
        public void Legacy110Shape_RealDpapi_MigratesToProtectedAndStillPairs()
        {
            File.WriteAllText(FilePath, "[\"synthetic-legacy-token\"]");
            var store = new PairedStore(FilePath);
            Assert.True(store.IsPaired("synthetic-legacy-token"));
            var text = File.ReadAllText(FilePath);
            Assert.Contains("dpapi:", text);
            Assert.DoesNotContain("synthetic-legacy-token", text);
            Assert.True(new PairedStore(FilePath).IsPaired("synthetic-legacy-token"));
        }

        [Fact]
        public void HandWritten202Shape_ShowsBothPhonesInThePairingPanel()
        {
            string? error = null;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    File.WriteAllText(FilePath, "[{\"token\":\"" + Dpapi.Wrap("synthetic-token-a") + "\",\"name\":\"Kitchen phone\",\"pairedAt\":null}," +
                        "{\"token\":\"" + Dpapi.Wrap("synthetic-token-b") + "\",\"name\":\"Den phone\",\"pairedAt\":null}]");
                    var panel = new PairingPanel(new PairingGate(), new PairedStore(FilePath), _ => { });
                    var host = new Window { Content = panel, Width = 640, Height = 600, ShowInTaskbar = false };
                    host.Show(); host.UpdateLayout();
                    Assert.Equal(2, ((ItemsControl)panel.FindName("PhoneList")!).Items.Count);
                    Assert.Equal(Visibility.Collapsed, ((TextBlock)panel.FindName("NoPhonesText")!).Visibility);
                    host.Close();
                }
                catch (Exception ex) { error = ex.ToString(); }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA); t.Start();
            Assert.True(t.Join(30000), "STA body timed out");
            Assert.True(error == null, error);
        }
    }
}
