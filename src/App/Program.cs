using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace StigGpoManager
{
    /// <summary>
    /// StigGpoManager.exe
    ///   (no args)        open the main window (and tray icon)
    ///   /tray            start minimized to the tray (used by the logon scheduled task)
    ///   /install [/quiet]   install to Program Files, Programs and Features, Start menu, tray at logon
    ///   /uninstall [/quiet]
    ///   /config &lt;path&gt;   use a specific config.json
    ///   /demo            simulated domain with sample data (no AD access) - training / evaluation
    ///   /screenshots &lt;dir&gt;  /demo, save a PNG of every screen to dir, then exit (used by CI for the README)
    ///   cli &lt;command&gt; ...  scriptable command line (see README), run from an elevated console
    /// </summary>
    public static class Program
    {
        public const string ShowEventName = @"Local\StigGpoManager.ShowWindow";
        private const string MutexName = @"Local\StigGpoManager.Gui";

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();

        [STAThread]
        public static int Main(string[] args)
        {
            bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-" + flag.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
            bool quiet = Has("/quiet");
            string configPath = ArgValue(args, "/config");

            try
            {
                if (args.Length > 0 && string.Equals(args[0], "cli", StringComparison.OrdinalIgnoreCase))
                {
                    if (!AttachConsole(-1)) AllocConsole();
                    Console.WriteLine();
                    return Cli.Run(args.Skip(1).ToArray(), configPath);
                }
                if (Has("/install"))
                {
                    var msg = Installer.Install();
                    if (!quiet) MessageBox.Show(msg, "STIG GPO Manager - installed", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
                if (Has("/uninstall"))
                {
                    if (!quiet && MessageBox.Show("Uninstall STIG GPO Manager?\n\nThe configuration, run state, backups, reports and audit log are kept.",
                                                  "STIG GPO Manager", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        return 1;
                    var msg = Installer.Uninstall();
                    if (!quiet) MessageBox.Show(msg, "STIG GPO Manager - uninstalled", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                if (!quiet) MessageBox.Show(ex.Message, "STIG GPO Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            bool trayMode = Has("/tray");
            string screenshots = ArgValue(args, "/screenshots");
            bool demo = Has("/demo") || screenshots != null;
            if (demo) trayMode = false;
            using (var mutex = new Mutex(true, demo ? MutexName + ".Demo" : MutexName, out bool first))
            {
                if (!first && !demo)
                {
                    // Already running (e.g. tray at logon): ask that instance to show its window.
                    if (!trayMode)
                        try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                    return 0;
                }

                if (!trayMode && !demo && !Installer.IsInstalled && !quiet)
                {
                    var answer = MessageBox.Show(
                        "STIG GPO Manager is not installed on this computer.\n\nInstall it now? This adds it to Programs and Features and the Start menu, and shows the tray icon at logon.\n\nChoose No to run it once without installing.",
                        "STIG GPO Manager", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Cancel) return 0;
                    if (answer == MessageBoxResult.Yes)
                    {
                        try
                        {
                            MessageBox.Show(Installer.Install(startTray: false), "STIG GPO Manager - installed", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Install failed: " + ex.Message, "STIG GPO Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }

                var app = new App(trayMode, ResolveConfigPath(configPath), demo, screenshots == null ? null : Path.GetFullPath(screenshots));
                return app.Run();
            }
        }

        public static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) || string.Equals(args[i], "-" + name.TrimStart('/', '-'), StringComparison.OrdinalIgnoreCase) || string.Equals(args[i], "--" + name.TrimStart('/', '-'), StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        /// <summary>Explicit /config, else %ProgramData%\StigGpoManager\config.json, else config.json next to the exe.</summary>
        public static string ResolveConfigPath(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
            if (File.Exists(Installer.DefaultConfigPath)) return Installer.DefaultConfigPath;
            var local = Path.Combine(Path.GetDirectoryName(Installer.CurrentExe), "config.json");
            return File.Exists(local) ? local : Installer.DefaultConfigPath;
        }
    }

    /// <summary>WPF application with a tray icon. In tray mode the window starts hidden; closing it hides to the tray.</summary>
    public sealed class App : Application
    {
        private readonly bool _trayMode;
        private readonly bool _demo;
        private readonly string _screenshotDir;
        private string _configPath;
        private Session _demoSession;
        private int _exitCode;
        private TrayIcon _tray;
        private MainWindow _window;
        private EventWaitHandle _showEvent;
        private RegisteredWaitHandle _wait;

        public App(bool trayMode, string configPath, bool demo = false, string screenshotDir = null)
        {
            _trayMode = trayMode;
            _configPath = configPath;
            _demo = demo;
            _screenshotDir = screenshotDir;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, ex) =>
            {
                if (_screenshotDir != null) { Console.Error.WriteLine(ex.Exception); Shutdown(1); ex.Handled = true; return; }
                MessageBox.Show(ex.Exception.ToString(), "STIG GPO Manager - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };
            if (_demo)
            {
                try
                {
                    _demoSession = DemoData.Create();
                    _configPath = _demoSession.ConfigPath;
                }
                catch (Exception ex)
                {
                    if (_screenshotDir == null) MessageBox.Show(ex.ToString(), "STIG GPO Manager - demo", MessageBoxButton.OK, MessageBoxImage.Error);
                    else File.WriteAllText(Path.Combine(Directory.CreateDirectory(_screenshotDir).FullName, "error.txt"), ex.ToString());
                    Shutdown(1);
                    return;
                }
            }
            if (_screenshotDir == null) _tray = new TrayIcon(_configPath, ShowMain, ExitApp);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            _wait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (o, t) => Dispatcher.BeginInvoke(new Action(ShowMain)), null, -1, false);
            if (!_trayMode) ShowMain();
        }

        public void ShowMain()
        {
            if (_window == null)
            {
                _window = new MainWindow(_demoSession ?? new Session(_configPath));
                if (_screenshotDir != null)
                    _window.Loaded += async (s, e) => { _exitCode = await _window.RunScreenshotTour(_screenshotDir); ExitApp(); };
                _window.Closing += (s, e) =>
                {
                    if (_trayMode && !_window.ExitRequested)
                    {
                        e.Cancel = true;
                        _window.Hide();
                        _tray?.Refresh();
                    }
                };
                _window.Closed += (s, e) => { _window = null; if (!_trayMode) ExitApp(); };
                _window.RunChanged += () => _tray?.Refresh();
            }
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        private void ExitApp()
        {
            if (_window != null && _window.IsBusy &&
                MessageBox.Show("An operation is still running. Exit anyway?", "STIG GPO Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            if (_window != null) { _window.ExitRequested = true; _window.Close(); }
            _wait?.Unregister(null);
            _showEvent?.Dispose();
            _tray?.Dispose();
            Shutdown(_exitCode);
        }
    }
}
