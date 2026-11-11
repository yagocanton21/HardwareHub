using System.Text.Json.Serialization;

namespace HardwareHub.Models
{
    // ===================== 1. DRIVERS & WINDOWS UPDATE =====================

    public sealed class DriverItemModel
    {
        public string Device { get; init; } = "";
        public string ClassCode { get; init; } = "";
        public string ClassLabel { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string Version { get; init; } = "";
        public string FriendlyVersion { get; init; } = "";
        public string Date { get; init; } = "—";
        public bool IsSigned { get; init; }
        public bool IsGeneric { get; init; }

        public string DisplayVersion => string.IsNullOrWhiteSpace(FriendlyVersion) ? Version : $"{FriendlyVersion} ({Version})";
        public string SignatureLabel => IsSigned ? "Assinado" : "Não assinado";
        public string SignatureColor => IsSigned ? "#059669" : "#D97706";
    }

    public sealed class DriverProblemItem
    {
        public string Device { get; init; } = "";
        public string ClassLabel { get; init; } = "";
        public int ErrorCode { get; init; }
        public string Title { get; init; } = "";
        public string Tip { get; init; } = "";
        public string HardwareId { get; init; } = "";
        public bool IsSerious { get; init; }
        public int Count { get; init; } = 1;

        public string SeverityLabel => IsSerious ? $"Erro {ErrorCode}" : $"Aviso {ErrorCode}";
        public string SeverityColor => IsSerious ? "#DC2626" : "#D97706";
    }

    public sealed class DriverBoardInfo
    {
        public string Maker { get; set; } = "";
        public string Model { get; set; } = "";
        public string Bios { get; set; } = "";
        public string Brand { get; set; } = "";
        public string SupportUrl { get; set; } = "";

        public string DisplayTitle => string.Join(" ", new[] { Maker, Model }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
    }

    public sealed class DriverGpuInfo
    {
        public string Name { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string DriverVersion { get; init; } = "";
        public string FriendlyVersion { get; init; } = "";
        public string DriverDate { get; init; } = "—";
        public string SupportUrl { get; init; } = "";

        public string DisplayDriverVersion => string.IsNullOrWhiteSpace(FriendlyVersion)
            ? DriverVersion
            : $"{FriendlyVersion} ({DriverVersion})";
    }

    public sealed class WindowsUpdateItemModel : ObservableModel
    {
        private bool _isSelected = true;

        public string Title { get; init; } = "";
        public string KbId { get; init; } = "";
        public long SizeBytes { get; init; }
        public bool IsDriver { get; init; }
        public bool IsOptional { get; init; }
        public bool RequiresUserInput { get; init; }

        [JsonIgnore]
        public object? NativeComUpdate { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public string FormattedSize => SizeBytes > 0 ? InstalledProgramItem.FormatBytes(SizeBytes) : "—";
        public string CategoryLabel => IsDriver ? "Driver" : (IsOptional ? "Opcional" : "Segurança / Sistema");
        public string CategoryColor => IsDriver ? "#4F46E5" : (IsOptional ? "#71717A" : "#059669");
    }

    public sealed record WindowsUpdateRunResult(bool Success, string Message, bool RebootRequired);

    // ===================== 2. BACKUP ANTES DE FORMATAR =====================

    public sealed class FormatBackupManifest
    {
        public int FormatVersion { get; set; } = 1;
        public string HubVersion { get; set; } = "1.0.0";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string WindowsVersion { get; set; } = "";
        public string MachineName { get; set; } = "";
        public string Motherboard { get; set; } = "";
        public string DriversFolder { get; set; } = "Drivers";
        public int DriversCount { get; set; }
        public string WifiFolder { get; set; } = "WiFi";
        public int WifiCount { get; set; }
        public List<string> SsidList { get; set; } = new();
    }

    public sealed class FoundFormatBackupModel
    {
        public string FolderPath { get; init; } = "";
        public FormatBackupManifest Manifest { get; init; } = new();
        public string DriversPath { get; init; } = "";
        public int InfCount { get; init; }
        public List<(string XmlPath, string Ssid)> WifiProfiles { get; init; } = new();
        public bool IsDifferentHardware { get; init; }

