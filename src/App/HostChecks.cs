using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Security.Principal;
using System.Text.RegularExpressions;
using StigGpoManager.Core;

namespace StigGpoManager
{
    /// <summary>Pre-flight checks that concern this machine and the running user rather than AD content.</summary>
    public static class HostChecks
    {
        public static string CurrentUser()
        {
            try { return WindowsIdentity.GetCurrent().Name; }
            catch { return Environment.UserDomainName + "\\" + Environment.UserName; }
        }

        public static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static List<CheckResult> Modules()
        {
            var results = new List<CheckResult>();
            using (var ps = PowerShell.Create())
            {
                foreach (var m in new[] { "GroupPolicy", "ActiveDirectory", "Microsoft.PowerShell.Archive" })
                {
                    ps.Commands.Clear();
                    ps.AddCommand("Get-Module").AddParameter("ListAvailable").AddParameter("Name", m);
                    var found = ps.Invoke().Count > 0;
                    if (found) results.Add(CheckResult.Pass($"Module {m}"));
                    else if (m == "Microsoft.PowerShell.Archive") results.Add(CheckResult.Warn($"Module {m}", "Not found (the tool uses .NET zip support, so this is informational)."));
                    else results.Add(CheckResult.Fail($"Module {m}", "Not installed. Install RSAT (Group Policy Management / AD DS tools) or run on a DC."));
                }
            }
            return results;
        }

        public static CheckResult Elevation() =>
            IsElevated() ? CheckResult.Pass("Running elevated") : CheckResult.Fail("Running elevated", "Not elevated; GPO changes will fail under UAC. Restart the tool as administrator.");

        /// <summary>Domain Admins = &lt;domain SID&gt;-512 in the current (elevated) token.</summary>
        public static CheckResult DomainAdmin(string domainSid)
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    var sids = id.Groups?.Select(g => g.Value).ToList() ?? new List<string>();
                    bool da = !string.IsNullOrEmpty(domainSid)
                        ? sids.Contains(domainSid + "-512", StringComparer.OrdinalIgnoreCase)
                        : sids.Any(s => s.EndsWith("-512"));
                    bool ea = sids.Any(s => s.EndsWith("-519"));
                    if (da) return CheckResult.Pass("Domain Admins membership", id.Name);
                    if (ea) return CheckResult.Warn("Domain Admins membership", $"{id.Name} is Enterprise Admin but not Domain Admin; most actions should still work.");
                    return CheckResult.Warn("Domain Admins membership", $"{id.Name} is not a member of Domain Admins; most actions will fail.");
                }
            }
            catch (Exception ex) { return CheckResult.Warn("Domain Admins membership", ex.Message); }
        }

        /// <summary>Optional DC health check: dcdiag SYSVOL / DFSR / advertising tests against the target DC.</summary>
        public static CheckResult DcDiag(string server, int timeoutSeconds = 120)
        {
            var exe = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\dcdiag.exe");
            if (!System.IO.File.Exists(exe)) return CheckResult.Warn("DC health (dcdiag)", "dcdiag.exe not found; SYSVOL replication not checked.");
            try
            {
                var psi = new ProcessStartInfo(exe, $"/s:{server} /test:SysVolCheck /test:DFSREvent /test:Advertising /test:Replications")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutSeconds * 1000))
                    {
                        try { p.Kill(); } catch { }
                        return CheckResult.Warn("DC health (dcdiag)", $"dcdiag did not finish within {timeoutSeconds}s.");
                    }
                    var output = outTask.Result;
                    var failed = Regex.Matches(output, @"failed test (\S+)", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
                    return failed.Count == 0
                        ? CheckResult.Pass("DC health (dcdiag)", $"SysVolCheck, DFSREvent, Advertising, Replications passed on {server}")
                        : CheckResult.Warn("DC health (dcdiag)", $"Failed: {string.Join(", ", failed)}. GPO changes depend on SYSVOL replication; investigate before linking.");
                }
            }
            catch (Exception ex) { return CheckResult.Warn("DC health (dcdiag)", ex.Message); }
        }
    }
}
