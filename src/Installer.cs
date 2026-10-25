using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace DeskMeters
{
    /// <summary>
    /// Per-user install (no admin needed for the files): copies this exe to
    /// %LocalAppData%\Programs\DeskMeters, adds Start Menu / desktop shortcuts, registers
    /// in Apps &amp; features, and sets up "Start with Windows". The same exe uninstalls.
    /// </summary>
    internal static class Installer
    {
        public const string AppName = "DeskMeters";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskMeters";

        public static string InstallDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

        public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");

        public static string StartMenuShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        public static string DesktopShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        public static bool IsInstalled => File.Exists(InstalledExe);

        public sealed class Options
        {
            public bool StartWithWindows = true;
            public bool StartMenu = true;
            public bool Desktop;
            public bool Launch = true;
            public bool DeleteSettings;     // uninstall only
        }

        // ---- install ----------------------------------------------------------------------

        public static void Install(Options o, Action<string> status)
        {
            string source = Autostart.CurrentExe();

            status("Closing any running copy…");
            StopRunning();

            status("Copying files…");
            Directory.CreateDirectory(InstallDir);
            CopyWithRetry(source, InstalledExe);
            WriteReadme();

            status("Creating shortcuts…");
            if (o.StartMenu) CreateShortcut(StartMenuShortcut, InstalledExe, "", "Desktop CPU, GPU, network and drive meters");
            else TryDelete(StartMenuShortcut);
            if (o.Desktop) CreateShortcut(DesktopShortcut, InstalledExe, "", "Desktop CPU, GPU, network and drive meters");

            status("Registering in Apps & features…");
            RegisterUninstall(source);

            if (o.StartWithWindows)
            {
                status("Setting up Start with Windows (you may see a Windows security prompt)…");
                if (!Autostart.EnableTask(InstalledExe))
                {
                    status("Administrator prompt declined: starting without temperatures instead…");
                    Autostart.EnableRunEntry(InstalledExe);
                }
            }
            else
            {
                Autostart.Disable();
            }

            if (o.Launch)
            {
                status("Starting DeskMeters…");
                if (!Autostart.StartViaTask())
                    Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
            }

            status("Done.");
        }

        // ---- uninstall --------------------------------------------------------------------

        public static void Uninstall(Options o, Action<string> status)
        {
            status("Closing DeskMeters…");
            StopRunning();

            status("Removing startup entries…");
            Autostart.Disable();

            status("Removing shortcuts…");
            TryDelete(StartMenuShortcut);
            TryDelete(DesktopShortcut);

            status("Removing registry entries…");
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

            if (o.DeleteSettings)
            {
                status("Deleting settings…");
                try { Directory.Delete(Log.Folder, true); } catch { }
            }

            status("Removing program files…");
            string self = Autostart.CurrentExe();
            bool runningFromInstallDir = self.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase);
            if (runningFromInstallDir)
            {
                // Can't delete ourselves while running: hand it to cmd after we exit.
                string cmd = "/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"" + InstallDir + "\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            else
            {
                try { Directory.Delete(InstallDir, true); } catch (Exception ex) { Log.Error("Delete install dir", ex); }
            }

            status("Done.");
        }

        // ---- pieces -----------------------------------------------------------------------

        /// <summary>Ask a running instance to exit, then make sure it's gone.</summary>
        private static void StopRunning()
        {
            int self = Process.GetCurrentProcess().Id;
            IntPtr host = Native.FindWindow(null, MenuHostForm.Caption);
            if (host != IntPtr.Zero)
                Native.PostMessage(host, Native.WM_DESKMETERS_EXIT, IntPtr.Zero, IntPtr.Zero);

            for (int i = 0; i < 40; i++)
            {
                bool any = false;
                foreach (Process p in Process.GetProcessesByName(AppName))
                {
                    using (p)
                    {
                        if (p.Id == self) continue;
                        any = true;
                        if (i == 39)
                        {
                            try { p.Kill(); } catch { }
                        }
                    }
                }
                if (!any) return;
                Thread.Sleep(250);
            }
        }

        private static void CopyWithRetry(string source, string dest)
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                return;
            for (int i = 0; ; i++)
            {
                try
                {
                    File.Copy(source, dest, true);
                    return;
                }
                catch (IOException) when (i < 10)
                {
                    Thread.Sleep(500);
                }
            }
        }

        private static void WriteReadme()
        {
            try
            {
                File.WriteAllText(Path.Combine(InstallDir, "README.txt"),
                    "DeskMeters\r\n\r\n" +
                    "Right-click the tray icon for settings, or right-click any meter.\r\n" +
                    "To uninstall, use Apps & features, or run: DeskMeters.exe --uninstall\r\n" +
                    "Settings live in %APPDATA%\\DeskMeters.\r\n");
            }
            catch { }
        }

        private static void RegisterUninstall(string sourceExe)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", MeterApp.Version);
                    k.SetValue("Publisher", "DeskMeters");
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("DisplayIcon", InstalledExe);
                    k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                    k.SetValue("QuietUninstallString", "\"" + InstalledExe + "\" --uninstall --quiet");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                    try { k.SetValue("EstimatedSize", (int)(new FileInfo(sourceExe).Length / 1024), RegistryValueKind.DWord); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Register uninstall", ex);
            }
        }

        /// <summary>Shortcut via the Windows Script Host shell object (no extra references needed).</summary>
        private static void CreateShortcut(string path, string target, string args, string description)
        {
            object shell = null, link = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                shell = Activator.CreateInstance(t);
                link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { args });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch (Exception ex)
            {
                Log.Error("Shortcut " + path, ex);
            }
            finally
            {
                if (link != null && System.Runtime.InteropServices.Marshal.IsComObject(link)) System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
                if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
