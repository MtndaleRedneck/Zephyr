using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

/// <summary>Characterization, not a gate: what a standard Windows form and button do when the helper's real
/// InputSender delivers a button RELEASE with no press before it (the rule "never swallow a release" lets one
/// through after a wake-swallowed press). Uses only supported WinForms events (Button.Click, Button.MouseUp,
/// Form.MouseUp, ContextMenuStrip.Opening).
///
/// Safety and honesty rules:
/// - Real SendInput runs ONLY on an isolated GitHub-hosted runner (GITHUB_ACTIONS=true and RUNNER_ENVIRONMENT=github-hosted).
///   Anywhere else the test injects nothing and records SKIPPED.
/// - The result is INCONCLUSIVE unless BOTH controls work: a full left press+release reaches the button, AND a full
///   right press+release opens the form's context menu. Before every injection the probe form must be the foreground
///   window and the window under the pointer must be the intended one; otherwise INCONCLUSIVE and nothing is sent.
/// - The form, the menu and the pointer position are always put back in finally.
/// - It never fails the suite on what the runner does; only a hang past 60 s or an exception outside the probe's catch
///   fails it. A passing test is NOT the answer: read the note (zvr-loneup-proof.txt).
/// ProgramState runs alone, so no other test moves the pointer meanwhile.</summary>
[Collection("ProgramState")]
public class LoneButtonUpProofTests
{
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint p);

    private static string OutDir => Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();

    private static bool IsolatedGitHubRunner =>
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted";

    private static void Pump(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(10); }
    }

    [Fact]
    public void LoneUp_OnAStandardFormAndButton_IsRecorded()
    {
        var note = new StringBuilder();
        if (!IsolatedGitHubRunner)
        {
            note.AppendLine("RESULT: SKIPPED - not an isolated GitHub-hosted runner; no input was sent.");
            try { File.WriteAllText(Path.Combine(OutDir, "zvr-loneup-proof.txt"), note.ToString()); } catch { }
            return;
        }
        var thread = new Thread(() => Probe(note));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var finished = thread.Join(60000);
        if (!finished) note.AppendLine("RESULT: INCONCLUSIVE - the probe did not finish in 60 s.");
        try { File.WriteAllText(Path.Combine(OutDir, "zvr-loneup-proof.txt"), note.ToString()); } catch { }
        Assert.True(finished, "the probe did not finish in 60 s");
    }

    private static void Probe(StringBuilder note)
    {
        var saved = Cursor.Position;
        ContextMenuStrip? menu = null;
        Form? form = null;
        try
        {
            InputSender.ButtonSink = null;
            int click = 0, buttonUp = 0, formUp = 0, menuOpen = 0;
            menu = new ContextMenuStrip();
            menu.Items.Add("probe");
            menu.Opening += (_, _) => menuOpen++;
            form = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                Location = new Point(300, 300), Size = new Size(400, 300), TopMost = true, ShowInTaskbar = false,
                ContextMenuStrip = menu,
            };
            var button = new Button { Location = new Point(20, 20), Size = new Size(200, 100), Text = "probe" };
            button.Click += (_, _) => click++;
            button.MouseUp += (_, _) => buttonUp++;
            form.MouseUp += (_, _) => formUp++;
            form.Controls.Add(button);
            form.Show();
            form.Activate();
            SetForegroundWindow(form.Handle);
            Pump(500);

            var onButton = new Point(400, 370);     // inside the button (screen coordinates)
            var onForm = new Point(600, 500);       // plain form area, outside the button
            void Reset() { click = 0; buttonUp = 0; formUp = 0; menuOpen = 0; menu.Close(); Pump(150); }

            // Returns false (and records why) unless the form is foreground and the pointer is over the intended window.
            bool Aim(Point p, IntPtr expectedWindow, string what)
            {
                Cursor.Position = p; Pump(150);
                if (Cursor.Position != p) { note.AppendLine("cannot aim (" + what + "): the pointer did not move to the target."); return false; }
                if (GetForegroundWindow() != form.Handle) { note.AppendLine("cannot aim (" + what + "): the probe form is not the foreground window."); return false; }
                if (WindowFromPoint(new NativePoint { X = p.X, Y = p.Y }) != expectedWindow) { note.AppendLine("cannot aim (" + what + "): the window under the pointer is not the intended one."); return false; }
                return true;
            }
            bool Inject(Point p, IntPtr expected, string what, Action send)
            {
                if (!Aim(p, expected, what)) return false;
                send(); Pump(400);
                return true;
            }
            const string Inconclusive = "RESULT: INCONCLUSIVE - ";

            // Control 1: a full LEFT press and release must reach the button.
            Reset();
            if (!Inject(onButton, button.Handle, "left control", () => { InputSender.Button("left", true); InputSender.Button("left", false); }))
            { note.AppendLine(Inconclusive + "the left control could not be aimed; nothing was proven."); return; }
            var leftOk = buttonUp > 0 && click > 0;
            note.AppendLine("control LEFT down+up over button: mouse-up events " + buttonUp + ", Click events " + click);
            // Control 2: a full RIGHT press and release must open the form's context menu.
            Reset();
            if (!Inject(onForm, form.Handle, "right control", () => { InputSender.Button("right", true); InputSender.Button("right", false); }))
            { note.AppendLine(Inconclusive + "the right control could not be aimed; nothing was proven."); return; }
            var rightOk = menuOpen > 0;
            note.AppendLine("control RIGHT down+up over plain form: context menu opened " + menuOpen + " time(s)");
            if (!leftOk || !rightOk)
            { note.AppendLine(Inconclusive + "a control did not work (left " + (leftOk ? "ok" : "failed") + ", right " + (rightOk ? "ok" : "failed") + "); nothing was proven."); return; }

            Reset();
            var a = Inject(onButton, button.Handle, "lone left over button", () => InputSender.Button("left", false));
            note.AppendLine(a ? "lone LEFT up over button: mouse-up events " + buttonUp + ", Click events " + click : "lone LEFT up over button: not sent (could not aim)");
            Reset();
            var b = Inject(onForm, form.Handle, "lone left over form", () => InputSender.Button("left", false));
            note.AppendLine(b ? "lone LEFT up over plain form: form mouse-up events " + formUp : "lone LEFT up over plain form: not sent (could not aim)");
            Reset();
            var c = Inject(onForm, form.Handle, "lone right over form", () => InputSender.Button("right", false));
            note.AppendLine(c ? "lone RIGHT up over plain form: context menu opened " + menuOpen + " time(s)" : "lone RIGHT up over plain form: not sent (could not aim)");
            note.AppendLine(a && b && c ? "RESULT: recorded (both controls worked)" : Inconclusive + "a lone-up probe could not be aimed; see lines above.");
        }
        catch (Exception ex) { note.AppendLine("RESULT: INCONCLUSIVE - probe stopped (" + ex.GetType().Name + ")."); }
        finally
        {
            try { menu?.Close(); } catch { }
            try { form?.Close(); } catch { }
            try { form?.Dispose(); } catch { }
            try { menu?.Dispose(); } catch { }
            try { Cursor.Position = saved; } catch { }
        }
    }
}
