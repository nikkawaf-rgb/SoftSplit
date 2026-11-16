using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SoftSplit.Core;

namespace SoftSplit.Ui
{
    /// <summary>Main window: one row per display with its role and resolution.</summary>
    internal sealed class SettingsForm : Form
    {
        private static readonly Color Accent = Color.FromArgb(37, 99, 235);
        private static readonly Color Muted = Color.FromArgb(100, 116, 139);
        private static readonly Color HeaderBack = Color.FromArgb(248, 250, 252);
        private static readonly Color BorderColor = Color.FromArgb(226, 232, 240);

        private readonly SoftSplitApp _app;
        private readonly TableLayoutPanel _table;
        private readonly Label _statusLabel;
        private readonly NumericUpDown _fpsBox;
        private readonly CheckBox _cursorBox;
        private readonly CheckBox _autostartBox;
        private readonly CheckBox _startWithBox;
        private readonly ComboBox _modeBox;
        private readonly Button _applyButton;
        private readonly Button _stopButton;
        private readonly List<RowControls> _rows = new List<RowControls>();

        private bool _suppressEvents;
        private bool _allowClose;

        private sealed class RowControls
        {
            public DisplayInfo Display;
            public ComboBox Role;
            public ComboBox Source;
            public ComboBox Mode;
            public ComboBox Scale;
        }

