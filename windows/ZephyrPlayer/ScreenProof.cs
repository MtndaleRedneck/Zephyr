using System;
using System.Runtime.InteropServices;

namespace ZephyrPlayer
{
    /// <summary>What the screen actually shows (CP2 round 5). Reads the composed desktop
    /// back through a GDI screen DC, after DwmFlush (the call returns once the desktop
    /// compositor has presented a frame), and compares sampled points with what the
    /// shell expects there. Only points whose top-level window is the shell count: a
    /// point covered by another window, off screen, or a shell that is hidden or
    /// minimized says nothing, and a check with too few such points reports
    /// NotOnScreen instead of a match. Safe on any thread.</summary>
    internal static class ScreenProof
    {
        internal enum Verdict { Match, Mismatch, NotOnScreen }

        internal readonly record struct Result(Verdict Verdict, int Owned, int Matched, int Total, int Old = -1, string Misses = "")
        {
            public override string ToString() => $"{Verdict} matched={Matched}/{Owned} on-screen-of={Total}" + (Old >= 0 ? $" old-colour={Old}" : "") + (Misses.Length > 0 ? " misses " + Misses : "");
        }

        /// <summary>Waits for the desktop compositor's next presented frame (DwmFlush);
        /// when that is not available, about one 60 Hz frame.</summary>
        internal static void WaitForComposedFrame()
        {
            try { if (DwmFlush() == 0) return; } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            System.Threading.Thread.Sleep(16);
        }

        /// <summary>The top-left [w] x [h] of the picture [expected] (top-down BGRA, rows
        /// [stride] pixels wide) is what the screen shows at [screenX],[screenY]: a 32 x 20
        /// grid of points, each within 16 levels per channel of the expected pixel (or of
        /// one of its 8 neighbours, for a one-pixel rounding of the window position).</summary>
        internal static Result PictureShown(IntPtr root, int screenX, int screenY, int w, int h, byte[] expected, int stride)
        {
            const int GX = 32, GY = 20, Tol = 16;
            if (w < 4 || h < 4) return new Result(Verdict.NotOnScreen, 0, 0, 0);
            int ew = w, eh = h;
            var shot = Grab(screenX, screenY, w, h);
            int owned = 0, matched = 0;
            var misses = new System.Text.StringBuilder();
            for (int j = 0; j < GY; j++)
                for (int i = 0; i < GX; i++)
                {
                    int x = (int)((i + 0.5) * ew / GX), y = (int)((j + 0.5) * eh / GY);
                    if (!Owned(root, screenX + x, screenY + y)) continue;
                    owned++;
                    int o = (y * w + x) * 4;
                    bool ok = false;
                    for (int dy = -1; dy <= 1 && !ok; dy++)
                        for (int dx = -1; dx <= 1 && !ok; dx++)
                        {
                            int xx = Math.Clamp(x + dx, 0, ew - 1), yy = Math.Clamp(y + dy, 0, eh - 1), e = (yy * stride + xx) * 4;
                            ok = Math.Abs(shot[o] - expected[e]) <= Tol && Math.Abs(shot[o + 1] - expected[e + 1]) <= Tol && Math.Abs(shot[o + 2] - expected[e + 2]) <= Tol;
                        }
                    if (ok) matched++;
                    else if (misses.Length < 400) misses.Append($"{x},{y}:{shot[o + 2]:X2}{shot[o + 1]:X2}{shot[o]:X2}/{expected[(y * stride + x) * 4 + 2]:X2}{expected[(y * stride + x) * 4 + 1]:X2}{expected[(y * stride + x) * 4]:X2} ");
                }
            var res = Judge(owned, matched, GX * GY, 0.97);
            return res.Verdict == Verdict.Mismatch ? res with { Misses = misses.ToString().TrimEnd() } : res;
        }

