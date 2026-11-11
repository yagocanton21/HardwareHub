using System;
using System.Collections.Generic;

namespace HardwareHub.Models
{
    public enum WindowsHealthStatus
    {
        Healthy,
        Attention,
        Critical,
        Unknown
    }

    public sealed record PcInfoLine(string Name, string Value);

    public sealed class RamModuleDetailModel
    {
        public string BankLabel { get; set; } = "—";
        public string Slot { get; set; } = "—";
        public string Manufacturer { get; set; } = "—";
        public string PartNumber { get; set; } = "—";
        public ulong CapacityBytes { get; set; }
        public string Type { get; set; } = "—";
        public string FormFactor { get; set; } = "—";
        public int NominalSpeedMt { get; set; }
        public int ConfiguredSpeedMt { get; set; }
        public int DataWidth { get; set; }
        public int TotalWidth { get; set; }

        public string FormattedCapacity => FormatBytes((long)CapacityBytes);

        public string FormattedSpeed => ConfiguredSpeedMt > 0 && NominalSpeedMt > 0 && ConfiguredSpeedMt != NominalSpeedMt
            ? $"{ConfiguredSpeedMt} MT/s (pente {NominalSpeedMt} MT/s)"
            : ConfiguredSpeedMt > 0
                ? $"{ConfiguredSpeedMt} MT/s"
                : NominalSpeedMt > 0 ? $"{NominalSpeedMt} MT/s" : "—";