        public SettingsForm(SoftSplitApp app)
        {
            _app = app;

            Text = "SoftSplit — настройки";
            Icon = LoadAppIcon();
            ClientSize = new Size(1000, 640);
            MinimumSize = new Size(860, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            DoubleBuffered = true;

            var header = BuildHeader();

            _table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 5,
                Padding = new Padding(20, 6, 20, 6),
                BackColor = Color.White
            };
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28f));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14f));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21f));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17f));
            AddHeaderRow();

            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.White,
                Padding = new Padding(0)
            };
            scroll.Controls.Add(_table);

            // Docking is processed from the last control to the first, so the Fill
            // panel must be added before the top and bottom panels.
            Controls.Add(scroll);
            Controls.Add(header);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 150, BackColor = HeaderBack };

            _fpsBox = new NumericUpDown
            {
                Minimum = 5,
                Maximum = 60,
                Increment = 5,
                Value = Math.Max(5, Math.Min(60, app.Config.Fps)),
                Width = 64,
                Location = new Point(128, 18)
            };

            var fpsLabel = new Label
            {
                Text = "Частота кадров:",
                AutoSize = true,
                Location = new Point(20, 21),
                ForeColor = Color.FromArgb(51, 65, 85)
            };

            _cursorBox = new CheckBox
            {
                Text = "Показывать курсор",
                AutoSize = true,
                Checked = app.Config.ShowCursor,
                Location = new Point(220, 19),
                ForeColor = Color.FromArgb(51, 65, 85)
            };

            _autostartBox = new CheckBox
            {
                Text = "Запускать при входе в систему",
                AutoSize = true,
                Checked = Autostart.IsEnabled(),
                Location = new Point(400, 19),
                ForeColor = Color.FromArgb(51, 65, 85)
            };

            _startWithBox = new CheckBox
            {
                Text = "Начинать дублирование сразу",
                AutoSize = true,
                Checked = app.Config.StartMirroringOnLaunch,
                Location = new Point(660, 19),
                ForeColor = Color.FromArgb(51, 65, 85)
            };

            _applyButton = MakeButton("Применить", Accent, Color.White, new Point(20, 58), 132);
            _applyButton.Click += (sender, args) => ApplyLayout();

            _stopButton = MakeButton("Остановить", Color.White, Color.FromArgb(51, 65, 85), new Point(162, 58), 122);
            _stopButton.FlatAppearance.BorderColor = BorderColor;
            _stopButton.Click += (sender, args) => StopMirroring();

            var restoreButton = MakeButton("Вернуть раскладку «Расширить»", Color.White,
                Color.FromArgb(51, 65, 85), new Point(294, 58), 232);
            restoreButton.FlatAppearance.BorderColor = BorderColor;
            restoreButton.Click += (sender, args) => RestoreLayout();

            var refreshButton = MakeButton("Обновить список", Color.White, Color.FromArgb(51, 65, 85),
                new Point(536, 58), 152);
            refreshButton.FlatAppearance.BorderColor = BorderColor;
            refreshButton.Click += (sender, args) => RebuildRows();

            var modeLabel = new Label
            {
                Text = "Режим вывода:",
                AutoSize = true,
                Location = new Point(20, 108),
                ForeColor = Color.FromArgb(51, 65, 85)
            };

            _modeBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(128, 104),
                Width = 230,
                FlatStyle = FlatStyle.System
            };
            _modeBox.Items.Add("Быстрый (видеокарта)");
            _modeBox.Items.Add("Совместимость (с курсором)");
            _modeBox.SelectedIndex = app.Config.UseGpu ? 0 : 1;

            var hint = new Label
            {
                Text = "Быстрый режим почти не нагружает процессор, но не показывает курсор. " +
                       "Если курсор нужен — выберите совместимость.",
                AutoSize = false,
                AutoEllipsis = true,
                ForeColor = Muted,
                Location = new Point(372, 106),
                Height = 32,
                Width = 600
            };

            footer.Controls.AddRange(new Control[]
            {
                fpsLabel, _fpsBox, _cursorBox, _autostartBox, _startWithBox,
                _applyButton, _stopButton, restoreButton, refreshButton, modeLabel, _modeBox, hint
            });
            Controls.Add(footer);

            _statusLabel = new Label
            {
                AutoSize = false,
                Width = 300,
                Height = 44,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
                ForeColor = Muted,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(ClientSize.Width - 320, 18)
            };
            header.Controls.Add(_statusLabel);
            header.Resize += (sender, args) =>
                _statusLabel.Left = header.ClientSize.Width - _statusLabel.Width - 20;

            app.StateChanged += (sender, args) => UpdateStatus();
            RebuildRows();
            UpdateStatus();
        }

        private Panel BuildHeader()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = HeaderBack };

            var title = new Label
            {
                Text = "SoftSplit",
                AutoSize = true,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                Location = new Point(20, 14)
            };

            var subtitle = new Label
            {
                Text = "Дублирование и расширение мониторов: у каждого дисплея своя роль и своё разрешение",
                AutoSize = true,
                ForeColor = Muted,
                Location = new Point(22, 48)
            };

            var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = BorderColor };

            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(line);
            return header;
        }

        private void AddHeaderRow()
        {
            var style = new Font(Font.FontFamily, 8.5f, FontStyle.Regular);
            var color = Muted;
            var labels = new[]
            {
                "Дисплей", "Роль", "Дублировать с", "Разрешение", "Вписывание"
            };

            _table.RowCount = 1;
            _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            for (var i = 0; i < labels.Length; i++)
            {
                _table.Controls.Add(new Label
                {
                    Text = labels[i],
                    Font = style,
                    ForeColor = color,
                    AutoSize = true,
                    Margin = new Padding(4, 2, 4, 2)
                }, i, 0);
            }
        }

        private void RebuildRows()
        {
            _suppressEvents = true;
            try
            {
                _app.RefreshDisplays();

                foreach (var row in _rows)
                {
                    row.Role.Dispose();
                    row.Source.Dispose();
                    row.Mode.Dispose();
                    row.Scale.Dispose();
                }
                _rows.Clear();

                // Remove every control except the header row.
                while (_table.Controls.Count > 5)
                {
                    var control = _table.Controls[_table.Controls.Count - 1];
                    _table.Controls.RemoveAt(_table.Controls.Count - 1);
                    control.Dispose();
                }
                _table.RowCount = 1;
                while (_table.RowStyles.Count > 1) _table.RowStyles.RemoveAt(_table.RowStyles.Count - 1);

                foreach (var display in _app.Displays)
                {
                    AddRow(display);
                }

                if (_app.Displays.Count == 0)
                {
                    _table.RowCount = 2;
                    _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 60f));
                    var empty = new Label
                    {
                        Text = "Дисплеи не найдены. Проверьте подключение и нажмите «Обновить список».",
                        AutoSize = true,
                        ForeColor = Muted,
                        Margin = new Padding(4, 12, 4, 4)
                    };
                    _table.Controls.Add(empty, 0, 1);
                    _table.SetColumnSpan(empty, 5);
                }
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void AddRow(DisplayInfo display)
        {
            var setting = _app.SettingFor(display);
            var rowIndex = _table.RowCount;
            _table.RowCount = rowIndex + 1;
            _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 68f));

            var row = new RowControls { Display = display };

            var namePanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(4, 8, 4, 8) };
            var nameLabel = new Label
            {
                Text = display.Caption,
                Dock = DockStyle.Top,
                AutoEllipsis = true,
                Font = new Font(Font.FontFamily, 9.75f, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                Height = 22
            };
            var detailLabel = new Label
            {
                Text = display.Detail,
                Dock = DockStyle.Top,
                AutoEllipsis = true,
                Font = new Font(Font.FontFamily, 8.25f),
                ForeColor = Muted,
                Height = 18
            };
            namePanel.Controls.Add(detailLabel);
            namePanel.Controls.Add(nameLabel);

            row.Role = MakeCombo(rowIndex);
            row.Role.Items.Add("Расширить");
            row.Role.Items.Add("Дублировать");
            row.Role.SelectedIndex = setting != null && setting.GetRole() == DisplayRole.Duplicate ? 1 : 0;

            row.Source = MakeCombo(rowIndex);
            foreach (var other in _app.Displays.Where(d =>
                         !string.Equals(d.DevicePath, display.DevicePath, StringComparison.OrdinalIgnoreCase)))
            {
                row.Source.Items.Add(new DisplayChoice(other));
            }
            SelectSource(row, setting);

            row.Mode = MakeCombo(rowIndex);
            row.Mode.Items.Add(new ModeChoice(null));
            try
            {
                foreach (var mode in DisplayCatalog.GetModes(display))
                {
                    row.Mode.Items.Add(new ModeChoice(mode));
                }
            }
            catch (Exception)
            {
            }
            SelectMode(row, setting);

            row.Scale = MakeCombo(rowIndex);
            row.Scale.Items.Add("Вписать (поля)");
            row.Scale.Items.Add("Заполнить (обрезка)");
            row.Scale.Items.Add("Растянуть");
            row.Scale.SelectedIndex = setting == null
                ? 0
                : setting.GetScale() == ScaleMode.Fill ? 1 : setting.GetScale() == ScaleMode.Stretch ? 2 : 0;

            row.Role.SelectedIndexChanged += (sender, args) => UpdateRowEnabled(row);
            UpdateRowEnabled(row);

            _table.Controls.Add(namePanel, 0, rowIndex);
            _table.Controls.Add(row.Role, 1, rowIndex);
            _table.Controls.Add(row.Source, 2, rowIndex);
            _table.Controls.Add(row.Mode, 3, rowIndex);
            _table.Controls.Add(row.Scale, 4, rowIndex);

            _rows.Add(row);
        }

        private static void SelectSource(RowControls row, DisplaySetting setting)
        {
            row.Source.SelectedIndex = -1;
            if (setting == null || string.IsNullOrEmpty(setting.SourcePath)) return;

            for (var i = 0; i < row.Source.Items.Count; i++)
            {
                var choice = row.Source.Items[i] as DisplayChoice;
                if (choice != null && string.Equals(choice.Display.DevicePath, setting.SourcePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    row.Source.SelectedIndex = i;
                    return;
                }
            }
        }

        private static void SelectMode(RowControls row, DisplaySetting setting)
        {
            row.Mode.SelectedIndex = 0;
            if (setting == null || setting.ModeWidth == null || setting.ModeHeight == null) return;

            for (var i = 0; i < row.Mode.Items.Count; i++)
            {
                var choice = row.Mode.Items[i] as ModeChoice;
                if (choice == null || choice.Mode == null) continue;
                if (choice.Mode.Width == setting.ModeWidth.Value &&
                    choice.Mode.Height == setting.ModeHeight.Value &&
                    (setting.ModeRefresh == null || choice.Mode.RefreshRate == setting.ModeRefresh.Value))
                {
                    row.Mode.SelectedIndex = i;
                    return;
                }
            }
        }

        private ComboBox MakeCombo(int rowIndex)
        {
            return new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 22, 8, 22),
                FlatStyle = FlatStyle.System,
                Tag = rowIndex
            };
        }

        private static void UpdateRowEnabled(RowControls row)
        {
            var isDuplicate = row.Role.SelectedIndex == 1;
            row.Source.Enabled = isDuplicate;
            row.Scale.Enabled = isDuplicate;
            if (isDuplicate && row.Source.SelectedIndex < 0 && row.Source.Items.Count > 0)
                row.Source.SelectedIndex = 0;
        }

        private void ApplyLayout()
        {
            if (_suppressEvents) return;

            foreach (var row in _rows)
            {
                var setting = _app.EnsureSetting(row.Display);
                setting.DevicePath = row.Display.DevicePath;
                setting.SetRole(row.Role.SelectedIndex == 1 ? DisplayRole.Duplicate : DisplayRole.Extend);

                var sourceChoice = row.Source.SelectedItem as DisplayChoice;
                setting.SourcePath = setting.GetRole() == DisplayRole.Duplicate && sourceChoice != null
                    ? sourceChoice.Display.DevicePath
                    : null;

                var scaleIndex = row.Scale.SelectedIndex;
                setting.SetScale(scaleIndex == 1 ? ScaleMode.Fill
                    : scaleIndex == 2 ? ScaleMode.Stretch
                    : ScaleMode.Fit);

                var modeChoice = row.Mode.SelectedItem as ModeChoice;
                if (modeChoice != null && modeChoice.Mode != null)
                {
                    setting.ModeWidth = modeChoice.Mode.Width;
                    setting.ModeHeight = modeChoice.Mode.Height;
                    setting.ModeRefresh = modeChoice.Mode.RefreshRate;
                }
                else
                {
                    setting.ModeWidth = null;
                    setting.ModeHeight = null;
                    setting.ModeRefresh = null;
                }
            }

            _app.Config.Fps = (int)_fpsBox.Value;
            _app.Config.ShowCursor = _cursorBox.Checked;
            _app.Config.StartMirroringOnLaunch = _startWithBox.Checked;
            _app.Config.UseGpu = _modeBox.SelectedIndex == 0;

            var autostartError = SetAutostart(_autostartBox.Checked);
            _app.Config.Autostart = Autostart.IsEnabled();

            string saveError;
            _app.TrySave(out saveError);

            string error;
            if (!_app.StartMirroring(out error))
            {
                if (!string.IsNullOrEmpty(error))
                {
                    MessageBox.Show(this, error, "SoftSplit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            if (!string.IsNullOrEmpty(autostartError))
            {
                MessageBox.Show(this, autostartError, "SoftSplit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            RebuildRows();
        }

        private static string SetAutostart(bool enabled)
        {
            string error;
            return Autostart.TrySet(enabled, out error) ? null : error;
        }

        private void StopMirroring()
        {
            _app.StopMirroring();
        }

        private void RestoreLayout()
        {
            if (MessageBox.Show(this,
                    "Всем дисплеям будет применён режим «Расширить», а дублирование остановлено. Продолжить?",
                    "SoftSplit", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _app.StopMirroring();
            string error;
            if (!TopologyService.TryRestoreExtend(out error))
            {
                MessageBox.Show(this, error, "SoftSplit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RebuildRows();
        }

        private void UpdateStatus()
        {
            if (_app.IsMirroring)
            {
                _statusLabel.Text = "Дублирование включено";
                _statusLabel.ForeColor = Color.FromArgb(22, 163, 74);
                _stopButton.Enabled = true;
            }
            else
            {
                _statusLabel.Text = "Дублирование выключено";
                _statusLabel.ForeColor = Muted;
                _stopButton.Enabled = false;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RebuildRows();
            UpdateStatus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        public void CloseForExit()
        {
            _allowClose = true;
            Close();
        }

        private static Button MakeButton(string text, Color back, Color fore, Point location, int width)
        {
            var button = new Button
            {
                Text = text,
                Location = location,
                Size = new Size(width, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = fore,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = back;
            return button;
        }

        private static Icon LoadAppIcon()
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

        private sealed class DisplayChoice
        {
            public DisplayChoice(DisplayInfo display)
            {
                Display = display;
            }

            public DisplayInfo Display { get; private set; }

            public override string ToString()
            {
                return Display.Caption;
            }
        }

        private sealed class ModeChoice
        {
            public ModeChoice(DisplayModeInfo mode)
            {
                Mode = mode;
            }

            public DisplayModeInfo Mode { get; private set; }

            public override string ToString()
            {
                return Mode == null ? "Как в Windows" : Mode.ToString();
            }
        }
    }
}
