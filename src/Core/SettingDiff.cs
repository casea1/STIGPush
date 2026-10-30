using System;
using System.Collections.Generic;
using System.Linq;

namespace StigGpoManager.Core
{
    public enum ChangeKind { Added, Removed, Changed }

    public sealed class SettingChange
    {
        public ChangeKind Kind { get; set; }
        public string Scope { get; set; }
        public string Category { get; set; }
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public bool IsReplacementType { get; set; }
        public GpoSetting OldSetting { get; set; }
        public GpoSetting NewSetting { get; set; }
    }

    public enum ExceptionFindingKind
    {
        /// <summary>DISA changed a setting that an exception GPO also sets.</summary>
        ExceptionMayNeedReview,
        /// <summary>The exception's value now equals DISA's new value.</summary>
        ExceptionMayBeUnnecessary,
        /// <summary>DISA changed a user right / restricted group that an exception also defines; the exception replaces DISA's list entirely.</summary>
        ReplacementTypeConflict
    }

    public sealed class ExceptionFinding
    {
        public ExceptionFindingKind Kind { get; set; }
        public string ExceptionGpo { get; set; }
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string DisaOldValue { get; set; }
        public string DisaNewValue { get; set; }
        public string ExceptionValue { get; set; }
        /// <summary>For replacement-type settings: entries DISA's new list requires that the exception's list does not contain.</summary>
        public List<string> MissingEntries { get; set; } = new List<string>();
        public string Message { get; set; }
    }

    /// <summary>One row in the Review grid / report (a change plus any exception findings).</summary>
    public sealed class ReviewRow
    {
        public string Mapping { get; set; }
        public string NewGpo { get; set; }
        public string PreviousGpo { get; set; }
        public string Change { get; set; }
        public string Scope { get; set; }
        public string Category { get; set; }
        public string Setting { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public string Flag { get; set; }
        public string ExceptionGpo { get; set; }
        public string ExceptionValue { get; set; }
        public string Detail { get; set; }
        /// <summary>0 = info, 1 = review, 2 = prominent warning.</summary>
        public int Severity { get; set; }
    }

    public static class SettingDiff
    {
        /// <summary>Setting-level diff between the previous and new DISA GPO: Added, Removed, Changed (old -> new).</summary>
        public static List<SettingChange> Compare(IEnumerable<GpoSetting> previous, IEnumerable<GpoSetting> current)
        {
            var oldMap = ToMap(previous);
            var newMap = ToMap(current);
            var changes = new List<SettingChange>();
            foreach (var kv in newMap)
            {
                if (!oldMap.TryGetValue(kv.Key, out var old))
                    changes.Add(Make(ChangeKind.Added, null, kv.Value));
                else if (!string.Equals(old.Value, kv.Value.Value, StringComparison.Ordinal))
                    changes.Add(Make(ChangeKind.Changed, old, kv.Value));
            }
            foreach (var kv in oldMap)
                if (!newMap.ContainsKey(kv.Key))
                    changes.Add(Make(ChangeKind.Removed, kv.Value, null));
            return changes.OrderBy(c => c.Scope).ThenBy(c => c.Category).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Dictionary<string, GpoSetting> ToMap(IEnumerable<GpoSetting> settings)
        {
            var map = new Dictionary<string, GpoSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in settings ?? Enumerable.Empty<GpoSetting>()) map[s.Key] = s;
            return map;
        }

        private static SettingChange Make(ChangeKind kind, GpoSetting old, GpoSetting cur)
        {
            var s = cur ?? old;
            return new SettingChange
            {
                Kind = kind, Scope = s.Scope, Category = s.Category, Key = s.Key, DisplayName = s.DisplayName,
                OldValue = old?.Value, NewValue = cur?.Value,
                IsReplacementType = (old?.IsReplacementType ?? false) || (cur?.IsReplacementType ?? false),
                OldSetting = old, NewSetting = cur,
            };
        }

