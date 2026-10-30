using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StigGpoManager.Core;

namespace StigGpoManager
{
    /// <summary>
    /// The wizard UI. It only calls StigEngine (and Session); every AD change goes through a
    /// Plan -> ConfirmWindow -> Execute sequence on a background thread, with log lines streamed back.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Session _session;
        private RunState _run;
        private PackageScanResult _scan;
        private List<BackupRow> _backupRows = new List<BackupRow>();
        private List<StoreRow> _storeRows = new List<StoreRow>();
        private ReviewResult _review;
        private ScanGateResult _gate;
        private ObservableCollection<ExceptionEntry> _register = new ObservableCollection<ExceptionEntry>();
        private readonly ObservableCollection<LogLine> _log = new ObservableCollection<LogLine>();
        private string _zipPath;

        public bool IsBusy { get; private set; }
        public bool ExitRequested { get; set; }
        public event Action RunChanged;

        private StigEngine Engine => _session.Engine;

        public MainWindow(Session session)
        {
            InitializeComponent();
            _session = session;
            if (session.IsDemo)
            {
                Title = "STIG GPO Manager - DEMO (simulated domain, no AD changes)";
                HeaderBorder.Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x4B, 0x08));
                session.Engine.Log += AddLog;
            }
            LogList.ItemsSource = _log;
            PilotChecklist.ItemsSource = StigEngine.PilotChecklist;
            RegisterGrid.ItemsSource = _register;
            StepList.SelectedIndex = 0;
            Loaded += (s, e) => Connect();
            Closed += (s, e) => _session.Dispose();
        }

        // ================================================================ infrastructure

        private void AddLog(LogLevel level, string text)
        {
            Brush brush;
            switch (level)
            {
                case LogLevel.Error: brush = Brushes.Firebrick; break;
                case LogLevel.Warning: brush = Brushes.DarkOrange; break;
                case LogLevel.Success: brush = Brushes.DarkGreen; break;
                default: brush = Brushes.Black; break;
            }
            var line = new LogLine { Text = $"{DateTime.Now:HH:mm:ss} {text}", Brush = brush };
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _log.Add(line);
                while (_log.Count > 5000) _log.RemoveAt(0);
                LogList.ScrollIntoView(line);
            }));
        }

        private void AddCommand(string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ShowCommands.IsChecked != true) return;
                var line = new LogLine { Text = $"{DateTime.Now:HH:mm:ss}     {text}", Brush = Brushes.SteelBlue };
                _log.Add(line);
                LogList.ScrollIntoView(line);
            }));
        }

        private void SetBusy(bool busy, string text = null)
        {
            IsBusy = busy;
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            Busy.IsIndeterminate = busy;
            StatusText.Text = busy ? text + " ..." : "Ready";
            Panels.IsEnabled = !busy;
        }

        /// <summary>Runs work off the UI thread; errors are logged and shown; then continues on the UI thread.</summary>
        private void RunOp<T>(string title, Func<T> work, Action<T> done = null)
        {
            if (IsBusy) { MessageBox.Show(this, "Another operation is running.", Title); return; }
            SetBusy(true, title);
            AddLog(LogLevel.Info, "== " + title);
            Task.Run(work).ContinueWith(t =>
            {
                SetBusy(false);
                if (t.IsFaulted)
                {
                    var ex = t.Exception.GetBaseException();
                    AddLog(LogLevel.Error, title + " failed: " + ex.Message);
                    MessageBox.Show(this, ex.Message, title + " failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    try { done?.Invoke(t.Result); }
                    catch (Exception ex) { AddLog(LogLevel.Error, ex.Message); MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
                }
                RefreshRunViews();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void RunOp(string title, Action work, Action done = null) =>
            RunOp<object>(title, () => { work(); return null; }, _ => done?.Invoke());

        /// <summary>Plan (read-only) -> show exact changes -> confirm -> execute.</summary>
        private void Push(string title, Func<OperationPlan> plan, Action<ConfirmResult> execute, Action after = null)
        {
            if (!RequireEngine()) return;
            RunOp("Planning: " + title, plan, p =>
            {
                var r = ConfirmWindow.Ask(this, p);
                if (!r.Confirmed) { AddLog(LogLevel.Info, title + ": cancelled, nothing changed."); return; }
                RunOp(title, () => execute(r), after);
            });
        }

        private bool RequireEngine()
        {
            if (Engine != null) return true;
            MessageBox.Show(this, "Not connected to the domain. Fix the config / pre-flight problems on the Start page, then Reload config.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private bool RequireRun()
        {
            if (!RequireEngine()) return false;
            if (_run != null) return true;
            MessageBox.Show(this, "No run is open. Load a package (step 1) or resume a run on the Start page.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        private static void Open(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(StepList.SelectedItem is ListBoxItem item) || !(item.Tag is string panel)) return;
            foreach (UIElement child in Panels.Children)
                child.Visibility = (child as FrameworkElement)?.Name == panel ? Visibility.Visible : Visibility.Collapsed;
            if (panel == "PanelAudit") LoadAudit();
            if (panel == "PanelRegister") LoadRegister();
        }

        private void GoTo(string panel)
        {
            foreach (var o in StepList.Items)
                if (o is ListBoxItem li && (li.Tag as string) == panel) { StepList.SelectedItem = li; return; }
        }

        // ================================================================ start / config / runs

        private void Connect()
        {
            if (_session.IsDemo && _session.Config != null)
            {
                MappingColumn.ItemsSource = _session.Config.Mappings;
                StorePathText.Text = "Central Store: " + _session.Config.EffectiveCentralStorePath;
                RegisterPathText.Text = "Register file: " + _session.Config.ExceptionsRegisterPath;
                AuditPathText.Text = "Append-only log: " + _session.Config.AuditLogPath;
                ConfigPathText.Text = "Config: " + _session.ConfigPath + "   (DEMO: simulated domain, nothing touches Active Directory)";
                AddLog(LogLevel.Warning, "DEMO MODE: simulated domain " + _session.Config.Domain + "; no Active Directory or SYSVOL access.");
                RunOp("Running pre-flight checks (demo)", () => _session.Preflight(false), results =>
                {
                    PreflightGrid.ItemsSource = results;
                    UpdateHeader();
                    RefreshRuns(autoResume: true);
                });
                return;
            }
            UpdateHeader();
            ConfigPathText.Text = "Config: " + _session.ConfigPath;
            foreach (var i in _session.Issues) AddLog(i.IsError ? LogLevel.Error : LogLevel.Warning, "Config: " + i.Message);
            if (_session.Config == null || !_session.ConfigValid)
            {
                PreflightGrid.ItemsSource = _session.Preflight(false);
                if (!File.Exists(_session.ConfigPath))
                {
                    if (MessageBox.Show(this, $"No configuration yet ({_session.ConfigPath}).\n\nRun the setup wizard now? It detects the domain, exception-GPO prefix, pilot group and current DISA GPO links, and asks you to confirm each value.",
                                        Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        Dispatcher.BeginInvoke(new Action(() => OpenSetup()));
                }
                return;
            }
            MappingColumn.ItemsSource = _session.Config.Mappings;
            StorePathText.Text = "Central Store: " + _session.Config.EffectiveCentralStorePath;
            RegisterPathText.Text = "Register file: " + _session.Config.ExceptionsRegisterPath;
            AuditPathText.Text = "Append-only log: " + _session.Config.AuditLogPath;
            bool dcdiag = DcDiagCheck.IsChecked == true;
            RunOp("Connecting and running pre-flight checks", () =>
            {
                try { _session.Connect(AddLog, AddCommand); }
                catch (Exception ex) { AddLog(LogLevel.Error, ex.Message); }
                return _session.Preflight(dcdiag);
            }, results =>
            {
                PreflightGrid.ItemsSource = results;
                UpdateHeader();
                var fails = results.Count(r => r.Status == CheckStatus.Fail);
                AddLog(fails == 0 ? LogLevel.Success : LogLevel.Warning, $"Pre-flight: {results.Count(r => r.Status == CheckStatus.Pass)} passed, {results.Count(r => r.Status == CheckStatus.Warning)} warnings, {fails} failed.");
                RefreshRuns(autoResume: true);
            });
        }

        private void UpdateHeader()
        {
            var cfg = _session.Config;
            HeaderText.Text = cfg == null ? "config not loaded" :
                $"Domain {cfg.Domain}   DC {(_session.Directory?.Server ?? "(not connected)")}   User {_session.User}   Working folder {cfg.WorkingFolder}";
            RunText.Text = _run == null ? "No run open" : $"Run {_run.RunId} - {_run.StatusText(cfg?.SoakDays ?? 14, DateTime.UtcNow)}";
        }

        private void Preflight_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            bool dcdiag = DcDiagCheck.IsChecked == true;
            RunOp("Pre-flight checks", () => _session.Preflight(dcdiag), r => PreflightGrid.ItemsSource = r);
        }

        private void Setup_Click(object sender, RoutedEventArgs e) => OpenSetup();

        /// <summary>Opens the setup wizard (pre-filled from the current config, if any) and reloads on save.</summary>
        public void OpenSetup()
        {
            if (IsBusy) return;
            var path = _session.IsDemo ? Path.Combine(Path.GetDirectoryName(_session.ConfigPath), "config.setup-demo.json") : _session.ConfigPath;
            var wizard = new SetupWindow(this, path, _session.ConfigValid ? _session.Config : null, _session.IsDemo ? _session.Directory : null, _session.IsDemo);
            if (wizard.ShowDialog() != true) return;
            AddLog(LogLevel.Success, "Config saved: " + wizard.SavedPath);
            if (_session.IsDemo) { MessageBox.Show(this, "Demo: config written to " + wizard.SavedPath + " (not used).", Title); return; }
            _session.LoadConfig();
            Connect();
        }

        private void ReloadConfig_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy) return;
            if (_session.IsDemo) { MessageBox.Show(this, "Demo mode uses a generated config; restart without /demo to use your own.", Title); return; }
            _session.LoadConfig();
            Connect();
        }

        private void OpenConfig_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(_session.ConfigPath)) Process.Start("notepad.exe", "\"" + _session.ConfigPath + "\"");
            else MessageBox.Show(this, "Config file not found: " + _session.ConfigPath, Title);
        }

        private void OpenWorking_Click(object sender, RoutedEventArgs e) => Open(_session.Config?.WorkingFolder);

        private void RefreshRuns_Click(object sender, RoutedEventArgs e) => RefreshRuns(false);

        private void RefreshRuns(bool autoResume)
        {
            if (_session.Config == null) return;
            var runs = new RunStore(_session.Config.RunsFolder).LoadAll(m => AddLog(LogLevel.Warning, m));
            RunsGrid.ItemsSource = runs.Select(r => new RunRow { State = r, Status = r.StatusText(_session.Config.SoakDays, DateTime.UtcNow) }).ToList();
            if (autoResume && _run == null)
            {
                var active = runs.Where(r => r.Phase != RunPhase.Closed && r.Phase != RunPhase.RolledBack).ToList();
                if (active.Count == 1 && Engine != null)
                {
                    AddLog(LogLevel.Info, $"Resuming active run {active[0].RunId} ({active[0].Phase}).");
                    ResumeRun(active[0].RunId);
                }
                else if (active.Count > 1) AddLog(LogLevel.Warning, $"{active.Count} runs are open; select one on the Start page and click Resume.");
            }
        }

        private void ResumeRun_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            if (!(RunsGrid.SelectedItem is RunRow row)) { MessageBox.Show(this, "Select a run first.", Title); return; }
            ResumeRun(row.RunId);
        }

        private void ResumeRun(string runId)
        {
            RunOp("Resuming run " + runId, () =>
            {
                var run = Engine.Store.Load(runId);
                PackageScanResult scan = null;
                try { scan = Engine.RescanPackage(run); }
                catch (Exception ex) { AddLog(LogLevel.Warning, ex.Message + " Steps 1-3 are unavailable for this run; later steps still work."); }
                return Tuple.Create(run, scan);
            }, t =>
            {
                _run = t.Item1;
                _scan = t.Item2;
                _review = null;
                BuildBackupRows();
                ZipPathText.Text = _run.Package?.ZipPath;
                HashText.Text = HashLine(_run.Package);
                AddLog(LogLevel.Success, $"Run {_run.RunId} resumed: {_run.StatusText(_session.Config.SoakDays, DateTime.UtcNow)}");
                RunChanged?.Invoke();
                GoTo(NextPanelFor(_run));
            });
        }

        private static string NextPanelFor(RunState r)
        {
            switch (r.Phase)
            {
                case RunPhase.PackageLoaded: return "PanelStore";
                case RunPhase.CentralStoreUpdated: return "PanelImport";
                case RunPhase.Imported: return "PanelReview";
                case RunPhase.Reviewed: return "PanelPilot";
                case RunPhase.Piloted: return "PanelScans";
                case RunPhase.Promoted: return "PanelPromote";
                default: return "PanelStart";
            }
        }

        private void NewRun_Click(object sender, RoutedEventArgs e)
        {
            _run = null; _scan = null; _review = null; _gate = null;
            _backupRows = new List<BackupRow>();
            BackupsGrid.ItemsSource = null;
            ZipPathText.Text = "(no file selected)";
            HashText.Text = "";
            RefreshRunViews();
            GoTo("PanelLoad");
        }

        private void RunReport_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            var run = (RunsGrid.SelectedItem as RunRow)?.State ?? _run;
            if (run == null) { MessageBox.Show(this, "Select a run.", Title); return; }
            RunOp("Writing run report", () => Engine.WriteRunReport(Engine.Store.Load(run.RunId)), Open);
        }

        private void CloseRun_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            if (!(RunsGrid.SelectedItem is RunRow row)) { MessageBox.Show(this, "Select a run.", Title); return; }
            if (MessageBox.Show(this, $"Mark run {row.RunId} as closed? It stays on disk for audit and can still be reported on.", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            RunOp("Closing run", () => Engine.CloseRun(Engine.Store.Load(row.RunId)), () =>
            {
                if (_run?.RunId == row.RunId) _run = Engine.Store.Load(row.RunId);
                RefreshRuns(false);
                RunChanged?.Invoke();
            });
        }

        // ================================================================ 1. load package

        private void Drop_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ZipDrop_Drop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var zip = files?.FirstOrDefault(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (zip == null) { MessageBox.Show(this, "Drop a .zip file.", Title); return; }
            SetZip(zip);
        }

        private void BrowseZip_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "DISA GPO package (*.zip)|*.zip", Title = "Select DISA GPO package" };
            if (dlg.ShowDialog(this) == true) SetZip(dlg.FileName);
        }

        private void SetZip(string path)
        {
            _zipPath = path;
            ZipPathText.Text = path;
        }

        private static string HashLine(PackageRecord p)
        {
            if (p == null) return "";
            var match = p.HashMatches == null ? "(no expected hash given)" : p.HashMatches == true ? "MATCHES expected hash" : "*** DOES NOT MATCH the expected hash ***";
            return $"SHA-256: {p.Sha256}   {match}";
        }

        private void LoadPackage_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            if (string.IsNullOrEmpty(_zipPath)) { MessageBox.Show(this, "Drop or browse to the DISA zip first.", Title); return; }
            if (_run != null && _run.Phase < RunPhase.Closed &&
                MessageBox.Show(this, $"Run {_run.RunId} is open. Start a NEW run for this package? (The open run is kept and can be resumed.)", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            var zip = _zipPath;
            var expected = ExpectedHashBox.Text;
            RunOp("Loading package", () =>
            {
                var run = Engine.CreateRun(zip, expected, out var scan);
                return Tuple.Create(run, scan);
            }, t =>
            {
                _run = t.Item1;
                _scan = t.Item2;
                _review = null; _gate = null;
                HashText.Text = HashLine(_run.Package);
                HashText.Foreground = _run.Package.HashMatches == false ? Brushes.Firebrick : Brushes.Black;
                BuildBackupRows();
                RefreshRuns(false);
                RunChanged?.Invoke();
                if (_run.Package.HashMatches == false)
                    MessageBox.Show(this, "The package hash does NOT match the expected value. Do not continue unless you have resolved this with the transfer record.", "Hash mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        private void BuildBackupRows()
        {
            if (_scan == null || _session.Config == null) { BackupsGrid.ItemsSource = null; TemplateSummary.Text = ""; return; }
            var candidates = MappingResolver.Resolve(_scan.Backups, _session.Config);
            _backupRows = candidates.Select(c =>
            {
                var done = _run?.Items.FirstOrDefault(i => i.BackupId == c.Backup.BackupId && i.NewGpoId != null);
                return new BackupRow(c, done != null ? $"Already imported in this run as '{done.NewGpoName}'." : null);
            }).ToList();
            BackupsGrid.ItemsSource = _backupRows;
            TemplateSummary.Text = $"{_scan.Backups.Count} GPO backups; {_scan.Templates.Count(t => t.Kind == TemplateKind.Admx)} ADMX and {_scan.Templates.Count(t => t.Kind == TemplateKind.Adml)} {_session.Config.Language} ADML templates found in {_scan.ExtractedPath}."
                                   + (_scan.Warnings.Count > 0 ? "  Warnings: " + string.Join(" | ", _scan.Warnings) : "");
        }

        // ================================================================ 2. central store

        private void CompareStore_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            if (_scan == null) { MessageBox.Show(this, "The package for this run is not available.", Title); return; }
            RunOp("Comparing Central Store", () => Engine.CompareCentralStore(_scan), items =>
            {
                _storeRows = items.Select(i => new StoreRow(i)).ToList();
                ApplyStoreFilter();
                StoreSummary.Text = $"New {items.Count(i => i.Status == CentralStoreStatus.New)}, Updated {items.Count(i => i.Status == CentralStoreStatus.Updated)}, Unchanged {items.Count(i => i.Status == CentralStoreStatus.Unchanged)}, Skipped (store newer) {items.Count(i => i.Status == CentralStoreStatus.SkippedStoreNewer)}";
            });
        }

        private void StoreFilter_Changed(object sender, RoutedEventArgs e) => ApplyStoreFilter();

        private void ApplyStoreFilter()
        {
            if (StoreGrid == null) return;
            StoreGrid.ItemsSource = StoreHideUnchanged.IsChecked == true
                ? _storeRows.Where(r => r.Item.Status != CentralStoreStatus.Unchanged).ToList()
                : _storeRows;
        }

        private void PushStore_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            if (_storeRows.Count == 0) { MessageBox.Show(this, "Click Compare first.", Title); return; }
            var items = _storeRows.Select(r => r.Item).ToList();
            var run = _run;
            Push("Update Central Store", () => Engine.PlanCentralStore(items), r => Engine.ExecuteCentralStore(run, items), () => CompareStore_Click(null, null));
        }

        // ================================================================ 3. import

        private List<ImportCandidate> SelectedCandidates() => _backupRows.Where(r => !r.Skip).Select(r => r.Candidate).ToList();

        private void PushImport_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            var selected = SelectedCandidates();
            var run = _run;
            Push("Import GPOs", () => Engine.PlanImport(run, selected), r => Engine.ExecuteImport(run, selected), () =>
            {
                BuildBackupRows();
                AddLog(LogLevel.Info, "Next: step 4, Review.");
            });
        }

        // ================================================================ 4. review

        private void Review_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            var run = _run;
            RunOp("Reviewing changes", () => Engine.Review(run), r =>
            {
                _review = r;
                ReviewWarnings.ItemsSource = r.Warnings;
                ApplyReviewFilter();
                ReviewSummary.Text = $"{r.Rows.Count} rows; {r.Rows.Count(x => x.Severity == 2)} replacement-type warnings; {r.Rows.Count(x => x.Severity == 1)} exceptions to review. Report: {r.HtmlPath}";
            });
        }

        private void ReviewFilter_Changed(object sender, RoutedEventArgs e) => ApplyReviewFilter();

        private void ApplyReviewFilter()
        {
            if (ReviewGrid == null || _review == null) return;
            IEnumerable<ReviewRow> rows = _review.Rows;
            switch (ReviewFilter.SelectedIndex)
            {
                case 1: rows = rows.Where(r => r.Severity > 0); break;
                case 2: rows = rows.Where(r => r.Change == "Added"); break;
                case 3: rows = rows.Where(r => r.Change == "Removed"); break;
                case 4: rows = rows.Where(r => r.Change == "Changed"); break;
            }
            var q = ReviewSearch.Text?.Trim();
            if (!string.IsNullOrEmpty(q))
                rows = rows.Where(r => new[] { r.Setting, r.OldValue, r.NewValue, r.ExceptionGpo, r.NewGpo, r.Detail }.Any(s => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            ReviewGrid.ItemsSource = rows.OrderByDescending(r => r.Severity).ToList();
        }

        private void OpenReviewReport_Click(object sender, RoutedEventArgs e) =>
            Open(_review?.HtmlPath ?? _run?.Reports.LastOrDefault(p => p.EndsWith(".html") && Path.GetFileName(p).StartsWith("review-")));

        private void OpenReports_Click(object sender, RoutedEventArgs e) => Open(_session.Config?.ReportsFolder);

        // ================================================================ 5. pilot

        private void PushPilot_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            var run = _run;
            var items = Selected(PilotGrid).Where(i => Engine.PilotableItems(run).Contains(i)).ToList();
            if (items.Count == 0) { MessageBox.Show(this, "No imported GPOs are selected for pilot.", Title); return; }
            if (run.Phase < RunPhase.Reviewed &&
                MessageBox.Show(this, "The review (step 4) has not been run for this run. Link to pilot anyway?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            Push("Pilot", () => Engine.PlanPilot(run, items), r => Engine.ExecutePilot(run, items, r.DcBackupConfirmed), () =>
            {
                RunChanged?.Invoke();
                MessageBox.Show(this, "Pilot links are in place.\n\n" + string.Join("\n", StigEngine.PilotChecklist.Select(s => "  - " + s)), "Pilot - next steps", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private static List<RunItem> Selected(DataGrid grid) =>
            (grid.ItemsSource as IEnumerable<ItemRow> ?? Enumerable.Empty<ItemRow>()).Where(r => r.Selected).Select(r => r.Item).ToList();

        // ================================================================ scans / gate

        private string Stage => StageBaseline.IsChecked == true ? StigEngine.StageBaseline : StigEngine.StagePilot;

        private void ScanDrop_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportScans(files);
        }

        private void BrowseScan_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Scan results (*.ckl;*.cklb;*.xml)|*.ckl;*.cklb;*.xml|All files|*.*", Multiselect = true };
            if (dlg.ShowDialog(this) == true) ImportScans(dlg.FileNames);
        }

        private void ImportScans(string[] files)
        {
            if (!RequireRun()) return;
            var run = _run;
            var stage = Stage;
            RunOp($"Importing {files.Length} scan result file(s) as {stage}", () =>
            {
                foreach (var f in files)
                {
                    try { Engine.ImportScanResult(run, f, stage); }
                    catch (Exception ex) { AddLog(LogLevel.Error, $"{Path.GetFileName(f)}: {ex.Message}"); }
                }
                return Engine.EvaluateGate(run);
            }, ShowGate);
        }

        private void EvaluateGate_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            var run = _run;
            RunOp("Evaluating gate", () => Engine.EvaluateGate(run), ShowGate);
        }

        private void ShowGate(ScanGateResult g)
        {
            _gate = g;
            GateText.Text = (g.Passed ? "GATE PASS - " : "GATE FAIL - ") + g.Summary;
            GateBorder.Background = new SolidColorBrush(g.Passed ? Color.FromRgb(0xDF, 0xF5, 0xDF) : Color.FromRgb(0xFF, 0xC9, 0xC9));
            FindingsGrid.ItemsSource = g.Blocking.Select(f => new FindingRow { Finding = f })
                                        .Concat(g.Expected.Select(x => new FindingRow { Finding = x.Finding, Entry = x.Entry })).ToList();
            ChangesGrid.ItemsSource = g.StatusChanges;
            PromoteGateText.Text = GateText.Text;
            PromoteGateBorder.Background = GateBorder.Background;
        }

        private void RemoveScan_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun() || !(ScansGrid.SelectedItem is ScanRow row)) return;
            if (MessageBox.Show(this, $"Remove {row.OriginalFileName} from the gate? (The stored copy is kept as evidence.)", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var run = _run;
            RunOp("Removing scan import", () => { Engine.RemoveScanImport(run, row.Record); return Engine.EvaluateGate(run); }, ShowGate);
        }

        private void AddToRegister_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            var rows = FindingsGrid.SelectedItems.OfType<FindingRow>().Where(r => r.Entry == null).ToList();
            if (rows.Count == 0) { MessageBox.Show(this, "Select one or more BLOCKING findings first.", Title); return; }
            var dlg = new InputWindow(this, "Add to exceptions register",
                $"{rows.Count} finding(s): {string.Join(", ", rows.Select(r => r.VulnId).Distinct())}. A justification is required.",
                "Exception GPO (that implements the deviation)", "Justification", "POA&M / risk acceptance reference");
            if (dlg.ShowDialog() != true) return;
            var v = dlg.Values;
            if (string.IsNullOrWhiteSpace(v[1])) { MessageBox.Show(this, "Justification is required; nothing added.", Title); return; }
            var register = Engine.LoadRegister();
            foreach (var r in rows.GroupBy(r => r.VulnId + "|" + r.Stig).Select(g => g.First()))
                register.Entries.Add(new ExceptionEntry
                {
                    VulnId = r.VulnId, StigName = r.Stig, ExceptionGpo = v[0], Justification = v[1], PoamReference = v[2],
                    DateAdded = DateTime.UtcNow, AddedBy = _session.User,
                });
            var run = _run;
            RunOp("Saving exceptions register", () => { Engine.SaveRegister(register); return run == null ? null : Engine.EvaluateGate(run); }, g =>
            {
                LoadRegister();
                if (g != null) ShowGate(g);
            });
        }

        // ================================================================ 6. promote

        private void PromoteChecks_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            var run = _run;
            RunOp("Evaluating promotion pre-checks", () => Engine.EvaluatePromotionChecks(run), results =>
            {
                bool pass = results.All(r => r.Status != CheckStatus.Fail);
                PromoteGateText.Text = string.Join(Environment.NewLine, results.Select(r => $"{r.Status.ToString().ToUpperInvariant()}: {r.Name} - {r.Detail}"));
                if (results.Count == 0) PromoteGateText.Text = "No pre-checks configured.";
                PromoteGateBorder.Background = new SolidColorBrush(pass ? Color.FromRgb(0xDF, 0xF5, 0xDF) : Color.FromRgb(0xFF, 0xC9, 0xC9));
            });
        }

        private void PushPromote_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            var run = _run;
            var items = Selected(PromoteGrid).Where(i => Engine.PromotableItems(run).Contains(i)).ToList();
            if (items.Count == 0) { MessageBox.Show(this, "No piloted GPOs are selected.", Title); return; }
            Push("Promote", () => Engine.PlanPromote(run, items), r => Engine.ExecutePromote(run, r.TypedWord, items, r.DcBackupConfirmed), () =>
            {
                RunChanged?.Invoke();
                if (MessageBox.Show(this, "Promote complete. Trigger a policy refresh on the affected computers now?\n\nSome settings still need a logoff or reboot.", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    ListComputers(thenPush: true);
            });
        }

        private void ListComputers_Click(object sender, RoutedEventArgs e) => ListComputers(false);

        private void ListComputers(bool thenPush)
        {
            if (!RequireRun()) return;
            var run = _run;
            RunOp("Enumerating computers in production targets", () => Engine.RefreshTargets(run), list =>
            {
                RefreshGrid.ItemsSource = list.Select(c => new RefreshRow { Computer = c }).ToList();
                AddLog(LogLevel.Info, $"{list.Count} computer(s) under the production targets.");
                if (thenPush) PushRefresh_Click(null, null);
            });
        }

        private void PushRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            var rows = (RefreshGrid.ItemsSource as List<RefreshRow>) ?? new List<RefreshRow>();
            if (rows.Count == 0) { MessageBox.Show(this, "Click List computers first.", Title); return; }
            var plan = new OperationPlan { Title = "Policy refresh (Invoke-GPUpdate -Force -RandomDelayInMinutes 0)" };
            foreach (var r in rows) plan.Add("GPUpdate", r.Computer, "Force Group Policy refresh");
            plan.Warnings.Add("Users on these computers may see a brief policy refresh. Settings such as user rights and services may need a logoff or reboot.");
            var confirm = ConfirmWindow.Ask(this, plan);
            if (!confirm.Confirmed) return;
            var run = _run;
            var byName = rows.ToDictionary(r => r.Computer, StringComparer.OrdinalIgnoreCase);
            RunOp("Refreshing policy", () => Engine.ExecuteRefresh(run, rows.Select(r => r.Computer).ToList(), res =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (byName.TryGetValue(res.Computer, out var row)) { row.Result = res.Success ? "OK" : "FAILED"; row.Message = res.Message; }
                }))), results => AddLog(results.All(r => r.Success) ? LogLevel.Success : LogLevel.Warning,
                    $"Refresh: {results.Count(r => r.Success)} OK, {results.Count(r => !r.Success)} failed."));
        }

        // ================================================================ 7. rollback

        private void PushRollbackSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            var items = Selected(RollbackGrid);
            if (items.Count == 0) { MessageBox.Show(this, "Tick the GPOs to roll back.", Title); return; }
            Rollback(items);
        }

        private void PushRollbackAll_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireRun()) return;
            Rollback(Engine.RollbackableItems(_run));
        }

        private void Rollback(List<RunItem> items)
        {
            var run = _run;
            Push("Rollback", () => Engine.PlanRollback(run, items), r => Engine.ExecuteRollback(run, items, r.DcBackupConfirmed), () => RunChanged?.Invoke());
        }

        // ================================================================ 8. cleanup

        private void FindCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            RunOp("Finding old versions", () => Engine.GetCleanupCandidates(), list =>
            {
                CleanupGrid.ItemsSource = list.Select(c => new CleanupRow(c)).ToList();
                AddLog(LogLevel.Info, $"{list.Count} cleanup candidate(s).");
            });
        }

        private void PushCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            var selected = (CleanupGrid.ItemsSource as List<CleanupRow> ?? new List<CleanupRow>()).Where(r => r.Selected).Select(r => r.Candidate).ToList();
            if (selected.Count == 0) { MessageBox.Show(this, "Tick the GPOs to delete.", Title); return; }
            Push("Delete old versions", () => Engine.PlanCleanup(selected), r => Engine.ExecuteCleanup(selected, r.TypedWord), () => FindCleanup_Click(null, null));
        }

        // ================================================================ register

        private void LoadRegister()
        {
            if (_session.Config == null) return;
            try
            {
                var reg = ExceptionsRegister.Load(_session.Config.ExceptionsRegisterPath);
                _register = new ObservableCollection<ExceptionEntry>(reg.Entries);
                RegisterGrid.ItemsSource = _register;
            }
            catch (Exception ex) { AddLog(LogLevel.Error, "Cannot read exceptions register: " + ex.Message); }
        }

        private void RegisterAdd_Click(object sender, RoutedEventArgs e)
        {
            var entry = new ExceptionEntry { DateAdded = DateTime.UtcNow, AddedBy = _session.User };
            _register.Add(entry);
            RegisterGrid.SelectedItem = entry;
            RegisterGrid.ScrollIntoView(entry);
        }

        private void RegisterDelete_Click(object sender, RoutedEventArgs e)
        {
            var rows = RegisterGrid.SelectedItems.OfType<ExceptionEntry>().ToList();
            if (rows.Count == 0) return;
            if (MessageBox.Show(this, $"Delete {rows.Count} register entr{(rows.Count == 1 ? "y" : "ies")}? (Takes effect when you click Save.)", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var r in rows) _register.Remove(r);
        }

        private void RegisterReload_Click(object sender, RoutedEventArgs e) => LoadRegister();

        private void RegisterSave_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            RegisterGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var reg = new ExceptionsRegister { Entries = _register.ToList() };
            var problems = reg.Validate();
            if (problems.Count > 0) { MessageBox.Show(this, string.Join("\n", problems), "Register not saved", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            RunOp("Saving exceptions register", () => Engine.SaveRegister(reg), () => AddLog(LogLevel.Success, $"Register saved ({reg.Entries.Count} entries)."));
        }

        // ================================================================ audit / log

        private void AuditRefresh_Click(object sender, RoutedEventArgs e) => LoadAudit();

        private void LoadAudit()
        {
            if (_session.Config == null) return;
            var runId = AuditCurrentRun.IsChecked == true ? _run?.RunId : null;
            var entries = new AuditLog(_session.Config.AuditLogPath, _session.User).ReadAll(runId);
            AuditGrid.ItemsSource = entries.AsEnumerable().Reverse().Take(5000).Select(o => new AuditRow
            {
                Time = Json.GetString(o, "timestamp"), User = Json.GetString(o, "user"), Run = Json.GetString(o, "runId"),
                Action = Json.GetString(o, "action"), Target = Json.GetString(o, "target"), Result = Json.GetString(o, "result"),
                Error = Json.GetString(o, "error") ?? Json.GetString(o, "detail") ?? Json.GetString(o, "raw"),
            }).ToList();
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

        // ================================================================ run views

        private void RefreshRunViews()
        {
            UpdateHeader();
            if (_run == null)
            {
                RunItemsGrid.ItemsSource = null; PilotGrid.ItemsSource = null; PromoteGrid.ItemsSource = null;
                RollbackGrid.ItemsSource = null; ScansGrid.ItemsSource = null; ImportGrid.ItemsSource = null;
                return;
            }
            ImportGrid.ItemsSource = _backupRows.Where(r => !r.Skip).Select(r => new
            {
                r.ProposedName, r.DisplayName, r.Version, r.Scope, Mapping = r.Mapping?.NameTemplate,
            }).ToList();
            var imported = _run.Items.Where(i => i.NewGpoId != null || i.Status == ItemStatus.Failed).ToList();
            RunItemsGrid.ItemsSource = imported.Select(i => new ItemRow(i, false)).ToList();

            if (Engine != null)
            {
                var pilotable = Engine.PilotableItems(_run);
                PilotGrid.ItemsSource = imported.Select(i => new ItemRow(i, pilotable.Contains(i))).ToList();
                var promotable = Engine.PromotableItems(_run);
                PromoteGrid.ItemsSource = imported.Select(i => new ItemRow(i, promotable.Contains(i))).ToList();
                var rollbackable = Engine.RollbackableItems(_run);
                RollbackGrid.ItemsSource = rollbackable.Select(i => new ItemRow(i, false,
                    string.Join("; ", _run.LinkActions.Where(a => a.ForItem != null && LinkOrdering.SameGpo(a.ForItem, i.NewGpoId) && a.Target != null && !a.Target.StartsWith("("))
                                                      .Select(a => a.Target).Distinct(StringComparer.OrdinalIgnoreCase)))).ToList();
            }
            ScansGrid.ItemsSource = _run.ScanImports.Select(s => new ScanRow { Record = s }).ToList();
            if (_run.Gate != null && _gate == null)
            {
                PromoteGateText.Text = $"Last gate evaluation {_run.Gate.EvaluatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}: {(_run.Gate.Passed ? "PASS" : "FAIL")} - {_run.Gate.Summary}";
                GateText.Text = PromoteGateText.Text;
            }
        }
    
        // ================================================================ screenshot tour (/screenshots)

        /// <summary>
        /// Walks every step of the demo run and saves a PNG of each screen (used by CI to produce the
        /// README screenshots). Returns the process exit code.
        /// </summary>
        public async Task<int> RunScreenshotTour(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Width = 1400; Height = 880;
                await WaitIdle(requireRun: true);
                ShowCommands.IsChecked = false;

                await Shot(folder, "01-start-preflight", "PanelStart");
                await Shot(folder, "02-load-package", "PanelLoad");
                CompareStore_Click(null, null); await WaitIdle();
                StoreHideUnchanged.IsChecked = false;
                await Shot(folder, "03-central-store", "PanelStore");
                await Shot(folder, "04-import", "PanelImport");
                Review_Click(null, null); await WaitIdle();
                await Shot(folder, "05-review-diff", "PanelReview");
                await Shot(folder, "06-pilot", "PanelPilot");
                EvaluateGate_Click(null, null); await WaitIdle();
                await Shot(folder, "07-scan-gate", "PanelScans");
                PromoteChecks_Click(null, null); await WaitIdle();
                ListComputers(false); await WaitIdle();
                await Shot(folder, "08-promote", "PanelPromote");

                var plan = await Task.Run(() => Engine.PlanPilot(_run, _run.Items));
                var dlg = new ConfirmWindow(this, plan) { Width = 1000, Height = 720, WindowStartupLocation = WindowStartupLocation.Manual, Left = Left + 60, Top = Top + 40 };
                dlg.Show();
                await Settle();
                Save(dlg, Path.Combine(folder, "09-confirm-dialog.png"));
                dlg.Close();

                var wizard = new SetupWindow(this, Path.Combine(Path.GetTempPath(), "StigGpoManager-demo", "config.setup-demo.json"), null, _session.Directory, demo: true)
                    { WindowStartupLocation = WindowStartupLocation.Manual, Left = Left + 40, Top = Top + 30 };
                wizard.Show();
                for (int t = 0; t < 100 && wizard.IsDetecting; t++) await Task.Delay(200);
                if (_run?.Package?.ZipPath != null) await wizard.LoadPackageFile(_run.Package.ZipPath);
                for (int page = 0; page < 4; page++)
                {
                    wizard.Pages.SelectedIndex = page;
                    await Settle();
                    Save(wizard, Path.Combine(folder, $"{14 + page:00}-setup-{page + 1}.png"));
                }
                wizard.Close();

                await Shot(folder, "10-rollback", "PanelRollback");
                FindCleanup_Click(null, null); await WaitIdle();
                if (CleanupGrid.ItemsSource is List<CleanupRow> rows && rows.Count > 0) rows[0].Selected = true;
                await Shot(folder, "11-cleanup", "PanelCleanup");
                await Shot(folder, "12-exceptions-register", "PanelRegister");
                await Shot(folder, "13-audit-log", "PanelAudit");
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString());
                return 1;
            }
        }

        private async Task WaitIdle(bool requireRun = false)
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            int idle = 0;
            while (DateTime.UtcNow < deadline && idle < 3)
            {
                await Task.Delay(200);
                idle = !IsBusy && (!requireRun || _run != null) ? idle + 1 : 0;
            }
            await Settle();
        }

        private async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private async Task Shot(string folder, string name, string panel)
        {
            GoTo(panel);
            await Settle();
            Save(this, Path.Combine(folder, name + ".png"));
        }

        /// <summary>Renders a window's client area (with an opaque background) to PNG.</summary>
        private static void Save(Window window, string path)
        {
            window.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
    }
}
