using System.Windows;
using System.Windows.Media;

namespace StigGpoManager
{
    public enum StepState { Pending, Next, Done, Attention, Optional, Tool }

    /// <summary>One entry in the left step list, with a status badge (done / next / attention).</summary>
    public sealed class StepItem : Row
    {
        public string Panel { get; set; }
        public string Title { get; set; }
        public string Number { get; set; }
        public bool IsMain { get; set; }
        public bool IsSeparator { get; set; }
        public string What { get; set; }
        public string Do { get; set; }
        public string Changes { get; set; }

        private StepState _state = StepState.Pending;
        private string _caption = "";
        private bool _selectedLook;

        public StepState State { get => _state; set { _state = value; ChangedAll(); } }
        public string Caption { get => _caption; set { _caption = value; Changed(); } }
        public bool IsCurrentNext { get => _selectedLook; set { _selectedLook = value; ChangedAll(); } }

        public Visibility SeparatorVisibility => IsSeparator ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ItemVisibility => IsSeparator ? Visibility.Collapsed : Visibility.Visible;
        public FontWeight TitleWeight => _state == StepState.Next ? FontWeights.SemiBold : FontWeights.Normal;

        public string Badge
        {
            get
            {
                switch (_state)
                {
                    case StepState.Done: return "✓";
                    case StepState.Attention: return "!";
                    case StepState.Tool: return "•";
                    default: return Number ?? "•";
                }
            }
        }

        private static SolidColorBrush B(uint argb) => new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

        public Brush BadgeBackground =>
            _state == StepState.Done ? B(0xFF16A34A) : _state == StepState.Next ? B(0xFF2563EB) : _state == StepState.Attention ? B(0xFFD97706) : Brushes.White;
        public Brush BadgeBorder =>
            _state == StepState.Done ? B(0xFF16A34A) : _state == StepState.Next ? B(0xFF2563EB) : _state == StepState.Attention ? B(0xFFD97706) : B(0xFFCBD2DB);
        public Brush BadgeForeground =>
            _state == StepState.Done || _state == StepState.Next || _state == StepState.Attention ? Brushes.White : B(0xFF6B7280);
        public Brush CaptionBrush =>
            _state == StepState.Attention ? B(0xFFB45309) : _state == StepState.Next ? B(0xFF2563EB) : _state == StepState.Done ? B(0xFF15803D) : B(0xFF6B7280);
    }
}
