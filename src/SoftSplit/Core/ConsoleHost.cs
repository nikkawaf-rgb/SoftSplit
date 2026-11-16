using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SoftSplit.Core
{
    /// <summary>Console output for the command-line switches of a windowed executable.</summary>
    internal static class ConsoleHost
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        public static bool AttachToParent()
        {
            if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) return false;
            Redirect();
            return true;
        }

        public static bool Allocate()
        {
            if (!AllocConsole()) return false;
            Redirect();
            return true;
        }

        private static void Redirect()
        {
            try
            {
                // Switch the console to UTF-8 so Cyrillic is not mangled and pipelines
                // receive predictable bytes.
                try
                {
                    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                }
                catch (Exception)
                {
                }

                var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                Console.SetOut(stdout);
                var stderr = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                Console.SetError(stderr);
            }
            catch (IOException)
            {
            }
        }
    }
}
