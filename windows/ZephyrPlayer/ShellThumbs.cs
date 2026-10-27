using System;
using System.Runtime.InteropServices;
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

        /// <summary>The shell's real thumbnail for a file, or null (no thumbnail
        /// handler / not Windows / any failure). Call off the UI thread.</summary>
        public static BitmapSource? Get(string path, int size)
        {
            if (!OperatingSystem.IsWindows()) return null;
            IShellItemImageFactory? factory = null;
            IntPtr hbitmap = IntPtr.Zero;
            try
            {
                var hr = SHCreateItemFromParsingName(path, IntPtr.Zero,
                    new Guid("bcc18b79-ba16-442f-80c4-8a59c393c463"), out factory);
                if (hr != 0 || factory == null) return null;
                factory.GetImage(new SIZE(size, size), SIIGBF_RESIZETOFIT | SIIGBF_THUMBNAILONLY, out hbitmap);
                if (hbitmap == IntPtr.Zero) return null;
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally
            {
                if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }
    }
}
