using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SoftSplit.Core
{
    /// <summary>Per-user autostart through HKCU\...\Run (no admin rights required).</summary>
    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SoftSplit";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    var value = key.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TrySet(bool enabled, out string error)
        {
            error = null;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ??
                                 Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                    {
                        error = "Нет доступа к ветке автозапуска в реестре.";
                        return false;
                    }

                    if (enabled)
                    {
                        var path = Application.ExecutablePath;
                        key.SetValue(ValueName, "\"" + path + "\" --minimized", RegistryValueKind.String);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Не удалось изменить автозапуск: " + ex.Message;
                return false;
            }
        }

        public static string CommandValue
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    {
                        if (key == null) return null;
                        return key.GetValue(ValueName) as string;
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }
}
