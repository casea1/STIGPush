using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Threading;
using StigGpoManager.Core;

namespace StigGpoManager
{
    /// <summary>
    /// System tray icon: launcher plus status of the active run (e.g. "Pilot - day 9 of 14"),
    /// read from the run state files every few minutes. Reads only; never changes AD.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _status;
        private readonly string _configPath;
        private readonly DispatcherTimer _timer;

        public TrayIcon(string configPath, Action open, Action exit)
        {
            _configPath = configPath;
            _status = new ToolStripMenuItem("Status: loading...") { Enabled = false };
            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("Open STIG GPO Manager", null, (s, e) => open());
            openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(openItem);
            menu.Items.Add(_status);
            menu.Items.Add(new ToolStripMenuItem("Refresh status", null, (s, e) => Refresh()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => exit()));

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "STIG GPO Manager",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.DoubleClick += (s, e) => open();
            _icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) open(); };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Refresh();
        }

        private static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location) ?? SystemIcons.Shield; }
            catch { return SystemIcons.Shield; }
        }

        public void Refresh()
        {
            string status, shortStatus;
            try
            {
                var cfg = AppConfig.Load(_configPath, out List<ConfigIssue> issues);
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.WorkingFolder)) status = shortStatus = "config not loaded";
                else
                {
                    var run = new RunStore(cfg.RunsFolder).ActiveRun();
                    shortStatus = run == null ? "no active run" : run.StatusText(cfg.SoakDays, DateTime.UtcNow);
                    status = run == null ? shortStatus : $"run {run.RunId}: {shortStatus}";
                }
            }
            catch (Exception ex) { status = shortStatus = "error: " + ex.Message; }
            _status.Text = "Status: " + status;
            var tip = "STIG GPO: " + shortStatus;
            _icon.Text = tip.Length > 63 ? tip.Substring(0, 60) + "..." : tip; // NotifyIcon limit
        }

        public void Dispose()
        {
            _timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
