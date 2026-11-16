using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using SoftSplit.Core;
using SoftSplit.Ui;

namespace SoftSplit
{
    /// <summary>Owns the tray icon, the settings window and the application lifetime.</summary>
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly SoftSplitApp _app;
        private readonly SettingsForm _settings;
        private readonly TrayIcon _tray;
        private readonly EventWaitHandle _showEvent;
        private readonly Thread _showThread;
        private readonly Action<string> _log;

        private volatile bool _exiting;

        public TrayApplicationContext(bool startMinimized, bool forceGdi, Action<string> log)
        {
            _log = log ?? (text => { });

            _app = new SoftSplitApp();
            if (forceGdi) _app.Config.UseGpu = false;
            _app.Engine.Log = log;
            MirrorWindow.DiagnosticLog = log;
            _settings = new SettingsForm(_app);
            _tray = new TrayIcon(_app, ShowSettings, Exit);

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            var eventName = @"Local\SoftSplit.ShowSettings." + Environment.UserName;
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
                _showThread = new Thread(ShowSignalLoop) { IsBackground = true, Name = "SoftSplit ipc" };
                _showThread.Start();
            }
            catch (Exception ex)
            {
                _log("Single-instance signalling unavailable: " + ex.Message);
            }

            if (!startMinimized)
            {
                ShowSettings();
            }
            else if (_app.Config.StartMirroringOnLaunch)
            {
                StartMirroringQuietly();
            }
        }

        private void ShowSignalLoop()
        {
            while (!_exiting)
            {
                try
                {
                    if (!_showEvent.WaitOne(500)) continue;
                    if (_exiting) return;
                    var form = _settings;
                    if (form != null && form.IsHandleCreated)
                        form.BeginInvoke(new Action(ShowSettings));
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        private void StartMirroringQuietly()
        {
            string error;
            if (!_app.StartMirroring(out error) && !string.IsNullOrEmpty(error))
            {
                _log("Start failed: " + error);
            }
        }

        public void ShowSettings()
        {
            if (_exiting) return;
            _settings.Show();
            if (_settings.WindowState == FormWindowState.Minimized)
                _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (_exiting) return;
            try
            {
                if (_settings.IsHandleCreated)
                {
                    _settings.BeginInvoke(new Action(() =>
                    {
                        _app.OnDisplayChanged();
                        _tray.UpdateState();
                    }));
                }
            }
            catch (Exception ex)
            {
                _log("Display change handling failed: " + ex.Message);
            }
        }

        public void Exit()
        {
            if (_exiting) return;
            _exiting = true;

            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

            try
            {
                _app.StopMirroring();
            }
            catch (Exception)
            {
            }

            string error;
            _app.TrySave(out error);

            _settings.CloseForExit();
            _tray.Dispose();
            _app.Dispose();

            ExitThread();
        }
    }
}
