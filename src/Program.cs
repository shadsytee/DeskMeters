using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

namespace DeskMeters
{
    internal static class Program
    {
        private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        [STAThread]
        private static void Main(string[] args)
        {
            // Must be registered before any type that touches LibreHardwareMonitor is loaded,
            // which is why the real work lives in Run() (never inlined into Main).
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;

            // "--after <pid>": we were started by an instance that is about to exit
            // (restart as admin). Wait for it so the single-instance check passes.
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--after" && int.TryParse(args[i + 1], out int pid))
                {
                    try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(10000); }
                    catch { /* already gone */ }
                }
            }

            // Setup: the same exe run as "DeskMeters-Setup.exe", or with --install / --uninstall.
            string exeName = "";
            try { exeName = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName); } catch { }
            bool wantsInstall = Array.IndexOf(args, "--install") >= 0 || exeName.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0;
            bool wantsUninstall = Array.IndexOf(args, "--uninstall") >= 0;
            if (wantsInstall || wantsUninstall)
            {
                RunSetup(wantsUninstall);
                return;
            }

            Mutex mutex;
            bool created;
            try
            {
                mutex = new Mutex(true, @"Local\DeskMeters.SingleInstance", out created);
            }
            catch (UnauthorizedAccessException)
            {
                // An elevated instance owns the mutex.
                SignalRunningInstance();
                return;
            }

            if (!created)
            {
                mutex.Dispose();
                SignalRunningInstance();
                return;
            }

            try
            {
                Run();
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
            }
        }

        /// <summary>Tell the copy that is already running to show itself.</summary>
        private static void SignalRunningInstance()
        {
            try
            {
                IntPtr host = Native.FindWindow(null, MenuHostForm.Caption);
                if (host != IntPtr.Zero)
                    Native.PostMessage(host, Native.WM_DESKMETERS_SHOW, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // Nothing to do; the other instance keeps running.
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunSetup(bool uninstall)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new SetupForm(uninstall))
                Application.Run(form);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log.Error("UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);

            using (var app = new MeterApp())
                Application.Run(app);
        }

        private static Assembly ResolveEmbedded(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            lock (Loaded)
            {
                if (Loaded.TryGetValue(name, out Assembly cached))
                    return cached;

                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("lib." + name + ".dll"))
                {
                    if (s == null)
                        return null;

                    var buffer = new byte[s.Length];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = s.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    // Loaded from memory: nothing is written to disk, so nothing can be
                    // swapped out from under an elevated process.
                    Assembly asm = Assembly.Load(buffer);
                    Loaded[name] = asm;
                    return asm;
                }
            }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string Folder
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskMeters");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static void Error(string where, Exception ex) => Write($"ERROR [{where}] {ex}");

        public static void Info(string message) => Write("INFO  " + message);

        private static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    string path = Path.Combine(Folder, "DeskMeters.log");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
