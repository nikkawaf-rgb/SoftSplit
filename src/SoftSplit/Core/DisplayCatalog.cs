using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SoftSplit.Core
{
    /// <summary>A display mode offered by a monitor.</summary>
    public sealed class DisplayModeInfo : IEquatable<DisplayModeInfo>
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int RefreshRate { get; private set; }

        public DisplayModeInfo(int width, int height, int refreshRate)
        {
            Width = width;
            Height = height;
            RefreshRate = refreshRate;
        }

        public override string ToString()
        {
            return string.Format("{0} × {1} · {2} Гц", Width, Height, RefreshRate);
        }

        public bool Equals(DisplayModeInfo other)
        {
            if (other == null) return false;
            return Width == other.Width && Height == other.Height && RefreshRate == other.RefreshRate;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as DisplayModeInfo);
        }

        public override int GetHashCode()
        {
            return (Width * 397) ^ (Height * 31) ^ RefreshRate;
        }
    }

    /// <summary>One connected display, as Windows reports it.</summary>
    public sealed class DisplayInfo
    {
        /// <summary>Stable identifier from the EDID/connector path; used in the config file.</summary>
        public string DevicePath { get; set; }
        /// <summary>GDI name such as \\.\DISPLAY2.</summary>
        public string GdiName { get; set; }
        /// <summary>Friendly name from the monitor EDID.</summary>
        public string FriendlyName { get; set; }
        /// <summary>Physical pixel bounds on the virtual desktop.</summary>
        public Rectangle Bounds { get; set; }
        public bool IsPrimary { get; set; }
        public uint Dpi { get; set; }
        public DisplayModeInfo CurrentMode { get; set; }

        public string Caption
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(FriendlyName) ? GdiName : FriendlyName;
                return name + " · " + Bounds.Width + " × " + Bounds.Height;
            }
        }

        public string Detail
        {
            get
            {
                var parts = new List<string> { GdiName };
                if (IsPrimary) parts.Add("основной");
                if (Dpi > 0) parts.Add(Math.Round(Dpi / 96.0 * 100) + "% масштаб");
                return string.Join(" · ", parts);
            }
        }

        public override string ToString()
        {
            return Caption;
        }
    }

    /// <summary>Reads the display list and their modes through the CCD API.</summary>
    public static class DisplayCatalog
    {
        public static List<DisplayInfo> GetDisplays()
        {
            var result = new List<DisplayInfo>();
            var screens = Screen.AllScreens;
            var byGdi = new Dictionary<string, Screen>(StringComparer.OrdinalIgnoreCase);
            foreach (var screen in screens)
            {
                if (!byGdi.ContainsKey(screen.DeviceName)) byGdi[screen.DeviceName] = screen;
            }

            uint numPath = 0;
            uint numMode = 0;
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out numPath, out numMode) != 0)
                return result;

            var paths = new Native.PATH_INFO[numPath];
            var modes = new Native.MODE_INFO64[numMode];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref numPath, paths,
                    ref numMode, modes, IntPtr.Zero) != 0)
                return result;

            for (var i = 0; i < numPath; i++)
            {
                var path = paths[i];

                var sourceName = new Native.SOURCE_DEVICE_NAME
                {
                    type = Native.GET_SOURCE_NAME,
                    size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.SOURCE_DEVICE_NAME)),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id
                };

                var targetName = new Native.TARGET_DEVICE_NAME
                {
                    type = Native.GET_TARGET_NAME,
                    size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.TARGET_DEVICE_NAME)),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id
                };

                var gdi = Native.GetSourceDeviceName(ref sourceName) == 0 ? sourceName.gdiDeviceName : null;
                if (string.IsNullOrEmpty(gdi)) continue;

                var friendly = Native.GetTargetDeviceName(ref targetName) == 0
                    ? targetName.monitorFriendlyDeviceName
                    : null;
                var devicePath = targetName.monitorDevicePath;

                Screen screen;
                if (!byGdi.TryGetValue(gdi, out screen)) continue;

                var display = new DisplayInfo
                {
                    GdiName = gdi,
                    FriendlyName = string.IsNullOrWhiteSpace(friendly) ? gdi : friendly.Trim(),
                    Bounds = screen.Bounds,
                    IsPrimary = screen.Primary,
                    DevicePath = string.IsNullOrWhiteSpace(devicePath)
                        ? MakeFallbackId(friendly, gdi)
                        : devicePath,
                    CurrentMode = ReadCurrentMode(gdi),
                    Dpi = ReadDpi(screen.Bounds)
                };

                result.Add(display);
            }

            result.Sort((a, b) =>
            {
                var byX = a.Bounds.X.CompareTo(b.Bounds.X);
                return byX != 0 ? byX : a.Bounds.Y.CompareTo(b.Bounds.Y);
            });

            return result;
        }

        public static List<DisplayModeInfo> GetModes(DisplayInfo display)
        {
            var modes = new List<DisplayModeInfo>();
            if (display == null) return modes;

            var devMode = new Native.DEVMODE();
            devMode.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.DEVMODE));

            for (uint i = 0; Native.EnumDisplaySettings(display.GdiName, i, ref devMode); i++)
            {
                if (devMode.dmPelsWidth <= 0 || devMode.dmPelsHeight <= 0) continue;
                modes.Add(new DisplayModeInfo(devMode.dmPelsWidth, devMode.dmPelsHeight,
                    devMode.dmDisplayFrequency));
            }

            var unique = new HashSet<DisplayModeInfo>(modes);
            var ordered = unique
                .OrderByDescending(m => m.Width * (long)m.Height)
                .ThenByDescending(m => m.Width)
                .ThenByDescending(m => m.RefreshRate)
                .ToList();
            return ordered;
        }

        public static DisplayInfo Find(IEnumerable<DisplayInfo> displays, string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;
            return displays.FirstOrDefault(d =>
                string.Equals(d.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));
        }

        private static DisplayModeInfo ReadCurrentMode(string gdiName)
        {
            var devMode = new Native.DEVMODE();
            devMode.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.DEVMODE));
            if (!Native.EnumDisplaySettings(gdiName, Native.ENUM_CURRENT_SETTINGS, ref devMode)) return null;
            return new DisplayModeInfo(devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency);
        }

        private static uint ReadDpi(Rectangle bounds)
        {
            try
            {
                var point = new Native.POINT
                {
                    X = bounds.X + bounds.Width / 2,
                    Y = bounds.Y + bounds.Height / 2
                };
                var monitor = Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTOPRIMARY);
                uint dpiX;
                uint dpiY;
                if (Native.GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0) return dpiX;
            }
            catch (DllNotFoundException)
            {
                // Very old Windows without shcore: fall back to 100%.
            }
            catch (EntryPointNotFoundException)
            {
            }
            return 96;
        }

        private static string MakeFallbackId(string friendly, string gdi)
        {
            return "gdi:" + (string.IsNullOrEmpty(gdi) ? friendly : gdi);
        }
    }
}
