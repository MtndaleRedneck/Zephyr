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

        private const int SIIGBF_RESIZETOFIT = 0x00;
        private const int SIIGBF_THUMBNAILONLY = 0x08;

        // Shell thumbnail handlers expect STA (review should-fix 10): every shell call
        // runs on ONE dedicated STA worker fed by a queue, never a thread-pool MTA thread.
        private static readonly System.Collections.Concurrent.BlockingCollection<Action> _jobs = new();
        private static readonly Thread _worker;

        static ShellThumbs()
        {
            _worker = new Thread(() =>
            {
                foreach (var job in _jobs.GetConsumingEnumerable()) { try { job(); } catch { } }
            })
            { IsBackground = true, Name = "ShellThumbs" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        /// <summary>The shell's real thumbnail for a file, or null (no thumbnail
        /// handler / not Windows / any failure). Call off the UI thread; the call
        /// blocks until the STA worker has produced (or failed) this bitmap.</summary>
        public static BitmapSource? Get(string path, int size)
        {
            if (!OperatingSystem.IsWindows()) return null;
            var done = new System.Threading.ManualResetEventSlim(false);
            BitmapSource? result = null;
            _jobs.Add(() => { try { result = GetOnSta(path, size); } finally { done.Set(); } });
            try { done.Wait(TimeSpan.FromSeconds(30)); } catch { return null; }
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

        /// <summary>HBITMAP -> frozen BitmapSource with alpha intact:
        /// Imaging.CreateBitmapSourceFromHBitmap drops the alpha channel (transparent
        /// thumbnails got black corners), so the raw 32bpp pixels are copied instead.</summary>
        private static BitmapSource? ToBitmapSource(IntPtr hbitmap)
        {
            using var bmp = System.Drawing.Image.FromHbitmap(hbitmap);
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp is System.Drawing.Bitmap b
                ? b.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb)
                : throw new InvalidOperationException();
            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * data.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, bytes, stride);
                src.Freeze();
                return src;
            }
            finally { (bmp as System.Drawing.Bitmap)?.UnlockBits(data); }
        }
    }
}
