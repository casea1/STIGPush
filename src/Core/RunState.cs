using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StigGpoManager.Core
{
    public enum RunPhase { Created, PackageLoaded, CentralStoreUpdated, Imported, Reviewed, Piloted, Promoted, RolledBack, Closed }

    public enum ItemStatus { Selected, Imported, Piloted, Promoted, RolledBack, Failed }

    public sealed class PackageRecord
    {
        public string Name { get; set; }
        public string ZipPath { get; set; }
        public string Sha256 { get; set; }
        public string ExpectedSha256 { get; set; }
        /// <summary>null = no expected hash given.</summary>
        public bool? HashMatches { get; set; }
        public string ExtractedPath { get; set; }
        public DateTime LoadedUtc { get; set; }
    }

    /// <summary>One backup selected for import in this run, and everything the run did with it.</summary>
    public sealed class RunItem
    {
        public string BackupId { get; set; }
        public string BackupDisplayName { get; set; }
        public string BackupRoot { get; set; }
        public string ProductFolder { get; set; }
        public string MappingKey { get; set; }
        public string Version { get; set; }
        public string NewGpoName { get; set; }
        public string NewGpoId { get; set; }
        public DateTime? ImportedUtc { get; set; }
        public string PreviousGpoName { get; set; }
        public string PreviousGpoId { get; set; }
        public string PreviousVersion { get; set; }
        /// <summary>IDs of all older versions of this mapping that exist in AD (used for ordering).</summary>
        public List<string> OlderVersionIds { get; set; } = new List<string>();
        public PilotMode PilotMode { get; set; }
        /// <summary>Targets copied from the mapping at import time, so later config edits cannot change what rollback touches.</summary>
        public List<string> PilotTargets { get; set; } = new List<string>();
        public List<string> ProdTargets { get; set; } = new List<string>();
        public string PilotGroup { get; set; }
        public ItemStatus Status { get; set; }
        public string LastError { get; set; }
        public int DiffAdded { get; set; }
        public int DiffRemoved { get; set; }
        public int DiffChanged { get; set; }
        public int ExceptionFlags { get; set; }
        public int ReplacementWarnings { get; set; }
    }

    public sealed class LinkRecord
    {
        public string GpoId { get; set; }
        public string GpoName { get; set; }
        public bool Enabled { get; set; }
        public bool Enforced { get; set; }
        public int Order { get; set; }

        public static LinkRecord From(LinkInfo l) => new LinkRecord { GpoId = l.GpoId, GpoName = l.GpoName, Enabled = l.Enabled, Enforced = l.Enforced, Order = l.Order };
        public LinkInfo ToLinkInfo() => new LinkInfo { GpoId = GpoId, GpoName = GpoName, Enabled = Enabled, Enforced = Enforced, Order = Order };
    }

    /// <summary>Link order on one OU, captured before the run first changed it in a phase.</summary>
    public sealed class OuSnapshot
    {
        public string Phase { get; set; }
        public string Target { get; set; }
        public DateTime TakenUtc { get; set; }
        public List<LinkRecord> Links { get; set; } = new List<LinkRecord>();
    }

    /// <summary>Every link created/enabled/disabled/removed/reordered and every permission change.</summary>
    public sealed class LinkAction
    {
        public DateTime Utc { get; set; }
        public string Phase { get; set; }
        public string Action { get; set; }
        /// <summary>NewGpoId of the run item this action was performed for.</summary>
        public string ForItem { get; set; }
        public string GpoId { get; set; }
        public string GpoName { get; set; }
        public string Target { get; set; }
        public string Detail { get; set; }
        public string Result { get; set; }
        public List<LinkRecord> OrderBefore { get; set; } = new List<LinkRecord>();
        public List<LinkRecord> OrderAfter { get; set; } = new List<LinkRecord>();
    }

    public sealed class PhaseEvent
    {
        public string Phase { get; set; }
        public DateTime Utc { get; set; }
        public string User { get; set; }
        public string Note { get; set; }
    }

    public sealed class Confirmation
    {
        public DateTime Utc { get; set; }
        public string User { get; set; }
        public string Kind { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>Phase 2: a scan result file imported for this run.</summary>
    public sealed class ScanImportRecord
    {
        /// <summary>Baseline (before pilot link) or Pilot.</summary>
        public string Stage { get; set; }
        public string OriginalFileName { get; set; }
        public string StoredPath { get; set; }
        public string Sha256 { get; set; }
        public string Format { get; set; }
        public DateTime ImportedUtc { get; set; }
        public List<string> Hosts { get; set; } = new List<string>();
        public int OpenCount { get; set; }
        public int TotalCount { get; set; }
    }

    /// <summary>Phase 2: outcome of the promotion gate.</summary>
    public sealed class GateRecord
    {
        public DateTime EvaluatedUtc { get; set; }
        public bool Passed { get; set; }
        public int BlockingCount { get; set; }
        public int ExpectedCount { get; set; }
        public string Summary { get; set; }
        public string ReportPath { get; set; }
    }

    /// <summary>
    /// Persistent state of one quarterly run (WorkingFolder\runs\&lt;runId&gt;.json). Lets the tool resume
    /// after the pilot soak, and gives rollback exact records rather than guesswork.
    /// </summary>
    public sealed class RunState
    {
        public int SchemaVersion { get; set; } = 1;
        public string RunId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string CreatedBy { get; set; }
        public string Domain { get; set; }
        public string TargetDc { get; set; }
        public RunPhase Phase { get; set; }
        public DateTime? PilotStartedUtc { get; set; }
        public DateTime? PromotedUtc { get; set; }
        public PackageRecord Package { get; set; } = new PackageRecord();
        public List<RunItem> Items { get; set; } = new List<RunItem>();
        public List<PhaseEvent> History { get; set; } = new List<PhaseEvent>();
        public List<OuSnapshot> Snapshots { get; set; } = new List<OuSnapshot>();
        public List<LinkAction> LinkActions { get; set; } = new List<LinkAction>();
        public List<Confirmation> Confirmations { get; set; } = new List<Confirmation>();
        public List<string> Backups { get; set; } = new List<string>();
        public List<string> Reports { get; set; } = new List<string>();
        public List<ScanImportRecord> ScanImports { get; set; } = new List<ScanImportRecord>();
        public GateRecord Gate { get; set; }
        public string Notes { get; set; }

        public static string NewRunId(DateTime now) =>
            now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);

        public void SetPhase(RunPhase phase, string user, string note = null)
        {
            Phase = phase;
            History.Add(new PhaseEvent { Phase = phase.ToString(), Utc = DateTime.UtcNow, User = user, Note = note });
        }

        /// <summary>The earliest snapshot this run took of the OU (its state before the run touched it).</summary>
        public OuSnapshot EarliestSnapshot(string target) =>
            Snapshots.Where(s => AppConfig.DnEquals(s.Target, target)).OrderBy(s => s.TakenUtc).FirstOrDefault();

        public bool HasSnapshot(string phase, string target) =>
            Snapshots.Any(s => s.Phase == phase && AppConfig.DnEquals(s.Target, target));

        /// <summary>One-line status for the tray icon and run picker, e.g. "Pilot - day 9 of 14".</summary>
        public string StatusText(int soakDays, DateTime nowUtc)
        {
            switch (Phase)
            {
                case RunPhase.Piloted:
                    if (PilotStartedUtc.HasValue)
                    {
                        int day = (int)Math.Floor((nowUtc - PilotStartedUtc.Value).TotalDays) + 1;
                        return soakDays > 0 && day > soakDays
                            ? $"Pilot - day {day} (soak of {soakDays} days complete; ready to promote)"
                            : $"Pilot - day {day} of {soakDays}";
                    }
                    return "Pilot";
                case RunPhase.Promoted: return "Promoted" + (PromotedUtc.HasValue ? " " + PromotedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd") : "");
                case RunPhase.RolledBack: return "Rolled back";
                case RunPhase.Closed: return "Closed";
                default: return Phase.ToString();
            }
        }
    }

    /// <summary>Loads and atomically saves run state files.</summary>
    public sealed class RunStore
    {
        private readonly string _folder;
        public RunStore(string runsFolder) { _folder = runsFolder; }

        public string PathFor(string runId) => Path.Combine(_folder, runId + ".json");
        public string EvidenceFolder(string runId) => Path.Combine(_folder, runId);

        /// <summary>Writes to a temp file, then swaps it in (keeping a .bak), so a crash never leaves a half-written state file.</summary>
        public void Save(RunState state)
        {
            Directory.CreateDirectory(_folder);
            var path = PathFor(state.RunId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonMapper.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                var bak = path + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            File.Move(tmp, path);
        }

        public RunState Load(string runId) => LoadFile(PathFor(runId));

        public static RunState LoadFile(string path)
        {
            var state = JsonMapper.Deserialize<RunState>(File.ReadAllText(path));
            if (string.IsNullOrEmpty(state.RunId)) throw new InvalidDataException($"{path} is not a run state file.");
            return state;
        }

        public List<RunState> LoadAll(Action<string> onError = null)
        {
            var list = new List<RunState>();
            if (!Directory.Exists(_folder)) return list;
            foreach (var f in Directory.GetFiles(_folder, "*.json"))
            {
                try { list.Add(LoadFile(f)); }
                catch (Exception ex) { onError?.Invoke($"Cannot read run file {f}: {ex.Message}"); }
            }
            return list.OrderByDescending(r => r.CreatedUtc).ToList();
        }

        /// <summary>Most recent run that is not Closed / RolledBack (the tray shows this one).</summary>
        public RunState ActiveRun(Action<string> onError = null) =>
            LoadAll(onError).FirstOrDefault(r => r.Phase != RunPhase.Closed && r.Phase != RunPhase.RolledBack);
    }
}
