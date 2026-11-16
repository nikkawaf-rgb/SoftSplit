using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace SoftSplit.Core
{
    /// <summary>One "duplicate this display onto that one" link.</summary>
    public sealed class MirrorMapping
    {
        public DisplayInfo Source { get; set; }
        public DisplayInfo Target { get; set; }
        public ScaleMode Scale { get; set; }
    }

    /// <summary>
    /// Owns the frame pump and the mirror windows. Uses the GPU path (Desktop
    /// Duplication + swap chain) when possible and falls back to GDI capture.
    /// All public members except <see cref="PresentFrame"/> must be used from the UI thread.
    /// </summary>
    public sealed class MirrorEngine : IDisposable
    {
        private sealed class Job
        {
            public string SourceGdi;
            public Rectangle SourceBounds;
            public List<MirrorWindow> Windows = new List<MirrorWindow>();
        }

        private readonly Dictionary<string, ScreenCapture> _captures =
            new Dictionary<string, ScreenCapture>(StringComparer.OrdinalIgnoreCase);

        private readonly List<MirrorWindow> _windows = new List<MirrorWindow>();

        private Job[] _jobs = new Job[0];
        private DxgiSession _dxgi;
        private Thread _thread;
        private volatile bool _running;
        private volatile int _fps = 30;
        private bool _disposed;
        private int _failureReports;

        public bool ShowCursor { get; set; }

        /// <summary>
        /// When true the engine tries Desktop Duplication (fast, GPU only); when false
        /// it always uses GDI capture, which is slower but draws the mouse cursor.
        /// </summary>
        public bool UseGpu { get; set; }

        /// <summary>Optional diagnostics sink; only the first few failures are reported.</summary>
        public Action<string> Log { get; set; }

        public int Fps
        {
            get { return _fps; }
            set { _fps = Math.Max(1, Math.Min(120, value)); }
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>True when frames are copied on the GPU instead of through GDI.</summary>
        public bool IsGpuMode
        {
            get { return _dxgi != null; }
        }

        public int WindowCount
        {
            get { return _windows.Count; }
        }

        public event EventHandler Stopped;

        public MirrorEngine()
        {
            ShowCursor = true;
            UseGpu = true;
        }

        /// <summary>Stops mirroring, then rebuilds windows for the given mappings.</summary>
        public void Apply(IEnumerable<MirrorMapping> mappings)
        {
            ThrowIfDisposed();
            Stop();

            DisposeGpu();
            DisposeWindows();
            DisposeCaptures();

            var jobs = new List<Job>();
            foreach (var group in mappings.GroupBy(m => m.Source.GdiName, StringComparer.OrdinalIgnoreCase))
            {
                var source = group.First().Source;
                var job = new Job
                {
                    SourceGdi = source.GdiName,
                    SourceBounds = source.Bounds
                };

                foreach (var mapping in group)
                {
                    var window = new MirrorWindow(this, mapping.Target.DevicePath, source.GdiName,
                        mapping.Target.Bounds)
                    {
                        ScaleMode = mapping.Scale
                    };
                    job.Windows.Add(window);
                    _windows.Add(window);
                }

                jobs.Add(job);
            }

            _jobs = jobs.ToArray();
        }

        public void Start()
        {
            ThrowIfDisposed();
            if (_running || _jobs.Length == 0) return;

            foreach (var window in _windows) window.ShowOnTarget();

            if (UseGpu && TryStartGpu())
            {
                DisposeCaptures();
                foreach (var window in _windows) window.GpuMode = true;
            }
            else
            {
                EnsureCaptures();
                foreach (var window in _windows) window.GpuMode = false;
            }

            _running = true;
            _thread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "SoftSplit frames",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            var thread = _thread;
            _thread = null;
            if (thread != null)
            {
                try
                {
                    thread.Join(3000);
                }
                catch (ThreadStateException)
                {
                }
            }
        }

        public bool PresentFrame(string sourceGdi, IntPtr hdc, Rectangle client, ScaleMode mode)
        {
            ScreenCapture capture;
            if (!_captures.TryGetValue(sourceGdi, out capture)) return false;
            return capture.Present(hdc, client, mode);
        }

        private bool TryStartGpu()
        {
            var session = DxgiSession.TryCreate(ReportFailure);
            if (session == null) return false;

            try
            {
                foreach (var job in _jobs)
                {
                    if (session.AddSource(job.SourceGdi, job.SourceBounds)) continue;
                    session.Dispose();
                    return false;
                }

                foreach (var job in _jobs)
                {
                    foreach (var window in job.Windows)
                    {
                        if (session.AddTarget(window.TargetPath, job.SourceGdi, window.Handle,
                                window.TargetBounds, window.ScaleMode)) continue;
                        session.Dispose();
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                ReportFailure("GPU initialization failed: " + ex.Message);
                session.Dispose();
                return false;
            }

            _dxgi = session;
            _dxgi.ShowCursor = ShowCursor;
            return true;
        }

        private void EnsureCaptures()
        {
            foreach (var job in _jobs)
            {
                if (_captures.ContainsKey(job.SourceGdi)) continue;
                _captures[job.SourceGdi] = new ScreenCapture(job.SourceBounds.Width, job.SourceBounds.Height);
            }
        }

        private void PumpLoop()
        {
            var clock = Stopwatch.StartNew();
            try
            {
                while (_running)
                {
                    var startTicks = clock.ElapsedTicks;

                    if (_dxgi != null) PumpGpu();
                    else PumpGdi();

                    var target = TimeSpan.TicksPerSecond / (double)_fps;
                    var elapsed = clock.ElapsedTicks - startTicks;
                    var sleepMs = (target - elapsed) * 1000.0 / TimeSpan.TicksPerSecond;
                    if (sleepMs > 1) Thread.Sleep((int)sleepMs);
                    else Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                ReportFailure("frame pump stopped: " + ex.Message);
                _running = false;
            }
            finally
            {
                var handler = Stopped;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        private void PumpGpu()
        {
            try
            {
                _dxgi.Tick();
            }
            catch (SharpDX.SharpDXException)
            {
                // Display mode change or driver reset: rebuild the pipeline.
                _dxgi.Invalidate();
            }
        }

        private void PumpGdi()
        {
            foreach (var job in _jobs)
            {
                ScreenCapture capture;
                if (!_captures.TryGetValue(job.SourceGdi, out capture)) continue;

                bool ok;
                try
                {
                    ok = capture.Capture(job.SourceBounds, ShowCursor);
                }
                catch (Exception ex)
                {
                    ok = false;
                    ReportFailure("capture exception: " + ex.Message);
                }

                if (!ok)
                {
                    ReportFailure(string.Format("capture failed ({0}x{1} at {2},{3})",
                        job.SourceBounds.Width, job.SourceBounds.Height,
                        job.SourceBounds.X, job.SourceBounds.Y));
                    continue;
                }

                foreach (var window in job.Windows) window.RequestRepaint();
            }
        }

        private void ReportFailure(string message)
        {
            if (Interlocked.Increment(ref _failureReports) > 5) return;
            var log = Log;
            if (log != null) log(message);
        }

        private void DisposeWindows()
        {
            foreach (var window in _windows.ToList())
            {
                try
                {
                    window.Hide();
                    window.Dispose();
                }
                catch (Exception)
                {
                }
            }
            _windows.Clear();
            _jobs = new Job[0];
        }

        private void DisposeCaptures()
        {
            foreach (var capture in _captures.Values.ToList())
            {
                try
                {
                    capture.Dispose();
                }
                catch (Exception)
                {
                }
            }
            _captures.Clear();
        }

        private void DisposeGpu()
        {
            if (_dxgi == null) return;
            try
            {
                _dxgi.Dispose();
            }
            catch (Exception)
            {
            }
            _dxgi = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().Name);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            DisposeGpu();
            DisposeWindows();
            DisposeCaptures();
        }
    }
}
