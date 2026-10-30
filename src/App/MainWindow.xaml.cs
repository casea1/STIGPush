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
    /// Home (calendar of quarterly updates, current update, history) and a four-stage update view
    /// (Prepare, Review, Pilot, Promote) with one primary action per stage. Every AD change still goes
    /// through Plan -> ConfirmWindow -> Execute on a background thread.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Session _session;
        private RunState _run;
        private PackageScanResult _scan;
        private List<BackupRow> _backupRows = new List<BackupRow>();
        private List<CentralStoreItem> _storeItems = new List<CentralStoreItem>();
        private ReviewResult _review;
        private List<CheckResult> _lastPreflight;
        private ObservableCollection<ExceptionEntry> _register = new ObservableCollection<ExceptionEntry>();
        private readonly ObservableCollection<LogLine> _log = new ObservableCollection<LogLine>();
        private string _zipPath;
        private Stage _viewStage = Stage.Prepare;

        public bool IsBusy { get; private set; }
        public bool ExitRequested { get; set; }
        public event Action RunChanged;

        private StigEngine Engine => _session.Engine;

        public MainWindow(Session session)
        {
            InitializeComponent();
            _session = session;
            LogList.ItemsSource = _log;
            RegisterGrid.ItemsSource = _register;
            VersionText.Text = "v" + Installer.Version;
            Title = "STIG GPO Manager " + Installer.Version;
            Calendar.RowClicked += id => OpenRun(id);
            if (session.IsDemo)
            {
                Title = "STIG GPO Manager " + Installer.Version + " - DEMO (simulated domain, no AD changes)";
                HeaderBorder.Background = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E));
                session.Engine.Log += AddLog;
            }
            Loaded += (s, e) => Connect();
            Closed += (s, e) => _session.Dispose();
        }

        // ================================================================ infrastructure

        private void AddLog(LogLevel level, string text)
        {
            Brush brush = level == LogLevel.Error ? Brushes.Firebrick : level == LogLevel.Warning ? Brushes.DarkOrange : level == LogLevel.Success ? Brushes.DarkGreen : Brushes.Black;
            var line = new LogLine { Text = $"{DateTime.Now:HH:mm:ss} {text}", Brush = brush };
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _log.Add(line);
                while (_log.Count > 5000) _log.RemoveAt(0);
                LogList.ScrollIntoView(line);
                if (level != LogLevel.Info) StatusText.Text = text;
            }));
        }

        private void AddCommand(string text) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ShowCommands.IsChecked != true) return;
            var line = new LogLine { Text = $"{DateTime.Now:HH:mm:ss}     {text}", Brush = Brushes.SteelBlue };
            _log.Add(line);
            LogList.ScrollIntoView(line);
        }));

        private void LogToggle_Changed(object sender, RoutedEventArgs e)
        {
            var on = LogToggle.IsChecked == true;
            LogList.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            ShowCommands.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetBusy(bool busy, string text = null)
        {
            IsBusy = busy;
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            Busy.IsIndeterminate = busy;
            StatusText.Text = busy ? text + " ..." : "Ready";
            Views.IsEnabled = !busy;
            NewUpdateButton.IsEnabled = !busy;
        }

        /// <summary>Runs work off the UI thread; errors are logged and shown; then continues on the UI thread.</summary>
        private void RunOp<T>(string title, Func<T> work, Action<T> done = null)
        {
            if (IsBusy) { MessageBox.Show(this, "Another operation is still running.", Title); return; }
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
                RefreshAll();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void RunOp(string title, Action work, Action done = null) =>
            RunOp<object>(title, () => { work(); return null; }, _ => done?.Invoke());

        /// <summary>Plan (read-only) -> show exact changes -> confirm -> execute.</summary>
        private void Push(string title, Func<OperationPlan> plan, Action<ConfirmResult> execute, Action after = null)
        {
            if (!RequireEngine()) return;
            RunOp("Preparing: " + title, plan, p =>
            {
                var r = ConfirmWindow.Ask(this, p);
                if (!r.Confirmed) { AddLog(LogLevel.Info, title + ": cancelled, nothing changed."); return; }
                RunOp(title, () => execute(r), after);
            });
        }

        private bool RequireEngine()
        {
            if (Engine != null) return true;
            MessageBox.Show(this, "Not connected to the domain. Open Health to see what is wrong, or run the setup wizard.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowView(HealthView);
            return false;
        }

        private static void Open(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private void ShowView(FrameworkElement view)
        {
            foreach (UIElement child in Views.Children) child.Visibility = child == view ? Visibility.Visible : Visibility.Collapsed;
            if (view == AuditView) LoadAudit();
            if (view == RegisterView) LoadRegister();
            if (view == HomeView) RefreshHome();
        }

        private void RefreshAll()
        {
            UpdateHeader();
            if (HomeView.Visibility == Visibility.Visible) RefreshHome();
            if (UpdateView.Visibility == Visibility.Visible) RefreshUpdate();
            RunChanged?.Invoke();
        }

        private void UpdateHeader()
        {
            var cfg = _session.Config;
            HeaderText.Text = cfg == null ? "not configured" : $"{cfg.Domain}  •  DC {_session.Directory?.Server ?? "not connected"}";
        }

        // ================================================================ connect / health

        private void Connect()
        {
            UpdateHeader();
            ConfigPathText.Text = "Config: " + _session.ConfigPath + (_session.IsDemo ? "   (DEMO: simulated domain, nothing touches Active Directory)" : "");
            foreach (var i in _session.Issues) AddLog(i.IsError ? LogLevel.Error : LogLevel.Warning, "Config: " + i.Message);
            if (_session.Config == null || !_session.ConfigValid)
            {
                _lastPreflight = _session.Preflight(false);
                PreflightGrid.ItemsSource = _lastPreflight;
                RefreshHome();
                if (!File.Exists(_session.ConfigPath) &&
                    MessageBox.Show(this, "Welcome. There is no configuration yet.\n\nRun the setup wizard now? It detects your domain, exception-GPO prefix, pilot group and current DISA GPOs, and asks you to confirm each value.",
                                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    Dispatcher.BeginInvoke(new Action(() => OpenSetup()));
                return;
            }
            MappingColumn.ItemsSource = _session.Config.Mappings;
            RegisterPathText.Text = "File: " + _session.Config.ExceptionsRegisterPath;
            AuditPathText.Text = "Append-only log: " + _session.Config.AuditLogPath;
            bool dcdiag = DcDiagCheck.IsChecked == true && !_session.IsDemo;
            RunOp("Checking the domain", () =>
            {
                if (!_session.IsDemo)
                {
                    try { _session.Connect(AddLog, AddCommand); }
                    catch (Exception ex) { AddLog(LogLevel.Error, ex.Message); }
                }
                return _session.Preflight(dcdiag);
            }, results =>
            {
                _lastPreflight = results;
                PreflightGrid.ItemsSource = results;
                var fails = results.Count(r => r.Status == CheckStatus.Fail);
                AddLog(fails == 0 ? LogLevel.Success : LogLevel.Warning, $"Checks: {results.Count(r => r.Status == CheckStatus.Pass)} passed, {results.Count(r => r.Status == CheckStatus.Warning)} warnings, {fails} failed.");
            });
        }

        private void Preflight_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            bool dcdiag = DcDiagCheck.IsChecked == true && !_session.IsDemo;
            RunOp("Running checks", () => _session.Preflight(dcdiag), r => { _lastPreflight = r; PreflightGrid.ItemsSource = r; });
        }

        private void HealthCard_Click(object sender, RoutedEventArgs e) => ShowView(HealthView);

        private void RefreshHealthCard()
        {
            if (_lastPreflight == null) { HealthTitle.Text = "Checking the domain ..."; HealthDetail.Text = ""; HealthDot.Background = (Brush)FindResource("Muted"); return; }
            var fails = _lastPreflight.Where(r => r.Status == CheckStatus.Fail).ToList();
            var warns = _lastPreflight.Where(r => r.Status == CheckStatus.Warning).ToList();
            HealthDot.Background = (Brush)FindResource(fails.Count > 0 ? "Danger" : warns.Count > 0 ? "Warn" : "Success");
            HealthTitle.Text = fails.Count > 0 ? $"{fails.Count} problem(s) need fixing" : warns.Count > 0 ? "Ready, with warnings" : "Domain checks passed";
            HealthDetail.Text = fails.Count > 0 ? fails[0].Name + ": " + fails[0].Detail
                              : warns.Count > 0 ? string.Join("; ", warns.Take(2).Select(w => w.Name)) + ". Click for details."
                              : $"{_session.Config?.Domain} via {_session.Directory?.Server}. Click for details.";
        }

        // ================================================================ home

        private sealed class HistoryRow
        {
            public RunState State { get; set; }
            public string Release { get; set; }
            public string Status { get; set; }
            public int Items { get; set; }
            public string Created { get; set; }
        }

        public sealed class StageItem
        {
            public int Index { get; set; }
            public string Name { get; set; }
            public string Badge { get; set; }
            public Brush Fill { get; set; }
            public Brush Stroke { get; set; }
            public Brush BadgeInk { get; set; }
            public FontWeight Weight { get; set; }
            public TextDecorationCollection Underline { get; set; }
        }

        private static readonly string[] StageNames = { "Prepare", "Review", "Pilot", "Promote" };

        private List<StageItem> Stages(Stage actual, Stage? viewing)
        {
            var accent = (Brush)FindResource("Accent");
            var success = (Brush)FindResource("Success");
            var line = new SolidColorBrush(Color.FromRgb(0xCB, 0xD2, 0xDB));
            var list = new List<StageItem>();
            for (int i = 0; i < 4; i++)
            {
                bool done = (int)actual > i, current = (int)actual == i;
                list.Add(new StageItem
                {
                    Index = i, Name = StageNames[i], Badge = done ? "✓" : (i + 1).ToString(),
                    Fill = done ? success : current ? accent : Brushes.White,
                    Stroke = done ? success : current ? accent : line,
                    BadgeInk = done || current ? Brushes.White : (Brush)FindResource("Muted"),
                    Weight = viewing.HasValue && (int)viewing.Value == i ? FontWeights.Bold : current ? FontWeights.SemiBold : FontWeights.Normal,
                    Underline = viewing.HasValue && (int)viewing.Value == i ? TextDecorations.Underline : null,
                });
            }
            return list;
        }

        private List<RunState> LoadRuns() =>
            _session.Config == null ? new List<RunState>() : new RunStore(_session.Config.RunsFolder).LoadAll(m => AddLog(LogLevel.Warning, m));

        private RunState ActiveRun(List<RunState> runs) =>
            runs.FirstOrDefault(r => r.Phase != RunPhase.Closed && r.Phase != RunPhase.RolledBack);

        private void RefreshHome()
        {
            RefreshHealthCard();
            var runs = LoadRuns();
            var soak = _session.Config?.SoakDays ?? 14;
            var tl = Core.Timeline.Build(runs, soak, DateTime.Now);
            Calendar.Timeline = tl;
            NextReleaseText.Text = $"Next DISA release expected: {tl.NextExpectedRelease:MMMM yyyy}";
            HistoryGrid.ItemsSource = runs.Select(r => new HistoryRow
            {
                State = r, Release = Core.Timeline.QuarterTitle(r).Replace(" release", ""),
                Status = r.StatusText(soak, DateTime.UtcNow), Items = r.Items.Count(i => i.NewGpoId != null),
                Created = r.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd"),
            }).ToList();

            var active = ActiveRun(runs);
            if (active == null)
            {
                CurrentTitle.Text = "No update in progress";
                CurrentStatus.Text = $"When DISA's {tl.NextExpectedRelease:MMMM yyyy} GPO package has been transferred in, start the update here.";
                MiniStepper.ItemsSource = null;
                ContinueButton.Content = "Start a new quarterly update";
                CurrentReportButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                var stage = Core.Timeline.StageOf(active);
                CurrentTitle.Text = Core.Timeline.QuarterTitle(active);
                CurrentStatus.Text = StageSummary(active, stage);
                MiniStepper.ItemsSource = Stages(stage, null);
                ContinueButton.Content = stage == Stage.Done ? "Open this update" : "Continue: " + StageNames[(int)stage] + "  →";
                CurrentReportButton.Visibility = Visibility.Visible;
            }
        }

        private string StageSummary(RunState r, Stage stage)
        {
            var soak = _session.Config?.SoakDays ?? 14;
            switch (stage)
            {
                case Stage.Prepare: return "Package loaded. Next: import the new GPOs.";
                case Stage.Review: return $"{r.Items.Count(i => i.NewGpoId != null)} new GPO(s) imported, not linked yet. Next: review what changed.";
                case Stage.Pilot: return "Reviewed. Next: link the new GPOs to the pilot machines.";
                case Stage.Promote:
                    var s = r.StatusText(soak, DateTime.UtcNow);
                    var gate = r.Gate == null ? "no scan results imported yet" : r.Gate.Passed ? "scan gate passed" : $"{r.Gate.BlockingCount} blocking finding(s)";
                    return $"{s}; {gate}.";
                default: return r.StatusText(soak, DateTime.UtcNow) + ". The previous version is disabled, kept for rollback.";
            }
        }

        private void NavHome_Click(object sender, RoutedEventArgs e) => ShowView(HomeView);
        private void NavRegister_Click(object sender, RoutedEventArgs e) => ShowView(RegisterView);
        private void NavAudit_Click(object sender, RoutedEventArgs e) => ShowView(AuditView);
        private void NavHealth_Click(object sender, RoutedEventArgs e) => ShowView(HealthView);
        private void NavCleanup_Click(object sender, RoutedEventArgs e) { ShowView(CleanupView); FindCleanup_Click(null, null); }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            var active = ActiveRun(LoadRuns());
            if (active == null) NewUpdate_Click(null, null);
            else OpenRun(active.RunId);
        }

        private void NewUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            var active = ActiveRun(LoadRuns());
            if (active != null && Core.Timeline.StageOf(active) != Stage.Done &&
                MessageBox.Show(this, $"The {Core.Timeline.QuarterTitle(active)} update is still in progress ({active.StatusText(_session.Config.SoakDays, DateTime.UtcNow)}).\n\nStart another update anyway? (The open one is kept.)",
                                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _run = null; _scan = null; _review = null;
            _backupRows = new List<BackupRow>(); _storeItems = new List<CentralStoreItem>();
            BackupsGrid.ItemsSource = null; HashText.Text = ""; StoreSummary.Text = "";
            ZipPathText.Text = "The tool reads it, shows its SHA-256 and finds every GPO inside. Nothing changes yet.";
            _zipPath = null;
            _viewStage = Stage.Prepare;
            ShowView(UpdateView);
            RefreshUpdate();
        }

        private HistoryRow SelectedHistory()
        {
            if (HistoryGrid.SelectedItem is HistoryRow row) return row;
            MessageBox.Show(this, "Select an update in the History list first.", Title);
            return null;
        }

        private void HistoryOpen_Click(object sender, RoutedEventArgs e) { var r = SelectedHistory(); if (r != null) OpenRun(r.State.RunId); }

        private void HistoryReport_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedHistory();
            if (r == null || !RequireEngine()) return;
            RunOp("Writing run report", () => Engine.WriteRunReport(Engine.Store.Load(r.State.RunId)), Open);
        }

        private void CurrentReport_Click(object sender, RoutedEventArgs e)
        {
            var active = ActiveRun(LoadRuns());
            if (active == null || !RequireEngine()) return;
            RunOp("Writing run report", () => Engine.WriteRunReport(Engine.Store.Load(active.RunId)), Open);
        }

        private void HistoryRollback_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedHistory();
            if (r == null || !RequireEngine()) return;
            var run = Engine.Store.Load(r.State.RunId);
            Rollback(run);
        }

        private void HistoryClose_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedHistory();
            if (r == null || !RequireEngine()) return;
            if (MessageBox.Show(this, $"Mark the {r.Release} update as finished? It stays on disk for audit and reports.", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            RunOp("Closing update", () => Engine.CloseRun(Engine.Store.Load(r.State.RunId)));
        }

        // ================================================================ update view

        private void OpenRun(string runId)
        {
            if (!RequireEngine()) return;
            RunOp("Opening update", () =>
            {
                var run = Engine.Store.Load(runId);
                PackageScanResult scan = null;
                List<CentralStoreItem> store = new List<CentralStoreItem>();
                try
                {
                    scan = Engine.RescanPackage(run);
                    if (Core.Timeline.StageOf(run) == Stage.Prepare) store = Engine.CompareCentralStore(scan);
                }
                catch (Exception ex) { AddLog(LogLevel.Warning, ex.Message); }
                return Tuple.Create(run, scan, store);
            }, t =>
            {
                _run = t.Item1; _scan = t.Item2; _storeItems = t.Item3; _review = null;
                ZipPathText.Text = _run.Package?.ZipPath;
                HashText.Text = HashLine(_run.Package);
                BuildBackupRows();
                ShowStoreSummary();
                _viewStage = Core.Timeline.StageOf(_run) == Stage.Done ? Stage.Done : Core.Timeline.StageOf(_run);
                ShowView(UpdateView);
                EnterStage(_viewStage);
            });
        }

        private void Stage_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is int i)) return;
            var actual = _run == null ? Stage.Prepare : Core.Timeline.StageOf(_run);
            if (i > (int)actual) { StatusText.Text = $"Finish '{StageNames[(int)actual]}' first."; return; }
            EnterStage((Stage)i);
        }

        /// <summary>Shows a stage and loads what it needs (review runs automatically the first time).</summary>
        private void EnterStage(Stage stage)
        {
            _viewStage = stage;
            RefreshUpdate();
            if (_run == null || Engine == null) return;
            if (stage == Stage.Review)
            {
                bool reviewed = _run.History.Any(h => h.Phase == RunPhase.Reviewed.ToString());
                if (_review == null && !reviewed) Review_Click(null, null);
                else if (_review == null) RunOp("Loading review", () => Engine.Review(_run), r => { _review = r; ShowReview(); });
                else ShowReview();
            }
            else if (stage == Stage.Promote)
            {
                var run = _run;
                RunOp("Checking promotion gate", () => Engine.EvaluatePromotionChecks(run), ShowPromoteChecks);
            }
        }

        private void RefreshUpdate()
        {
            var actual = _run == null ? Stage.Prepare : Core.Timeline.StageOf(_run);
            UpdateTitle.Text = _run == null ? "New quarterly update" : Core.Timeline.QuarterTitle(_run) + "  •  " + Path.GetFileName(_run.Package?.ZipPath ?? "");
            Stepper.ItemsSource = Stages(actual, _viewStage == Stage.Done ? (Stage?)null : _viewStage);
            RollbackButton.Visibility = _run != null && Engine != null && Engine.RollbackableItems(_run).Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            PreparePanel.Visibility = _viewStage == Stage.Prepare ? Visibility.Visible : Visibility.Collapsed;
            ReviewPanel.Visibility = _viewStage == Stage.Review ? Visibility.Visible : Visibility.Collapsed;
            PilotPanel.Visibility = _viewStage == Stage.Pilot ? Visibility.Visible : Visibility.Collapsed;
            PromotePanel.Visibility = _viewStage == Stage.Promote ? Visibility.Visible : Visibility.Collapsed;
            DonePanel.Visibility = _viewStage == Stage.Done ? Visibility.Visible : Visibility.Collapsed;

            var items = _run?.Items.Where(i => i.NewGpoId != null).ToList() ?? new List<RunItem>();
            PilotGrid.ItemsSource = items.Select(i => new ItemRow(i, false)).ToList();
            PromoteGrid.ItemsSource = items.Select(i => new ItemRow(i, false)).ToList();
            ShowSoak();
            ShowGate();

            bool changesAd = false;
            string heading = "", explain = "", action = null, hint = "";
            bool enabled = true, primary = true;
            switch (_viewStage)
            {
                case Stage.Prepare:
                    heading = "1. Prepare: load DISA's package and import the new GPOs";
                    explain = "Drop the zip. The tool lists every GPO in it and the name each will get (with its version, e.g. v1r4). Red rows match none of your mappings and are skipped unless you pick one. Matching uses the names inside the package, not this computer.";
                    changesAd = true;
                    if (_run == null)
                    {
                        action = "Load package";
                        enabled = !string.IsNullOrEmpty(_zipPath);
                        hint = enabled ? "Reads the zip. Nothing changes in AD." : "Drop the DISA zip above, or click Browse.";
                    }
                    else if (actual == Stage.Prepare)
                    {
                        var n = _backupRows.Count(r => !r.Skip);
                        action = $"Import {n} new GPO(s)";
                        enabled = n > 0;
                        hint = "Backs up all GPOs, updates the Central Store if needed, then creates new GPOs. Never overwrites an existing GPO; links nothing.";
                    }
                    else { action = "Next: Review  →"; hint = "✓ Imported."; primary = false; }
                    break;
                case Stage.Review:
                    heading = "2. Review: what DISA changed, and what it means for your exceptions";
                    explain = "Each new GPO is compared with the version in use. Red rows are user rights / restricted groups that one of your exception GPOs replaces: its list must contain every STIG entry. Yellow rows are exceptions to look at.";
                    action = "Next: Pilot  →";
                    hint = _review == null ? "Running the review ..." : "When you have read the flagged rows, continue. The full report is saved for your records.";
                    break;
                case Stage.Pilot:
                    heading = "3. Pilot: try the new GPOs on pilot machines";
                    explain = "Links each new GPO to the pilot OUs (or to production filtered to the pilot group), always below your exception GPOs. Then gpupdate and scan the pilot machines, test, and let them soak. You can close the tool meanwhile; this update is saved.";
                    changesAd = true;
                    if (actual == Stage.Pilot) { action = "Start pilot"; hint = "Shows the exact links before anything changes."; }
                    else { action = "Next: Promote  →"; primary = SoakDone() && (_run?.Gate?.Passed ?? false); hint = primary ? "✓ Soak complete and scan gate passed." : "Promote when the soak is over and the scan gate passes."; }
                    break;
                case Stage.Promote:
                    heading = "4. Promote: move the new GPOs to production";
                    explain = "Links each new GPO to its production OUs, disables (does not delete) the previous version so rollback is one click, and removes the pilot links. Then refresh policy on the computers.";
                    changesAd = true;
                    action = "Promote to production"; hint = "You will see every change and type PROMOTE to confirm.";
                    break;
                default:
                    heading = "Done";
                    explain = "This quarter's GPOs are in production. Run a production scan (SCC / Evaluate-STIG), and next quarter use Clean up old versions.";
                    action = "Back to Home"; primary = false;
                    DoneDetail.Text = (_run == null ? "" : _run.StatusText(_session.Config?.SoakDays ?? 14, DateTime.UtcNow) + ". ") +
                                      "The previous version's links are disabled and kept, so Roll back puts everything back in one step.";
                    break;
            }
            StageHeading.Text = heading;
            StageExplain.Text = explain;
            ChangesPillText.Text = changesAd ? "Changes AD (asks first)" : "No AD changes";
            ChangesPill.Background = (Brush)FindResource(changesAd ? "WarnSoft" : "SuccessSoft");
            PrimaryAction.Content = action;
            PrimaryAction.IsEnabled = enabled;
            PrimaryAction.Style = (Style)FindResource(primary ? "PrimaryButton" : typeof(Button));
            PrimaryHint.Text = hint;
        }

        private bool SoakDone() =>
            _run?.PilotStartedUtc != null && (DateTime.UtcNow - _run.PilotStartedUtc.Value).TotalDays >= (_session.Config?.SoakDays ?? 14);

        private void ShowSoak()
        {
            var soak = _session.Config?.SoakDays ?? 14;
            SoakBar.Maximum = Math.Max(1, soak);
            if (_run?.PilotStartedUtc == null)
            {
                SoakDayText.Text = "Pilot not started";
                SoakEndText.Text = $"Soak period: {soak} days";
                SoakBar.Value = 0;
                return;
            }
            var day = (int)Math.Floor((DateTime.UtcNow - _run.PilotStartedUtc.Value).TotalDays) + 1;
            SoakDayText.Text = day > soak ? "Soak complete" : $"Day {day} of {soak}";
            SoakEndText.Text = "Soak ends " + _run.PilotStartedUtc.Value.ToLocalTime().AddDays(soak).ToString("dddd d MMMM");
            SoakBar.Value = Math.Min(day, soak);
        }

        private void ShowGate()
        {
            if (_run?.Gate == null)
            {
                GateText.Text = (_session.Config?.RequireScanGate ?? true) ? "Scan gate: no pilot results imported yet (required before Promote)." : "Scan gate: optional (off).";
                GateText.Foreground = (Brush)FindResource("Muted");
            }
            else
            {
                GateText.Text = (_run.Gate.Passed ? "✓ Scan gate passed: " : "✖ Scan gate failed: ") + _run.Gate.Summary;
                GateText.Foreground = (Brush)FindResource(_run.Gate.Passed ? "Success" : "Danger");
            }
        }

        private void PrimaryAction_Click(object sender, RoutedEventArgs e)
        {
            var actual = _run == null ? Stage.Prepare : Core.Timeline.StageOf(_run);
            switch (_viewStage)
            {
                case Stage.Prepare:
                    if (_run == null) LoadPackage();
                    else if (actual == Stage.Prepare) PushImport();
                    else EnterStage(Stage.Review);
                    break;
                case Stage.Review: EnterStage(Stage.Pilot); break;
                case Stage.Pilot:
                    if (actual == Stage.Pilot) PushPilot();
                    else EnterStage(Stage.Promote);
                    break;
                case Stage.Promote: PushPromote(); break;
                default: ShowView(HomeView); break;
            }
        }

        // ---------------------------------------------------------------- prepare

        private void Drop_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ZipDrop_Drop(object sender, DragEventArgs e)
        {
            var zip = (e.Data.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (zip == null) { MessageBox.Show(this, "Drop a .zip file.", Title); return; }
            SetZip(zip);
        }

        private void BrowseZip_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "DISA GPO package (*.zip)|*.zip", Title = "Select DISA's GPO package" };
            if (dlg.ShowDialog(this) == true) SetZip(dlg.FileName);
        }

        private void SetZip(string path)
        {
            if (_run != null) { MessageBox.Show(this, "This update already has its package. Start a new quarterly update for another package.", Title); return; }
            _zipPath = path;
            ZipPathText.Text = path;
            RefreshUpdate();
            LoadPackage();
        }

        private static string HashLine(PackageRecord p)
        {
            if (p == null) return "";
            var match = p.HashMatches == null ? "(no expected hash given)" : p.HashMatches == true ? "✓ matches the expected hash" : "✖ DOES NOT MATCH the expected hash";
            return $"SHA-256 {p.Sha256}   {match}";
        }

        private void LoadPackage()
        {
            if (!RequireEngine() || string.IsNullOrEmpty(_zipPath)) return;
            var zip = _zipPath;
            var expected = ExpectedHashBox.Text;
            RunOp("Reading the package", () =>
            {
                var run = Engine.CreateRun(zip, expected, out var scan);
                return Tuple.Create(run, scan, Engine.CompareCentralStore(scan));
            }, t =>
            {
                _run = t.Item1; _scan = t.Item2; _storeItems = t.Item3; _review = null;
                HashText.Text = HashLine(_run.Package);
                HashText.Foreground = (Brush)FindResource(_run.Package.HashMatches == false ? "Danger" : "Ink");
                BuildBackupRows();
                ShowStoreSummary();
                if (_run.Package.HashMatches == false)
                    MessageBox.Show(this, "The package hash does NOT match the expected value. Do not continue unless this is resolved with your transfer record.", "Hash mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        private void BuildBackupRows()
        {
            if (_scan == null || _session.Config == null) { BackupsGrid.ItemsSource = null; return; }
            _backupRows = MappingResolver.Resolve(_scan.Backups, _session.Config).Select(c =>
            {
                var done = _run?.Items.FirstOrDefault(i => i.BackupId == c.Backup.BackupId && i.NewGpoId != null);
                return new BackupRow(c, done != null ? $"Imported as '{done.NewGpoName}'" : null);
            }).ToList();
            foreach (var r in _backupRows) r.PropertyChanged += (s, e) => RefreshUpdate();
            BackupsGrid.ItemsSource = _backupRows;
        }

        private void ShowStoreSummary()
        {
            if (_storeItems.Count == 0) { StoreSummary.Text = _run == null ? "" : "Central Store: compared when the update was prepared."; return; }
            int copy = _storeItems.Count(i => i.WillCopy), newer = _storeItems.Count(i => i.Status == CentralStoreStatus.SkippedStoreNewer && !i.OverrideDowngrade);
            StoreSummary.Text = copy == 0
                ? $"Central Store: up to date ({_storeItems.Count} templates checked){(newer > 0 ? $"; {newer} kept because the store copy is newer" : "")}."
                : $"Central Store: {copy} template file(s) will be added or updated when you import (the store is backed up first){(newer > 0 ? $"; {newer} kept because the store copy is newer" : "")}.";
        }

        private void StoreDetails_Click(object sender, RoutedEventArgs e)
        {
            if (_storeItems.Count == 0) { MessageBox.Show(this, "Load a package first.", Title); return; }
            var rows = _storeItems.Select(i => new StoreRow(i)).ToList();
            var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false };
            grid.Columns.Add(new DataGridTextColumn { Header = "File", Binding = new System.Windows.Data.Binding("File"), IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new System.Windows.Data.Binding("Status"), IsReadOnly = true, Width = new DataGridLength(1.5, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Package date", Binding = new System.Windows.Data.Binding("PackageDate"), IsReadOnly = true, Width = 130 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Store date", Binding = new System.Windows.Data.Binding("StoreDate"), IsReadOnly = true, Width = 130 });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Overwrite newer store copy", Binding = new System.Windows.Data.Binding("Override") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 170 });
            var w = new Window { Title = "Central Store templates", Owner = this, Width = 900, Height = 520, Content = new DockPanel { Margin = new Thickness(12), Children = { grid } }, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = new FontFamily("Segoe UI") };
            w.ShowDialog();
            ShowStoreSummary();
        }

        private void UseSuggestions_Click(object sender, RoutedEventArgs e)
        {
            var rows = _backupRows.Where(r => r.Candidate.Mapping == null && r.Candidate.SuggestedMapping != null).ToList();
            if (rows.Count == 0) { MessageBox.Show(this, "No suggestions for the red rows. Pick a mapping in the Mapping column, or add one with Edit mappings.", Title); return; }
            foreach (var r in rows) r.Mapping = r.Candidate.SuggestedMapping;
            MappingResolver.FlagConflicts(_backupRows.Select(r => r.Candidate).ToList());
            AddLog(LogLevel.Info, $"Assigned suggested mappings to {rows.Count} GPO(s). Check 'Will be created as' before importing.");
            BackupsGrid.Items.Refresh();
            RefreshUpdate();
        }

        private void PushImport()
        {
            var run = _run;
            var selected = _backupRows.Where(r => !r.Skip).Select(r => r.Candidate).ToList();
            var store = _storeItems.ToList();
            Push("Import new GPOs", () =>
            {
                var plan = Engine.PlanImport(run, selected);
                plan.Title = $"Import {selected.Count} new GPO(s)" + (store.Any(i => i.WillCopy) ? " and update the Central Store" : "");
                var sp = Engine.PlanCentralStore(store);
                plan.Changes.InsertRange(0, sp.Changes);
                plan.Warnings.AddRange(sp.Warnings);
                if (store.Any(i => i.WillCopy)) plan.Blockers.AddRange(sp.Blockers);
                return plan;
            }, r =>
            {
                if (store.Any(i => i.WillCopy)) Engine.ExecuteCentralStore(run, store);
                else Engine.MarkCentralStoreCurrent(run);
                Engine.ExecuteImport(run, selected);
                _review = Engine.Review(run);
            }, () =>
            {
                BuildBackupRows();
                EnterStage(Stage.Review);
            });
        }

        // ---------------------------------------------------------------- review

        private void Review_Click(object sender, RoutedEventArgs e)
        {
            if (_run == null || !RequireEngine()) return;
            var run = _run;
            RunOp("Reviewing changes", () => Engine.Review(run), r => { _review = r; ShowReview(); });
        }

        private void ShowReview()
        {
            if (_review == null) return;
            ReviewWarnings.ItemsSource = _review.Warnings;
            var attention = _review.Rows.Count(r => r.Severity > 0);
            ChipAttentionText.Text = attention == 0 ? "✓ Nothing needs attention" : $"{attention} item(s) need attention";
            ChipAttention.Background = (Brush)FindResource(attention == 0 ? "SuccessSoft" : "DangerSoft");
            ChipChanges.Text = $"{_review.Rows.Count(r => r.Change == "Added")} added • {_review.Rows.Count(r => r.Change == "Changed")} changed • {_review.Rows.Count(r => r.Change == "Removed")} removed";
            ApplyReviewFilter();
            RefreshUpdate();
        }

        private void ReviewFilter_Changed(object sender, RoutedEventArgs e) => ApplyReviewFilter();

        private void ApplyReviewFilter()
        {
            if (_review == null || ReviewGrid == null) return;
            var rows = ShowAllChanges.IsChecked == true || _review.Rows.All(r => r.Severity == 0) ? _review.Rows : _review.Rows.Where(r => r.Severity > 0).ToList();
            ReviewGrid.ItemsSource = rows.OrderByDescending(r => r.Severity).Select(r =>
            {
                if (string.IsNullOrEmpty(r.Flag)) r.Flag = r.Change;
                return r;
            }).ToList();
        }

        private void OpenReviewReport_Click(object sender, RoutedEventArgs e) =>
            Open(_review?.HtmlPath ?? _run?.Reports.LastOrDefault(p => Path.GetFileName(p).StartsWith("review-") && p.EndsWith(".html")));

        // ---------------------------------------------------------------- pilot / scans

        private void PushPilot()
        {
            var run = _run;
            var items = Engine.PilotableItems(run);
            Push("Start pilot", () => Engine.PlanPilot(run, items), r => Engine.ExecutePilot(run, items, r.DcBackupConfirmed), () =>
                MessageBox.Show(this, "Pilot links are in place.\n\n" + string.Join("\n", StigEngine.PilotChecklist.Select(s => "  • " + s)), "Pilot started", MessageBoxButton.OK, MessageBoxImage.Information));
        }

        private string ScanStage => StageBaseline.IsChecked == true ? StigEngine.StageBaseline : StigEngine.StagePilot;

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
            if (_run == null || !RequireEngine()) return;
            var run = _run;
            var stage = ScanStage;
            RunOp($"Importing {files.Length} scan result file(s)", () =>
            {
                foreach (var f in files)
                {
                    try { Engine.ImportScanResult(run, f, stage); }
                    catch (Exception ex) { AddLog(LogLevel.Error, $"{Path.GetFileName(f)}: {ex.Message}"); }
                }
                return Engine.EvaluateGate(run);
            }, ShowFindings);
        }

        private void ShowFindings(ScanGateResult g)
        {
            FindingsGrid.ItemsSource = g.Blocking.Select(f => new FindingRow { Finding = f })
                                        .Concat(g.Expected.Select(x => new FindingRow { Finding = x.Finding, Entry = x.Entry })).ToList();
            ShowGate();
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
            if (string.IsNullOrWhiteSpace(v[1])) { MessageBox.Show(this, "A justification is required; nothing was added.", Title); return; }
            var register = Engine.LoadRegister();
            foreach (var r in rows.GroupBy(r => r.VulnId + "|" + r.Stig).Select(g => g.First()))
                register.Entries.Add(new ExceptionEntry { VulnId = r.VulnId, StigName = r.Stig, ExceptionGpo = v[0], Justification = v[1], PoamReference = v[2], DateAdded = DateTime.UtcNow, AddedBy = _session.User });
            var run = _run;
            RunOp("Saving exceptions register", () => { Engine.SaveRegister(register); return run == null ? null : Engine.EvaluateGate(run); }, g => { if (g != null) ShowFindings(g); });
        }

        // ---------------------------------------------------------------- promote / refresh / rollback

        private void ShowPromoteChecks(List<CheckResult> results)
        {
            bool pass = results.All(r => r.Status != CheckStatus.Fail);
            PromoteGateText.Text = results.Count == 0 ? "No promotion checks configured."
                : string.Join(Environment.NewLine, results.Select(r => (r.Status == CheckStatus.Pass ? "✓ " : r.Status == CheckStatus.Fail ? "✖ " : "! ") + r.Name + ": " + r.Detail));
            PromoteGateBorder.Background = (Brush)FindResource(pass ? "SuccessSoft" : "DangerSoft");
            if (_run != null && Engine != null)
            {
                var run = _run;
                Task.Run(() => Engine.RefreshTargets(run)).ContinueWith(t =>
                {
                    if (!t.IsFaulted) RefreshGrid.ItemsSource = t.Result.Select(c => new RefreshRow { Computer = c }).ToList();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        private void PushPromote()
        {
            var run = _run;
            var items = Engine.PromotableItems(run);
            if (items.Count == 0) { MessageBox.Show(this, "Nothing is ready to promote. Start the pilot first.", Title); return; }
            Push("Promote", () => Engine.PlanPromote(run, items), r => Engine.ExecutePromote(run, r.TypedWord, items, r.DcBackupConfirmed), () =>
            {
                var refresh = MessageBox.Show(this, "Promote complete. Refresh policy on the affected computers now?\n\nSome settings still need a logoff or reboot.", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                ShowPromoteChecks(new List<CheckResult>());
                if (refresh) Dispatcher.BeginInvoke(new Action(() => PushRefresh_Click(null, null)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            });
        }

        private void PushRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_run == null || !RequireEngine()) return;
            var run = _run;
            RunOp("Listing computers", () => Engine.RefreshTargets(run), computers =>
            {
                var rows = computers.Select(c => new RefreshRow { Computer = c }).ToList();
                RefreshGrid.ItemsSource = rows;
                if (rows.Count == 0) { MessageBox.Show(this, "No computers were found under the production OUs.", Title); return; }
                var plan = new OperationPlan { Title = "Refresh Group Policy (Invoke-GPUpdate -Force -RandomDelayInMinutes 0)" };
                foreach (var r in rows) plan.Add("GPUpdate", r.Computer, "Force a policy refresh");
                plan.Warnings.Add("Some settings (user rights, services) need a logoff or reboot to take effect.");
                if (!ConfirmWindow.Ask(this, plan).Confirmed) return;
                var byName = rows.ToDictionary(x => x.Computer, StringComparer.OrdinalIgnoreCase);
                RunOp("Refreshing policy", () => Engine.ExecuteRefresh(run, computers, res => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (byName.TryGetValue(res.Computer, out var row)) { row.Result = res.Success ? "OK" : "FAILED"; row.Message = res.Message; }
                }))), results => AddLog(results.All(x => x.Success) ? LogLevel.Success : LogLevel.Warning, $"Policy refresh: {results.Count(x => x.Success)} OK, {results.Count(x => !x.Success)} failed."));
            });
        }

        private void UpdateRollback_Click(object sender, RoutedEventArgs e) { if (_run != null) Rollback(_run); }

        private void Rollback(RunState run)
        {
            var items = Engine.RollbackableItems(run);
            if (items.Count == 0) { MessageBox.Show(this, "Nothing to roll back: this update has not been piloted or promoted.", Title); return; }
            Push("Roll back " + Core.Timeline.QuarterTitle(run), () => Engine.PlanRollback(run, items), r => Engine.ExecuteRollback(run, items, r.DcBackupConfirmed), () =>
            {
                if (_run?.RunId == run.RunId) _run = Engine.Store.Load(run.RunId);
            });
        }

        // ================================================================ setup / config

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
            if (_scan != null) BuildBackupRows();
        }

        private void OpenConfig_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(_session.ConfigPath)) Process.Start("notepad.exe", "\"" + _session.ConfigPath + "\"");
            else MessageBox.Show(this, "Config file not found: " + _session.ConfigPath + "\nRun the setup wizard.", Title);
        }

        private void OpenWorking_Click(object sender, RoutedEventArgs e) => Open(_session.Config?.WorkingFolder);

        // ================================================================ cleanup

        private void FindCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            RunOp("Finding old versions", () => Engine.GetCleanupCandidates(), list =>
            {
                CleanupGrid.ItemsSource = list.Select(c => new CleanupRow(c)).ToList();
                AddLog(LogLevel.Info, list.Count == 0 ? "No old versions to clean up." : $"{list.Count} old version(s) can be cleaned up.");
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
                _register = new ObservableCollection<ExceptionEntry>(ExceptionsRegister.Load(_session.Config.ExceptionsRegisterPath).Entries);
                RegisterGrid.ItemsSource = _register;
            }
            catch (Exception ex) { AddLog(LogLevel.Error, "Cannot read the exceptions register: " + ex.Message); }
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
            if (MessageBox.Show(this, $"Delete {rows.Count} entr{(rows.Count == 1 ? "y" : "ies")}? (Takes effect when you click Save.)", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var r in rows) _register.Remove(r);
        }

        private void RegisterReload_Click(object sender, RoutedEventArgs e) => LoadRegister();

        private void RegisterSave_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireEngine()) return;
            RegisterGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var reg = new ExceptionsRegister { Entries = _register.ToList() };
            var problems = reg.Validate();
            if (problems.Count > 0) { MessageBox.Show(this, string.Join("\n", problems), "Not saved", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            RunOp("Saving exceptions register", () => Engine.SaveRegister(reg), () => AddLog(LogLevel.Success, $"Exceptions register saved ({reg.Entries.Count} entries)."));
        }

        // ================================================================ audit

        private void AuditRefresh_Click(object sender, RoutedEventArgs e) => LoadAudit();

        private void LoadAudit()
        {
            if (_session.Config == null) return;
            var runId = AuditCurrentRun.IsChecked == true ? _run?.RunId : null;
            AuditGrid.ItemsSource = new AuditLog(_session.Config.AuditLogPath, _session.User).ReadAll(runId).AsEnumerable().Reverse().Take(5000).Select(o => new AuditRow
            {
                Time = Json.GetString(o, "timestamp"), User = Json.GetString(o, "user"), Run = Json.GetString(o, "runId"),
                Action = Json.GetString(o, "action"), Target = Json.GetString(o, "target"), Result = Json.GetString(o, "result"),
                Error = Json.GetString(o, "error") ?? Json.GetString(o, "detail") ?? Json.GetString(o, "raw"),
            }).ToList();
        }

        // ================================================================ screenshot tour (/screenshots)

        /// <summary>Walks the demo and saves a PNG of each screen (used by CI for the README). Returns the exit code.</summary>
        public async Task<int> RunScreenshotTour(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                foreach (var g in FindChildren<DataGrid>(this)) { g.EnableRowVirtualization = false; g.EnableColumnVirtualization = false; }
                await WaitIdle(() => _lastPreflight != null);
                ShowView(HomeView);
                await Shot(folder, "01-home");

                var active = ActiveRun(LoadRuns());
                OpenRun(active.RunId);
                await WaitIdle(() => _run != null && UpdateView.Visibility == Visibility.Visible);
                EnterStage(Stage.Prepare); await WaitIdle();
                await Shot(folder, "02-prepare");
                EnterStage(Stage.Review); await WaitIdle(() => _review != null);
                await Shot(folder, "03-review");
                EnterStage(Stage.Pilot); await WaitIdle();
                ShowFindings(await Task.Run(() => Engine.EvaluateGate(_run)));
                await Shot(folder, "04-pilot");
                EnterStage(Stage.Promote); await WaitIdle();
                await Task.Delay(500);
                await Shot(folder, "05-promote");

                var plan = await Task.Run(() => Engine.PlanPromote(_run));
                var dlg = new ConfirmWindow(this, plan) { Width = 1000, Height = 720, WindowStartupLocation = WindowStartupLocation.Manual, Left = Left + 60, Top = Top + 40 };
                dlg.Show();
                await Settle();
                Save(dlg, Path.Combine(folder, "06-confirm.png"));
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
                    Save(wizard, Path.Combine(folder, $"{7 + page:00}-setup-{page + 1}.png"));
                }
                wizard.Close();

                ShowView(RegisterView); await Shot(folder, "11-exceptions");
                ShowView(AuditView); await Shot(folder, "12-audit-log");
                ShowView(HealthView); await Shot(folder, "13-health");
                ShowView(CleanupView); FindCleanup_Click(null, null); await WaitIdle();
                if (CleanupGrid.ItemsSource is List<CleanupRow> rows && rows.Count > 0) rows[0].Selected = true;
                await Shot(folder, "14-cleanup");
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString());
                return 1;
            }
        }

        private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T t) yield return t;
                foreach (var c in FindChildren<T>(child)) yield return c;
            }
        }

        private async Task WaitIdle(Func<bool> condition = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            int idle = 0;
            while (DateTime.UtcNow < deadline && idle < 3)
            {
                await Task.Delay(200);
                idle = !IsBusy && (condition == null || condition()) ? idle + 1 : 0;
            }
            await Settle();
        }

        private async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private async Task Shot(string folder, string name)
        {
            await Settle();
            Save(this, Path.Combine(folder, name + ".png"));
        }

        /// <summary>Renders a window at a fixed size (independent of the build machine's screen) to PNG.</summary>
        private static void Save(Window window, string path)
        {
            window.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            bool main = window is MainWindow;
            int w = main ? 1440 : (int)Math.Ceiling(root.ActualWidth), h = main ? 920 : (int)Math.Ceiling(root.ActualHeight);
            if (main) { root.Measure(new Size(w, h)); root.Arrange(new Rect(0, 0, w, h)); }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, w, h));
            bmp.Render(bg);
            bmp.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
            root.InvalidateMeasure();
            window.UpdateLayout();
        }
    }
}