        internal static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "—";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double val = bytes;
            int idx = 0;
            while (val >= 1024 && idx < units.Length - 1)
            {
                val /= 1024.0;
                idx++;
            }
            return idx >= 3 ? $"{val:0.#} {units[idx]}" : $"{val:0} {units[idx]}";
        }
    }

    public sealed class GpuInventoryDetailModel
    {
        public string Name { get; set; } = "—";
        public string PnpDeviceId { get; set; } = string.Empty;
        public ulong VramBytes { get; set; }
        public string FormattedVram => RamModuleDetailModel.FormatBytes((long)VramBytes);
        public string DriverVersion { get; set; } = "—";
        public string DriverDate { get; set; } = "—";
        public string Resolution { get; set; } = "—";
        public List<PcInfoLine> Lines { get; set; } = new();
    }

    public sealed class VolumeInventoryModel
    {
        public string DriveLetter { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Kind { get; set; } = "Interno";
        public string FileSystem { get; set; } = string.Empty;
        public string DiskModel { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public bool IsRemovable { get; set; }

        public double UsagePercent => TotalBytes > 0 ? (double)(TotalBytes - FreeBytes) * 100.0 / TotalBytes : 0;
        public bool IsNearlyFull => TotalBytes > 0 && UsagePercent >= 90.0;
        public string Title => string.IsNullOrWhiteSpace(Label) ? DriveLetter : $"{DriveLetter} ({Label})";
        public string FormattedTotal => RamModuleDetailModel.FormatBytes(TotalBytes);
        public string FormattedFree => RamModuleDetailModel.FormatBytes(FreeBytes);
    }

    public sealed class SystemInventoryReport
    {
        public DateTime CollectedAt { get; set; } = DateTime.Now;
        public string MachineName { get; set; } = Environment.MachineName;
        public string SystemSummary { get; set; } = "—";
        public string MotherboardSummary { get; set; } = "—";
        public string CpuSummary { get; set; } = "—";
        public string MemorySummary { get; set; } = "—";
        public List<PcInfoLine> SystemLines { get; set; } = new();
        public List<PcInfoLine> MotherboardLines { get; set; } = new();
        public List<PcInfoLine> CpuLines { get; set; } = new();
        public List<PcInfoLine> MemoryLines { get; set; } = new();
        public List<RamModuleDetailModel> RamModules { get; set; } = new();
        public List<GpuInventoryDetailModel> VideoAdapters { get; set; } = new();
        public List<VolumeInventoryModel> Volumes { get; set; } = new();
        public List<DiskHealthInfo> Disks { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public sealed record RamCleanupResult(
        bool Success,
        long FreedBytes,
        long BeforeFreeBytes,
        long AfterFreeBytes,
        int ProcessesTrimmed,
        string Message)
    {
        public string FormattedFreed => FreedBytes >= 1024 * 1024
            ? $"{FreedBytes / (1024.0 * 1024.0):0.#} MB"
            : $"{FreedBytes / 1024.0:0} KB";
    }

    public sealed class WindowsHealthCheckItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string StatusText { get; set; } = "—";
        public string Detail { get; set; } = string.Empty;
        public WindowsHealthStatus Status { get; set; } = WindowsHealthStatus.Unknown;

        public string StatusColor => Status switch
        {
            WindowsHealthStatus.Healthy => "#10B981",
            WindowsHealthStatus.Attention => "#F59E0B",
            WindowsHealthStatus.Critical => "#EF4444",
            _ => "#A1A1AA"
        };

        public string BadgeBg => Status switch
        {
            WindowsHealthStatus.Healthy => "#064E3B",
            WindowsHealthStatus.Attention => "#78350F",
            WindowsHealthStatus.Critical => "#7F1D1D",
            _ => "#27272A"
        };
    }

    public sealed class WindowsHealthReport
    {
        public DateTime CheckedAt { get; set; } = DateTime.Now;
        public bool IsFullScan { get; set; }
        public bool PendingReboot { get; set; }
        public string? ImageHealthState { get; set; }
        public string WmiStatus { get; set; } = "Não verificado";
        public bool HasWmiIssue => string.Equals(WmiStatus, "Inconsistente", StringComparison.OrdinalIgnoreCase);
        public bool HasSystemDriveDirtyBit { get; set; }
        public bool? SfcFoundCorruption { get; set; }
        public TimeSpan? ClockDelta { get; set; }
        public string TimeZoneName { get; set; } = TimeZoneInfo.Local.DisplayName;
        public string SummaryMessage { get; set; } = string.Empty;
        public List<WindowsHealthCheckItem> Items { get; set; } = new();
    }

    public sealed class RepairStepDefinition : ObservableModel
    {
        private bool _isSelected = true;
        private string _statusText = "Pronto";
        private string _statusColor = "#A1A1AA";

        public string Id { get; init; } = string.Empty;
        public string Icon { get; init; } = "🛠️";
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string CommandDisplay { get; init; } = string.Empty;
        public string EstimatedTime { get; init; } = string.Empty;
        public bool IsRecommended { get; init; }
        public bool RequiresReboot { get; init; }
        public string? CautionNote { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        public string StatusColor
        {
            get => _statusColor;
            set => SetField(ref _statusColor, value);
        }
    }

    public sealed record RepairStepResult(
        string StepId,
        string Title,
        WindowsHealthStatus Status,
        string Detail,
        string? SuggestedAction = null,
        bool RequiresReboot = false);

    public sealed class SystemSuggestionItem
    {
        public string Id { get; init; } = string.Empty;
        public WindowsHealthStatus Severity { get; init; } = WindowsHealthStatus.Attention;
        public string Icon { get; init; } = "💡";
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string ActionLabel { get; init; } = "Abrir";
        public string NavigationTarget { get; init; } = string.Empty;

        public string BorderColor => Severity switch
        {
            WindowsHealthStatus.Critical => "#7F1D1D",
            WindowsHealthStatus.Attention => "#78350F",
            WindowsHealthStatus.Healthy => "#064E3B",
            _ => "#27272A"
        };

        public string BadgeColor => Severity switch
        {
            WindowsHealthStatus.Critical => "#EF4444",
            WindowsHealthStatus.Attention => "#F59E0B",
            WindowsHealthStatus.Healthy => "#10B981",
            _ => "#A1A1AA"
        };
    }
}