        public string Title => string.IsNullOrWhiteSpace(Manifest.MachineName)
            ? System.IO.Path.GetFileName(FolderPath)
            : $"{Manifest.MachineName} • {Manifest.CreatedAt:dd/MM/yyyy HH:mm}";

        public string HardwareSummary => string.IsNullOrWhiteSpace(Manifest.Motherboard)
            ? Manifest.WindowsVersion
            : $"{Manifest.Motherboard} • {Manifest.WindowsVersion}";

        public string ContentsSummary =>
            $"{InfCount} arquivo(s) .inf de driver • {WifiProfiles.Count} rede(s) Wi-Fi";

        public string HardwareWarning => IsDifferentHardware
            ? "Placa-mãe diferente da máquina original (drivers só devem ser restaurados no mesmo modelo)."
            : "";
    }

    public sealed record RestoreDriversResult(bool Success, int Total, int NewlyInstalled, int AlreadyPresent, int Failed, bool RebootRequired, string Message);
    public sealed record RestoreWifiResult(bool Success, int Total, int Restored, int Failed, string Message);

    // ===================== 3. DETECTOR DE CAPACIDADE FALSA =====================

    public enum CapacityPointState
    {
        Untested,
        Written,
        Ok,
        Wrapped,
        Lost,
        Error
    }

    public enum FakeCapacityVerdict
    {
        Original,
        Counterfeit,
        Defective,
        Inconclusive
    }

    public sealed class RemovableDriveCandidate
    {
        public int DiskNumber { get; init; }
        public string Model { get; init; } = "";
        public string Serial { get; init; } = "";
        public int BusType { get; init; }
        public string BusLabel { get; init; } = "USB";
        public long AnnouncedBytes { get; init; }
        public string[] MountLetters { get; init; } = Array.Empty<string>();
        public string? BlockReason { get; init; }

        public bool CanTest => string.IsNullOrWhiteSpace(BlockReason) && AnnouncedBytes > 0;
        public string FormattedSize => AnnouncedBytes > 0 ? InstalledProgramItem.FormatBytes(AnnouncedBytes) : "—";
        public string LettersText => MountLetters.Length > 0 ? string.Join(", ", MountLetters) : "Sem letra montada";
        public string DisplayTitle => $"Disco {DiskNumber}: {Model} ({FormattedSize} • {BusLabel} • {LettersText})";
    }

    public sealed class FakeCapacityOptions
    {
        public int Points { get; init; } = 64;
        public int BlockSizeBytes { get; init; } = 4096;
        public bool IncludeSafeFileVerify { get; init; } = true;
    }

    public sealed record FakeCapacityProgress(string Stage, double? Ratio, int CompletedPoints = 0, int TotalPoints = 0);

    public sealed class FakeCapacityReport
    {
        public RemovableDriveCandidate Drive { get; init; } = new();
        public FakeCapacityVerdict Verdict { get; set; } = FakeCapacityVerdict.Inconclusive;
        public long AnnouncedBytes { get; set; }
        public long RealBytes { get; set; }
        public bool WrapAroundDetected { get; set; }
        public int PointsOk { get; set; }
        public int PointsBad { get; set; }
        public int IrregularPoints { get; set; }
        public double WriteSpeedMBps { get; set; }
        public double ReadSpeedMBps { get; set; }
        public TimeSpan Duration { get; set; }
        public bool OriginalDataRestored { get; set; } = true;
        public string? ErrorMessage { get; set; }
        public CapacityPointState[] PointStates { get; set; } = Array.Empty<CapacityPointState>();

        public string VerdictLabel => Verdict switch
        {
            FakeCapacityVerdict.Original => "AUTÊNTICO — Capacidade real confirmada",
            FakeCapacityVerdict.Counterfeit => "FALSIFICADO — Capacidade real inferior à anunciada",
            FakeCapacityVerdict.Defective => "DEFEITUOSO — Falhas irregulares de gravação/leitura",
            _ => "INCONCLUSIVO — Teste interrompido ou bloqueado"
        };

        public string VerdictColor => Verdict switch
        {
            FakeCapacityVerdict.Original => "#059669",
            FakeCapacityVerdict.Counterfeit => "#DC2626",
            FakeCapacityVerdict.Defective => "#D97706",
            _ => "#71717A"
        };
    }
}

