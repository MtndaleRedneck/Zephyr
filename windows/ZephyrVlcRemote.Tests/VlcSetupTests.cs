using System.Text.Json;
using ZephyrVlcRemote;
using Xunit;

namespace ZephyrVlcRemote.Tests;

[Collection("ProgramState")]
public class VlcSetupTests : IDisposable
{
    // Round-7 N8: never touch the real %APPDATA%\vlc\vlcrc - a crashed run used to
    // leave the backup un-restored. The seam redirects every vlcrc access to a temp dir.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zvrt-vlc-" + Guid.NewGuid().ToString("N"));
    private string VlcrcPath => Path.Combine(_dir, "vlcrc");

    public VlcSetupTests()
    {
        Directory.CreateDirectory(_dir);
        VlcSetup.VlcDirOverride = _dir;
        VlcSetup.ForgetCandidateForTests();
        VlcSetup.VlcRunningOverride = () => false;   // the tests must not depend on a VLC open on the machine
    }

    public void Dispose()
    {
        VlcSetup.VlcDirOverride = null;
        VlcSetup.VlcRunningOverride = null;
        VlcSetup.AfterPlanReadForTests = null;
        VlcSetup.ForgetCandidateForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void WriteVlcrc(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VlcrcPath)!);
        File.WriteAllLines(VlcrcPath, lines);
    }

    [Fact]
    public void UnchangedSettingsAreANoOpNotAPendingWrite()
    {
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=pw-1-long-password");
        var r = VlcSetup.WriteVlcrc("pw-1-long-password");
        using var doc = JsonDocument.Parse(r);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        // A running VLC read exactly this vlcrc at startup, so nothing may be deferred:
        // a false pending would tell the user a reopen is required when control works now.
        Assert.False(VlcSetup.Pending);
        Assert.Equal("pw-1-long-password", VlcSetup.ReadVlcrcValue("http-password"));
    }

    [Fact]
    public void ReusingThePasswordAlsoEnsuresInterfaceAndBind()
    {
        WriteVlcrc("http-password=pw-1-long-password"); // a vlcrc can carry a password with the interface off
        var r = VlcSetup.WriteVlcrc("pw-1-long-password");
        using var doc = JsonDocument.Parse(r);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("http", VlcSetup.ReadVlcrcValue("extraintf"));
        Assert.Equal("0.0.0.0", VlcSetup.ReadVlcrcValue("http-host"));
        Assert.False(VlcSetup.Pending);
        Assert.True(VlcSetup.Configured);
    }

    [Fact]
    public void PasswordAloneIsNotConfigured()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VlcrcPath)!);
        if (File.Exists(VlcrcPath)) File.Delete(VlcrcPath);
        WriteVlcrc("http-password=pw-1-long-password");
        Assert.False(VlcSetup.Configured); // no interface, no bind: never a green attestation
    }

    [Fact]
    public void UnwritableVlcrc_AfterAPair_KeepsThePasswordPending_NoException()
    {
        // The profile dir cannot be created (its parent is a regular file): ReadOrCreatePassword used to throw
        // here AFTER the phone was saved and the code spent.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        VlcSetup.VlcDirOverride = Path.Combine(blocker, "vlc");
        try
        {
            var pw = VlcSetup.ReadOrCreatePassword();            // must not throw
            Assert.Equal(24, pw.Length);                          // a real generated password, usable by the phone
            Assert.True(VlcSetup.Pending);                        // the existing "not on disk yet" shape
            Assert.Equal(pw, VlcSetup.PendingValue);
            Assert.Equal(pw, VlcSetup.EffectivePassword());
            Assert.Equal(pw, VlcSetup.ReadOrCreatePassword());    // stable: a second pair gets the same one
            using var doc = JsonDocument.Parse(VlcSetup.WriteVlcrc(pw));
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("vlcrc write failed", doc.RootElement.GetProperty("reason").GetString());
            // The disk recovers: the retry timer's call completes the write and clears pending.
            VlcSetup.VlcDirOverride = _dir;
            Assert.False(VlcSetup.RetryPending());
            Assert.False(VlcSetup.Pending);
            Assert.Equal(pw, VlcSetup.ReadVlcrcValue("http-password"));
            Assert.True(VlcSetup.Configured);
        }
        finally { VlcSetup.VlcDirOverride = _dir; }
    }

    [Fact]
    public void VlcrcThatIsADirectory_IsUnreadable_NotNothingConfigured()
    {
        // A directory where the file should be: the read is denied, which is not "no password set". Nothing is
        // generated or queued; pairing answers 503 and the same code works once it is fixed.
        Directory.CreateDirectory(VlcrcPath);
        Assert.Throws<VlcrcUnreadableException>(() => VlcSetup.PlanPassword());
        Assert.False(VlcSetup.Pending);
        Directory.Delete(VlcrcPath);
        var pw = VlcSetup.ReadOrCreatePassword();
        Assert.Equal(24, pw.Length);
        Assert.Equal(pw, VlcSetup.ReadVlcrcValue("http-password"));
    }

    [Fact]
    public void MissingVlcrc_IsNothingConfigured_AFreshPasswordIsPlanned()
    {
        if (File.Exists(VlcrcPath)) File.Delete(VlcrcPath);
        var pw = VlcSetup.PlanPassword();
        Assert.Equal(24, pw.Length);
        Assert.False(VlcSetup.Pending);
        Assert.False(File.Exists(VlcrcPath));                     // planning writes nothing
    }

    [Fact]
    public void MissingVlcFolder_IsNothingConfigured_NotUnreadable()
    {
        VlcSetup.VlcDirOverride = Path.Combine(_dir, "no-such-folder", "vlc");
        try { Assert.Equal(24, VlcSetup.PlanPassword().Length); }
        finally { VlcSetup.VlcDirOverride = _dir; }
    }

    [Fact]
    public void ExistingVlcrcThatCannotBeRead_IsNeverRotated()
    {
        WriteVlcrc("extraintf=http", "http-host=0.0.0.0", "http-password=existing-password-1");
        using (new FileStream(VlcrcPath, FileMode.Open, FileAccess.Read, FileShare.None))   // a transient lock
        {
            Assert.Throws<VlcrcUnreadableException>(() => VlcSetup.ReadOrCreatePassword());
            Assert.False(VlcSetup.Pending);                       // nothing generated, nothing queued
        }
        Assert.Equal("existing-password-1", VlcSetup.ReadOrCreatePassword());   // lock gone: the same password
        Assert.Equal("existing-password-1", VlcSetup.ReadVlcrcValue("http-password"));
        Assert.False(VlcSetup.Pending);
    }

    [Fact]
    public void TwoPlansBeforeAnyWrite_AgreeOnOnePassword_AndChangeNothing()
    {
        var a = VlcSetup.PlanPassword();
        var b = VlcSetup.PlanPassword();                      // e.g. an adopt call overlapping a pair
        Assert.Equal(a, b);
        Assert.False(VlcSetup.Pending);                       // planning mutates nothing
        Assert.False(File.Exists(VlcrcPath));
        Assert.Equal(a, VlcSetup.ApplyPlannedPassword(b));
        Assert.Equal(a, VlcSetup.ReadVlcrcValue("http-password"));
        Assert.Equal(a, VlcSetup.PlanPassword());             // afterwards the disk is the source, no new candidate
    }

    [Fact]
    public void AnOverlappingCallerBetweenTheReadAndTheAllocation_CannotMakeTwoPasswords()
    {
        // The overlapping caller (an adopt) runs its whole plan+apply from INSIDE the first plan, right after its
        // vlcrc read said "nothing configured". With the read and the allocation in one Gate hold the overlapping
        // caller waits and gets the same password; with a read outside the hold it would write its own first and
        // this plan would then allocate a second one from the stale read.
        Task<string>? other = null;
        VlcSetup.AfterPlanReadForTests = () =>
        {
            VlcSetup.AfterPlanReadForTests = null;
            other = Task.Run(() => VlcSetup.ReadOrCreatePassword());
            other.Wait(300);                                   // finishes only if nothing blocks it
        };
        var planned = VlcSetup.PlanPassword();
        Assert.NotNull(other);
        Assert.True(other!.Wait(5000));                        // the other caller finishes first, so the two writes never race
        var applied = VlcSetup.ApplyPlannedPassword(planned);
        Assert.Equal(planned, other.Result);                   // one password for both callers
        Assert.Equal(planned, VlcSetup.ReadVlcrcValue("http-password"));   // and it is the one on disk
        Assert.Equal(planned, applied);
    }
}
