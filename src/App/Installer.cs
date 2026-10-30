using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace StigGpoManager
{
    /// <summary>
    /// Self-install / uninstall of the single exe:
    ///  - copies itself to %ProgramFiles%\StigGpoManager
    ///  - registers in Programs and Features (HKLM Uninstall key)
    ///  - adds an all-users Start menu shortcut
    ///  - registers a logon scheduled task (highest privileges, Administrators group) that starts the tray icon
    ///  - creates %ProgramData%\StigGpoManager (admins/SYSTEM only) with a sample config.json if none exists
    /// Uninstall removes all of that except ProgramData config and the WorkingFolder (audit evidence).
    /// </summary>
    public static class Installer
    {
        public const string AppKey = "StigGpoManager";
        public const string DisplayName = "STIG GPO Manager";
        public const string Publisher = "Site ISSO / Systems Administration";
        public const string TaskName = "StigGpoManager Tray";

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppKey);
        public static string InstalledExe => Path.Combine(InstallDir, "StigGpoManager.exe");
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppKey);
        public static string DefaultConfigPath => Path.Combine(DataDir, "config.json");
        public static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), DisplayName + ".lnk");
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppKey;

        public static string CurrentExe => Assembly.GetExecutingAssembly().Location;
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static bool IsInstalled
        {
            get
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(UninstallKey))
                    return k != null;
            }
        }

        public static string Install(bool startTray = true)
        {
            var log = new StringBuilder();
            Directory.CreateDirectory(InstallDir);
            if (!string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                StopOtherInstances();
                File.Copy(CurrentExe, InstalledExe, true);
                log.AppendLine($"Copied to {InstalledExe}");
            }

            CreateDataDir(log);
            RegisterUninstall();
            log.AppendLine("Registered in Programs and Features.");
            CreateShortcut();
            log.AppendLine($"Start menu shortcut: {ShortcutPath}");
            RegisterTrayTask();
            log.AppendLine($"Scheduled task '{TaskName}' starts the tray icon at logon for administrators.");
            if (startTray)
            {
                RunHidden("schtasks.exe", $"/Run /TN \"{TaskName}\"");
                log.AppendLine("Tray icon started.");
            }
            return log.ToString();
        }

        private static void CreateDataDir(StringBuilder log)
        {
            Directory.CreateDirectory(DataDir);
            // Config decides what the tool changes in AD: only Administrators and SYSTEM may write it.
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(DataDir, sec);
            if (!File.Exists(DefaultConfigPath))
            {
                File.WriteAllText(DefaultConfigPath, SampleConfig(), new UTF8Encoding(false));
                log.AppendLine($"Sample config written to {DefaultConfigPath} - edit it for this domain before use.");
            }
            else log.AppendLine($"Existing config kept: {DefaultConfigPath}");
        }

        public static string SampleConfig()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("StigGpoManager.config.sample.json"))
            using (var r = new StreamReader(s)) return r.ReadToEnd();
        }

        private static void RegisterUninstall()
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = hklm.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", DisplayName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", Publisher);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("DisplayIcon", InstalledExe + ",0");
                k.SetValue("UninstallString", $"\"{InstalledExe}\" /uninstall");
                k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" /uninstall /quiet");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("Comments", "Quarterly DISA STIG GPO import tool. Data and audit log are kept on uninstall.");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }
        }

        /// <summary>Creates the .lnk with the in-box WScript.Shell COM object (no interop assembly needed).</summary>
        private static void CreateShortcut()
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
            var t = lnk.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { InstalledExe });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { InstallDir });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Quarterly DISA STIG GPO import" });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { InstalledExe + ",0" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }

        private static void RegisterTrayTask()
        {
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Shows the STIG GPO Manager tray icon (run status) for administrators.</Description></RegistrationInfo>
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
  <Actions Context=""Admins""><Exec><Command>""{InstalledExe}""</Command><Arguments>/tray</Arguments><WorkingDirectory>{InstallDir}</WorkingDirectory></Exec></Actions>
</Task>";
            var tmp = Path.Combine(Path.GetTempPath(), "StigGpoManager-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                var code = RunHidden("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
                if (code != 0) throw new InvalidOperationException($"schtasks /Create failed with exit code {code}.");
            }
            finally { File.Delete(tmp); }
        }

        public static string Uninstall()
        {
            var log = new StringBuilder();
            RunHidden("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
            log.AppendLine("Removed tray scheduled task.");
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                hklm.DeleteSubKeyTree(UninstallKey, false);
            log.AppendLine("Removed from Programs and Features.");
            StopOtherInstances();
            if (Directory.Exists(InstallDir))
            {
                // A running exe cannot delete itself: a detached cmd removes the folder once this process exits.
                var cmd = $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
                log.AppendLine($"{InstallDir} will be removed when this window closes.");
            }
            log.AppendLine($"Kept: {DataDir} (config) and the WorkingFolder (runs, backups, reports, audit.log).");
            return log.ToString();
        }

        private static void StopOtherInstances()
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledExe)))
            {
                if (p.Id == me.Id) continue;
                try
                {
                    if (string.Equals(p.MainModule?.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { /* other session or already gone */ }
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
