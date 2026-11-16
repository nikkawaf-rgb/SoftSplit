using System;
using System.Runtime.InteropServices;

namespace SoftSplit.Core
{
    /// <summary>Applies display mode changes and the emergency "extend" layout.</summary>
    public static class TopologyService
    {
        /// <summary>Changes the resolution/refresh rate of one display.</summary>
        public static bool TrySetMode(DisplayInfo display, DisplayModeInfo mode, out string error)
        {
            error = null;
            if (display == null)
            {
                error = "Дисплей не найден.";
                return false;
            }

            var devMode = new Native.DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
            if (!Native.EnumDisplaySettings(display.GdiName, Native.ENUM_CURRENT_SETTINGS, ref devMode))
            {
                error = "Не удалось прочитать текущий режим дисплея.";
                return false;
            }

            devMode.dmPelsWidth = mode.Width;
            devMode.dmPelsHeight = mode.Height;
            devMode.dmDisplayFrequency = mode.RefreshRate;
            devMode.dmFields |= Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY;

            // Test first: a rejected mode must not leave the desktop half-changed.
            var testResult = Native.ChangeDisplaySettingsEx(display.GdiName, ref devMode, IntPtr.Zero,
                Native.CDS_TEST, IntPtr.Zero);
            if (testResult != Native.DISP_CHANGE_SUCCESSFUL)
            {
                error = "Этот режим недоступен для дисплея (" + Describe(testResult) + ").";
                return false;
            }

            var result = Native.ChangeDisplaySettingsEx(display.GdiName, ref devMode, IntPtr.Zero,
                Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (result != Native.DISP_CHANGE_SUCCESSFUL)
            {
                error = "Windows отказалась применять режим (" + Describe(result) + ").";
                return false;
            }

            return true;
        }

        /// <summary>Applies the standard "extend" topology to every display.</summary>
        public static bool TryRestoreExtend(out string error)
        {
            error = null;
            var result = Native.SetDisplayConfig(0, null, 0, null, Native.SDC_TOPOLOGY_EXTEND | Native.SDC_APPLY);
            if (result != 0)
            {
                error = "Не удалось вернуть раскладку «Расширить» (код " + result + ").";
                return false;
            }
            return true;
        }

        private static string Describe(int code)
        {
            switch (code)
            {
                case -1: return "неверные параметры";
                case -2: return "неподдерживаемый режим";
                case -3: return "нет доступа к реестру";
                case -4: return "изменение не удалось";
                case -6: return "плохая комбинация режимов";
                default: return "код " + code;
            }
        }
    }
}