        /// <summary>The flat colour [rgb] is what the screen shows over most of the strip
        /// at [screenX],[screenY] ([w] x [h]): at least half of a 48 x 3 grid reads it
        /// within 3 levels. Text, icons and buttons on the strip are allowed for by the
        /// half. [notRgb], the colour the strip had before, is counted for the record:
        /// before the new frame is presented most points read it and few read [rgb].</summary>
        internal static Result StripShows(IntPtr root, int screenX, int screenY, int w, int h, int rgb, int notRgb)
        {
            const int GX = 48, GY = 3;
            if (w < 8 || h < 3) return new Result(Verdict.NotOnScreen, 0, 0, 0);
            var shot = Grab(screenX, screenY, w, h);
            int owned = 0, matched = 0, old = 0;
            for (int j = 0; j < GY; j++)
                for (int i = 0; i < GX; i++)
                {
                    int x = (int)((i + 0.5) * w / GX), y = (int)((j + 0.5) * h / GY);
                    if (!Owned(root, screenX + x, screenY + y)) continue;
                    owned++;
                    int o = (y * w + x) * 4;
                    if (Near(shot, o, rgb, 3)) matched++;
                    else if (Near(shot, o, notRgb, 2)) old++;
                }
            var r = Judge(owned, matched, GX * GY, 0.5);
            return r with { Old = old };
        }

        private static int _dumps;

        /// <summary>Trace only: writes what the screen showed and what was expected at the
        /// host's place (BMP files in SwitchTrace.DumpDir, at most 6 per process), so a
        /// mismatch can be looked at.</summary>
        internal static void DumpMismatch(int x, int y, int w, int h, byte[] expected, int stride)
        {
            var dir = SwitchTrace.DumpDir;
            if (string.IsNullOrEmpty(dir) || w < 4 || h < 4 || System.Threading.Interlocked.Increment(ref _dumps) > 6) return;
            try
            {
                var shot = Grab(x, y, w, h);
                var exp = new byte[w * h * 4];
                for (int r = 0; r < h; r++) Buffer.BlockCopy(expected, r * stride * 4, exp, r * w * 4, w * 4);
                var tag = System.Diagnostics.Stopwatch.GetTimestamp().ToString();
                SaveBmp(System.IO.Path.Combine(dir, $"handoff-mismatch-{tag}-screen.bmp"), shot, w, h);
                SaveBmp(System.IO.Path.Combine(dir, $"handoff-mismatch-{tag}-expected.bmp"), exp, w, h);
                SwitchTrace.Mark("presented.dump", tag);
            }
            catch (Exception ex) { SwitchTrace.Mark("presented.dump-failed", ex.GetType().Name); }
        }

        private static void SaveBmp(string path, byte[] bits, int w, int h)
        {
            using var f = new System.IO.BinaryWriter(System.IO.File.Create(path));
            f.Write((ushort)0x4D42); f.Write(54 + bits.Length); f.Write(0); f.Write(54);
            f.Write(40); f.Write(w); f.Write(-h); f.Write((ushort)1); f.Write((ushort)32); f.Write(0); f.Write(bits.Length); f.Write(2835); f.Write(2835); f.Write(0); f.Write(0);
            f.Write(bits);
        }

        private static bool Near(byte[] b, int o, int rgb, int tol) =>
            Math.Abs(b[o + 2] - ((rgb >> 16) & 0xFF)) <= tol && Math.Abs(b[o + 1] - ((rgb >> 8) & 0xFF)) <= tol && Math.Abs(b[o] - (rgb & 0xFF)) <= tol;

        private static Result Judge(int owned, int matched, int total, double need)
        {
            if (owned < total / 4) return new Result(Verdict.NotOnScreen, owned, matched, total);
            return new Result(matched >= Math.Ceiling(owned * need) ? Verdict.Match : Verdict.Mismatch, owned, matched, total);
        }

        private static bool Owned(IntPtr root, int x, int y)
        {
            var h = WindowFromPoint(new POINT { X = x, Y = y });
            return h != IntPtr.Zero && GetAncestor(h, 2 /* GA_ROOT */) == root;
        }

        /// <summary>w x h screen pixels at x,y as top-down BGRA (the composed desktop).</summary>
        internal static byte[] Grab(int x, int y, int w, int h)
        {
            IntPtr scr = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(scr), bmp = CreateCompatibleBitmap(scr, w, h), old = SelectObject(mem, bmp);
            try
            {
                BitBlt(mem, 0, 0, w, h, scr, x, y, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */);
                SelectObject(mem, old);
                var bi = new BIH { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                var bits = new byte[w * h * 4];
                GetDIBits(mem, bmp, 0, (uint)h, bits, ref bi, 0);
                return bits;
            }
            finally { DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, scr); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct BIH { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biX, biY; public uint biClrUsed, biClrImportant; }
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BIH bi, uint usage);
    }
}
