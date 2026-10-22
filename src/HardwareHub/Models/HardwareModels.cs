namespace HardwareHub.Models
{
    public class CpuMetrics
    {
        public string Name { get; set; } = "Identificando CPU...";
        public int Cores { get; set; }
        public int Threads { get; set; }
        public double UsagePercentage { get; set; }
        public double Temperature { get; set; }
        public double ClockSpeedGhz { get; set; }

        public string FormattedTemperature => Temperature > 0 ? $"{Temperature:F0} °C" : "-- °C";
        public string FormattedUsage => $"{UsagePercentage:F0}%";
        public string FormattedClock => ClockSpeedGhz > 0 ? $"{ClockSpeedGhz:F2} GHz" : "--";
    }

    public class GpuMetrics
    {
        public string Name { get; set; } = "Identificando GPU...";
        public double UsagePercentage { get; set; }
        public double Temperature { get; set; }
        public double MemoryTotalGb { get; set; }
        public double MemoryUsedGb { get; set; }
        public double MemoryUsagePercentage
        {
            get => MemoryTotalGb > 0 ? (MemoryUsedGb / MemoryTotalGb) * 100 : 0;
            set { }
        }

        public string FormattedTemperature => Temperature > 0 ? $"{Temperature:F0} °C" : "-- °C";
        public string FormattedUsage => $"{UsagePercentage:F0}%";
        public string FormattedVram => MemoryTotalGb > 0 ? $"{MemoryUsedGb:F1} / {MemoryTotalGb:F1} GB" : "--";
    }

    public class RamMetrics
    {
        public double TotalGb { get; set; }
        public double UsedGb { get; set; }
        public double FreeGb => Math.Max(0, TotalGb - UsedGb);
        public double UsagePercentage
        {
            get => TotalGb > 0 ? (UsedGb / TotalGb) * 100 : 0;
            set { }
        }
        public string Speed { get; set; } = "";

        public string FormattedUsage => $"{UsagePercentage:F0}%";
        public string FormattedCapacity => $"{UsedGb:F1} GB / {TotalGb:F1} GB";
    }

    public class DiskMetrics
    {
        public string DriveLetter { get; set; } = "";
        public string VolumeLabel { get; set; } = "";
        public double TotalGb { get; set; }
        public double FreeGb { get; set; }
        public double UsedGb => Math.Max(0, TotalGb - FreeGb);
        public double UsagePercentage
        {
            get => TotalGb > 0 ? (UsedGb / TotalGb) * 100 : 0;
            set { }
        }

        public string DisplayName => string.IsNullOrEmpty(VolumeLabel) 
            ? $"Disco Local ({DriveLetter})" 
            : $"{VolumeLabel} ({DriveLetter})";

        public string FormattedSpace => $"{UsedGb:F0} GB usados de {TotalGb:F0} GB ({FreeGb:F0} GB livres)";
    }

    public class SystemOverview
    {
        public string OsName { get; set; } = "Windows";
        public string OsArchitecture { get; set; } = "64-bit";
        public string MachineName { get; set; } = Environment.MachineName;
        public string UserName { get; set; } = Environment.UserName;
        public TimeSpan Uptime { get; set; }

        public string FormattedUptime
        {
            get
            {
                if (Uptime.TotalDays >= 1.0)
                {
                    return $"{Uptime.Days}d {Uptime.Hours:D2}h {Uptime.Minutes:D2}m {Uptime.Seconds:D2}s";
                }
                return $"{Uptime.Hours:D2}h {Uptime.Minutes:D2}m {Uptime.Seconds:D2}s";
            }
        }
    }
}
