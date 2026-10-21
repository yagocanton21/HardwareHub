using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HardwareHub.Models
{
    public class DiskHealthInfo : INotifyPropertyChanged
    {
        private string _healthStatus = "Saudável";
        private int _healthPercentage = 100;
        private double? _temperature;
        private double? _readSpeedMbps;
        private double? _writeSpeedMbps;
        private bool _isTestingSpeed;
        private string _speedStatusText = "";

        private string _interface = "SATA";
        private string _mediaType = "SSD";
        private long? _powerOnCount;

        public int Index { get; set; }
        public string Model { get; set; } = "";
        public string Serial { get; set; } = "";
        
        public string Interface
        {
            get => _interface;
            set { if (_interface != value) { _interface = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayInterface)); } }
        }

        public string DisplayInterface => (_interface.Equals("IDE", StringComparison.OrdinalIgnoreCase) || _interface.Contains("ATA", StringComparison.OrdinalIgnoreCase)) ? "SATA" : _interface;

        public string MediaType
        {
            get => _mediaType;
            set { if (_mediaType != value) { _mediaType = value; OnPropertyChanged(); OnPropertyChanged(nameof(HealthBadgeText)); } }
        }

        public double CapacityGb { get; set; }
        public string DriveLetters { get; set; } = "";
        public long? PowerOnHours { get; set; }
        public double? TotalWrittenTb { get; set; }
        public double? TotalReadTb { get; set; }
        public bool HasSmartErrors { get; set; }

        public long? PowerOnCount
        {
            get => _powerOnCount;
            set { if (_powerOnCount != value) { _powerOnCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedPowerOnCount)); } }
        }

        public string FormattedPowerOnCount => PowerOnCount.HasValue ? $"{PowerOnCount.Value:N0} vezes" : "--";

        public string HealthStatus
        {
            get => _healthStatus;
            set { if (_healthStatus != value) { _healthStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HealthColor)); OnPropertyChanged(nameof(HealthBadgeText)); } }
        }

        public int HealthPercentage
        {
            get => _healthPercentage;
            set { if (_healthPercentage != value) { _healthPercentage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HealthBadgeText)); } }
        }

        public string HealthBadgeText => MediaType == "SSD" ? $"{HealthStatus} {HealthPercentage}%" : HealthStatus;

        public double? Temperature
        {
            get => _temperature;
            set { if (_temperature != value) { _temperature = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedTemperature)); } }
        }

        public string FormattedTemperature => Temperature.HasValue && Temperature.Value > 0 ? $"{Temperature.Value:F0} °C" : "-- °C";

        public double? ReadSpeedMbps
        {
            get => _readSpeedMbps;
            set { if (_readSpeedMbps != value) { _readSpeedMbps = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedReadSpeed)); } }
        }

        public double? WriteSpeedMbps
        {
            get => _writeSpeedMbps;
            set { if (_writeSpeedMbps != value) { _writeSpeedMbps = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedWriteSpeed)); } }
        }

        public string FormattedReadSpeed => ReadSpeedMbps.HasValue ? $"{ReadSpeedMbps.Value:F1} MB/s" : "--";
        public string FormattedWriteSpeed => WriteSpeedMbps.HasValue ? $"{WriteSpeedMbps.Value:F1} MB/s" : "--";

        public bool IsTestingSpeed
        {
            get => _isTestingSpeed;
            set { if (_isTestingSpeed != value) { _isTestingSpeed = value; OnPropertyChanged(); } }
        }

        public string SpeedStatusText
        {
            get => _speedStatusText;
            set { if (_speedStatusText != value) { _speedStatusText = value; OnPropertyChanged(); } }
        }

        public string HealthColor => HealthStatus switch
        {
            "Saudável" => "#10B981",
            "Atenção" => "#F59E0B",
            "Crítico" => "#EF4444",
            _ => "#6B7280"
        };

        public string FormattedCapacity => CapacityGb >= 1000 ? $"{CapacityGb / 1000.0:F1} TB" : $"{CapacityGb:F0} GB";
        public string FormattedPowerOnHours => PowerOnHours.HasValue ? $"{PowerOnHours.Value:N0} horas" : "Não informado";
        public string FormattedWritten => TotalWrittenTb.HasValue ? $"{TotalWrittenTb.Value:F1} TB" : "--";
        public string FormattedRead => TotalReadTb.HasValue ? $"{TotalReadTb.Value:F1} TB" : "--";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
