using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SoftSplit.Core;

namespace SoftSplit
{
    internal static class Program
    {
        private const string MutexName = @"Local\SoftSplit.SingleInstance";

        [STAThread]
        private static int Main(string[] args)
        {
            var verbose = Has(args, "--verbose");
            var diagnose = Has(args, "--diagnose");
            var minimized = Has(args, "--minimized");
            var forceGdi = Has(args, "--gdi");
            var probeDx = Has(args, "--probe-dx");

            if (diagnose || verbose || probeDx || Has(args, "--test-cursor"))
            {
                if (!ConsoleHost.AttachToParent()) ConsoleHost.Allocate();
            }

            if (diagnose)
            {
                Console.WriteLine(BuildDiagnostics());
                return 0;
            }

            if (probeDx)
            {
                Console.WriteLine(DxProbe.Run());
                return 0;
            }

            if (Has(args, "--test-cursor"))
            {
                Console.WriteLine(TestCursor());
                return 0;
            }

            var log = new Action<string>(message => Log(verbose, message));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, eventArgs) =>
                ReportCrash(eventArgs.Exception, log);
            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
                ReportCrash(eventArgs.ExceptionObject as Exception, log);

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    SignalExistingInstance();
                    return 0;
                }

                try
                {
                    using (var context = new TrayApplicationContext(minimized, forceGdi, log))
                    {
                        Application.Run(context);
                    }
                }
                catch (Exception ex)
                {
                    ReportCrash(ex, log);
                    return 1;
                }

                GC.KeepAlive(mutex);
            }

            return 0;
        }

        private static bool Has(string[] args, string flag)        {
            return args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using (var handle = EventWaitHandle.OpenExisting(
                           @"Local\SoftSplit.ShowSettings." + Environment.UserName))
                {
                    handle.Set();
                }
            }
            catch (Exception)
            {
                // Older instance without the event: nothing to signal, just exit.
            }
        }

        private static void Log(bool verbose, string message)
        {
            if (verbose) Console.WriteLine(message);

            try
            {
                var path = Path.Combine(ConfigStore.DirectoryPath, "log.txt");
                Directory.CreateDirectory(ConfigStore.DirectoryPath);

                var info = new FileInfo(path);
                if (info.Exists && info.Length > 256 * 1024) info.Delete();

                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
        }

        private static void ReportCrash(Exception exception, Action<string> log)
        {
            if (exception == null) return;

            try
            {
                log(exception.ToString());
            }
            catch (Exception)
            {
            }

            var text = "Произошла непредвиденная ошибка.\r\n\r\n" + exception.Message +
                       "\r\n\r\nПодробности записаны в журнал: " + Path.Combine(ConfigStore.DirectoryPath, "log.txt");

            try
            {
                MessageBox.Show(text, "SoftSplit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception)
            {
            }
        }

        private static string TestCursor()
        {
            int hotspotX;
            int hotspotY;
            var pixels = CursorImage.Capture(out hotspotX, out hotspotY);
            if (pixels == null) return "cursor image: null (no cursor?)";

            var opaque = 0;
            var maxAlpha = 0;
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = -1;
            var maxY = -1;
            const int size = CursorImage.Size;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var alpha = pixels[(y * size + x) * 4 + 3];
                    if (alpha == 0) continue;
                    opaque++;
                    if (alpha > maxAlpha) maxAlpha = alpha;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            return string.Format("hotspot={0},{1} opaque={2} maxAlpha={3} bbox={4},{5}..{6},{7}",
                hotspotX, hotspotY, opaque, maxAlpha, minX, minY, maxX, maxY);
        }

        private static string BuildDiagnostics()
        {
            var builder = new StringBuilder();
            builder.AppendLine("SoftSplit диагностика");
            builder.AppendLine("Версия: " + typeof(Program).Assembly.GetName().Version);
            builder.AppendLine("ОС: " + Environment.OSVersion.VersionString);
            builder.AppendLine("Процесс 64-бит: " + Environment.Is64BitProcess);
            builder.AppendLine("Настройки: " + ConfigStore.FilePath);
            builder.AppendLine("Автозапуск: " + (Autostart.IsEnabled() ? "включён" : "выключен"));
            builder.AppendLine();

            var displays = DisplayCatalog.GetDisplays();
            builder.AppendLine("Дисплеев найдено: " + displays.Count);
            foreach (var display in displays)
            {
                builder.AppendLine("  " + display.Caption);
                builder.AppendLine("    " + display.Detail);
                builder.AppendLine("    id: " + display.DevicePath);
                builder.AppendLine("    координаты: " + display.Bounds);
                if (display.CurrentMode != null)
                    builder.AppendLine("    режим: " + display.CurrentMode);
            }

            builder.AppendLine();
            try
            {
                var config = ConfigStore.Load();
                builder.AppendLine("Сохранённые роли:");
                foreach (var setting in config.Displays)
                {
                    builder.AppendLine("  " + setting.DevicePath + " → " + setting.Role +
                                       (string.IsNullOrEmpty(setting.SourcePath)
                                           ? string.Empty
                                           : " ← " + setting.SourcePath) +
                                       " · " + setting.Scale);
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine("Не удалось прочитать настройки: " + ex.Message);
            }

            return builder.ToString();
        }
    }
}
