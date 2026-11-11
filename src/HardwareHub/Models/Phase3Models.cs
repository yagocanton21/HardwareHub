using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;

namespace HardwareHub.Models
{
    // ===================== 1. LIMPEZA SEGURA =====================

    public sealed class CleanupTargetDef
    {
        public string Caminho { get; set; } = "";
        public string Padrao { get; set; } = "*";
        public double IdadeMinimaHoras { get; set; }
        public bool Recursivo { get; set; } = true;
    }

    public sealed class CleanupRuleDef
    {
        public string Id { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string Titulo { get; set; } = "";
        public string Descricao { get; set; } = "";
        public string Nivel { get; set; } = "seguro"; // "seguro" | "moderado" | "avancado"
        public bool Recomendado { get; set; }
        public string? Especial { get; set; }
        public string? Aviso { get; set; }
        public string? Programa { get; set; }
        public List<string> Processos { get; set; } = new();
        public List<string> Servicos { get; set; } = new();
        public List<CleanupTargetDef> Alvos { get; set; } = new();
    }

    public sealed class CleanupItemViewModel : ObservableModel
    {
        private bool _isSelected;
        private long _estimatedBytes = -1;
        private int _fileCount;
        private string _note = "";
        private string? _runningBlockerApp;

        public CleanupRuleDef Definition { get; }

        public CleanupItemViewModel(CleanupRuleDef def)
        {
            Definition = def;
            _isSelected = def.Recomendado;
        }

        public string Id => Definition.Id;
        public string Categoria => Definition.Categoria;
        public string Titulo => Definition.Titulo;
        public string Descricao => Definition.Descricao;
        public string Nivel => Definition.Nivel;
        public bool Recomendado => Definition.Recomendado;
        public string? Aviso => Definition.Aviso;
        public bool HasWarning => !string.IsNullOrWhiteSpace(Definition.Aviso);

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public long EstimatedBytes
        {
            get => _estimatedBytes;
            set
            {
                if (SetField(ref _estimatedBytes, value))
                {
                    OnPropertyChanged(nameof(FormattedSize));
                    OnPropertyChanged(nameof(HasCleanableData));
                }
            }
        }

        public int FileCount
        {
            get => _fileCount;
            set => SetField(ref _fileCount, value);
        }

        public string Note
        {
            get => _note;
            set
            {
                if (SetField(ref _note, value))
                    OnPropertyChanged(nameof(HasNote));
            }
        }

        public bool HasNote => !string.IsNullOrWhiteSpace(_note);

        public string? RunningBlockerApp
        {
            get => _runningBlockerApp;
            set
            {
                if (SetField(ref _runningBlockerApp, value))
                    OnPropertyChanged(nameof(IsBlockedByRunningApp));
            }
        }

        public bool IsBlockedByRunningApp => !string.IsNullOrWhiteSpace(_runningBlockerApp);

        public bool HasCleanableData => EstimatedBytes > 0;

        public string FormattedSize => EstimatedBytes switch
        {
            < 0 => "—",
            0 => "0 B",
            _ => InstalledProgramItem.FormatBytes(EstimatedBytes)
        };

        public string LevelLabel => (Nivel ?? "").ToLowerInvariant() switch
        {
            "seguro" => "Seguro",
            "moderado" => "Moderado",
            "avancado" => "Avançado",
            _ => "Seguro"
        };

        public string LevelColor => (Nivel ?? "").ToLowerInvariant() switch
        {
            "seguro" => "#059669",
            "moderado" => "#D97706",
            "avancado" => "#DC2626",
            _ => "#059669"
        };
    }

    public sealed record CleanScanResult(long Bytes, int FileCount, int DirCount, string? Note);
    public sealed record CleanExecutionResult(int RulesExecuted, long BytesFreed, int FilesDeleted, List<string> Errors, List<string> SkippedRunningApps);

    // ===================== 2. DUPLICADOS =====================

    public sealed class DuplicateFileItemViewModel : ObservableModel
    {
        private bool _isSelected;

        public DuplicateFileEntry Entry { get; }

        public DuplicateFileItemViewModel(DuplicateFileEntry entry, bool initialSelected = false)
        {
            Entry = entry;
            _isSelected = initialSelected;
        }

        public string FilePath => Entry.FilePath;
        public string FileName => Path.GetFileName(Entry.FilePath);
        public string DirectoryName => Path.GetDirectoryName(Entry.FilePath) ?? "";
        public long SizeBytes => Entry.SizeBytes;
        public DateTime ModifiedUtc => Entry.ModifiedUtc;
        public DateTime CreatedUtc => Entry.CreatedUtc;
        public string FormattedModified => ModifiedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }
    }

