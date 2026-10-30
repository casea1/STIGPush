using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StigGpoManager.Core;

namespace StigGpoManager
{
    public sealed class ConfirmResult
    {
        public bool Confirmed { get; set; }
        public string TypedWord { get; set; }
        public bool DcBackupConfirmed { get; set; }
    }

    /// <summary>
    /// Shows exactly what a Push will change. Execution is only possible when there are no blockers,
    /// every acknowledgement is ticked and (for Promote / Delete) the confirmation word is typed.
    /// </summary>
    public sealed class ConfirmWindow : Window
    {
        private readonly OperationPlan _plan;
        private readonly List<CheckBox> _acks = new List<CheckBox>();
        private readonly TextBox _word;
        private readonly Button _ok;
        public ConfirmResult Result { get; } = new ConfirmResult();

        public ConfirmWindow(Window owner, OperationPlan plan)
        {
            _plan = plan;
            Owner = owner;
            Title = "Confirm: " + plan.Title;
            Width = 900; Height = 680; MinWidth = 500; MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;

            var root = new DockPanel { Margin = new Thickness(16) };
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = plan.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            header.Children.Add(new TextBlock { Text = "Nothing has changed yet. Review exactly what will happen below, then confirm.", Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var b in plan.Blockers)
                header.Children.Add(Banner("BLOCKED: " + b, Brushes.DarkRed, Color.FromRgb(0xFF, 0xD6, 0xD6)));
            foreach (var w in plan.Warnings)
                header.Children.Add(Banner("WARNING: " + w, Brushes.SaddleBrown, Color.FromRgb(0xFF, 0xF3, 0xCD)));
            header.Children.Add(new TextBlock { Text = $"{plan.Changes.Count} change(s) will be made:", Margin = new Thickness(0, 6, 0, 2) });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var a in plan.RequiredAcknowledgements)
            {
                var cb = new CheckBox { Content = new TextBlock { Text = a, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold, Foreground = Brushes.DarkRed }, Margin = new Thickness(0, 2, 0, 2) };
                cb.Checked += (s, e) => Update();
                cb.Unchecked += (s, e) => Update();
                _acks.Add(cb);
                footer.Children.Add(cb);
            }
            if (plan.ConfirmationWord != null)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                row.Children.Add(new TextBlock { Text = $"Type {plan.ConfirmationWord} to confirm: ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });
                _word = new TextBox { Width = 160, FontFamily = new FontFamily("Consolas") };
                _word.TextChanged += (s, e) => Update();
                row.Children.Add(_word);
                footer.Children.Add(row);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            _ok = new Button { Content = "Push changes", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0), IsDefault = false };
            _ok.SetResourceReference(StyleProperty, "PrimaryButton");
            _ok.Click += (s, e) => { Result.Confirmed = true; Result.TypedWord = _word?.Text; Result.DcBackupConfirmed = _acks.Count > 0 && _acks.All(c => c.IsChecked == true); DialogResult = true; };
            var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(0) };
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);
            footer.Children.Add(buttons);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var text = new TextBox
            {
                Text = PlanText(plan), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
            };
            root.Children.Add(text);
            Content = root;
            Update();
        }

        private static string PlanText(OperationPlan plan)
        {
            var lines = new List<string>();
            foreach (var g in plan.Changes.GroupBy(c => c.Target))
            {
                lines.Add(g.Key);
                foreach (var c in g) lines.Add("   [" + c.Kind + "] " + c.Description.Replace("\n", "\n        "));
                lines.Add("");
            }
            return lines.Count == 0 ? "(no changes)" : string.Join(Environment.NewLine, lines);
        }

        private static Border Banner(string text, Brush fg, Color bg) => new Border
        {
            Background = new SolidColorBrush(bg), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 2, 0, 2), CornerRadius = new CornerRadius(6),
            Child = new TextBlock { Text = text, Foreground = fg, TextWrapping = TextWrapping.Wrap },
        };

        private void Update()
        {
            bool ok = _plan.CanExecute && _acks.All(c => c.IsChecked == true);
            if (_plan.ConfirmationWord != null) ok &= string.Equals(_word.Text, _plan.ConfirmationWord, StringComparison.Ordinal);
            _ok.IsEnabled = ok;
        }

        public static ConfirmResult Ask(Window owner, OperationPlan plan)
        {
            var w = new ConfirmWindow(owner, plan);
            w.ShowDialog();
            return w.Result;
        }
    }

    /// <summary>Small multi-field input dialog (e.g. justification when adding to the exceptions register).</summary>
    public sealed class InputWindow : Window
    {
        private readonly List<TextBox> _boxes = new List<TextBox>();
        public string[] Values => _boxes.Select(b => b.Text).ToArray();

        public InputWindow(Window owner, string title, string intro, params string[] labels)
        {
            Owner = owner; Title = title; Width = 560; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            var panel = new StackPanel { Margin = new Thickness(12) };
            if (!string.IsNullOrEmpty(intro)) panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var l in labels)
            {
                panel.Children.Add(new TextBlock { Text = l });
                var tb = new TextBox { Margin = new Thickness(0, 2, 0, 8), AcceptsReturn = l.IndexOf("Justification", StringComparison.OrdinalIgnoreCase) >= 0, MinHeight = 24 };
                if (tb.AcceptsReturn) { tb.Height = 70; tb.TextWrapping = TextWrapping.Wrap; }
                _boxes.Add(tb);
                panel.Children.Add(tb);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            ok.Click += (s, e) => DialogResult = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button { Content = "Cancel", Width = 80, IsCancel = true });
            panel.Children.Add(buttons);
            Content = panel;
        }
    }
}
