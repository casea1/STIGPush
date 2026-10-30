using System;
using System.Collections.Generic;
using System.Linq;
using StigGpoManager.Core;

namespace StigGpoManager
{
    /// <summary>Config + AD connection + engine, shared by the GUI and the CLI.</summary>
    public sealed class Session : IDisposable
    {
        public string ConfigPath { get; }
        public AppConfig Config { get; private set; }
        public List<ConfigIssue> Issues { get; private set; } = new List<ConfigIssue>();
        public IGpoDirectory Directory { get; private set; }
        /// <summary>True in /demo mode: an in-memory domain, no AD access at all.</summary>
        public bool IsDemo { get; private set; }
        public StigEngine Engine { get; private set; }
        public string User { get; } = HostChecks.CurrentUser();
        public List<CheckResult> HostResults { get; } = new List<CheckResult>();

        public bool ConfigValid => Config != null && !Issues.Any(i => i.IsError);
        public bool Connected => Engine != null;

        public Session(string configPath)
        {
            ConfigPath = configPath;
            LoadConfig();
        }

        public void LoadConfig()
        {
            Config = AppConfig.Load(ConfigPath, out var issues);
            Issues = issues;
        }

        /// <summary>Checks modules, connects to the target DC and builds the engine. Throws if not possible.</summary>
        public void Connect(Action<LogLevel, string> log, Action<string> commandLog = null)
        {
            if (!ConfigValid) throw new InvalidOperationException("config.json has errors; fix them first.");
            HostResults.Clear();
            HostResults.Add(HostChecks.Elevation());
            HostResults.AddRange(HostChecks.Modules());
            if (HostResults.Any(r => r.Status == CheckStatus.Fail && r.Name.StartsWith("Module")))
                throw new InvalidOperationException("Required PowerShell modules are missing: " + string.Join("; ", HostResults.Where(r => r.Status == CheckStatus.Fail).Select(r => r.Name)));

            (Directory as IDisposable)?.Dispose();
            var ps = new PowerShellDirectory(Config);
            if (commandLog != null) ps.CommandLog += commandLog;
            Directory = ps;
            BuildEngine(log);
        }

        /// <summary>/demo: in-memory domain seeded with sample data (see DemoData). Nothing touches AD.</summary>
        public void ConnectDemo(IGpoDirectory directory, Action<LogLevel, string> log)
        {
            IsDemo = true;
            HostResults.Clear();
            HostResults.Add(CheckResult.Warn("Demo mode", "Simulated domain; no Active Directory or SYSVOL access. Data is in " + Config.WorkingFolder));
            Directory = directory;
            BuildEngine(log);
        }

        private void BuildEngine(Action<LogLevel, string> log)
        {
            var audit = new AuditLog(Config.AuditLogPath, User);
            Engine = new StigEngine(Config, Directory, audit, new RunStore(Config.RunsFolder), User);
            Engine.PromotionChecks.Add(new ScanGateCheck(Engine));
            if (log != null) Engine.Log += log;
        }

        /// <summary>Full pre-flight: host checks, AD checks, Domain Admins, optional dcdiag.</summary>
        public List<CheckResult> Preflight(bool runDcDiag)
        {
            var results = new List<CheckResult>();
            results.Add(ConfigValid ? CheckResult.Pass("Config file valid", ConfigPath)
                                    : CheckResult.Fail("Config file valid", string.Join(" | ", Issues.Where(i => i.IsError).Select(i => i.Message))));
            foreach (var w in Issues.Where(i => !i.IsError)) results.Add(CheckResult.Warn("Config", w.Message));
            results.AddRange(HostResults);
            if (Engine == null) return results;
            var ad = Engine.Preflight();
            results.AddRange(ad);
            string sid = null;
            try { sid = Engine.Domain.DomainSid; } catch { }
            if (!IsDemo) results.Add(HostChecks.DomainAdmin(sid));
            if (IsDemo) return results;
            if (runDcDiag) results.Add(HostChecks.DcDiag(Directory.Server));
            return results;
        }

        public void Dispose() => (Directory as IDisposable)?.Dispose();
    }
}
