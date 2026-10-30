using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace StigGpoManager
{
    /// <summary>
    /// Install-time hooks called by the Inno Setup installer (StigGpoManager-Setup-x.y.z.exe), which
    /// itself handles the files, Programs and Features, the Start menu and upgrades:
    ///   /postinstall      create %ProgramData%\StigGpoManager (Administrators/SYSTEM only)
    ///   /register-tray    logon scheduled task (highest privileges, Administrators) that starts the tray icon, and start it now
    ///   /preuninstall     remove the tray task and stop running copies
    /// Uninstall keeps the config and the WorkingFolder (runs, backups, reports, audit log: audit evidence).
    /// </summary>
    public static class Installer
    {
        public const string AppKey = "StigGpoManager";
        public const string TaskName = "StigGpoManager Tray";

        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppKey);
        public static string DefaultConfigPath => Path.Combine(DataDir, "config.json");
        public static string CurrentExe => Assembly.GetExecutingAssembly().Location;
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static void PostInstall()
        {
            Directory.CreateDirectory(DataDir);
            // The config decides what the tool changes in AD: only Administrators and SYSTEM may write it.
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(DataDir, sec);
        }

        public static void RegisterTray()
        {
            var exe = CurrentExe;
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Shows the STIG GPO Manager status icon (current quarterly update) for administrators.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
  <Principals>
    <Principal id=""Admins""><GroupId>S-1-5-32-544</GroupId><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Admins""><Exec><Command>""{exe}""</Command><Arguments>/tray</Arguments><WorkingDirectory>{Path.GetDirectoryName(exe)}</WorkingDirectory></Exec></Actions>
</Task>";
            var tmp = Path.Combine(Path.GetTempPath(), "StigGpoManager-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                var code = RunHidden("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
                if (code != 0) throw new InvalidOperationException($"schtasks /Create failed with exit code {code}.");
            }
            finally { File.Delete(tmp); }
            RunHidden("schtasks.exe", $"/Run /TN \"{TaskName}\"");
        }

        public static void PreUninstall()
        {
            RunHidden("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
            StopOtherInstances();
        }

        public static string SampleConfig()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("StigGpoManager.config.sample.json"))
            using (var r = new StreamReader(s)) return r.ReadToEnd();
        }

        private static void StopOtherInstances()
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(CurrentExe)))
            {
                if (p.Id == me.Id) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { /* other session or already gone */ }
            }
        }

        private static int RunHidden(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }
    }
}
