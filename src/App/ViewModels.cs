using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using StigGpoManager.Core;

namespace StigGpoManager
{
    public abstract class Row : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected void ChangedAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>Load-package grid row: one DISA backup and the mapping/version it will be imported with.</summary>
    public sealed class BackupRow : Row
    {
        public ImportCandidate Candidate { get; }
        private readonly string _lockedReason;

        public BackupRow(ImportCandidate c, string lockedReason = null)
        {
            Candidate = c;
            _lockedReason = lockedReason;
            if (lockedReason != null) c.Skip = true;
        }

        public bool Skip
        {
            get => Candidate.Skip;
            set
            {
                if (_lockedReason != null) { Changed(); return; }
                Candidate.Skip = value;
                Changed(); Changed(nameof(Problem));
            }
        }

        public string DisplayName => Candidate.Backup.DisplayName;
        public string Product => Candidate.Backup.ProductFolder;
        public string Scope => Candidate.Backup.Scope;
        public bool IsUnmatched => Candidate.Mapping == null;
        public string ProposedName => Candidate.ProposedName;

        public string Version
        {
            get => Candidate.Version?.ToString() ?? "";
            set
            {
                try { Candidate.Version = string.IsNullOrWhiteSpace(value) ? null : StigVersion.ParseUserInput(value); _error = null; }
                catch (FormatException ex) { _error = ex.Message; }
                ChangedAll();
            }
        }

        public GpoMapping Mapping
        {
            get => Candidate.Mapping;
            set
            {
                bool wasUnmatched = Candidate.Mapping == null;
                Candidate.Mapping = value;
                if (wasUnmatched && value != null && Candidate.Version != null && _lockedReason == null) Candidate.Skip = false;
                ChangedAll();
            }
        }

        private string _error;
        public string Problem
        {
            get
            {
                if (_lockedReason != null) return _lockedReason;
                if (_error != null) return _error;
                if (Candidate.Mapping == null)
                    return Candidate.SuggestedMapping != null
                        ? $"No mapping matches. Closest: {Candidate.SuggestedMapping.NameTemplate}"
                        : "No mapping matches; skipped unless you assign one.";
                if (Candidate.Version == null) return "Enter the version (e.g. v1r4).";
                return Candidate.AllMatches.Count > 1 && Candidate.AllMatches.Contains(Candidate.Mapping) ? Candidate.Problem : "";
            }
        }
    }

    public sealed class StoreRow : Row
    {
        public CentralStoreItem Item { get; }
        public StoreRow(CentralStoreItem item) { Item = item; }
        public string File => Item.Source.RelativeTarget;
        public string Status => Item.StatusText;
        public string PackageDate => Item.Source.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string StoreDate => Item.StoreLastWriteUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
        public string Source => Item.Source.FullPath;
        public bool Override
        {
            get => Item.OverrideDowngrade;
            set
            {
                Item.OverrideDowngrade = value && Item.Status == CentralStoreStatus.SkippedStoreNewer;
                Changed(); Changed(nameof(Status));
            }
        }
    }

    public sealed class ItemRow : Row
    {
        public RunItem Item { get; }
        private bool _selected;
        public ItemRow(RunItem item, bool selected, string touched = null) { Item = item; _selected = selected; Touched = touched; }
        public bool Selected { get => _selected; set { _selected = value; Changed(); } }
        public string Name => Item.NewGpoName;
        public string Id => Item.NewGpoId;
        public string Mode => Item.PilotMode.ToString();
        public string PilotTargets => Item.PilotMode == PilotMode.Group
            ? $"group {Item.PilotGroup} at: " + string.Join("; ", Item.ProdTargets)
            : Item.PilotMode == PilotMode.None ? "(no pilot)" : string.Join("; ", Item.PilotTargets);
        public string ProdTargets => string.Join("; ", Item.ProdTargets);
        public string Previous => Item.PreviousGpoName ?? "(none)";
        public string Status => Item.Status.ToString();
        public string Error => Item.LastError;
        public string Touched { get; }
    }

    public sealed class RunRow
    {
        public RunState State { get; set; }
        public string RunId => State.RunId;
        public string Created => State.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string CreatedBy => State.CreatedBy;
        public string Package => State.Package?.Name;
        public int Items => State.Items.Count(i => i.NewGpoId != null);
        public string Status { get; set; }
    }

    public sealed class CleanupRow : Row
    {
        public CleanupCandidate Candidate { get; }
        private bool _selected;
        public CleanupRow(CleanupCandidate c) { Candidate = c; }
        public bool Selected { get => _selected; set { _selected = value; Changed(); } }
        public string Name => Candidate.Gpo.DisplayName;
        public string Mapping => Candidate.MappingKey;
        public string Version => Candidate.Version;
        public string Latest => Candidate.LatestVersion;
        public string Reason => Candidate.Reason;
        public string Modified => Candidate.Gpo.ModificationTime == DateTime.MinValue ? "" : Candidate.Gpo.ModificationTime.ToString("yyyy-MM-dd HH:mm");
    }

    public sealed class ScanRow
    {
        public ScanImportRecord Record { get; set; }
        public string Stage => Record.Stage;
        public string OriginalFileName => Record.OriginalFileName;
        public string Format => Record.Format;
        public string HostsText => string.Join(", ", Record.Hosts);
        public int OpenCount => Record.OpenCount;
        public int TotalCount => Record.TotalCount;
        public string Imported => Record.ImportedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public sealed class FindingRow
    {
        public ScanFinding Finding { get; set; }
        public ExceptionEntry Entry { get; set; }
        public string Result => Entry != null ? "Expected" : Finding.Status == ScanStatus.Error ? "BLOCKING (error)" : "BLOCKING";
        public int Severity => Entry == null ? 2 : 0;
        public string Host => Finding.Host;
        public string VulnId => Finding.DisplayId;
        public string Stig => Finding.StigName;
        public string Title => Finding.Title;
        public string Exception => Entry == null ? "" : $"{Entry.ExceptionGpo} {Entry.PoamReference}".Trim();
    }

    public sealed class RefreshRow : Row
    {
        private string _result = "pending", _message = "";
        public string Computer { get; set; }
        public string Result { get => _result; set { _result = value; Changed(); } }
        public string Message { get => _message; set { _message = value; Changed(); } }
    }

    public sealed class AuditRow
    {
        public string Time { get; set; }
        public string User { get; set; }
        public string Run { get; set; }
        public string Action { get; set; }
        public string Target { get; set; }
        public string Result { get; set; }
        public string Error { get; set; }
    }

    public sealed class LogLine
    {
        public string Text { get; set; }
        public Brush Brush { get; set; }
    }
}