        /// <summary>
        /// Cross-checks DISA changes against exception GPOs linked to the same targets.
        /// <paramref name="exceptionSettings"/> maps exception GPO name to its parsed settings.
        /// </summary>
        public static List<ExceptionFinding> CrossCheck(IEnumerable<SettingChange> changes, IEnumerable<GpoSetting> newDisaSettings,
                                                        IDictionary<string, List<GpoSetting>> exceptionSettings)
        {
            var findings = new List<ExceptionFinding>();
            var changeList = changes.ToList();
            var changedKeys = new HashSet<string>(changeList.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
            var newMap = new Dictionary<string, GpoSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in newDisaSettings) newMap[s.Key] = s;

            foreach (var ex in exceptionSettings)
            {
                var exMap = new Dictionary<string, GpoSetting>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in ex.Value) exMap[s.Key] = s;

                foreach (var change in changeList)
                {
                    if (!exMap.TryGetValue(change.Key, out var exSetting)) continue;
                    if (change.IsReplacementType)
                    {
                        var required = change.NewSetting?.MemberIds ?? new List<string>();
                        var names = change.NewSetting?.MemberNames ?? new List<string>();
                        var have = new HashSet<string>(exSetting.MemberIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                        var missing = new List<string>();
                        for (int i = 0; i < required.Count; i++)
                            if (!have.Contains(required[i])) missing.Add(i < names.Count ? names[i] : required[i]);
                        findings.Add(new ExceptionFinding
                        {
                            Kind = ExceptionFindingKind.ReplacementTypeConflict, ExceptionGpo = ex.Key, Key = change.Key,
                            DisplayName = change.DisplayName, DisaOldValue = change.OldValue, DisaNewValue = change.NewValue,
                            ExceptionValue = exSetting.Value, MissingEntries = missing,
                            Message = $"{change.DisplayName} is replaced, not merged: the exception GPO's list wins entirely. " +
                                      (change.Kind == ChangeKind.Removed
                                          ? "DISA no longer sets it; confirm the exception list is still intended."
                                          : missing.Count == 0
                                              ? "The exception list already contains every entry DISA now sets; confirm the extra entries are still justified."
                                              : "The exception list is missing STIG entries: " + string.Join("; ", missing) + ". Update the exception GPO so it includes every STIG-required entry.")
                        });
                    }
                    else
                    {
                        findings.Add(new ExceptionFinding
                        {
                            Kind = ExceptionFindingKind.ExceptionMayNeedReview, ExceptionGpo = ex.Key, Key = change.Key,
                            DisplayName = change.DisplayName, DisaOldValue = change.OldValue, DisaNewValue = change.NewValue,
                            ExceptionValue = exSetting.Value,
                            Message = $"DISA {change.Kind.ToString().ToLowerInvariant()} this setting and exception GPO '{ex.Key}' overrides it; the exception may need review."
                        });
                    }
                }

                foreach (var kv in exMap)
                {
                    if (!newMap.TryGetValue(kv.Key, out var disa)) continue;
                    if (!string.Equals(disa.Value, kv.Value.Value, StringComparison.Ordinal)) continue;
                    findings.Add(new ExceptionFinding
                    {
                        Kind = ExceptionFindingKind.ExceptionMayBeUnnecessary, ExceptionGpo = ex.Key, Key = kv.Key,
                        DisplayName = disa.DisplayName, DisaNewValue = disa.Value, ExceptionValue = kv.Value.Value,
                        Message = changedKeys.Contains(kv.Key)
                            ? $"DISA's new value now matches exception GPO '{ex.Key}'; the exception may be unnecessary."
                            : $"Exception GPO '{ex.Key}' sets the same value as DISA; the exception may be unnecessary."
                    });
                }
            }
            return findings;
        }

        /// <summary>Builds the grid / report rows for one mapping.</summary>
        public static List<ReviewRow> BuildRows(string mapping, string newGpo, string previousGpo,
                                                List<SettingChange> changes, List<ExceptionFinding> findings)
        {
            var rows = new List<ReviewRow>();
            var byKey = findings.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var c in changes)
            {
                var related = byKey.TryGetValue(c.Key, out var l) ? l : new List<ExceptionFinding>();
                if (related.Count == 0)
                {
                    rows.Add(Row(mapping, newGpo, previousGpo, c, null));
                }
                else
                {
                    foreach (var f in related) rows.Add(Row(mapping, newGpo, previousGpo, c, f));
                }
            }
            var changed = new HashSet<string>(changes.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var f in findings.Where(f => !changed.Contains(f.Key)))
            {
                rows.Add(new ReviewRow
                {
                    Mapping = mapping, NewGpo = newGpo, PreviousGpo = previousGpo, Change = "Unchanged",
                    Setting = f.DisplayName, NewValue = f.DisaNewValue, OldValue = f.DisaNewValue,
                    Flag = FlagText(f.Kind), ExceptionGpo = f.ExceptionGpo, ExceptionValue = f.ExceptionValue,
                    Detail = f.Message, Severity = SeverityOf(f.Kind),
                });
            }
            return rows;
        }

        private static ReviewRow Row(string mapping, string newGpo, string prev, SettingChange c, ExceptionFinding f) => new ReviewRow
        {
            Mapping = mapping, NewGpo = newGpo, PreviousGpo = prev ?? "(none)", Change = c.Kind.ToString(),
            Scope = c.Scope, Category = c.Category, Setting = c.DisplayName, OldValue = c.OldValue, NewValue = c.NewValue,
            Flag = f == null ? (c.IsReplacementType ? "Replacement-type setting" : "") : FlagText(f.Kind),
            ExceptionGpo = f?.ExceptionGpo, ExceptionValue = f?.ExceptionValue, Detail = f?.Message,
            Severity = f == null ? 0 : SeverityOf(f.Kind),
        };

        public static string FlagText(ExceptionFindingKind k)
        {
            switch (k)
            {
                case ExceptionFindingKind.ReplacementTypeConflict: return "!! REPLACEMENT-TYPE: exception list must include every STIG entry";
                case ExceptionFindingKind.ExceptionMayNeedReview: return "Exception may need review";
                default: return "Exception may be unnecessary";
            }
        }

        private static int SeverityOf(ExceptionFindingKind k) =>
            k == ExceptionFindingKind.ReplacementTypeConflict ? 2 : k == ExceptionFindingKind.ExceptionMayNeedReview ? 1 : 0;
    }
}
