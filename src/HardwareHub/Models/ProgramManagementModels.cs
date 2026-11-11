using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HardwareHub.Models
{
    public abstract class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public enum StartupAdvice
    {
        CanDisable,
        Caution,
        KeepEnabled,
        Unknown
    }

    public sealed class StartupCatalogRule
    {
        public string Padrao { get; set; } = "";
        public string? Nome { get; set; }
        public string OQueE { get; set; } = "";
        public string Desligar { get; set; } = "confira";

        [JsonIgnore]
        public Regex? CompiledRegex { get; set; }
    }

    public sealed class StartupEntryViewModel : ObservableModel
    {
        private bool _isEnabled;
        private bool _isBusy;
        private string _displayName = "";
        private string _publisher = "";
        private string _description = "";
        private string _filePath = "";
        private bool _fileExists = true;
        private StartupAdvice _advice = StartupAdvice.Unknown;
        private DateTime? _disabledAt;

        public string Name { get; init; } = "";
        public string Command { get; init; } = "";
        public string Origin { get; init; } = "";
        public string ApprovedKey { get; init; } = "";
        public string ApprovedName { get; init; } = "";

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (SetField(ref _isEnabled, value))
                {
                    OnPropertyChanged(nameof(StatusLabel));
                    OnPropertyChanged(nameof(StatusColor));
                    OnPropertyChanged(nameof(ActionButtonText));
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetField(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanInteract));
                    OnPropertyChanged(nameof(ActionButtonText));
                }
            }
        }

        public string DisplayName
        {
            get => string.IsNullOrWhiteSpace(_displayName) ? Name : _displayName;
            set => SetField(ref _displayName, value);
        }

        public string Publisher
        {
            get => _publisher;
            set => SetField(ref _publisher, value);
        }

        public string Description
        {
            get => _description;
            set => SetField(ref _description, value);
        }

        public string FilePath
        {
            get => _filePath;
            set => SetField(ref _filePath, value);
        }

        public bool FileExists
        {
            get => _fileExists;
            set => SetField(ref _fileExists, value);
        }

        public StartupAdvice Advice
        {
            get => _advice;
            set
            {
                if (SetField(ref _advice, value))
                {
                    OnPropertyChanged(nameof(AdviceLabel));
                    OnPropertyChanged(nameof(AdviceColor));
                }
            }
        }

        public DateTime? DisabledAt
        {
            get => _disabledAt;
            set => SetField(ref _disabledAt, value);
        }

        public bool CanInteract => !IsBusy;
        public string StatusLabel => IsEnabled ? "Ativo na inicialização" : "Desativado";
        public string StatusColor => IsEnabled ? "#059669" : "#52525B";
        public string ActionButtonText => IsBusy ? "Aguarde..." : (IsEnabled ? "Desligar" : "Ligar");

        public string AdviceLabel => Advice switch
        {
            StartupAdvice.CanDisable => "Pode desligar",
            StartupAdvice.Caution => "Desligue só se não usar",
            StartupAdvice.KeepEnabled => "Recomendado manter",
            _ => "Confira antes"
        };

        public string AdviceColor => Advice switch
        {
            StartupAdvice.CanDisable => "#10B981",
            StartupAdvice.Caution => "#F59E0B",
            StartupAdvice.KeepEnabled => "#6366F1",
            _ => "#71717A"
        };
    }

    public sealed class ProgramUpdateItemViewModel : ObservableModel
    {
        private bool _isSelected = true;
        private bool _isUpdating;
        private bool _isUpdated;
        private string _statusMessage = "Disponível";

        public string Name { get; init; } = "";
        public string Id { get; init; } = "";
        public string InstalledVersion { get; init; } = "";
        public string AvailableVersion { get; init; } = "";
        public string Source { get; init; } = "winget";

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public bool IsUpdating
        {
            get => _isUpdating;
            set
            {
                if (SetField(ref _isUpdating, value))
                {
                    OnPropertyChanged(nameof(CanInteract));
                }
            }
        }

        public bool IsUpdated
        {
            get => _isUpdated;
            set
            {
                if (SetField(ref _isUpdated, value))
                {
                    OnPropertyChanged(nameof(CanInteract));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        public bool CanInteract => !IsUpdating && !IsUpdated;
        public string StatusColor => IsUpdated ? "#10B981" : (IsUpdating ? "#6366F1" : "#F59E0B");
    }

    public enum LeftoverLevel
    {
        Safe = 0,
        Moderate = 1,
        Advanced = 2
    }

    public enum LeftoverKind
    {
        Folder,
        File,
        RegistryKey,
        RegistryValue
    }

    public sealed class InstalledProgramItem : ObservableModel
    {
        public string KeyId { get; init; } = "";
        public string RegistryPath { get; init; } = "";
        public string Name { get; init; } = "";
        public string Publisher { get; init; } = "";
        public string Version { get; init; } = "";
        public DateTime? InstallDate { get; init; }
        public long EstimatedBytes { get; init; } = -1;
        public string? DisplayIcon { get; init; }
        public string? UninstallString { get; init; }
        public string? QuietUninstallString { get; init; }
        public string? ProductCode { get; init; }
        public string? InstallLocation { get; init; }
        public bool IsUserInstall { get; init; }
        public bool IsMissingUninstaller { get; init; }

        public string FormattedSize => EstimatedBytes > 0 ? FormatBytes(EstimatedBytes) : "—";
        public string FormattedDate => InstallDate?.ToString("dd/MM/yyyy") ?? "—";
        public string ScopeLabel => IsUserInstall ? "Usuário" : "Sistema";

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double val = bytes;
            int idx = 0;
            while (val >= 1024 && idx < units.Length - 1)
            {
                val /= 1024;
                idx++;
            }
            return $"{val:0.#} {units[idx]}";
        }
    }

    public sealed class ProgramLeftoverItem : ObservableModel
    {
        private bool _isSelected = true;

        public LeftoverKind Kind { get; init; }
        public LeftoverLevel Level { get; init; }
        public string Path { get; init; } = "";
        public string? ValueName { get; init; }
        public string Reason { get; init; } = "";
        public long SizeBytes { get; init; }
        public bool RequiresReview { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public bool IsRegistry => Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue;

        public string DisplayPath => string.IsNullOrEmpty(ValueName) ? Path : $"{Path} [{ValueName}]";

        public string KindLabel => Kind switch
        {
            LeftoverKind.Folder => "Pasta",
            LeftoverKind.File => "Arquivo",
            LeftoverKind.RegistryKey => "Chave Reg",
            LeftoverKind.RegistryValue => "Valor Reg",
            _ => "Item"
        };

        public string LevelLabel => Level switch
        {
            LeftoverLevel.Safe => "Seguro",
            LeftoverLevel.Moderate => "Moderado",
            _ => "Avançado"
        };

        public string LevelColor => Level switch
        {
            LeftoverLevel.Safe => "#059669",
            LeftoverLevel.Moderate => "#D97706",
            _ => "#DC2626"
        };

        public string FormattedSize => IsRegistry || SizeBytes <= 0 ? "—" : InstalledProgramItem.FormatBytes(SizeBytes);
    }

    public sealed class UninstallBackupItem
    {
        public string Description { get; set; } = "";
        public string FileName { get; set; } = "";
    }

    public sealed class UninstallBackupInfo
    {
        public string Title { get; set; } = "";
        public DateTime Date { get; set; }
        public DateTime? RestoredAt { get; set; }
        public List<UninstallBackupItem> Items { get; set; } = new();
        public List<string> RemovedPaths { get; set; } = new();

        [JsonIgnore]
        public string FolderPath { get; set; } = "";

        [JsonIgnore]
        public string Summary => $"{Title} ({Date:dd/MM/yyyy HH:mm}) — {Items.Count} chave(s) Reg, {RemovedPaths.Count} pasta(s)";

        [JsonIgnore]
        public bool CanRestoreRegistry => Items.Count > 0;
    }

    public sealed record UninstallResult(bool Ok, string Message, bool RebootRequired = false, bool Cancelled = false);

    public sealed record LeftoverCleanResult(int RemovedCount, List<string> Errors, UninstallBackupInfo? Backup);
}

