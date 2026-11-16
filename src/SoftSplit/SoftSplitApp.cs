using System;
using System.Collections.Generic;
using System.Linq;
using SoftSplit.Core;

namespace SoftSplit
{
    /// <summary>
    /// Coordinates the persisted settings, the display list and the mirror engine.
    /// The UI talks to this class and never to the engine directly.
    /// </summary>
    public sealed class SoftSplitApp : IDisposable
    {
        private bool _disposed;

        public SoftSplitApp()
        {
            Config = ConfigStore.Load();
            Engine = new MirrorEngine
            {
                Fps = Config.Fps,
                ShowCursor = Config.ShowCursor,
                UseGpu = Config.UseGpu
            };
            Engine.Stopped += (sender, args) => RaiseStateChanged();
            RefreshDisplays();
        }

        public AppConfig Config { get; private set; }

        public MirrorEngine Engine { get; private set; }

        public List<DisplayInfo> Displays { get; private set; }

        public bool IsMirroring
        {
            get { return Engine.IsRunning; }
        }

        public event EventHandler StateChanged;

        public void RefreshDisplays()
        {
            Displays = DisplayCatalog.GetDisplays();
        }

        public DisplaySetting SettingFor(DisplayInfo display)
        {
            return display == null ? null : ConfigStore.FindDisplay(Config, display.DevicePath);
        }

        public DisplaySetting EnsureSetting(DisplayInfo display)
        {
            var setting = SettingFor(display);
            if (setting != null) return setting;

            setting = new DisplaySetting
            {
                DevicePath = display.DevicePath,
                SourcePath = null
            };
            setting.SetRole(DisplayRole.Extend);
            setting.SetScale(ScaleMode.Fit);

            var list = Config.Displays.ToList();
            list.Add(setting);
            Config.Displays = list.ToArray();
            return setting;
        }

        public List<MirrorMapping> BuildMappings()
        {
            var mappings = new List<MirrorMapping>();
            foreach (var target in Displays)
            {
                var setting = SettingFor(target);
                if (setting == null || setting.GetRole() != DisplayRole.Duplicate) continue;

                var source = DisplayCatalog.Find(Displays, setting.SourcePath);
                if (source == null) continue;
                if (string.Equals(source.DevicePath, target.DevicePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                mappings.Add(new MirrorMapping
                {
                    Source = source,
                    Target = target,
                    Scale = setting.GetScale()
                });
            }
            return mappings;
        }

        /// <summary>Applies the resolution chosen for each display, if it differs from the current one.</summary>
        public bool TryApplyModes(out string error)
        {
            error = null;
            foreach (var display in Displays)
            {
                var setting = SettingFor(display);
                if (setting == null || setting.ModeWidth == null || setting.ModeHeight == null) continue;

                var current = display.CurrentMode;
                if (current != null &&
                    current.Width == setting.ModeWidth.Value &&
                    current.Height == setting.ModeHeight.Value &&
                    (setting.ModeRefresh == null || current.RefreshRate == setting.ModeRefresh.Value))
                    continue;

                var mode = new DisplayModeInfo(setting.ModeWidth.Value, setting.ModeHeight.Value,
                    setting.ModeRefresh ?? (current != null ? current.RefreshRate : 60));

                string modeError;
                if (!TopologyService.TrySetMode(display, mode, out modeError))
                {
                    error = display.Caption + ": " + modeError;
                    return false;
                }
            }

            RefreshDisplays();
            return true;
        }

        public bool StartMirroring(out string error)
        {
            error = null;
            if (!TryApplyModes(out error)) return false;

            var mappings = BuildMappings();
            if (mappings.Count == 0)
            {
                error = "Не выбран ни один дисплей для дублирования.";
                return false;
            }

            try
            {
                Engine.Fps = Config.Fps;
                Engine.ShowCursor = Config.ShowCursor;
                Engine.UseGpu = Config.UseGpu;
                Engine.Apply(mappings);
                Engine.Start();
            }
            catch (Exception ex)
            {
                error = "Не удалось запустить дублирование: " + ex.Message;
                return false;
            }

            RaiseStateChanged();
            return true;
        }

        public void StopMirroring()
        {
            Engine.Stop();
            RaiseStateChanged();
        }

        public bool ToggleMirroring(out string error)
        {
            error = null;
            if (IsMirroring)
            {
                StopMirroring();
                return true;
            }
            return StartMirroring(out error);
        }

        public bool TrySave(out string error)
        {
            error = null;
            Config.Fps = Math.Max(5, Math.Min(60, Config.Fps));
            return ConfigStore.TrySave(Config, out error);
        }

        public void RaiseStateChanged()
        {
            var handler = StateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>Called when Windows reports a display change.</summary>
        public void OnDisplayChanged()
        {
            var wasRunning = Engine.IsRunning;
            if (wasRunning) Engine.Stop();

            RefreshDisplays();

            if (wasRunning)
            {
                var mappings = BuildMappings();
                if (mappings.Count == 0)
                {
                    StopMirroring();
                }
                else
                {
                    string error;
                    StartMirroring(out error);
                }
            }

            RaiseStateChanged();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Engine.Dispose();
        }
    }
}
