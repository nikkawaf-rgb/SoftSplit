using System;
using System.IO;
using System.Web.Script.Serialization;

namespace SoftSplit.Core
{
    /// <summary>Role of a display in the user's layout.</summary>
    public enum DisplayRole
    {
        /// <summary>Normal independent part of the desktop.</summary>
        Extend,
        /// <summary>Shows the content of another display.</summary>
        Duplicate
    }

    /// <summary>Persisted settings of one display. Strings keep the JSON readable and stable.</summary>
    public sealed class DisplaySetting
    {
        public string DevicePath { get; set; }
        public string Role { get; set; }
        public string SourcePath { get; set; }
        public string Scale { get; set; }

        /// <summary>Requested resolution; when null the current mode is kept.</summary>
        public int? ModeWidth { get; set; }
        public int? ModeHeight { get; set; }
        public int? ModeRefresh { get; set; }

        public DisplayRole GetRole()
        {
            return string.Equals(Role, "duplicate", StringComparison.OrdinalIgnoreCase)
                ? DisplayRole.Duplicate
                : DisplayRole.Extend;
        }

        public void SetRole(DisplayRole role)
        {
            Role = role == DisplayRole.Duplicate ? "duplicate" : "extend";
        }

        public ScaleMode GetScale()
        {
            if (string.Equals(Scale, "fill", StringComparison.OrdinalIgnoreCase)) return ScaleMode.Fill;
            if (string.Equals(Scale, "stretch", StringComparison.OrdinalIgnoreCase)) return ScaleMode.Stretch;
            return ScaleMode.Fit;
        }

        public void SetScale(ScaleMode mode)
        {
            Scale = mode == ScaleMode.Fill ? "fill" : mode == ScaleMode.Stretch ? "stretch" : "fit";
        }
    }

    /// <summary>Everything that survives a restart.</summary>
    public sealed class AppConfig
    {
        public int Version { get; set; }
        public int Fps { get; set; }
        public bool ShowCursor { get; set; }
        public bool Autostart { get; set; }
        public bool StartMirroringOnLaunch { get; set; }
        public bool UseGpu { get; set; }
        public DisplaySetting[] Displays { get; set; }

        public AppConfig()
        {
            Version = 1;
            Fps = 30;
            ShowCursor = true;
            Autostart = false;
            StartMirroringOnLaunch = true;
            UseGpu = true;
            Displays = new DisplaySetting[0];
        }
    }

    /// <summary>Reads and writes %APPDATA%\SoftSplit\config.json.</summary>
    public static class ConfigStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SoftSplit");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(DirectoryPath, "config.json"); }
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppConfig();
                var json = File.ReadAllText(FilePath);
                if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
                if (string.IsNullOrWhiteSpace(json)) return new AppConfig();
                var config = Serializer.Deserialize<AppConfig>(json);
                if (config == null) return new AppConfig();
                if (config.Displays == null) config.Displays = new DisplaySetting[0];
                if (config.Fps < 1 || config.Fps > 120) config.Fps = 30;
                return config;
            }
            catch (Exception)
            {
                // A broken config must never prevent the app from starting.
                return new AppConfig();
            }
        }

        public static bool TrySave(AppConfig config, out string error)
        {
            error = null;
            if (config == null) return false;

            try
            {
                var directory = DirectoryPath;
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                var json = Serializer.Serialize(config);
                File.WriteAllText(FilePath, json);
                return true;
            }
            catch (Exception ex)
            {
                error = "Не удалось сохранить настройки: " + ex.Message;
                return false;
            }
        }

        public static DisplaySetting FindDisplay(AppConfig config, string devicePath)
        {
            if (config == null || config.Displays == null || string.IsNullOrEmpty(devicePath)) return null;
            foreach (var display in config.Displays)
            {
                if (display != null &&
                    string.Equals(display.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                    return display;
            }
            return null;
        }
    }
}
