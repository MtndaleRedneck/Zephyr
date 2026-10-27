using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ZephyrPlayer
{
    /// <summary>
    /// Real Windows shell thumbnails for the Explorer-style Home grid (the
    /// owner's approved look: movie frames as film-strip thumbnails, poster
    /// peeks inside folder outlines). THUMBNAILONLY on purpose: where the shell
    /// has only an ICON (e.g. another app's file-type icon), we fall back to
    /// our own drawn placeholder - no other app's brand marks in Zephyr.
    /// Every call is Windows-only, never throws, and returns Frozen bitmaps
    /// safe to hand to the UI thread. Headless CI returns null (placeholder).
    /// </summary>
    internal static class ShellThumbs
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c393c463"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; public SIZE(int x, int y) { cx = x; cy = y; } }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory factory);

        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr hObject, int nSize, out BITMAP lpObject);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP { public int bmType; public int bmWidth; public int bmHeight; public int bmWidthBytes; public short bmPlanes; public short bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes;
            public ushort biBitCount; public uint biCompression; public uint biSizeImage;
            public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
        }

        private const int SIIGBF_RESIZETOFIT = 0x00;
        private const int SIIGBF_THUMBNAILONLY = 0x08;

        // Shell thumbnail handlers expect STA (review should-fix 10): every shell call
        // runs on ONE dedicated STA worker fed by a queue, never a thread-pool MTA thread.
        private static readonly System.Collections.Concurrent.BlockingCollection<Action> _jobs = new();
        private static readonly object _workerSync = new();
        private static Thread _worker = null!;
        // Bumped when a shell call hangs past the timeout: queued jobs from before the
        // hang skip themselves, and a FRESH worker takes over, so one bad thumbnail
        // handler (network file, bad codec pack) can no longer stall every later
        // thumbnail until restart (round-3 P3).
        private static long _generation;

        static ShellThumbs() { StartWorker(); }

        private static void StartWorker()
        {
            var t = new Thread(() =>
            {
                foreach (var job in _jobs.GetConsumingEnumerable()) { try { job(); } catch { } }
            })
            { IsBackground = true, Name = "ShellThumbs" };
            t.SetApartmentState(ApartmentState.STA);
            lock (_workerSync) _worker = t;
            t.Start();
        }

        /// <summary>The shell's real thumbnail for a file, or null (no thumbnail
        /// handler / not Windows / any failure). Call off the UI thread; the call
        /// blocks until the STA worker has produced (or failed) this bitmap.</summary>
        public static BitmapSource? Get(string path, int size)
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var done = new System.Threading.ManualResetEventSlim(false);
            BitmapSource? result = null;
            var gen = Interlocked.Read(ref _generation);
            _jobs.Add(() =>
            {
                if (gen != Interlocked.Read(ref _generation)) return; // caller gave up
                try { result = GetOnSta(path, size); } catch { } finally { try { done.Set(); } catch { } }
            });
            try
            {
                if (!done.Wait(TimeSpan.FromSeconds(30)))
                {
                    // The worker is stuck inside a shell call that may never return.
                    // Retire it (background thread; a hung COM call cannot be killed)
                    // and continue on a fresh one.
                    Interlocked.Increment(ref _generation);
                    StartWorker();
                    return null;
                }
            }
            catch { return null; }
            return result;
        }

        private static BitmapSource? GetOnSta(string path, int size)
        {
            IShellItemImageFactory? factory = null;
            IntPtr hbitmap = IntPtr.Zero;
            try
            {
                var hr = SHCreateItemFromParsingName(path, IntPtr.Zero,
                    new Guid("bcc18b79-ba16-442f-80c4-8a59c393c463"), out factory);
                if (hr != 0 || factory == null) return null;
                factory.GetImage(new SIZE(size, size), SIIGBF_RESIZETOFIT | SIIGBF_THUMBNAILONLY, out hbitmap);
                if (hbitmap == IntPtr.Zero) return null;
                return ToBitmapSource(hbitmap);
            }
            catch { return null; }
            finally
            {
                if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        /// <summary>HBITMAP -> frozen BitmapSource with alpha intact. The old path
        /// (System.Drawing.Image.FromHbitmap + LockBits) silently produced 32bppRgb
        /// with alpha 255 everywhere, so transparent thumbnails still got black
        /// corners (round-3 P2). Reading the DIB bits directly keeps the real alpha;
        /// shell bitmaps are premultiplied, so the result is Pbgra32. This also
        /// removes the last System.Drawing use from the Player.</summary>
        private static BitmapSource? ToBitmapSource(IntPtr hbitmap)
        {
            if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
            var w = bm.bmWidth; var h = bm.bmHeight;
            if (w <= 0 || h <= 0) return null;
            var hdc = CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;
            try
            {
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down: no row flip
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB: alpha bytes preserved as stored
                };
                var bytes = new byte[w * h * 4];
                if (GetDIBits(hdc, hbitmap, 0, (uint)h, bytes, ref bmi, 0) == 0) return null;
                var src = BitmapSource.Create(w, h, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32, null, bytes, w * 4);
                src.Freeze();
                return src;
            }
            finally { DeleteDC(hdc); }
        }
    }
}
