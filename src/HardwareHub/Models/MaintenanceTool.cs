using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HardwareHub.Models
{
    public class MaintenanceTool : INotifyPropertyChanged
    {
        private bool _isRunning = false;
        private string _statusText = "Pronto";
        private string _lastOutput = "";
        private int _progressPercent = 0;

        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "🛠️";
        public string ActionButtonText { get; set; } = "Executar";
        public string AccentColor { get; set; } = "#6366F1";

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanExecute));
                }
            }
        }

        public bool CanExecute => !IsRunning;

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public string LastOutput
        {
            get => _lastOutput;
            set { if (_lastOutput != value) { _lastOutput = value; OnPropertyChanged(); } }
        }

        public int ProgressPercent
        {
            get => _progressPercent;
            set { if (_progressPercent != value) { _progressPercent = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
