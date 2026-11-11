namespace HardwareHub.Models
{
    // ===================== 1. COMPATIBILIDADE WINDOWS 11 =====================

    public enum Windows11CheckStatus
    {
        Ok,
        Fail,
        Unknown
    }

    public sealed class Windows11RequirementItem
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        public Windows11CheckStatus Status { get; init; }
        public string Found { get; init; } = "";
        public string Required { get; init; } = "";
        public string Recommendation { get; init; } = "";

        public string StatusLabel => Status switch
        {
            Windows11CheckStatus.Ok => "Atende",
            Windows11CheckStatus.Fail => "Não atende",
            _ => "Não confirmado"
        };

        public string StatusColor => Status switch
        {
            Windows11CheckStatus.Ok => "#059669",
            Windows11CheckStatus.Fail => "#DC2626",
            _ => "#D97706"
        };
    }

    public sealed class Windows11Report
    {
        public string WindowsEdition { get; init; } = "";
        public int CurrentBuild { get; init; }
        public List<Windows11RequirementItem> Items { get; init; } = new();

        public int PassedCount => Items.Count(i => i.Status == Windows11CheckStatus.Ok);
        public int FailedCount => Items.Count(i => i.Status == Windows11CheckStatus.Fail);
        public int UnknownCount => Items.Count(i => i.Status == Windows11CheckStatus.Unknown);

        public bool IsEligible => Items.Count > 0 && FailedCount == 0 && UnknownCount == 0;
        public bool IsAlreadyWindows11 => CurrentBuild >= 22000;

        public string VerdictTitle => IsEligible
            ? (IsAlreadyWindows11 ? "Sistema compatível (Windows 11 em execução)" : "Este PC atende a todos os requisitos do Windows 11")
            : (FailedCount > 0
                ? $"Atenção: {FailedCount} requisito(s) não atendem ao Windows 11"
                : $"Verificação parcial: {UnknownCount} item(ns) não confirmado(s)");

        public string VerdictColor => IsEligible ? "#059669" : (FailedCount > 0 ? "#DC2626" : "#D97706");
    }

    // ===================== 2. PONTOS DE RESTAURAÇÃO =====================

    public sealed class RestorePointModel
    {
        public uint SequenceNumber { get; init; }
        public string Description { get; init; } = "";
        public uint RestorePointType { get; init; }
        public DateTime CreationTime { get; init; }
        public bool IsLatest { get; init; }

        public string TypeLabel => RestorePointType switch
        {
            0 => "Instalação de App",
            1 => "Remoção de App",
            7 => "Ponto Automático",
            10 => "Instalação de Driver",
            12 => "Ponto Manual / Alteração",
            18 => "Windows Update",
            _ => "Sistema"
        };

        public string FormattedDate => CreationTime == DateTime.MinValue
            ? "Data desconhecida"
            : CreationTime.ToString("dd/MM/yyyy HH:mm");

        public string SequenceBadge => IsLatest ? $"#{SequenceNumber} (Mais recente)" : $"#{SequenceNumber}";
    }

    public sealed class SystemProtectionStatus
    {
        public bool? IsEnabled { get; init; }
        public long? UsedBytes { get; init; }
        public long? AllocatedBytes { get; init; }
        public long? MaxBytes { get; init; }

        public string StatusLabel => IsEnabled switch
        {
            true => "Ativada na unidade do Windows",
            false => "Desativada — crie/ative para proteger o sistema",
            _ => "Status indeterminado (verifique permissões de administrador)"
        };

        public string StatusColor => IsEnabled switch
        {
            true => "#059669",
            false => "#DC2626",
            _ => "#D97706"
        };

        public string UsageSummary
        {
            get
            {
                if (!UsedBytes.HasValue && !MaxBytes.HasValue)
                    return "Espaço em sombra (VSS): não informado";
                string used = UsedBytes.HasValue ? InstalledProgramItem.FormatBytes(UsedBytes.Value) : "—";
                string max = MaxBytes.HasValue ? InstalledProgramItem.FormatBytes(MaxBytes.Value) : "—";
                return $"Espaço usado pelas cópias: {used} de {max} máximo";
            }
        }
    }

    // ===================== 3. OTIMIZAÇÃO & TRIM DE DISCOS =====================

    public sealed class DiskOptimizationConfig
    {
        public bool TrimEnabled { get; init; }
        public bool? WeeklyScheduleEnabled { get; init; }
        public bool ProtectionEnabled { get; init; }

        public bool HasMissingDefault => !TrimEnabled || WeeklyScheduleEnabled == false || !ProtectionEnabled;
    }

    public sealed class OptimizableVolumeModel : ObservableModel
    {
        private int? _fragmentationPercent;
        private string _statusMessage = "Não analisado";

        public string DriveLetter { get; init; } = "";
        public string Label { get; init; } = "";
        public string Model { get; init; } = "";
        public bool IsSsd { get; init; }
        public long SizeBytes { get; init; }

        public int? FragmentationPercent
        {
            get => _fragmentationPercent;
            set
            {
                if (SetField(ref _fragmentationPercent, value))
                    OnPropertyChanged(nameof(FragmentationText));
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        public string MediaBadge => IsSsd ? "SSD (TRIM)" : "HD Mecânico";
        public string MediaBadgeColor => IsSsd ? "#4F46E5" : "#0891B2";
        public string ActionButtonLabel => IsSsd ? "⚡ Executar ReTrim" : "⚙ Desfragmentar";
        public string FormattedSize => SizeBytes > 0 ? InstalledProgramItem.FormatBytes(SizeBytes) : "—";

        public string DisplayName => string.IsNullOrWhiteSpace(Label)
            ? $"{DriveLetter} ({FormattedSize})"
            : $"{DriveLetter} {Label} ({FormattedSize})";

        public string FragmentationText => FragmentationPercent.HasValue
            ? $"{FragmentationPercent.Value}% fragmentado"
            : "Fragmentação não medida";
    }
}

