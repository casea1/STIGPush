using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StigGpoManager.Core;

namespace StigGpoManager
{
    /// <summary>
    /// Setup wizard: detects what it can from the domain (ConfigDetector), pre-fills every field, lets
    /// the admin confirm or correct each page, previews mappings against a DISA package, validates, and
    /// writes config.json. It only reads from AD.
    /// </summary>
    public partial class SetupWindow : Window
    {
        public static readonly string[] Modes = { "Auto", "OU", "Group", "None" };

        private readonly string _savePath;
        private readonly AppConfig _existing;
        private IGpoDirectory _dir;
        private readonly bool _ownsDirectory;
        private DetectionResult _det;
        private readonly ObservableCollection<MappingRow> _rows = new ObservableCollection<MappingRow>();
        private List<string> _ous = new List<string>();
        private List<string> _backupNames;

        public bool IsDetecting { get; private set; } = true;
        public string SavedPath { get; private set; }

        /// <param name="directory">An already-connected directory (demo, or the main window's), or null to connect to this computer's domain.</param>
        public SetupWindow(Window owner, string savePath, AppConfig existing, IGpoDirectory directory, bool demo = false)
        {
            InitializeComponent();
            if (owner != null && owner.IsVisible) Owner = owner; else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _savePath = savePath;
            _existing = existing;
            _dir = directory;
            _ownsDirectory = directory == null;
            if (demo)
            {
                Title += " - DEMO (simulated domain)";
                SetupHeader.Background = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E));
            }
            ModeColumn.ItemsSource = Modes;
            MappingsGrid.ItemsSource = _rows;
            DcBox.Items.Add("auto-pdc");
            SavePathText.Text = "Will be saved to: " + savePath;
            Loaded += async (s, e) => await DetectAsync();
            Closed += (s, e) => { if (_ownsDirectory) (_dir as IDisposable)?.Dispose(); };
            UpdateButtons();
        }

        // ------------------------------------------------------------------ detection

        public static string DetectComputerDomain()
        {
            try { return System.DirectoryServices.ActiveDirectory.Domain.GetComputerDomain().Name.ToLowerInvariant(); } catch { }
            var env = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
            if (!string.IsNullOrEmpty(env)) return env.ToLowerInvariant();
            try { return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName; } catch { return ""; }
        }

        public static string DefaultWorkingFolder()
        {
            try
            {
                var d = new DriveInfo("D");
                if (d.IsReady && d.DriveType == DriveType.Fixed) return @"D:\StigGpoManager";
            }
            catch { }
            return @"C:\StigGpoManager";
        }

        private async Task DetectAsync()
        {
            SetBusy(true, "Detecting domain settings ...");
            string error = null;
            var disaPrefix = _existing?.DisaGpoPrefix ?? "DoD ";
            var culture = CultureInfo.InstalledUICulture.Name;
            try
            {
                _det = await Task.Run(() =>
                {
                    if (_dir == null)
                    {
                        var domain = _existing?.Domain ?? DetectComputerDomain();
                        if (string.IsNullOrEmpty(domain)) throw new InvalidOperationException("This computer is not joined to a domain.");
                        _dir = new PowerShellDirectory(new AppConfig { Domain = domain, TargetDC = _existing?.TargetDC ?? "auto-pdc" });
                    }
                    return ConfigDetector.Detect(_dir, _existing?.WorkingFolder ?? DefaultWorkingFolder(), culture, disaPrefix,
                                                 m => Dispatcher.BeginInvoke(new Action(() => SetupStatus.Text = m)));
                });
            }
            catch (Exception ex) { error = ex.Message; }

            Fill(error);
            IsDetecting = false;
            SetBusy(false, error == null
                ? $"Detected {_det.Mappings.Count} mapping(s) from {_det.OrganizationalUnits.Count} OUs. Check each page, then save."
                : "Could not read the domain (" + error + "). Fill in the fields by hand.");
        }

        private void Fill(string error)
        {
            var s = _existing ?? _det?.Suggested ?? new AppConfig { Domain = DetectComputerDomain(), WorkingFolder = DefaultWorkingFolder(), Language = CultureInfo.InstalledUICulture.Name };
            DomainBox.Text = s.Domain;
            DcBox.Text = s.TargetDC ?? "auto-pdc";
            LanguageBox.Text = s.Language ?? "en-US";
            ExceptionPrefixBox.Text = s.ExceptionPrefix ?? "";
            DisaPrefixBox.Text = s.DisaGpoPrefix ?? "DoD ";
            PilotGroupBox.Text = s.PilotGroup ?? "";
            WorkingBox.Text = s.WorkingFolder ?? DefaultWorkingFolder();
            SoakBox.Text = s.SoakDays.ToString(CultureInfo.InvariantCulture);
            GateCheck.IsChecked = s.RequireScanGate;
            CommentCheck.IsChecked = s.WriteGpoComment;
            if (!string.IsNullOrWhiteSpace(s.CentralStorePath)) { StoreOverrideCheck.IsChecked = true; StoreBox.Text = s.CentralStorePath; }

            if (_det != null)
            {
                var d = _det.Domain;
                _ous = _det.OrganizationalUnits;
                DomainDetected.Text = $"Detected: {d.DnsRoot} ({d.NetBiosName})";
                foreach (var dc in d.DomainControllers) if (!DcBox.Items.Contains(dc)) DcBox.Items.Add(dc);
                DomainFacts.Text = $"PDC emulator: {d.PdcEmulator}\nDomain controllers: {d.DomainControllers.Count} ({string.Join(", ", d.DomainControllers)})\n" +
                                   $"OUs: {_det.OrganizationalUnits.Count}\nDISA GPO families linked: {_det.Mappings.Count}; not linked: {_det.UnlinkedFamilies.Count}";
                foreach (var c in _det.ExceptionPrefixCandidates) ExceptionPrefixBox.Items.Add(c.Prefix);
                ExceptionPrefixHint.Text = _det.ExceptionPrefixCandidates.Count == 0 ? "No candidate prefix found in GPO names."
                    : "Detected from GPO names: " + string.Join("   ", _det.ExceptionPrefixCandidates.Take(4).Select(c => c.ToString()));
                foreach (var g in _det.PilotGroupCandidates) PilotGroupBox.Items.Add(g);
                PilotGroupHint.Text = _det.PilotGroupCandidates.Count == 0
                    ? "No group with 'pilot' in its name was found. Only needed for mappings without pilot OUs (group-based pilot)."
                    : "Groups found: " + string.Join(", ", _det.PilotGroupCandidates);
                NotesList.ItemsSource = _det.Notes;
            }
            else
            {
                DomainDetected.Text = "Detection failed: " + error;
                DomainDetected.Foreground = Brushes.Firebrick;
            }
            UpdateStoreText();

            _rows.Clear();
            if (_existing != null)
            {
                foreach (var m in _existing.Mappings) _rows.Add(MappingRow.From(m, true, "From the current config", OnRowChanged));
                foreach (var dm in (_det?.Mappings ?? new List<DetectedMapping>()).Where(dm => !_existing.Mappings.Any(m => string.Equals(m.NameTemplate, dm.Mapping.NameTemplate, StringComparison.OrdinalIgnoreCase))))
                    _rows.Add(MappingRow.From(dm.Mapping, false, "Detected (not in current config): " + dm.Reason, OnRowChanged));
            }
            else if (_det != null)
            {
                foreach (var dm in _det.Mappings) _rows.Add(MappingRow.From(dm.Mapping, true, dm.Reason, OnRowChanged));
                foreach (var dm in _det.UnlinkedFamilies) _rows.Add(MappingRow.From(dm.Mapping, false, dm.Reason, OnRowChanged));
            }
            Recompute();
        }

        private void UpdateStoreText()
        {
            var domain = (DomainBox.Text ?? "").Trim();
            var path = StoreOverrideCheck.IsChecked == true && !string.IsNullOrWhiteSpace(StoreBox.Text) ? StoreBox.Text.Trim()
                     : $@"\\{domain}\SYSVOL\{domain}\Policies\PolicyDefinitions";
            if (StoreOverrideCheck.IsChecked != true) StoreBox.Text = path;
            bool exists = false;
            if (_dir is InMemoryDirectory) exists = true;
            else try { exists = domain.Length > 0 && Directory.Exists(path); } catch { }
            StoreDetected.Text = (exists ? "Found: " : "NOT FOUND: ") + path + (exists ? "" : "  (create the Central Store before using the tool)");
            StoreDetected.Foreground = exists ? new SolidColorBrush(Color.FromRgb(0x1F, 0x6E, 0x2E)) : Brushes.Firebrick;
        }

        // ------------------------------------------------------------------ mappings page

        private void OnRowChanged() => Recompute();

        private void Recompute()
        {
            var included = _rows.Where(r => r.Include).ToList();
            var ouSet = new HashSet<string>(_ous.Select(AppConfig.NormalizeDn), StringComparer.OrdinalIgnoreCase);
            foreach (var r in _rows)
            {
                var problems = new List<string>();
                try { _ = new System.Text.RegularExpressions.Regex(r.Match ?? ""); }
                catch (ArgumentException) { problems.Add("invalid regex"); }
                if (r.Include && r.ProdTargets.Count == 0) problems.Add("no production OUs");
                if (ouSet.Count > 0)
                    foreach (var dn in r.ProdTargets.Concat(r.PilotTargets).Where(dn => !ouSet.Contains(AppConfig.NormalizeDn(dn)) && !AppConfig.DnEquals(dn, _det?.Domain?.DistinguishedName)))
                        problems.Add("OU not found: " + dn);
                if (r.Include && _backupNames != null)
                {
                    var m = r.ToMapping();
                    var hits = _backupNames.Where(n => SafeMatch(m, n)).ToList();
                    r.Preview = hits.Count == 0 ? "matches nothing" : string.Join("; ", hits);
                    if (hits.Count == 0) problems.Add("matches no backup in the package");
                    if (hits.Count > 1) problems.Add($"matches {hits.Count} backups");
                }
                else r.Preview = _backupNames == null ? "(load a package)" : "";
                r.Problem = problems.Count == 0 ? null : string.Join("; ", problems);
            }
            if (_backupNames != null)
            {
                var all = included.Select(r => r.ToMapping()).ToList();
                UnmatchedList.ItemsSource = _backupNames.Where(n => !all.Any(m => SafeMatch(m, n))).ToList();
            }
        }

        private static bool SafeMatch(GpoMapping m, string name)
        {
            try { return m.IsMatch(name); } catch (ArgumentException) { return false; }
        }

        private async void LoadPackage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "DISA GPO package (*.zip)|*.zip", Title = "DISA GPO package (only read, to preview matches)" };
            if (dlg.ShowDialog(this) != true) return;
            await LoadPackageFile(dlg.FileName);
        }

        public async Task LoadPackageFile(string zip)
        {
            SetBusy(true, "Reading package ...");
            try
            {
                var lang = LanguageBox.Text;
                _backupNames = await Task.Run(() =>
                {
                    // Short folder (DISA's folders are deep) and removed afterwards: the wizard only needs the backup names.
                    var folder = Path.Combine(Installer.DataDir, "setup-preview");
                    try { var old = Path.Combine(Path.GetTempPath(), "StigGpoManager-setup"); if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
                    var extracted = PackageScanner.Extract(zip, folder, PackageScanner.ComputeSha256(zip));
                    // Local Policy (LGPO) copies are for standalone machines; they are never imported, so leave them out of the preview.
                    try { return PackageScanner.Scan(extracted, lang).Backups.Where(b => !MappingResolver.IsLocalPolicy(b)).Select(b => b.DisplayName).OrderBy(n => n).ToList(); }
                    finally { try { PackageScanner.DeleteTree(folder); } catch { } }
                });
                PackageText.Text = $"Package {Path.GetFileName(zip)}: {_backupNames.Count} GPO backups.";
                Recompute();
                SetBusy(false, "Package loaded; check the 'Matches in package' column.");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Could not read package: " + ex.Message);
            }
        }

        private void AddMapping_Click(object sender, RoutedEventArgs e)
        {
            var r = MappingRow.From(new GpoMapping { NameTemplate = (DisaPrefixBox.Text ?? "DoD ") + "New STIG {Version}", Match = "" }, true, "Added by hand", OnRowChanged);
            _rows.Add(r);
            MappingsGrid.SelectedItem = r;
            Recompute();
        }

        private void RemoveMapping_Click(object sender, RoutedEventArgs e)
        {
            if (MappingsGrid.SelectedItem is MappingRow r) { _rows.Remove(r); Recompute(); }
        }

        private void AddFromBackup_Click(object sender, RoutedEventArgs e)
        {
            if (!(UnmatchedList.SelectedItem is string name)) return;
            var m = ConfigDetector.SuggestFromBackup(name, DisaPrefixBox.Text);
            var r = MappingRow.From(m, true, "From package backup '" + name + "'; pick its OUs", OnRowChanged);
            _rows.Add(r);
            MappingsGrid.SelectedItem = r;
            Recompute();
        }

        private void PickProd_Click(object sender, RoutedEventArgs e) => Pick(prod: true);
        private void PickPilot_Click(object sender, RoutedEventArgs e) => Pick(prod: false);

        private void Pick(bool prod)
        {
            MappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!(MappingsGrid.SelectedItem is MappingRow r)) { MessageBox.Show(this, "Select a mapping row first.", Title); return; }
            var list = _ous.ToList();
            if (_det?.Domain?.DistinguishedName != null) list.Insert(0, _det.Domain.DistinguishedName);
            var picker = new OuPickerWindow(this, (prod ? "Production OUs for " : "Pilot OUs for ") + r.NameTemplate, list, prod ? r.ProdTargets : r.PilotTargets);
            if (picker.ShowDialog() != true) return;
            if (prod) r.ProdText = string.Join("; ", picker.Selected); else r.PilotText = string.Join("; ", picker.Selected);
            Recompute();
        }

        // ------------------------------------------------------------------ build / validate / save

        private AppConfig BuildConfig()
        {
            int.TryParse(SoakBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var soak);
            return new AppConfig
            {
                Domain = (DomainBox.Text ?? "").Trim(),
                TargetDC = string.IsNullOrWhiteSpace(DcBox.Text) ? "auto-pdc" : DcBox.Text.Trim(),
                ExceptionPrefix = ExceptionPrefixBox.Text,
                DisaGpoPrefix = DisaPrefixBox.Text,
                WorkingFolder = (WorkingBox.Text ?? "").Trim(),
                Language = (LanguageBox.Text ?? "").Trim(),
                PilotGroup = string.IsNullOrWhiteSpace(PilotGroupBox.Text) ? null : PilotGroupBox.Text.Trim(),
                CentralStorePath = StoreOverrideCheck.IsChecked == true ? StoreBox.Text.Trim() : null,
                SoakDays = soak,
                RequireScanGate = GateCheck.IsChecked == true,
                WriteGpoComment = CommentCheck.IsChecked == true,
                MigrationTable = _existing?.MigrationTable ?? "auto",
                Mappings = _rows.Where(r => r.Include).Select(r => r.ToMapping()).ToList(),
            };
        }

        private sealed class IssueLine { public string Text { get; set; } public Brush Brush { get; set; } }

        private List<ConfigIssue> RefreshReview()
        {
            MappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var cfg = BuildConfig();
            var json = ConfigWriter.ToJson(cfg);
            var issues = new List<ConfigIssue>();
            AppConfig.FromJson(json, issues); // validates exactly what will be loaded
            foreach (var r in _rows.Where(r => r.Include && r.Problem != null))
                issues.Add(new ConfigIssue { IsError = r.Problem.Contains("invalid regex"), Message = $"{r.NameTemplate}: {r.Problem}" });
            if (StoreDetected.Text.StartsWith("NOT FOUND"))
                issues.Add(new ConfigIssue { IsError = false, Message = "The Central Store was not found; create it before the first run." });
            JsonPreview.Text = json;
            var lines = issues.Select(i => new IssueLine { Text = i.ToString(), Brush = i.IsError ? Brushes.Firebrick : Brushes.SaddleBrown }).ToList();
            if (lines.Count == 0) lines.Add(new IssueLine { Text = "No problems found. Save to finish; the tool runs its pre-flight checks next.", Brush = Brushes.DarkGreen });
            IssuesList.ItemsSource = lines;
            return issues;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var issues = RefreshReview();
            if (issues.Any(i => i.IsError))
            {
                Pages.SelectedIndex = 3;
                MessageBox.Show(this, "Fix the errors listed on the Review page before saving.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_savePath));
                if (File.Exists(_savePath)) File.Copy(_savePath, _savePath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak");
                File.WriteAllText(_savePath, JsonPreview.Text, new UTF8Encoding(false));
                SavedPath = _savePath;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------ navigation

        private void Back_Click(object sender, RoutedEventArgs e) { if (Pages.SelectedIndex > 0) Pages.SelectedIndex--; }
        private void Next_Click(object sender, RoutedEventArgs e) { if (Pages.SelectedIndex < Pages.Items.Count - 1) Pages.SelectedIndex++; }

        private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != Pages) return;
            if (Pages.SelectedIndex == 3) RefreshReview();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            if (BackButton == null || Pages == null) return;
            BackButton.IsEnabled = Pages.SelectedIndex > 0;
            NextButton.IsEnabled = Pages.SelectedIndex < Pages.Items.Count - 1;
        }

        private void Field_Changed(object sender, TextChangedEventArgs e) { if (StoreDetected != null && !IsDetecting) UpdateStoreText(); }

        private void StoreOverride_Changed(object sender, RoutedEventArgs e)
        {
            StoreBox.IsEnabled = StoreOverrideCheck.IsChecked == true;
            if (!IsDetecting) UpdateStoreText();
        }

        private void BrowseWorking_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Working folder for STIG GPO Manager", SelectedPath = WorkingBox.Text })
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) WorkingBox.Text = dlg.SelectedPath;
        }

        private void SetBusy(bool busy, string text)
        {
            SetupBusy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            SetupBusy.IsIndeterminate = busy;
            SetupStatus.Text = text;
            SaveButton.IsEnabled = !busy;
        }
    }

    /// <summary>One editable mapping row in the setup wizard.</summary>
    public sealed class MappingRow : Row
    {
        private bool _include;
        private string _template, _match, _prod, _pilot, _mode, _preview, _problem;
        private Action _changed;
        public string Reason { get; private set; }

        public static MappingRow From(GpoMapping m, bool include, string reason, Action changed) => new MappingRow
        {
            _include = include, _template = m.NameTemplate, _match = m.Match,
            _prod = string.Join("; ", m.ProdTargets), _pilot = string.Join("; ", m.PilotTargets),
            _mode = m.PilotModeOverride?.ToString() ?? "Auto", Reason = reason, _changed = changed,
        };

        private void Set(ref string field, string value, string name) { field = value; Changed(name); _changed?.Invoke(); }

        public bool Include { get => _include; set { _include = value; Changed(); _changed?.Invoke(); } }
        public string NameTemplate { get => _template; set => Set(ref _template, value, nameof(NameTemplate)); }
        public string Match { get => _match; set => Set(ref _match, value, nameof(Match)); }
        public string ProdText { get => _prod; set => Set(ref _prod, value, nameof(ProdText)); }
        public string PilotText { get => _pilot; set => Set(ref _pilot, value, nameof(PilotText)); }
        public string Mode { get => _mode; set => Set(ref _mode, value, nameof(Mode)); }
        public string Preview { get => _preview; set { _preview = value; Changed(); } }
        public string Problem { get => _problem; set { _problem = value; Changed(); Changed(nameof(Note)); Changed(nameof(HasProblem)); } }
        public bool HasProblem => _include && _problem != null;
        public string Note => _problem != null ? "CHECK: " + _problem : Reason;

        public List<string> ProdTargets => Split(_prod);
        public List<string> PilotTargets => Split(_pilot);

        private static List<string> Split(string s) =>
            (s ?? "").Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        public GpoMapping ToMapping() => new GpoMapping
        {
            NameTemplate = (_template ?? "").Trim(), Match = _match ?? "", ProdTargets = ProdTargets, PilotTargets = PilotTargets,
            PilotModeOverride = _mode == null || _mode == "Auto" ? (PilotMode?)null : (PilotMode)Enum.Parse(typeof(PilotMode), _mode),
        };
    }

    /// <summary>Filterable multi-select list of OUs.</summary>
    public sealed class OuPickerWindow : Window
    {
        private readonly ListBox _list;
        private readonly List<string> _all;
        private readonly HashSet<string> _selected;
        public List<string> Selected => _all.Where(_selected.Contains).ToList();

        public OuPickerWindow(Window owner, string title, List<string> ous, IEnumerable<string> current)
        {
            Owner = owner; Title = title; Width = 760; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            _all = ous;
            _selected = new HashSet<string>(current.Select(c => ous.FirstOrDefault(o => AppConfig.DnEquals(o, c)) ?? c), StringComparer.OrdinalIgnoreCase);
            foreach (var c in _selected.Where(c => !_all.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList()) _all.Add(c);
            var root = new DockPanel { Margin = new Thickness(10) };
            var filter = new TextBox { Margin = new Thickness(0, 0, 0, 6), MinHeight = 24 };
            var top = new DockPanel();
            top.Children.Add(new TextBlock { Text = "Filter: ", VerticalAlignment = VerticalAlignment.Center });
            top.Children.Add(filter);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            ok.Click += (s, e) => DialogResult = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button { Content = "Cancel", Width = 80, IsCancel = true });
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            _list = new ListBox { FontFamily = new FontFamily("Consolas") };
            root.Children.Add(_list);
            Content = root;
            filter.TextChanged += (s, e) => Populate(filter.Text);
            Populate("");
        }

        private void Populate(string filter)
        {
            _list.Items.Clear();
            foreach (var ou in _all.Where(o => string.IsNullOrEmpty(filter) || o.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var cb = new CheckBox { Content = ou, IsChecked = _selected.Contains(ou), Margin = new Thickness(2) };
                cb.Checked += (s, e) => _selected.Add(ou);
                cb.Unchecked += (s, e) => _selected.Remove(ou);
                _list.Items.Add(cb);
            }
        }
    }
}
