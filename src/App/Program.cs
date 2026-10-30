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
    ///   /postinstall, /register-tray, /preuninstall   hooks used by the Inno Setup installer
    ///   /config &lt;path&gt;   use a specific config.json
    ///   /setup           open the setup wizard (detects domain settings, writes config.json)
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
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll")] private static extern int GetFileType(IntPtr hFile);
        private const int StdOutputHandle = -11;

        /// <summary>
        /// The exe is a Windows (GUI) program, so "cli" has to find somewhere to write:
        ///  - stdout already redirected (WinRM / Invoke-Command, "| Out-String", "> file", CI): use it as is.
        ///    Calling AllocConsole here would replace the pipe with an invisible console and every line
        ///    would be lost (the WinRM "no output" report).
        ///  - started from a console window (cmd, PowerShell): attach to that console.
        ///  - neither, interactive desktop: open a console window. Never in a non-interactive session.
        /// </summary>
        private static void SetUpConsole()
        {
            var h = GetStdHandle(StdOutputHandle);
            if (h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) != 0) return;
            if (AttachConsole(-1)) return;
            if (Environment.UserInteractive) AllocConsole();
        }

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
                    SetUpConsole();
                    try { return Cli.Run(args.Skip(1).ToArray(), configPath); }
                    catch (Exception ex)
                    {
                        // Never a message box from the command line: in a WinRM / scheduled-task session nobody
                        // can see it, and the process would wait for a click forever.
                        Console.Error.WriteLine("ERROR: " + ex.Message);
                        return 1;
                    }
                    finally { Console.Out.Flush(); Console.Error.Flush(); }
                }
                if (Has("/postinstall")) { Installer.PostInstall(); return 0; }
                if (Has("/register-tray")) { Installer.RegisterTray(); return 0; }
                if (Has("/preuninstall")) { Installer.PreUninstall(); return 0; }
            }
            catch (Exception ex)
            {
                if (!quiet && Environment.UserInteractive) MessageBox.Show(ex.Message, "STIG GPO Manager", MessageBoxButton.OK, MessageBoxImage.Error);
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

                var app = new App(trayMode, ResolveConfigPath(configPath), demo, screenshots == null ? null : Path.GetFullPath(screenshots)) { OpenSetupOnStart = Has("/setup") };
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
        public bool OpenSetupOnStart { get; set; }
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
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/StigGpoManager;component/Theme.xaml", UriKind.Absolute) });
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
            if (OpenSetupOnStart && _window != null) _window.Dispatcher.BeginInvoke(new Action(() => _window.OpenSetup()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
