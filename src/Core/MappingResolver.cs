using System;
using System.Collections.Generic;
using System.Linq;

namespace StigGpoManager.Core
{
    /// <summary>One row of the Load Package grid: a backup, the mapping it matched and the proposed GPO name.</summary>
    public sealed class ImportCandidate
    {
        public GpoBackupInfo Backup { get; set; }
        public GpoMapping Mapping { get; set; }
        /// <summary>All mappings whose regex matched (more than one is a config problem worth showing).</summary>
        public List<GpoMapping> AllMatches { get; set; } = new List<GpoMapping>();
        /// <summary>For an unmatched backup: the mapping whose name looks most similar (a hint, never applied automatically).</summary>
        public GpoMapping SuggestedMapping { get; set; }
        public StigVersion Version { get; set; }
        public bool Skip { get; set; }
        public string Problem { get; set; }

        public bool IsMatched => Mapping != null;
        public string ProposedName => Mapping != null && Version != null ? Mapping.RenderName(Version) : null;
    }

    public static class MappingResolver
    {
        /// <summary>
        /// Matches each backup to the first mapping whose regex matches its display name.
        /// Unmatched backups, and backups whose version cannot be parsed, default to Skip.
        /// </summary>
        public static List<ImportCandidate> Resolve(IEnumerable<GpoBackupInfo> backups, AppConfig config)
        {
            var list = new List<ImportCandidate>();
            foreach (var b in backups)
            {
                var c = new ImportCandidate { Backup = b, Version = b.DetectedVersion };
                c.AllMatches = config.Mappings.Where(m => m.IsMatch(b.DisplayName)).ToList();
                c.Mapping = c.AllMatches.FirstOrDefault();
                if (c.Mapping == null)
                {
                    c.Skip = true;
                    c.SuggestedMapping = Closest(b.DisplayName, config.Mappings);
                    c.Problem = c.SuggestedMapping == null
                        ? "No mapping matches this backup; it will be skipped unless you assign one."
                        : $"No mapping matches. Closest: '{c.SuggestedMapping.NameTemplate}' - pick it in the Mapping column if that is right.";
                }
                else if (c.AllMatches.Count > 1)
                {
                    c.Problem = $"Matches {c.AllMatches.Count} mappings; using the first ({c.Mapping.NameTemplate}).";
                }
                if (c.Version == null)
                {
                    c.Skip = true;
                    c.Problem = (c.Problem == null ? "" : c.Problem + " ") + "Version could not be detected; enter it to import.";
                }
                list.Add(c);
            }
            PickDuplicates(list);
            FlagConflicts(list);
            return list;
        }

        /// <summary>
        /// DISA packages sometimes carry the same GPO twice (e.g. two Adobe backups producing the same name).
        /// Keep one, chosen the same way every time: highest version, then newest backup time, then the
        /// backup folder path (ordinal). The others are skipped with a note; the admin can still swap them.
        /// </summary>
        public static void PickDuplicates(List<ImportCandidate> candidates)
        {
            foreach (var g in candidates.Where(c => !c.Skip && c.ProposedName != null)
                                        .GroupBy(c => c.ProposedName, StringComparer.OrdinalIgnoreCase)
                                        .Where(g => g.Count() > 1).ToList())
            {
                var ordered = g.OrderByDescending(c => c.Version)
                               .ThenByDescending(c => BackupTime(c.Backup))
                               .ThenBy(c => c.Backup.BackupFolder ?? c.Backup.BackupId ?? "", StringComparer.OrdinalIgnoreCase)
                               .ToList();
                var keep = ordered[0];
                var keepHash = ReportHash(keep.Backup);
                foreach (var c in ordered.Skip(1))
                {
                    c.Skip = true;
                    var same = keepHash != null && keepHash == ReportHash(c.Backup);
                    c.Problem = $"Duplicate of the backup in {Where(keep.Backup)} (same name '{g.Key}'{(same ? ", identical settings" : "")}); skipped. " +
                                "The tool keeps the highest version / newest backup; tick this one and untick the other to swap.";
                }
                keep.Problem = (keep.Problem == null ? "" : keep.Problem + " ") +
                               $"{ordered.Count - 1} duplicate(s) of this backup in the package were skipped (this one is the newest).";
            }
        }

        private static DateTime BackupTime(GpoBackupInfo b) =>
            DateTime.TryParse(b.BackupTime, System.Globalization.CultureInfo.InvariantCulture,
                              System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue;

        private static string Where(GpoBackupInfo b) =>
            string.IsNullOrEmpty(b.ProductFolder) ? b.BackupId : b.ProductFolder + "\\" + System.IO.Path.GetFileName(b.BackupFolder ?? b.BackupId);

        private static string ReportHash(GpoBackupInfo b)
        {
            try { return b.ReportPath != null && System.IO.File.Exists(b.ReportPath) ? PackageScanner.ComputeSha256(b.ReportPath) : null; }
            catch { return null; }
        }

        /// <summary>Mapping whose name template shares the most words with the backup name (needs at least 60% overlap).</summary>
        public static GpoMapping Closest(string backupName, IEnumerable<GpoMapping> mappings)
        {
            var b = NameVariants.Tokens(backupName);
            GpoMapping best = null;
            double bestScore = 0;
            foreach (var m in mappings)
            {
                var t = NameVariants.Tokens(m.NameTemplate);
                if (t.Count == 0 || b.Count == 0) continue;
                double score = (double)t.Intersect(b).Count() / Math.Max(t.Count, b.Count);
                if (score > bestScore) { bestScore = score; best = m; }
            }
            return bestScore >= 0.6 ? best : null;
        }

        /// <summary>Two selected backups that would produce the same GPO name cannot both be imported.</summary>
        public static void FlagConflicts(List<ImportCandidate> candidates)
        {
            foreach (var g in candidates.Where(c => !c.Skip && c.ProposedName != null)
                                        .GroupBy(c => c.ProposedName, StringComparer.OrdinalIgnoreCase)
                                        .Where(g => g.Count() > 1))
            {
                foreach (var c in g)
                    c.Problem = $"Another selected backup also produces '{g.Key}'. Skip one of them or fix the mapping regexes.";
            }
        }

        /// <summary>Validation before import: returns blocking problems for selected rows.</summary>
        public static List<string> ValidateSelection(IEnumerable<ImportCandidate> candidates)
        {
            var problems = new List<string>();
            var selected = candidates.Where(c => !c.Skip).ToList();
            if (selected.Count == 0) problems.Add("No backups are selected for import.");
            foreach (var c in selected)
            {
                if (c.Mapping == null) problems.Add($"{c.Backup.DisplayName}: no mapping assigned.");
                if (c.Version == null) problems.Add($"{c.Backup.DisplayName}: version is unknown.");
            }
            foreach (var g in selected.Where(c => c.ProposedName != null).GroupBy(c => c.ProposedName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                problems.Add($"More than one selected backup would be imported as '{g.Key}'.");
            return problems;
        }
    }
}
