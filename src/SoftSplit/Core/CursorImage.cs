using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SoftSplit.Core
{
    /// <summary>
    /// Renders the current mouse cursor into an ARGB bitmap. The cursor is drawn twice,
    /// over black and over white; the two results differ only by the background that
    /// shows through, which gives the per-pixel alpha. This works for both classic
    /// monochrome cursors and modern 32-bit ones.
    /// </summary>
    internal static class CursorImage
    {
        public const int Size = 128;

        public static byte[] Capture(out int hotspotX, out int hotspotY)
        {
            hotspotX = 0;
            hotspotY = 0;

            var info = new Native.CURSORINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
            if (!Native.GetCursorInfo(ref info)) return null;
            if ((info.flags & Native.CURSOR_SHOWING) == 0) return null;
            if (info.hCursor == IntPtr.Zero) return null;

            var icon = new Native.ICONINFO();
            if (!Native.GetIconInfo(info.hCursor, ref icon)) return null;
            hotspotX = icon.xHotspot;
            hotspotY = icon.yHotspot;

            try
            {
                using (var overBlack = Draw(info.hCursor, Color.Black))
                using (var overWhite = Draw(info.hCursor, Color.White))
                {
                    if (overBlack == null || overWhite == null) return null;
                    return Combine(overBlack, overWhite);
                }
            }
            finally
            {
                if (icon.hbmMask != IntPtr.Zero) Native.DeleteObject(icon.hbmMask);
                if (icon.hbmColor != IntPtr.Zero) Native.DeleteObject(icon.hbmColor);
            }
        }

        private static Bitmap Draw(IntPtr cursor, Color background)
        {
            var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(background);
                var hdc = graphics.GetHdc();
                try
                {
                    Native.DrawIconEx(hdc, 0, 0, cursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            return bitmap;
        }

        private static byte[] Combine(Bitmap black, Bitmap white)
        {
            var stride = Size * 4;
            var result = new byte[stride * Size];
            var blackData = black.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            var whiteData = white.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                var blackBuffer = new byte[stride * Size];
                var whiteBuffer = new byte[stride * Size];
                Marshal.Copy(blackData.Scan0, blackBuffer, 0, blackBuffer.Length);
                Marshal.Copy(whiteData.Scan0, whiteBuffer, 0, whiteBuffer.Length);

                for (var i = 0; i < result.Length; i += 4)
                {
                    // BGRA byte order on both sides.
                    var dr = whiteBuffer[i + 2] - blackBuffer[i + 2];
                    var dg = whiteBuffer[i + 1] - blackBuffer[i + 1];
                    var db = whiteBuffer[i] - blackBuffer[i];

                    var alpha = 255 - (dr + dg + db) / 3;
                    if (alpha < 0) alpha = 0;
                    if (alpha > 255) alpha = 255;

                    if (alpha == 0) continue;

                    result[i] = Scale(blackBuffer[i], alpha);
                    result[i + 1] = Scale(blackBuffer[i + 1], alpha);
                    result[i + 2] = Scale(blackBuffer[i + 2], alpha);
                    result[i + 3] = (byte)alpha;
                }
            }
            finally
            {
                black.UnlockBits(blackData);
                white.UnlockBits(whiteData);
            }

            return result;
        }

        private static byte Scale(byte composited, int alpha)
        {
            var value = composited * 255 / alpha;
            return value > 255 ? (byte)255 : (byte)value;
        }
    }
}
