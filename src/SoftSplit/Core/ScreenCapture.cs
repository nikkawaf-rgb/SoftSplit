using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SoftSplit.Core
{
    /// <summary>
    /// Grabs a display into an off-screen GDI surface and blits it into the mirror
    /// windows. Two buffers are used so the frame being painted is never overwritten
    /// by the capture thread.
    /// </summary>
    internal sealed class ScreenCapture : IDisposable
    {
        private const uint BitBltFlags = Native.SRCCOPY;

        /// <summary>
        /// When enabled, layered windows (some tooltips and overlays) are included.
        /// CAPTUREBLT forces a full re-composite and noticeably raises the CPU cost,
        /// so it is off by default.
        /// </summary>
        public static bool IncludeLayeredWindows { get; set; }

        private readonly int _width;
        private readonly int _height;
        private readonly IntPtr _screenDc;
        private readonly IntPtr[] _bufferDc = new IntPtr[2];
        private readonly IntPtr[] _dib = new IntPtr[2];

        private int _nextIndex;
        private volatile int _publishedIndex = -1;
        private bool _disposed;

        public ScreenCapture(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("width");

            _width = width;
            _height = height;

            _screenDc = Native.GetDC(IntPtr.Zero);

            var header = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                biWidth = width,
                biHeight = -height, // top-down: row 0 is the top of the screen
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            for (var i = 0; i < 2; i++)
            {
                _bufferDc[i] = Native.CreateCompatibleDC(_screenDc);
                IntPtr bits;
                _dib[i] = Native.CreateDIBSection(_screenDc, ref header, Native.DIB_RGB_COLORS,
                    out bits, IntPtr.Zero, 0);
                if (_dib[i] == IntPtr.Zero || _bufferDc[i] == IntPtr.Zero)
                    throw new InvalidOperationException("Не удалось выделить буфер захвата.");

                // The bitmap stays selected for the whole lifetime of the buffer.
                Native.SelectObject(_bufferDc[i], _dib[i]);
            }
        }

        public int Width
        {
            get { return _width; }
        }

        public int Height
        {
            get { return _height; }
        }

        public bool HasFrame
        {
            get { return _publishedIndex >= 0; }
        }

        /// <summary>Captures <paramref name="source"/> (virtual-screen coordinates).</summary>
        public bool Capture(Rectangle source, bool drawCursor)
        {
            if (_disposed) return false;
            if (source.Width <= 0 || source.Height <= 0) return false;

            var index = _nextIndex;
            _nextIndex = 1 - _nextIndex;

            var target = _bufferDc[index];
            var flags = IncludeLayeredWindows ? BitBltFlags | Native.CAPTUREBLT : BitBltFlags;
            var ok = Native.BitBlt(target, 0, 0, _width, _height,
                _screenDc, source.X, source.Y, flags);

            if (!ok && IncludeLayeredWindows)
            {
                // CAPTUREBLT is refused by some drivers; plain copy still works.
                ok = Native.BitBlt(target, 0, 0, _width, _height,
                    _screenDc, source.X, source.Y, BitBltFlags);
            }

            if (!ok) return false;

            if (drawCursor) DrawCursor(target, source);

            _publishedIndex = index;
            return true;
        }

        /// <summary>Blits the published frame into a window device context.</summary>
        public bool Present(IntPtr destinationDc, Rectangle client, ScaleMode mode)
        {
            var index = _publishedIndex;
            if (index < 0) return false;

            Rectangle destination;
            Rectangle source;
            Placement.Compute(new Size(_width, _height), client, mode, out destination, out source);

            if (destination.Width <= 0 || destination.Height <= 0) return false;

            Native.SetStretchBltMode(destinationDc, Native.HALFTONE);
            Native.SetBrushOrgEx(destinationDc, 0, 0, IntPtr.Zero);

            return Native.StretchBlt(destinationDc, destination.X, destination.Y,
                destination.Width, destination.Height,
                _bufferDc[index], source.X, source.Y, source.Width, source.Height,
                Native.SRCCOPY);
        }

        private void DrawCursor(IntPtr targetDc, Rectangle source)
        {
            var info = new Native.CURSORINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
            if (!Native.GetCursorInfo(ref info)) return;
            if ((info.flags & Native.CURSOR_SHOWING) == 0) return;
            if ((info.flags & Native.CURSOR_SUPPRESSED) != 0) return;
            if (info.hCursor == IntPtr.Zero) return;

            var icon = new Native.ICONINFO();
            if (!Native.GetIconInfo(info.hCursor, ref icon)) return;

            try
            {
                var x = info.ptScreenPos.X - icon.xHotspot - source.X;
                var y = info.ptScreenPos.Y - icon.yHotspot - source.Y;
                if (x < -64 || y < -64 || x > _width + 64 || y > _height + 64) return;
                Native.DrawIconEx(targetDc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
            }
            finally
            {
                if (icon.hbmMask != IntPtr.Zero) Native.DeleteObject(icon.hbmMask);
                if (icon.hbmColor != IntPtr.Zero) Native.DeleteObject(icon.hbmColor);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _publishedIndex = -1;

            for (var i = 0; i < 2; i++)
            {
                if (_bufferDc[i] != IntPtr.Zero) Native.DeleteDC(_bufferDc[i]);
                if (_dib[i] != IntPtr.Zero) Native.DeleteObject(_dib[i]);
                _bufferDc[i] = IntPtr.Zero;
                _dib[i] = IntPtr.Zero;
            }

            if (_screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, _screenDc);
        }
    }
}
