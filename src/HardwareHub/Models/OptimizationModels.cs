using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HardwareHub.Models
{
    public sealed class TweakActionModel
    {
        public string Tipo { get; set; } = "reg";
        public string? Chave { get; set; }
        public string? Valor { get; set; }
        public string? Kind { get; set; }
        public string? Dados { get; set; }
        public string? Servico { get; set; }
        public string? Inicio { get; set; }
    }

    public sealed class TweakDefinition
    {
        public string Id { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string Titulo { get; set; } = "";
        public string Descricao { get; set; } = "";
        public string Risco { get; set; } = "seguro";
        public bool Recomendado { get; set; }
        public int MinBuild { get; set; }
        public int? MaxBuild { get; set; }
        public string? Reiniciar { get; set; }
        public List<TweakActionModel> Ativar { get; set; } = new();
        public List<TweakActionModel> Desativar { get; set; } = new();
    }

    public sealed class TweakBackupSnapshot
    {
        public string Tipo { get; set; } = "reg";
        public string? Chave { get; set; }
        public string? Valor { get; set; }
        public bool Existia { get; set; }
        public string? CriadaRaiz { get; set; }
        public string? Kind { get; set; }
        public long? Numero { get; set; }
        public string? Texto { get; set; }
        public string? Hex { get; set; }
        public string[]? Lista { get; set; }
        public string? Servico { get; set; }
        public int? Start { get; set; }
        public int? Delayed { get; set; }
    }

    public sealed class TweakBackupEntry
    {
        public DateTime Aplicado { get; set; }
        public List<TweakBackupSnapshot> Snapshots { get; set; } = new();
    }

    public readonly record struct RegReadResult(bool Exists, RegistryValueKind Kind, object? Data)
    {
        public static readonly RegReadResult Missing = new(false, RegistryValueKind.Unknown, null);
    }

    public sealed class TweakItemViewModel : INotifyPropertyChanged
    {
        private bool _isActive;
        private bool _hasBackup;
        private DateTime? _appliedAt;
        private bool _isBusy;

        public TweakDefinition Definition { get; }

        public TweakItemViewModel(TweakDefinition definition)
        {
            Definition = definition;
        }

        public string Id => Definition.Id;
        public string Categoria => Definition.Categoria;
        public string Titulo => Definition.Titulo;
        public string Descricao => Definition.Descricao;
        public string Risco => Definition.Risco;
        public bool Recomendado => Definition.Recomendado;
        public string? Reiniciar => Definition.Reiniciar;

        public string RiskLabel => Definition.Risco.ToLowerInvariant() switch
        {
            "seguro" => "Seguro",
            "cuidado" => "Cuidado",
            "avancado" => "Avançado",
            _ => Definition.Risco
        };

        public string RiskColor => Definition.Risco.ToLowerInvariant() switch
        {
            "seguro" => "#10B981",
            "cuidado" => "#F59E0B",
            "avancado" => "#EF4444",
            _ => "#6B7280"
        };

        public string RestartNote => Definition.Reiniciar?.ToLowerInvariant() switch
        {
            "explorer" => "Reinicia Explorer",
            "pc" => "Requer reinício do PC",
            _ => ""
        };

        public bool HasRestartNote => !string.IsNullOrEmpty(RestartNote);

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ActionButtonText));
                }
            }
        }

        public bool HasBackup
        {
            get => _hasBackup;
            set
            {
                if (_hasBackup != value)
                {
                    _hasBackup = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanRevert));
                }
            }
        }

        public DateTime? AppliedAt
        {
            get => _appliedAt;
            set
            {
                if (_appliedAt != value)
                {
                    _appliedAt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedAppliedAt));
                }
            }
        }

        public string FormattedAppliedAt => AppliedAt.HasValue
            ? AppliedAt.Value.ToString("dd/MM/yyyy HH:mm")
            : "";

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanInteract));
                }
            }
        }

        public bool CanInteract => !IsBusy;
        public bool CanRevert => HasBackup || IsActive;
        public string ActionButtonText => IsActive ? "Reverter" : "Aplicar";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class OptimizationProfile
    {
        public string Id { get; init; } = "";
        public string Nome { get; init; } = "";
        public string Resumo { get; init; } = "";
        public string Detalhes { get; init; } = "";
        public string AccentColor { get; init; } = "#6366F1";
        public IReadOnlyList<string> TweakIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> BloatwareIds { get; init; } = Array.Empty<string>();
        public string TweakCountText => $"{TweakIds.Count} ajustes • {BloatwareIds.Count} apps";
    }

    public enum BloatwareRecommendation
    {
        CanRemove,
        Optional,
        KeepProtected
    }

    public sealed class BloatwareDefinition
    {
        public string Id { get; set; } = "";
        public string Nome { get; set; } = "";
        public string Descricao { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string Recomendacao { get; set; } = "opcional";
        public List<string> Pacotes { get; set; } = new();
        public string? StoreId { get; set; }
        public string? Aviso { get; set; }

        [JsonIgnore]
        public BloatwareRecommendation Level => Recomendacao.ToLowerInvariant() switch
        {
            "remover" => BloatwareRecommendation.CanRemove,
            "manter" => BloatwareRecommendation.KeepProtected,
            _ => BloatwareRecommendation.Optional
        };

        public bool MatchesPackage(string? packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return false;
            return Pacotes.Any(pattern =>
                Regex.IsMatch(packageName, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase));
        }
    }

    public sealed class ProtectedProgramRule
    {
        public string Nome { get; set; } = "";
        public string Motivo { get; set; } = "";
    }

    public sealed class BloatwareCatalog
    {
        public List<BloatwareDefinition> Apps { get; set; } = new();
        public List<ProtectedProgramRule> ProgramasProtegidos { get; set; } = new();
        public List<ProtectedProgramRule> ProgramasCuidado { get; set; } = new();
    }

    public sealed class RemovedBloatwareRecord
    {
        public DateTime Data { get; set; }
        public string Id { get; set; } = "";
        public string Nome { get; set; } = "";
        public string Pacote { get; set; } = "";
        public string Familia { get; set; } = "";
        public string? StoreId { get; set; }
        public DateTime? Reinstalado { get; set; }
    }

    public sealed class BloatwareItemViewModel : INotifyPropertyChanged
    {
        private bool _isInstalled;
        private bool _isProvisioned;
        private bool _isSelected;
        private bool _isBusy;
        private string _statusText = "Não verificado";

        public BloatwareDefinition Definition { get; }

        public BloatwareItemViewModel(BloatwareDefinition definition)
        {
            Definition = definition;
            _isSelected = definition.Level == BloatwareRecommendation.CanRemove;
        }

        public string Id => Definition.Id;
        public string Nome => Definition.Nome;
        public string Descricao => Definition.Descricao;
        public string Categoria => Definition.Categoria;
        public string? Aviso => Definition.Aviso;
        public string? StoreId => Definition.StoreId;
        public BloatwareRecommendation Level => Definition.Level;
        public bool IsProtected => Level == BloatwareRecommendation.KeepProtected;
        public bool CanRemove => !IsProtected;
        public bool HasWarning => !string.IsNullOrWhiteSpace(Aviso);

        public string LevelText => Level switch
        {
            BloatwareRecommendation.CanRemove => "Pode remover",
            BloatwareRecommendation.KeepProtected => "Protegido (Não remova)",
            _ => "Opcional"
        };

        public string LevelColor => Level switch
        {
            BloatwareRecommendation.CanRemove => "#10B981",
            BloatwareRecommendation.KeepProtected => "#EF4444",
            _ => "#F59E0B"
        };

        public List<string> UserPackageFullNames { get; set; } = new();
        public List<string> ProvisionedPackageNames { get; set; } = new();
        public string? PrimaryPackageName { get; set; }
        public string? PackageFamilyName { get; set; }

        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (_isInstalled != value)
                {
                    _isInstalled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPresent));
                }
            }
        }

        public bool IsProvisioned
        {
            get => _isProvisioned;
            set
            {
                if (_isProvisioned != value)
                {
                    _isProvisioned = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPresent));
                }
            }
        }

        public bool IsPresent => IsInstalled || IsProvisioned;

        public bool IsSelected
        {
            get => _isSelected && CanRemove;
            set
            {
                bool next = CanRemove && value;
                if (_isSelected != next)
                {
                    _isSelected = next;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanInteract)); } }
        }

        public bool CanInteract => !IsBusy && CanRemove;

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class SoftwareKit
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public IReadOnlyList<string> SoftwareIds { get; init; } = Array.Empty<string>();
        public string CountLabel => $"{SoftwareIds.Count} apps";
    }

    public sealed class WindowsActivationStatus
    {
        public bool QuerySucceeded { get; init; }
        public bool IsActivated { get; init; }
        public int LicenseStatusCode { get; init; }
        public string Edition { get; init; } = "Windows";
        public string Channel { get; init; } = "";
        public string ChannelDescription { get; init; } = "";
        public string PartialKey { get; init; } = "";
        public bool HasFirmwareKey { get; init; }
        public string ErrorMessage { get; init; } = "";
        public string Summary => IsActivated
            ? $"Ativado ({Edition} • {ChannelDescription}{(string.IsNullOrEmpty(PartialKey) ? "" : $" • final {PartialKey}")})"
            : (QuerySucceeded ? $"Não ativado ({Edition})" : $"Não foi possível consultar ({ErrorMessage})");
    }

    public enum PostFormatStepState
    {
        Pending,
        Running,
        Success,
        Warning,
        Skipped,
        Error
    }

    public sealed class PostFormatStepViewModel : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        private PostFormatStepState _state = PostFormatStepState.Pending;
        private string _statusText = "Aguardando";
        private string _detail = "";

        public string Id { get; init; } = "";
        public int Order { get; init; }
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";

        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
        }

        public PostFormatStepState State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StateColor));
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public string Detail
        {
            get => _detail;
            set { if (_detail != value) { _detail = value; OnPropertyChanged(); } }
        }

        public string StateColor => State switch
        {
            PostFormatStepState.Running => "#6366F1",
            PostFormatStepState.Success => "#10B981",
            PostFormatStepState.Warning => "#F59E0B",
            PostFormatStepState.Error => "#EF4444",
            PostFormatStepState.Skipped => "#71717A",
            _ => "#52525B"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

