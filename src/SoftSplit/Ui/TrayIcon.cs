using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SoftSplit.Core;

namespace SoftSplit.Ui
{
    /// <summary>Notify icon and its context menu.</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _autostartItem;
        private readonly ToolStripMenuItem _statusItem;
        private readonly SoftSplitApp _app;
        private bool _disposed;

        public TrayIcon(SoftSplitApp app, Action showSettings, Action exit)
        {
            _app = app;

            var menu = new ContextMenuStrip();

            _statusItem = new ToolStripMenuItem("Дублирование выключено") { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());

            _toggleItem = new ToolStripMenuItem("Включить дублирование", null,
                (sender, args) => OnToggle(showSettings));
            menu.Items.Add(_toggleItem);

            var settingsItem = new ToolStripMenuItem("Настройки…", null,
                (sender, args) => showSettings());
            settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
            menu.Items.Add(settingsItem);

            menu.Items.Add(new ToolStripSeparator());

            _autostartItem = new ToolStripMenuItem("Запускать при входе в систему")
            {
                CheckOnClick = true
            };
            _autostartItem.Click += OnAutostartClicked;
            menu.Items.Add(_autostartItem);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("О программе…", null, (sender, args) => ShowAbout()));
            menu.Items.Add(new ToolStripMenuItem("Выход", null, (sender, args) => exit()));

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "SoftSplit",
                ContextMenuStrip = menu,
                Visible = true
            };
            _icon.DoubleClick += (sender, args) => showSettings();

            app.StateChanged += (sender, args) => UpdateState();
            UpdateState();
        }

        private void OnToggle(Action showSettings)
        {
            string error;
            if (_app.ToggleMirroring(out error)) return;
            if (!string.IsNullOrEmpty(error))
            {
                MessageBox.Show(error, "SoftSplit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                showSettings();
            }
        }

        private void OnAutostartClicked(object sender, EventArgs e)
        {
            string error;
            if (!Autostart.TrySet(_autostartItem.Checked, out error))
            {
                MessageBox.Show(error, "SoftSplit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            _app.Config.Autostart = Autostart.IsEnabled();
            string saveError;
            _app.TrySave(out saveError);
        }

        public void UpdateState()
        {
            var running = _app.IsMirroring;
            _statusItem.Text = running
                ? "Дублирование включено: " + _app.Engine.WindowCount + " экран(ов)"
                : "Дублирование выключено";
            _toggleItem.Text = running ? "Выключить дублирование" : "Включить дублирование";
            _autostartItem.Checked = Autostart.IsEnabled();
            _icon.Text = running ? "SoftSplit — дублирование включено" : "SoftSplit";
        }

        private static Icon LoadIcon()
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) return icon;
            }
            catch (Exception)
            {
            }
            return SystemIcons.Application;
        }

        private static void ShowAbout()
        {
            var version = typeof(TrayIcon).Assembly.GetName().Version;
            var text =
                "SoftSplit " + version + "\r\n" +
                "Дублирование и расширение мониторов в Windows.\r\n\r\n" +
                "Бесплатная программа с открытым кодом (MIT).\r\n" +
                "https://github.com/nikkawaf-rgb/SoftSplit";

            MessageBox.Show(text, "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