    public sealed record DuplicateFileEntry(string FilePath, long SizeBytes, DateTime ModifiedUtc, DateTime CreatedUtc);

    public sealed class DuplicateGroupViewModel : ObservableModel
    {
        public long FileSizeBytes { get; init; }
        public string HashHex { get; init; } = "";
        public ObservableCollection<DuplicateFileItemViewModel> Files { get; } = new();

        public string GroupTitle => Files.Count > 0 ? Files[0].FileName : "Grupo duplicado";
        public long WasteBytes => FileSizeBytes * Math.Max(0, Files.Count - 1);
        public string FormattedFileSize => InstalledProgramItem.FormatBytes(FileSizeBytes);
        public string FormattedWaste => InstalledProgramItem.FormatBytes(WasteBytes);
        public string Summary => $"{Files.Count} cópias • {FormattedFileSize} cada • {FormattedWaste} recuperáveis";
    }

    public sealed record DuplicateScanResult(
        List<DuplicateGroupViewModel> Groups,
        long FilesScanned,
        long CloudSkipped,
        long UnreadableSkipped,
        TimeSpan Elapsed,
        bool Cancelled);

    // ===================== 3. MAPA DE DISCO =====================

    public enum DiskFileCategory
    {
        Videos,
        Images,
        Music,
        Documents,
        Programs,
        Archives,
        System,
        Other
    }

    public sealed record DiskLargestFileItem(string FileName, string FullPath, long SizeBytes, DiskFileCategory Category)
    {
        public string FormattedSize => InstalledProgramItem.FormatBytes(SizeBytes);
        public string CategoryLabel => Category switch
        {
            DiskFileCategory.Videos => "Vídeos",
            DiskFileCategory.Images => "Imagens",
            DiskFileCategory.Music => "Músicas",
            DiskFileCategory.Documents => "Documentos",
            DiskFileCategory.Programs => "Programas/Jogos",
            DiskFileCategory.Archives => "Compactados",
            DiskFileCategory.System => "Sistema",
            _ => "Outros"
        };
    }

    public sealed class DiskFolderNode
    {
        public string Name { get; }
        public string FullPath { get; }
        public DiskFolderNode? Parent { get; }
        public long OwnBytes { get; set; }
        public long TotalBytes { get; set; }
        public long FileCount { get; set; }
        public bool AccessDenied { get; set; }
        public long[] BytesByCategory { get; } = new long[8];
        public List<DiskFolderNode> Children { get; } = new();
        public List<DiskLargestFileItem> LargestFiles { get; } = new();

        public DiskFolderNode(string name, string fullPath, DiskFolderNode? parent)
        {
            Name = name;
            FullPath = fullPath;
            Parent = parent;
        }
    }

    public sealed class DiskFolderRowViewModel
    {
        public DiskFolderNode Node { get; init; } = null!;
        public double SharePercent { get; init; }
        public double BarWidthRatio => Math.Clamp(SharePercent / 100.0, 0.02, 1.0);
        public string Name => Node.Name;
        public string FullPath => Node.FullPath;
        public string FormattedSize => InstalledProgramItem.FormatBytes(Node.TotalBytes);
        public string FormattedPercent => $"{SharePercent:0.0}%";
        public string Summary => $"{Node.FileCount:N0} arquivo(s)" + (Node.AccessDenied ? " • acesso parcial" : "");
    }

    public sealed record DiskMapScanResult(
        DiskFolderNode Root,
        List<DiskLargestFileItem> TopFiles,
        long FolderCount,
        long FileCount,
        long DeniedCount,
        TimeSpan Elapsed,
        bool Cancelled);

    // ===================== 4. REDE & DNS =====================

    public sealed record DnsCandidate(
        string Id,
        string Name,
        string[] IPv4,
        string[] IPv6,
        bool IsCurrent = false);

    public sealed record DnsBenchmarkResult(
        DnsCandidate Candidate,
        double MedianMs,
        double AverageMs,
        int Failures,
        int Attempts)
    {
        public bool IsReachable => Failures < Attempts && MedianMs > 0;
        public double SortScore => IsReachable ? MedianMs + Failures * 50.0 : double.MaxValue;
        public string PrimaryIpv4 => Candidate.IPv4.FirstOrDefault() ?? "—";
        public string FormattedMedian => IsReachable ? $"{MedianMs:0.#} ms" : "Sem resposta";
        public string FormattedAverage => IsReachable ? $"média {AverageMs:0.#} ms" : $"{Failures}/{Attempts} falhas";
        public string BadgeText => Candidate.IsCurrent ? "Atual" : (IsReachable ? "Disponível" : "Falha");
        public string BadgeColor => Candidate.IsCurrent ? "#4F46E5" : (IsReachable ? "#059669" : "#DC2626");
    }

