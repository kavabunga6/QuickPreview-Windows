using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace QuickPreview;

internal static class ShellThumbnailService
{
    public static BitmapSource? TryGetThumbnail(string path, int size, bool thumbnailOnly = false)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out factory);
            var flags = ShellItemImageFlags.BiggerSizeOk;
            if (thumbnailOnly)
                flags |= ShellItemImageFlags.ThumbnailOnly;
            factory.GetImage(new NativeSize(size, size), flags, out bitmapHandle);
            if (bitmapHandle == IntPtr.Zero)
                return null;

            var bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
                DeleteObject(bitmapHandle);
            if (factory is not null && Marshal.IsComObject(factory))
                Marshal.FinalReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public readonly int Width;
        public readonly int Height;
        public NativeSize(int width, int height) => (Width, Height) = (width, height);
    }

    [Flags]
    private enum ShellItemImageFlags
    {
        BiggerSizeOk = 0x1,
        ThumbnailOnly = 0x8
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, ShellItemImageFlags flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
