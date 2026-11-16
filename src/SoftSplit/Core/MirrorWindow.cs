using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoftSplit.Core
{
    /// <summary>How a captured frame is fitted into the target display.</summary>
    public enum ScaleMode
    {
        /// <summary>Keep proportions, add black bars when needed.</summary>
        Fit,
        /// <summary>Keep proportions, crop the overflowing edges.</summary>
        Fill,
        /// <summary>Fill the whole display, proportions may change.</summary>
        Stretch
    }

    /// <summary>
    /// Borderless top-most window that covers one display and shows frames captured
    /// from another one. It never activates and never appears in the taskbar.
    /// </summary>
    internal sealed class MirrorWindow : Form
    {
        private const int WmAppRepaint = 0x8000 + 1;

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly string _sourceGdi;
        private readonly MirrorEngine _engine;
        private Rectangle _targetBounds;
        private int _paintPending;
        private int _boundsFixAttempts;

        /// <summary>Optional diagnostics sink.</summary>
        public static Action<string> DiagnosticLog { get; set; }

        public MirrorWindow(MirrorEngine engine, string targetPath, string sourcePath, Rectangle targetBounds)
        {
            _engine = engine;
            _sourceGdi = sourcePath;
            _targetBounds = targetBounds;

            TargetPath = targetPath;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            Text = "SoftSplit";
            AutoScaleMode = AutoScaleMode.None;

            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            UpdateStyles();
        }

        public string TargetPath { get; private set; }

        public string SourceGdi
        {
            get { return _sourceGdi; }
        }

        public Rectangle TargetBounds
        {
            get { return _targetBounds; }
        }

        public ScaleMode ScaleMode { get; set; }

        /// <summary>When set, frames are presented by a swap chain instead of GDI.</summary>
        public bool GpuMode { get; set; }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        /// <summary>Shows the window at the exact physical rectangle of its display.</summary>
        public void ShowOnTarget()
        {
            Bounds = new Rectangle(0, 0, 1, 1);
            Show();
            ApplyExactBounds();
        }

        /// <summary>
        /// Forces the window to cover exactly the display rectangle. Windows sends
        /// DPI-change notifications when the window lands on a scaled display, and
        /// WinForms answers them by resizing the form, so this runs again on every
        /// frame and after any DPI change.
        /// </summary>
        private void ApplyExactBounds()
        {
            if (!IsHandleCreated) return;

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST,
                _targetBounds.X, _targetBounds.Y, _targetBounds.Width, _targetBounds.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        private void EnsureExactBounds()
        {
            var bounds = Bounds;
            if (bounds.X == _targetBounds.X && bounds.Y == _targetBounds.Y &&
                bounds.Width == _targetBounds.Width && bounds.Height == _targetBounds.Height)
                return;

            var log = DiagnosticLog;
            if (log != null && _boundsFixAttempts < 3)
            {
                _boundsFixAttempts++;
                log(string.Format("window bounds {0} != target {1}, re-applying", bounds, _targetBounds));
            }

            ApplyExactBounds();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            // Never let WinForms resize the form to the DPI-suggested rectangle:
            // the window must stay exactly as large as the display.
            e.Cancel = true;
            ApplyExactBounds();
        }

        /// <summary>Asks the UI thread to repaint if a frame is available.</summary>
        public void RequestRepaint()
        {
            if (GpuMode) return;
            if (!IsHandleCreated || IsDisposed) return;
            if (System.Threading.Interlocked.CompareExchange(ref _paintPending, 1, 0) != 0) return;
            PostMessage(Handle, WmAppRepaint, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmAppRepaint)
            {
                System.Threading.Interlocked.Exchange(ref _paintPending, 0);
                EnsureExactBounds();
                Invalidate();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var client = ClientRectangle;
            var graphics = e.Graphics;
            graphics.Clear(Color.Black);
            if (GpuMode) return;

            var hdc = graphics.GetHdc();
            try
            {
                _engine.PresentFrame(_sourceGdi, hdc, client, ScaleMode);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }
    }
}