    public sealed class NetworkTweakItem : ObservableModel
    {
        private bool _isSelected;

        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Explanation { get; init; } = "";
        public string CurrentValue { get; init; } = "";
        public string TargetValue { get; init; } = "";
        public bool IsNeeded { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }
    }

    public sealed class NetworkSavedState
    {
        public DateTime SavedAt { get; set; }
        public string AdapterId { get; set; } = "";
        public string AdapterName { get; set; } = "";
        public List<string>? PreviousDnsV4 { get; set; }
        public List<string>? PreviousDnsV6 { get; set; }
        public string PreviousAutoTuning { get; set; } = "";
        public string PreviousNicPower { get; set; } = "";
        public int? PreviousDeliveryMode { get; set; }
        public bool PreviousDeliveryRead { get; set; }
        public List<string> AppliedIds { get; set; } = new();
        public string AppliedDnsName { get; set; } = "";
        public bool Reverted { get; set; }
        public DateTime? RevertedAt { get; set; }
    }

    public sealed class NetworkAnalysisPlan
    {
        public string AdapterId { get; init; } = "";
        public string AdapterName { get; init; } = "";
        public string AdapterDescription { get; init; } = "";
        public int? InterfaceIndex { get; init; }
        public bool IsWiFi { get; init; }
        public bool HasIPv6 { get; init; }
        public List<DnsBenchmarkResult> DnsBenchmarks { get; init; } = new();
        public DnsCandidate? RecommendedDns { get; init; }
        public List<NetworkTweakItem> Adjustments { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
        public NetworkSavedState Snapshot { get; init; } = new();
    }

    // ===================== 5. BATERIA & PERIFÉRICOS =====================

    public sealed record BatteryHistoryPoint(DateTime Date, long FullChargeMwh, long DesignMwh)
    {
        public double HealthPercent => DesignMwh > 0 ? Math.Round(FullChargeMwh * 100.0 / DesignMwh, 1) : 0;
    }

    public sealed record BatteryInfoReport
    {
        public bool HasBattery { get; init; }
        public int BatteryCount { get; init; }
        public int? ChargePercent { get; init; }
        public bool? OnAcPower { get; init; }
        public string Name { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public string Chemistry { get; init; } = "";
        public long DesignCapacityMwh { get; init; }
        public long FullChargeCapacityMwh { get; init; }
        public int? CycleCount { get; init; }
        public TimeSpan? EstimatedRuntimeCurrent { get; init; }
        public TimeSpan? EstimatedRuntimeOriginal { get; init; }
        public DateTime? ScannedAt { get; init; }
        public string? ErrorMessage { get; init; }
        public List<BatteryHistoryPoint> History { get; init; } = new();

        [JsonIgnore]
        public double HealthPercent => DesignCapacityMwh > 0
            ? Math.Clamp(Math.Round(FullChargeCapacityMwh * 100.0 / DesignCapacityMwh, 1), 0, 100)
            : 0;

        [JsonIgnore]
        public double WearPercent => DesignCapacityMwh > 0
            ? Math.Clamp(Math.Round(100.0 - HealthPercent, 1), 0, 100)
            : 0;

        [JsonIgnore]
        public string HealthVerdict => !HasBattery ? "Sem bateria detectada (PC de mesa ou bateria desconectada)" : HealthPercent switch
        {
            >= 80 => "Saudável — excelente retenção de carga",
            >= 60 => "Desgaste moderado — autonomia reduzida",
            > 0 => "Desgaste elevado — considere substituição em breve",
            _ => "Capacidade indisponível"
        };

        [JsonIgnore]
        public string HealthColor => HealthPercent switch
        {
            >= 80 => "#059669",
            >= 60 => "#D97706",
            _ => "#DC2626"
        };
    }

    public sealed class KeyboardKeyVisualItem : ObservableModel
    {
        private bool _isPressed;
        private bool _wasTested;

        public string KeyId { get; init; } = "";
        public string Label { get; init; } = "";
        public double WidthUnits { get; init; } = 1.0;
        public double VisualWidth => WidthUnits * 40.0;

        public bool IsPressed
        {
            get => _isPressed;
            set
            {
                if (SetField(ref _isPressed, value))
                    OnPropertyChanged(nameof(BackgroundHex));
            }
        }

        public bool WasTested
        {
            get => _wasTested;
            set
            {
                if (SetField(ref _wasTested, value))
                    OnPropertyChanged(nameof(BackgroundHex));
            }
        }

        public string BackgroundHex => IsPressed ? "#6366F1" : (WasTested ? "#065F46" : "#27272A");
    }
}

