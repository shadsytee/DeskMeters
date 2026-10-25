using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DeskMeters
{
    /// <summary>
    /// "Start with Windows". Preferred: a scheduled task that runs at logon with highest
    /// privileges, so temperatures work without a UAC prompt every time you sign in.
    /// Fallback (UAC declined): a plain HKCU Run entry, which starts it without admin.
    /// </summary>
    internal static class Autostart
    {
        private const string TaskName = "DeskMeters";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsEnabled() => TaskExists() || RunEntryExists();

        public static bool TaskExists()
        {
            try { return Run("/Query /TN \"" + TaskName + "\"", elevate: false) == 0; }
            catch { return false; }
        }

        public static bool RunEntryExists()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(TaskName) != null;
            }
            catch { return false; }
        }

        public static string CurrentExe()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch { return typeof(Autostart).Assembly.Location; }
        }

        /// <summary>Elevated scheduled task for <paramref name="exe"/>; one UAC prompt if we aren't admin.</summary>
        public static bool EnableTask(string exe)
        {
            string user;
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) user = id.Name;

            string xml =
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Starts DeskMeters at sign-in.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <Delay>PT8S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>6</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{SecurityElement.Escape(exe)}</Command>
      <Arguments>--autostart</Arguments>
      <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe))}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";

            string file = Path.Combine(Path.GetTempPath(), "DeskMeters-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try
            {
                bool ok = Run("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", elevate: !Sampler.IsAdmin) == 0;
                if (ok) RemoveRunEntry();
                return ok;
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        /// <summary>Plain Run-key entry: starts at sign-in without admin (so no temperatures).</summary>
        public static bool EnableRunEntry(string exe)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                    k?.SetValue(TaskName, "\"" + exe + "\" --autostart");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Run key", ex);
                return false;
            }
        }

        public static void RemoveRunEntry()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    k?.DeleteValue(TaskName, false);
            }
            catch { }
        }

        /// <summary>Task first, Run entry if the UAC prompt is declined.</summary>
        public static bool Enable() => Enable(CurrentExe());

        public static bool Enable(string exe)
        {
            if (EnableTask(exe)) return true;
            return EnableRunEntry(exe);
        }

        public static bool Disable()
        {
            RemoveRunEntry();
            if (!TaskExists()) return true;
            return Run("/Delete /TN \"" + TaskName + "\" /F", elevate: !Sampler.IsAdmin) == 0;
        }

        /// <summary>Start the app through its task (elevated, no prompt). False if there is no task.</summary>
        public static bool StartViaTask()
        {
            if (!TaskExists()) return false;
            return Run("/Run /TN \"" + TaskName + "\"", elevate: false) == 0;
        }

        private static int Run(string args, bool elevate)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            if (elevate)
            {
                psi.UseShellExecute = true;
                psi.Verb = "runas";
            }
            else
            {
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }

            try
            {
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    if (!elevate)
                    {
                        p.StandardOutput.ReadToEnd();
                        p.StandardError.ReadToEnd();
                    }
                    p.WaitForExit(20000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return -1; // UAC prompt cancelled
            }
            catch (Exception ex)
            {
                Log.Error("schtasks " + args, ex);
                return -1;
            }
        }
    }
}
