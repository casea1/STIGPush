using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StigGpoManager.Core
{
    public sealed class CleanupCandidate
    {
        public GpoInfo Gpo { get; set; }
        public string MappingKey { get; set; }
        public string Version { get; set; }
        public string LatestVersion { get; set; }
        public int LinkCount { get; set; }
        public string Reason { get; set; }
    }

    public sealed partial class StigEngine
    {
        public const string PhasePilot = "Pilot";
        public const string PhasePromote = "Promote";
        public const string PhaseRollback = "Rollback";

        public const string AuthenticatedUsers = "Authenticated Users";

        public string DcBackupAcknowledgement =>
            $"This domain has a single domain controller ({Domain.DomainControllers.FirstOrDefault()}). " +
            "I confirm a FRESH system-state backup of it was taken and verified before this change.";

        private bool IsSingleDcTarget(string target) =>
            AppConfig.DnEquals(target, Config.DomainControllersOuDn) && Domain.DomainControllers.Count <= 1;

        private static LinkInfo NewLinkFor(RunItem item) =>
            new LinkInfo { GpoId = item.NewGpoId, GpoName = item.NewGpoName, Enabled = true };

        private void Snapshot(RunState run, string phase, string target, List<LinkInfo> links)
        {
            if (run.HasSnapshot(phase, target)) return;
            run.Snapshots.Add(new OuSnapshot
            {
                Phase = phase, Target = target, TakenUtc = DateTime.UtcNow,
                Links = LinkOrdering.Sorted(links).Select(LinkRecord.From).ToList(),
            });
            Save(run);
        }

        private void Record(RunState run, string phase, string action, RunItem item, string gpoId, string gpoName, string target,
                            string detail, List<LinkInfo> before, List<LinkInfo> after)
        {
            run.LinkActions.Add(new LinkAction
            {
                Utc = DateTime.UtcNow, Phase = phase, Action = action, ForItem = item?.NewGpoId,
                GpoId = gpoId, GpoName = gpoName, Target = target, Detail = detail, Result = "Success",
                OrderBefore = (before ?? new List<LinkInfo>()).Select(LinkRecord.From).ToList(),
                OrderAfter = (after ?? new List<LinkInfo>()).Select(LinkRecord.From).ToList(),
            });
            Save(run);
        }

        // ---------------------------------------------------------------- placement helpers

        /// <summary>Describes (read-only) what placing the item's GPO on the target will do.</summary>
        private void PlanPlacement(OperationPlan plan, RunItem item, string target)
        {
            List<LinkInfo> current;
            try
            {
                if (!Directory.ContainerExists(target))
                {
                    plan.Blockers.Add($"Target OU {target} (mapping '{item.MappingKey}') does not exist in AD. Fix the OU in config.json (Settings) or create it in AD Users and Computers.");
                    return;
                }
                current = Directory.GetLinks(target);
            }
            catch (Exception ex) { plan.Blockers.Add($"Cannot read links on {target}: {ex.Message}"); return; }
            var existing = current.FirstOrDefault(l => LinkOrdering.SameGpo(l.GpoId, item.NewGpoId));
            var desired = LinkOrdering.PlaceNewLink(current, NewLinkFor(item), item.OlderVersionIds, Config);
            var pos = desired.First(l => LinkOrdering.SameGpo(l.GpoId, item.NewGpoId)).Order;
            string what = existing == null ? $"Link '{item.NewGpoName}' at order {pos}"
                        : !existing.Enabled ? $"Enable existing link of '{item.NewGpoName}' and set order {pos}"
                        : $"'{item.NewGpoName}' already linked; ensure order {pos}";
            var fixes = LinkOrdering.Verify(current, Config);
            plan.Add("Link", target, what + "\nBefore:\n" + LinkOrdering.Describe(current) + "\nAfter:\n" + LinkOrdering.Describe(desired));
            foreach (var f in fixes) plan.Warnings.Add($"{target}: existing order violates the rules and will be corrected: {f}");
            if (IsSingleDcTarget(target) && !plan.RequiredAcknowledgements.Contains(DcBackupAcknowledgement))
            {
                plan.Warnings.Add($"{target} contains the ONLY domain controller. A bad setting here can lock you out of the domain.");
                plan.RequiredAcknowledgements.Add(DcBackupAcknowledgement);
            }
        }

        /// <summary>
        /// Links (or enables) the item's GPO on the target in the correct position, then re-reads the
        /// order and fixes it if the rules do not hold. Returns problems that could not be fixed.
        /// </summary>
        private List<string> ApplyPlacement(RunState run, string phase, RunItem item, string target)
        {
            var before = Directory.GetLinks(target);
            Snapshot(run, phase, target, before);
            var desired = LinkOrdering.PlaceNewLink(before, NewLinkFor(item), item.OlderVersionIds, Config);
            var pos = desired.First(l => LinkOrdering.SameGpo(l.GpoId, item.NewGpoId)).Order;
            var existing = before.FirstOrDefault(l => LinkOrdering.SameGpo(l.GpoId, item.NewGpoId));

            if (existing == null)
            {
                Do(run, "NewLink", target, () => Directory.NewLink(item.NewGpoId, target, pos, true), $"gpo='{item.NewGpoName}'; order={pos}");
                Record(run, phase, "NewLink", item, item.NewGpoId, item.NewGpoName, target, $"order={pos}", before, Directory.GetLinks(target));
                Ok($"Linked '{item.NewGpoName}' to {target} at order {pos}.");
            }
            else if (!existing.Enabled)
            {
                Do(run, "EnableLink", target, () => Directory.SetLinkEnabled(item.NewGpoId, target, true), $"gpo='{item.NewGpoName}'");
                Record(run, phase, "EnableLink", item, item.NewGpoId, item.NewGpoName, target, null, before, Directory.GetLinks(target));
                Ok($"Enabled link of '{item.NewGpoName}' on {target}.");
            }
            ApplyOrder(run, phase, item, target, desired);
            return VerifyAndFix(run, phase, item, target);
        }

        /// <summary>Issues the Set-GPLink -Order moves needed to reach the desired sequence.</summary>
        private void ApplyOrder(RunState run, string phase, RunItem item, string target, List<LinkInfo> desired)
        {
            var current = Directory.GetLinks(target);
            foreach (var move in LinkOrdering.PlanMoves(current, desired))
            {
                var before = Directory.GetLinks(target);
                Do(run, "SetLinkOrder", target, () => Directory.SetLinkOrder(move.GpoId, target, move.NewOrder), $"gpo='{move.GpoName}'; order={move.NewOrder}");
                Record(run, phase, "SetLinkOrder", item, move.GpoId, move.GpoName, target, $"order={move.NewOrder}", before, Directory.GetLinks(target));
                Info($"  {target}: {move}");
            }
        }

        private List<string> VerifyAndFix(RunState run, string phase, RunItem item, string target)
        {
            var links = Directory.GetLinks(target);
            bool newShouldBeLinked = item != null && links.Any(l => LinkOrdering.SameGpo(l.GpoId, item.NewGpoId) && l.Enabled);
            var problems = LinkOrdering.Verify(links, Config, newShouldBeLinked ? item.NewGpoId : null, item?.OlderVersionIds);
            if (problems.Count == 0) { Ok($"  Verified order on {target}."); return problems; }

            foreach (var p in problems) Warn($"  {target}: {p} - fixing.");
            var desired = newShouldBeLinked
                ? LinkOrdering.PlaceNewLink(links, NewLinkFor(item), item.OlderVersionIds, Config)
                : LinkOrdering.MakeCompliant(links, Config);
            ApplyOrder(run, phase, item, target, desired);
            var after = Directory.GetLinks(target);
            var remaining = LinkOrdering.Verify(after, Config, newShouldBeLinked ? item.NewGpoId : null, item?.OlderVersionIds);
            Audit.Write(run.RunId, "VerifyOrder", target, remaining.Count == 0 ? "Fixed" : "Failure",
                        remaining.Count == 0 ? null : string.Join(" | ", remaining), string.Join(" | ", problems));
            if (remaining.Count == 0) Ok($"  Order on {target} corrected and verified.");
            else foreach (var r in remaining) Error($"  {target}: STILL WRONG: {r}");
            return remaining;
        }

        private void RequireAck(OperationPlan plan, bool dcBackupConfirmed, RunState run)
        {
            if (plan.RequiredAcknowledgements.Contains(DcBackupAcknowledgement))
            {
                if (!dcBackupConfirmed)
                    throw new InvalidOperationException("A system-state backup confirmation is required before changing links on the Domain Controllers OU of a single-DC domain.");
                run.Confirmations.Add(new Confirmation { Utc = DateTime.UtcNow, User = User, Kind = "SystemStateBackup", Detail = DcBackupAcknowledgement });
                Audit.Write(run.RunId, "ConfirmSystemStateBackup", Config.DomainControllersOuDn, "Confirmed", null, DcBackupAcknowledgement);
                Save(run);
            }
        }

        private void SetGroupFiltering(RunState run, string phase, RunItem item, bool pilot)
        {
            if (pilot)
            {
                // Grant pilot group Apply first, then drop Authenticated Users to Read (Read stays so the GPO can be processed).
                Do(run, "SetPermission", item.NewGpoName, () => Directory.SetPermission(item.NewGpoId, item.PilotGroup, "Group", "GpoApply"), $"{item.PilotGroup}=GpoApply");
                Record(run, phase, "SetPermission", item, item.NewGpoId, item.NewGpoName, "(security filtering)", $"{item.PilotGroup}=GpoApply", null, null);
                Do(run, "SetPermission", item.NewGpoName, () => Directory.SetPermission(item.NewGpoId, AuthenticatedUsers, "Group", "GpoRead"), $"{AuthenticatedUsers}=GpoRead");
                Record(run, phase, "SetPermission", item, item.NewGpoId, item.NewGpoName, "(security filtering)", $"{AuthenticatedUsers}=GpoRead", null, null);
                Ok($"Security filtering on '{item.NewGpoName}' set to group {item.PilotGroup}.");
            }
            else
            {
                Do(run, "SetPermission", item.NewGpoName, () => Directory.SetPermission(item.NewGpoId, AuthenticatedUsers, "Group", "GpoApply"), $"{AuthenticatedUsers}=GpoApply");
                Record(run, phase, "SetPermission", item, item.NewGpoId, item.NewGpoName, "(security filtering)", $"{AuthenticatedUsers}=GpoApply", null, null);
                if (!string.IsNullOrEmpty(item.PilotGroup))
                {
                    Do(run, "SetPermission", item.NewGpoName, () => Directory.SetPermission(item.NewGpoId, item.PilotGroup, "Group", "None"), $"{item.PilotGroup}=None");
                    Record(run, phase, "SetPermission", item, item.NewGpoId, item.NewGpoName, "(security filtering)", $"{item.PilotGroup}=None", null, null);
                }
                Ok($"Security filtering on '{item.NewGpoName}' restored to Authenticated Users.");
            }
        }

        // ================================================================ 5. pilot

        public List<RunItem> PilotableItems(RunState run) =>
            run.Items.Where(i => i.NewGpoId != null && (i.Status == ItemStatus.Imported || i.Status == ItemStatus.RolledBack)).ToList();

        public OperationPlan PlanPilot(RunState run, IEnumerable<RunItem> items = null)
        {
            var plan = new OperationPlan { Title = $"Pilot - run {run.RunId}" };
            var list = (items ?? PilotableItems(run)).ToList();
            if (list.Count == 0) plan.Blockers.Add("No imported GPOs are waiting for pilot.");
            foreach (var item in list)
            {
                switch (item.PilotMode)
                {
                    case PilotMode.OU:
                        foreach (var t in item.PilotTargets) PlanPlacement(plan, item, t);
                        plan.Warnings.AddRange(PilotInheritanceWarnings(item));
                        break;
                    case PilotMode.Group:
                        plan.Add("Security filter", item.NewGpoName, $"Grant {item.PilotGroup} GpoApply; set {AuthenticatedUsers} to GpoRead (only pilot group members apply it)");
                        foreach (var t in item.ProdTargets) PlanPlacement(plan, item, t);
                        break;
                    default:
                        plan.Warnings.Add($"'{item.NewGpoName}' has no pilot configured; it will be linked directly at Promote.");
                        break;
                }
            }
            return plan;
        }

        public void ExecutePilot(RunState run, IEnumerable<RunItem> items = null, bool dcBackupConfirmed = false)
        {
            var list = (items ?? PilotableItems(run)).ToList();
            var plan = PlanPilot(run, list);
            if (plan.Blockers.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Blockers));
            RequireAck(plan, dcBackupConfirmed, run);

            var unresolved = new List<string>();
            foreach (var item in list.Where(i => i.PilotMode != PilotMode.None))
            {
                Info($"Pilot: '{item.NewGpoName}' ({item.PilotMode}-based)");
                try
                {
                    if (item.PilotMode == PilotMode.Group)
                    {
                        SetGroupFiltering(run, PhasePilot, item, pilot: true);
                        foreach (var t in item.ProdTargets) unresolved.AddRange(ApplyPlacement(run, PhasePilot, item, t));
                    }
                    else
                    {
                        foreach (var t in item.PilotTargets) unresolved.AddRange(ApplyPlacement(run, PhasePilot, item, t));
                    }
                    item.Status = ItemStatus.Piloted;
                    item.LastError = null;
                }
                catch (Exception ex)
                {
                    item.LastError = ex.Message;
                    Save(run);
                    throw;
                }
                Save(run);
            }
            if (!run.PilotStartedUtc.HasValue || run.Phase < RunPhase.Piloted) run.PilotStartedUtc = DateTime.UtcNow;
            run.SetPhase(RunPhase.Piloted, User);
            Save(run);
            if (unresolved.Count > 0) throw new InvalidOperationException("Pilot linked, but link order could not be verified:" + Environment.NewLine + string.Join(Environment.NewLine, unresolved));
            Ok("Pilot links complete. Next: gpupdate on pilot machines, SCC / Evaluate-STIG scans, persona tests, soak period.");
        }

        public static readonly string[] PilotChecklist =
        {
            "Run gpupdate /force on pilot machines (or reboot them).",
            "Run SCC / Evaluate-STIG scans on pilot machines and keep the results (phase 2 gate imports them).",
            "Test each persona (admin, standard user, service accounts, line-of-business apps).",
            "Soak for 1-2 weeks; this run is saved and can be resumed after closing the app or rebooting.",
        };

        // ================================================================ 6. promote

        public List<RunItem> PromotableItems(RunState run) =>
            run.Items.Where(i => i.NewGpoId != null &&
                                 (i.Status == ItemStatus.Piloted || (i.PilotMode == PilotMode.None && (i.Status == ItemStatus.Imported || i.Status == ItemStatus.RolledBack))))
                     .ToList();

        public List<CheckResult> EvaluatePromotionChecks(RunState run)
        {
            var results = new List<CheckResult>();
            foreach (var c in PromotionChecks)
            {
                try { results.Add(c.Evaluate(run)); }
                catch (Exception ex) { results.Add(CheckResult.Fail(c.Name, ex.Message)); }
            }
            return results;
        }

        public OperationPlan PlanPromote(RunState run, IEnumerable<RunItem> items = null)
        {
            var plan = new OperationPlan { Title = $"PROMOTE to production - run {run.RunId}", ConfirmationWord = "PROMOTE" };
            var list = (items ?? PromotableItems(run)).ToList();
            if (list.Count == 0) plan.Blockers.Add("No piloted GPOs are ready to promote.");
            foreach (var r in EvaluatePromotionChecks(run))
            {
                if (r.Status == CheckStatus.Fail) plan.Blockers.Add($"{r.Name}: {r.Detail}");
                else if (r.Status == CheckStatus.Warning) plan.Warnings.Add($"{r.Name}: {r.Detail}");
            }
            foreach (var item in list)
            {
                if (item.PilotMode == PilotMode.Group)
                    plan.Add("Security filter", item.NewGpoName, $"Restore {AuthenticatedUsers} GpoApply; remove {item.PilotGroup}");
                foreach (var t in item.ProdTargets)
                {
                    PlanPlacement(plan, item, t);
                    try
                    {
                        foreach (var old in Directory.GetLinks(t).Where(l => l.Enabled && item.OlderVersionIds.Any(id => LinkOrdering.SameGpo(id, l.GpoId))))
                            plan.Add("Disable", t, $"Disable (not remove) link of previous version '{old.GpoName}'");
                    }
                    catch (Exception ex) { plan.Blockers.Add($"Cannot read links on {t}: {ex.Message}"); }
                }
                if (item.PilotMode == PilotMode.OU)
                    foreach (var p in item.PilotTargets.Where(p => !item.ProdTargets.Any(q => AppConfig.DnEquals(p, q))))
                        plan.Add("Remove", p, $"Remove pilot-only link of '{item.NewGpoName}' (production link on parent OU now applies)");
            }
            return plan;
        }

        public void ExecutePromote(RunState run, string typedConfirmation, IEnumerable<RunItem> items = null, bool dcBackupConfirmed = false)
        {
            if (!string.Equals(typedConfirmation, "PROMOTE", StringComparison.Ordinal))
                throw new InvalidOperationException("Promote requires typing PROMOTE.");
            var list = (items ?? PromotableItems(run)).ToList();
            var plan = PlanPromote(run, list);
            if (plan.Blockers.Count > 0) throw new InvalidOperationException("Promote is blocked:" + Environment.NewLine + string.Join(Environment.NewLine, plan.Blockers));
            RequireAck(plan, dcBackupConfirmed, run);
            Audit.Write(run.RunId, "Promote", string.Join("; ", list.Select(i => i.NewGpoName)), "Started");

            var unresolved = new List<string>();
            foreach (var item in list)
            {
                Info($"Promote: '{item.NewGpoName}'");
                try
                {
                    if (item.PilotMode == PilotMode.Group) SetGroupFiltering(run, PhasePromote, item, pilot: false);
                    foreach (var t in item.ProdTargets)
                    {
                        unresolved.AddRange(ApplyPlacement(run, PhasePromote, item, t));
                        foreach (var old in Directory.GetLinks(t).Where(l => l.Enabled && item.OlderVersionIds.Any(id => LinkOrdering.SameGpo(id, l.GpoId))))
                        {
                            var before = Directory.GetLinks(t);
                            Do(run, "DisableLink", t, () => Directory.SetLinkEnabled(old.GpoId, t, false), $"gpo='{old.GpoName}' (previous version)");
                            Record(run, PhasePromote, "DisableLink", item, old.GpoId, old.GpoName, t, "previous version", before, Directory.GetLinks(t));
                            Ok($"Disabled previous version '{old.GpoName}' on {t}.");
                        }
                        unresolved.AddRange(VerifyAndFix(run, PhasePromote, item, t));
                    }
                    if (item.PilotMode == PilotMode.OU)
                    {
                        foreach (var p in item.PilotTargets.Where(p => !item.ProdTargets.Any(q => AppConfig.DnEquals(p, q))))
                        {
                            var before = Directory.GetLinks(p);
                            if (!before.Any(l => LinkOrdering.SameGpo(l.GpoId, item.NewGpoId))) continue;
                            Snapshot(run, PhasePromote, p, before);
                            Do(run, "RemoveLink", p, () => Directory.RemoveLink(item.NewGpoId, p), $"gpo='{item.NewGpoName}' (pilot-only link)");
                            Record(run, PhasePromote, "RemoveLink", item, item.NewGpoId, item.NewGpoName, p, "pilot-only link", before, Directory.GetLinks(p));
                            Ok($"Removed pilot link of '{item.NewGpoName}' from {p}.");
                        }
                    }
                    item.Status = ItemStatus.Promoted;
                    item.LastError = null;
                }
                catch (Exception ex)
                {
                    item.LastError = ex.Message;
                    Save(run);
                    throw;
                }
                Save(run);
            }
            run.PromotedUtc = DateTime.UtcNow;
            run.SetPhase(RunPhase.Promoted, User);
            Save(run);
            Audit.Write(run.RunId, "Promote", string.Join("; ", list.Select(i => i.NewGpoName)), unresolved.Count == 0 ? "Success" : "Failure", unresolved.Count == 0 ? null : string.Join(" | ", unresolved));
            if (unresolved.Count > 0) throw new InvalidOperationException("Promoted, but link order could not be verified:" + Environment.NewLine + string.Join(Environment.NewLine, unresolved));
            Ok("Promote complete.");
        }

        // ---------------------------------------------------------------- policy refresh

        public List<string> RefreshTargets(RunState run, IEnumerable<RunItem> items = null)
        {
            var computers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            // After promote: the promoted GPOs' production OUs. Before promote: preview all of this run's production OUs.
            var source = items ?? (run.Items.Any(i => i.Status == ItemStatus.Promoted)
                ? run.Items.Where(i => i.Status == ItemStatus.Promoted)
                : run.Items.Where(i => i.NewGpoId != null));
            foreach (var t in source.SelectMany(i => i.ProdTargets).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var c in Directory.GetComputersUnder(t)) computers.Add(c);
            return computers.ToList();
        }

        /// <summary>Computers refreshed at the same time (the directory must be thread-safe for more than 1).</summary>
        public int RefreshParallelism { get; set; } = 1;

        public List<GpUpdateResult> ExecuteRefresh(RunState run, IEnumerable<string> computers, Action<GpUpdateResult> onResult = null)
        {
            var list = computers.ToList();
            var results = new List<GpUpdateResult>();
            var gate = new object();
            System.Threading.Tasks.Parallel.ForEach(list, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, RefreshParallelism) }, c =>
            {
                GpUpdateResult r;
                try { r = Directory.InvokeGpUpdate(c); }
                catch (Exception ex) { r = new GpUpdateResult { Computer = c, Success = false, Message = ex.Message }; }
                lock (gate)
                {
                    Audit.Write(run?.RunId, "InvokeGPUpdate", c, r.Success ? "Success" : "Failure", r.Success ? null : r.Message);
                    results.Add(r);
                    onResult?.Invoke(r);
                }
            });
            results = results.OrderBy(r => list.IndexOf(r.Computer)).ToList();
            Info($"Policy refresh: {results.Count(r => r.Success)} succeeded, {results.Count(r => !r.Success)} failed. Some settings need logoff or reboot to apply.");
            return results;
        }

        // ================================================================ 7. rollback

        public List<RunItem> RollbackableItems(RunState run) =>
            run.Items.Where(i => i.Status == ItemStatus.Piloted || i.Status == ItemStatus.Promoted).ToList();

        private List<string> TouchedTargets(RunState run, RunItem item) =>
            run.LinkActions.Where(a => a.ForItem != null && LinkOrdering.SameGpo(a.ForItem, item.NewGpoId) && a.Target != null && !a.Target.StartsWith("("))
                           .Select(a => a.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public OperationPlan PlanRollback(RunState run, IEnumerable<RunItem> items = null)
        {
            var list = (items ?? RollbackableItems(run)).ToList();
            var plan = new OperationPlan { Title = $"Rollback - run {run.RunId}" };
            if (list.Count == 0) plan.Blockers.Add("Nothing to roll back (no piloted or promoted GPOs in this run).");
            var rolledBackIds = list.Select(i => i.NewGpoId).ToList();
            foreach (var target in list.SelectMany(i => TouchedTargets(run, i)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                List<LinkInfo> current;
                try { current = Directory.GetLinks(target); }
                catch (Exception ex) { plan.Blockers.Add($"Cannot read links on {target}: {ex.Message}"); continue; }
                var snap = run.EarliestSnapshot(target);
                if (snap == null) { plan.Blockers.Add($"No recorded link order for {target}; cannot roll back safely."); continue; }
                var desired = BuildRollbackSequence(run, list, target, current, snap);
                foreach (var l in current)
                {
                    var d = desired.First(x => LinkOrdering.SameGpo(x.GpoId, l.GpoId));
                    if (l.Enabled != d.Enabled) plan.Add(d.Enabled ? "Enable" : "Disable", target, $"{(d.Enabled ? "Re-enable" : "Disable")} link of '{l.GpoName}'");
                }
                plan.Add("Order", target, "Restore recorded prior order\nBefore:\n" + LinkOrdering.Describe(current) + "\nAfter:\n" + LinkOrdering.Describe(desired));
                if (IsSingleDcTarget(target) && !plan.RequiredAcknowledgements.Contains(DcBackupAcknowledgement))
                    plan.RequiredAcknowledgements.Add(DcBackupAcknowledgement);
            }
            if (list.Any(i => i.PilotMode == PilotMode.Group))
                plan.Warnings.Add("Security filtering of group-piloted GPOs is left as is; their links are disabled.");
            return plan;
        }

        /// <summary>
        /// Desired sequence after rollback on one OU: rolled-back GPO links disabled; previous-version
        /// links this run disabled are re-enabled if they were enabled in the recorded snapshot; order
        /// restored to the snapshot (other new GPOs keep their position), then exceptions-first enforced.
        /// </summary>
        public List<LinkInfo> BuildRollbackSequence(RunState run, List<RunItem> items, string target, List<LinkInfo> current, OuSnapshot snap)
        {
            var rolledBack = new HashSet<string>(items.Select(i => LinkOrdering.NormalizeGuid(i.NewGpoId)), StringComparer.OrdinalIgnoreCase);
            var reEnable = new HashSet<string>(
                run.LinkActions.Where(a => a.Action == "DisableLink" && AppConfig.DnEquals(a.Target, target) && a.ForItem != null && rolledBack.Contains(LinkOrdering.NormalizeGuid(a.ForItem)))
                               .Select(a => LinkOrdering.NormalizeGuid(a.GpoId)), StringComparer.OrdinalIgnoreCase);
            var seq = LinkOrdering.Sorted(current);
            foreach (var l in seq)
            {
                var id = LinkOrdering.NormalizeGuid(l.GpoId);
                if (rolledBack.Contains(id)) l.Enabled = false;
                else if (reEnable.Contains(id))
                {
                    var s = snap.Links.FirstOrDefault(x => LinkOrdering.SameGpo(x.GpoId, id));
                    l.Enabled = s?.Enabled ?? true;
                }
            }
            return LinkOrdering.RestoreOrder(seq, snap.Links.OrderBy(x => x.Order).Select(x => x.GpoId), rolledBack, Config);
        }

        public void ExecuteRollback(RunState run, IEnumerable<RunItem> items = null, bool dcBackupConfirmed = false)
        {
            var list = (items ?? RollbackableItems(run)).ToList();
            var plan = PlanRollback(run, list);
            if (plan.Blockers.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Blockers));
            RequireAck(plan, dcBackupConfirmed, run);
            Audit.Write(run.RunId, "Rollback", string.Join("; ", list.Select(i => i.NewGpoName)), "Started");

            var problems = new List<string>();
            foreach (var target in list.SelectMany(i => TouchedTargets(run, i)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var current = Directory.GetLinks(target);
                Snapshot(run, PhaseRollback, target, current);
                var desired = BuildRollbackSequence(run, list, target, current, run.EarliestSnapshot(target));
                // Re-enable previous versions first so the OU is never left without a STIG GPO, then disable the new ones.
                foreach (var d in desired.Where(d => d.Enabled))
                {
                    var c = current.FirstOrDefault(x => LinkOrdering.SameGpo(x.GpoId, d.GpoId));
                    if (c == null || c.Enabled) continue;
                    var before = Directory.GetLinks(target);
                    Do(run, "EnableLink", target, () => Directory.SetLinkEnabled(d.GpoId, target, true), $"gpo='{d.GpoName}' (rollback)");
                    Record(run, PhaseRollback, "EnableLink", list.FirstOrDefault(), d.GpoId, d.GpoName, target, "rollback", before, Directory.GetLinks(target));
                    Ok($"Re-enabled '{d.GpoName}' on {target}.");
                }
                foreach (var d in desired.Where(d => !d.Enabled))
                {
                    var c = current.FirstOrDefault(x => LinkOrdering.SameGpo(x.GpoId, d.GpoId));
                    if (c == null || !c.Enabled) continue;
                    var before = Directory.GetLinks(target);
                    var owner = list.FirstOrDefault(i => LinkOrdering.SameGpo(i.NewGpoId, d.GpoId));
                    Do(run, "DisableLink", target, () => Directory.SetLinkEnabled(d.GpoId, target, false), $"gpo='{d.GpoName}' (rollback)");
                    Record(run, PhaseRollback, "DisableLink", owner, d.GpoId, d.GpoName, target, "rollback", before, Directory.GetLinks(target));
                    Ok($"Disabled '{d.GpoName}' on {target}.");
                }
                ApplyOrder(run, PhaseRollback, null, target, desired);
                var after = Directory.GetLinks(target);
                var v = LinkOrdering.Verify(after, Config);
                if (v.Count > 0) { problems.AddRange(v.Select(x => target + ": " + x)); foreach (var x in v) Error($"  {target}: {x}"); }
                else Ok($"  Verified order on {target}.");
            }
            foreach (var item in list) item.Status = ItemStatus.RolledBack;
            if (run.Items.Where(i => i.NewGpoId != null).All(i => i.Status == ItemStatus.RolledBack || i.Status == ItemStatus.Failed))
                run.SetPhase(RunPhase.RolledBack, User);
            else
                run.History.Add(new PhaseEvent { Phase = "PartialRollback", Utc = DateTime.UtcNow, User = User, Note = string.Join("; ", list.Select(i => i.NewGpoName)) });
            Save(run);
            Audit.Write(run.RunId, "Rollback", string.Join("; ", list.Select(i => i.NewGpoName)), problems.Count == 0 ? "Success" : "Failure", problems.Count == 0 ? null : string.Join(" | ", problems));
            if (problems.Count > 0) throw new InvalidOperationException("Rolled back, but link order could not be verified:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
            Ok("Rollback complete.");
        }

        // ================================================================ 8. cleanup

        /// <summary>DISA GPOs two or more versions older than the newest of their mapping, with no enabled links.</summary>
        public List<CleanupCandidate> GetCleanupCandidates(IEnumerable<RunState> runs = null)
        {
            var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in (runs ?? Store.LoadAll()).Where(r => r.Phase != RunPhase.Closed))
                foreach (var i in r.Items)
                {
                    if (i.NewGpoId != null) protectedIds.Add(LinkOrdering.NormalizeGuid(i.NewGpoId));
                    if (i.PreviousGpoId != null) protectedIds.Add(LinkOrdering.NormalizeGuid(i.PreviousGpoId));
                }

            var all = Directory.GetAllGpos();
            var result = new List<CleanupCandidate>();
            foreach (var m in Config.Mappings)
            {
                var family = all.Select(g => new { Gpo = g, Version = m.VersionOfName(g.DisplayName) }).Where(x => x.Version != null).ToList();
                var versions = family.Select(x => x.Version).Distinct().OrderByDescending(v => v).ToList();
                if (versions.Count < 3) continue;
                var keep = versions[1]; // newest and the one before it (rollback target) are always kept
                foreach (var x in family.Where(x => x.Version < keep))
                {
                    if (protectedIds.Contains(LinkOrdering.NormalizeGuid(x.Gpo.Id))) continue;
                    var links = Directory.GetGpoLinkLocations(x.Gpo.Id);
                    if (links.Any(l => l.Enabled)) continue;
                    result.Add(new CleanupCandidate
                    {
                        Gpo = x.Gpo, MappingKey = m.Key, Version = x.Version.ToString(), LatestVersion = versions[0].ToString(),
                        LinkCount = links.Count,
                        Reason = links.Count == 0 ? "Not linked anywhere" : $"{links.Count} link(s), all disabled",
                    });
                }
            }
            return result.OrderBy(c => c.MappingKey).ThenBy(c => c.Version).ToList();
        }

        public OperationPlan PlanCleanup(IEnumerable<CleanupCandidate> selected)
        {
            var list = selected.ToList();
            var plan = new OperationPlan { Title = "DELETE old DISA GPO versions", ConfirmationWord = "DELETE" };
            if (list.Count == 0) plan.Blockers.Add("No GPOs selected.");
            foreach (var c in list)
            {
                plan.Add("Backup", c.Gpo.DisplayName, $"Backup-GPO to {Config.BackupsFolder}{Path.DirectorySeparatorChar}deleted-(date-time)");
                plan.Add("DELETE", c.Gpo.DisplayName, $"Remove-GPO {c.Gpo.Id} ({c.Reason}; newest is {c.LatestVersion})");
            }
            return plan;
        }

        public void ExecuteCleanup(IEnumerable<CleanupCandidate> selected, string typedConfirmation)
        {
            if (!string.Equals(typedConfirmation, "DELETE", StringComparison.Ordinal))
                throw new InvalidOperationException("Deletion requires typing DELETE.");
            var list = selected.ToList();
            var auditRun = "cleanup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var folder = Path.Combine(Config.BackupsFolder, "deleted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            System.IO.Directory.CreateDirectory(folder);
            var pseudo = new RunState { RunId = auditRun };
            foreach (var c in list)
            {
                // Re-check right before deleting: still unlinked/disabled.
                if (Directory.GetGpoLinkLocations(c.Gpo.Id).Any(l => l.Enabled))
                {
                    Warn($"'{c.Gpo.DisplayName}' now has an enabled link; skipped.");
                    Audit.Write(auditRun, "RemoveGpo", c.Gpo.DisplayName, "Skipped", "GPO has an enabled link");
                    continue;
                }
                Do(pseudo, "BackupGpo", c.Gpo.DisplayName, () => Directory.BackupGpo(c.Gpo.Id, folder), folder);
                Do(pseudo, "RemoveGpo", c.Gpo.DisplayName, () => Directory.RemoveGpo(c.Gpo.Id), c.Gpo.Id);
                Ok($"Deleted '{c.Gpo.DisplayName}' (backup in {folder}).");
            }
        }

        /// <summary>Marks a run as closed (it no longer shows as active in the tray).</summary>
        public void CloseRun(RunState run, string note = null)
        {
            run.SetPhase(RunPhase.Closed, User, note);
            Save(run);
            Audit.Write(run.RunId, "CloseRun", run.RunId, "Success", null, note);
        }
    }
}
