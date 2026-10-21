using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HardwareHub.Models
{
    public enum PackageStatus
    {
        Available,
        Checking,
        Installed,
        Installing,
        Failed
    }

    public class SoftwareItem : INotifyPropertyChanged
    {
        private PackageStatus _status = PackageStatus.Available;
        private string _statusMessage = "Disponível";
        private int _progressValue = 0;
        private bool _isIndeterminate = false;

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "Utilitários";
        public string WingetId { get; set; } = "";
        public string IconKind { get; set; } = "Apps24";
        public string DefaultInstallArgs { get; set; } = "--silent --accept-package-agreements --accept-source-agreements";

        public string AppEmoji => Id.ToLowerInvariant() switch
        {
            "chrome" => "🌐",
            "firefox" => "🦊",
            "brave" => "🦁",
            "msoffice" => "📑",
            "libreoffice" => "📄",
            "vscode" => "💻",
            "git" => "🌿",
            "nodejs" => "🟢",
            "python" => "🐍",
            "discord" => "💬",
            "steam" => "🎮",
            "spotify" => "🎵",
            "vlc" => "🎬",
            "7zip" => "📦",
            "anydesk" => "🖥️",
            "cpuz" => "⚡",
            _ => "🚀"
        };

        public PackageStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanInstall));
                    OnPropertyChanged(nameof(IsInstalled));
                    OnPropertyChanged(nameof(IsInstalling));
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        public int ProgressValue
        {
            get => _progressValue;
            set
            {
                if (_progressValue != value)
                {
                    _progressValue = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set
            {
                if (_isIndeterminate != value)
                {
                    _isIndeterminate = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool CanInstall => Status == PackageStatus.Available || Status == PackageStatus.Failed;
        public bool IsInstalled => Status == PackageStatus.Installed;
        public bool IsInstalling => Status == PackageStatus.Installing;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
