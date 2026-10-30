using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StigGpoManager.Core
{
    public sealed partial class StigEngine
    {
        public const string StageBaseline = "Baseline";
        public const string StagePilot = "Pilot";

        public ExceptionsRegister LoadRegister() => ExceptionsRegister.Load(Config.ExceptionsRegisterPath);

        public void SaveRegister(ExceptionsRegister register)
        {
            register.Save(Config.ExceptionsRegisterPath);
            Audit.Write(null, "SaveExceptionsRegister", Config.ExceptionsRegisterPath, "Success", null, $"{register.Entries.Count} entries");
        }

        /// <summary>Parses a scan result file, copies it into the run's evidence folder and records it.</summary>
        public ScanImportRecord ImportScanResult(RunState run, string path, string stage)
        {
            if (stage != StageBaseline && stage != StagePilot) throw new ArgumentException("Stage must be Baseline or Pilot.", nameof(stage));
            var parsed = ScanParser.Parse(path);
            var folder = Path.Combine(Store.EvidenceFolder(run.RunId), "scans");
            System.IO.Directory.CreateDirectory(folder);
            var hash = PackageScanner.ComputeSha256(path);
            if (run.ScanImports.Any(s => s.Sha256 == hash && s.Stage == stage))
            {
                Warn($"{Path.GetFileName(path)} was already imported as {stage}; skipped.");
                return run.ScanImports.First(s => s.Sha256 == hash && s.Stage == stage);
            }
            var stored = Path.Combine(folder, $"{stage}-{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(path)}");
            File.Copy(path, stored, false);
            var rec = new ScanImportRecord
            {
                Stage = stage, OriginalFileName = Path.GetFileName(path), StoredPath = stored, Sha256 = hash,
                Format = parsed.Format, ImportedUtc = DateTime.UtcNow, Hosts = parsed.Hosts,
                OpenCount = parsed.Findings.Count(f => f.Status == ScanStatus.Open), TotalCount = parsed.Findings.Count,
            };
            run.ScanImports.Add(rec);
            Save(run);
            Audit.Write(run.RunId, "ImportScanResult", rec.OriginalFileName, "Success", null, $"stage={stage}; format={rec.Format}; hosts={string.Join(",", rec.Hosts)}; open={rec.OpenCount}/{rec.TotalCount}; sha256={hash}");
            Ok($"Imported {rec.Format} {rec.OriginalFileName} ({stage}): {string.Join(", ", rec.Hosts)}; {rec.OpenCount} open of {rec.TotalCount}.");
            return rec;
        }

        public void RemoveScanImport(RunState run, ScanImportRecord rec)
        {
            run.ScanImports.Remove(rec);
            Save(run);
            Audit.Write(run.RunId, "RemoveScanResult", rec.OriginalFileName, "Success", null, $"stage={rec.Stage}; file kept as evidence at {rec.StoredPath}");
        }

        private List<ScanFinding> FindingsFor(RunState run, string stage)
        {
            var list = new List<ScanFinding>();
            foreach (var s in run.ScanImports.Where(s => s.Stage == stage))
            {
                try { list.AddRange(ScanParser.Parse(s.StoredPath).Findings); }
                catch (Exception ex) { Warn($"Cannot re-read {s.StoredPath}: {ex.Message}"); }
            }
            return list;
        }

        /// <summary>Evaluates the promotion gate and records the outcome on the run.</summary>
        public ScanGateResult EvaluateGate(RunState run)
        {
            var baseline = run.ScanImports.Any(s => s.Stage == StageBaseline) ? FindingsFor(run, StageBaseline) : null;
            var result = ScanGate.Evaluate(FindingsFor(run, StagePilot), baseline, LoadRegister(), Config.RequireScanGate);
            run.Gate = new GateRecord
            {
                EvaluatedUtc = DateTime.UtcNow, Passed = result.Passed, BlockingCount = result.Blocking.Count,
                ExpectedCount = result.Expected.Count, Summary = result.Summary,
            };
            Save(run);
            Audit.Write(run.RunId, "EvaluateGate", run.RunId, result.Passed ? "Pass" : "Fail", null, result.Summary);
            return result;
        }

        public string WriteRunReport(RunState run, bool includeGate = true)
        {
            ScanGateResult gate = null;
            if (includeGate && run.ScanImports.Count > 0) gate = EvaluateGate(run);
            var path = Reports.WriteRunReport(Config.ReportsFolder, run, Audit, gate);
            run.Reports.Add(path);
            Save(run);
            Ok($"Run report written: {path}");
            return path;
        }
    }

    /// <summary>Blocks Promote while any pilot open finding is not in the exceptions register.</summary>
    public sealed class ScanGateCheck : IPromotionCheck
    {
        private readonly StigEngine _engine;
        public ScanGateCheck(StigEngine engine) { _engine = engine; }
        public string Name => "Scan results gate";

        public CheckResult Evaluate(RunState run)
        {
            if (!_engine.Config.RequireScanGate && run.ScanImports.Count == 0)
                return CheckResult.Warn(Name, "Gate is off (RequireScanGate=false) and no scan results were imported.");
            var r = _engine.EvaluateGate(run);
            if (r.Passed) return CheckResult.Pass(Name, r.Summary);
            var sb = new StringBuilder(r.Summary);
            foreach (var f in r.Blocking.Take(15)) sb.Append($" | {f.Host} {f.VulnId}");
            if (r.Blocking.Count > 15) sb.Append($" | ... and {r.Blocking.Count - 15} more");
            sb.Append(". Fix and re-import results, or add each to the exceptions register with a justification.");
            return CheckResult.Fail(Name, sb.ToString());
        }
    }
}
