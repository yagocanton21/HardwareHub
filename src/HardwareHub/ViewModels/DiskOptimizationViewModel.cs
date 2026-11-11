using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class DiskOptimizationViewModel : INotifyPropertyChanged
    {
        private readonly DiskOptimizationService _service;
        private readonly Action<string> _log;
        private CancellationTokenSource? _cts;

        private bool _loadedOnce;
        private bool _isBusy;
        private DiskOptimizationConfig _config = new();
        private string _statusBanner = "Audite TRIM/agendamento e execute ReTrim (SSDs) ou desfragmentação (HDs) com segurança.";

        public ObservableCollection<OptimizableVolumeModel> Volumes { get; } = new();

        public DiskOptimizationViewModel(
            DiskOptimizationService? service = null,
            Action<string>? logCallback = null)
        {
            _service = service ?? new DiskOptimizationService();
            _log = logCallback ?? (_ => { });

            RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
            AnalyzeAllCommand = new RelayCommand(_ => _ = AnalyzeAllAsync(), _ => !IsBusy && Volumes.Count > 0);
            OptimizeVolumeCommand = new RelayCommand(
                p => { if (p is OptimizableVolumeModel v) _ = OptimizeVolumeAsync(v); },
                p => !IsBusy && p is OptimizableVolumeModel);
            EnableDefaultsCommand = new RelayCommand(_ => _ = EnableDefaultsAsync(), _ => !IsBusy && HasMissingDefault);
            CancelCommand = new RelayCommand(_ => CancelActiveTask(), _ => IsBusy);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetField(ref _isBusy, value))
                    InvalidateCommands();
            }
        }

        public DiskOptimizationConfig Config
        {
            get => _config;
            private set
            {
                if (SetField(ref _config, value))
                {
                    OnPropertyChanged(nameof(TrimStatusLabel));
                    OnPropertyChanged(nameof(ScheduleStatusLabel));
                    OnPropertyChanged(nameof(ProtectionStatusLabel));
                    OnPropertyChanged(nameof(HasMissingDefault));
                    InvalidateCommands();
                }
            }
        }

        public string TrimStatusLabel => Config.TrimEnabled ? "TRIM: Ativado" : "TRIM: Desativado";
        public string ScheduleStatusLabel => Config.WeeklyScheduleEnabled switch
        {
            true => "Agendamento Semanal: Ativo",
            false => "Agendamento Semanal: Desativado",
            _ => "Agendamento Semanal: Indeterminado"
        };
        public string ProtectionStatusLabel => Config.ProtectionEnabled ? "Proteção do Sistema: Ativa" : "Proteção do Sistema: Inativa";
        public bool HasMissingDefault => Config.HasMissingDefault;

        public string StatusBanner
        {
            get => _statusBanner;
            private set => SetField(ref _statusBanner, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand AnalyzeAllCommand { get; }
        public ICommand OptimizeVolumeCommand { get; }
        public ICommand EnableDefaultsCommand { get; }
        public ICommand CancelCommand { get; }

        public void EnsureLoaded()
        {
            if (_loadedOnce || IsBusy) return;
            _loadedOnce = true;
            _ = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            if (IsBusy) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusy = true;
            StatusBanner = "Lendo volumes NTFS, estado do TRIM e tarefa agendada do Windows...";

            try
            {
                var snap = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var cfg = _service.ReadConfig();
                    var vols = _service.ListVolumes();
                    return (cfg, vols);
                }, ct);

                Config = snap.cfg;
                Volumes.Clear();
                foreach (var v in snap.vols)
                    Volumes.Add(v);

                StatusBanner = $"{Volumes.Count} unidade(s) NTFS detectada(s). Clique em 'Medir Fragmentação' ou otimize diretamente.";
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Leitura cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro ao carregar volumes: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task AnalyzeAllAsync()
        {
            if (IsBusy || Volumes.Count == 0) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusy = true;
            StatusBanner = "Medindo nível de fragmentação das unidades...";

            try
            {
                foreach (var vol in Volumes)
                {
                    ct.ThrowIfCancellationRequested();
                    StatusBanner = $"Analisando {vol.DriveLetter}...";
                    int pct = await _service.AnalyzeFragmentationAsync(vol.DriveLetter, ct);
                    vol.FragmentationPercent = pct >= 0 ? pct : null;
                    vol.StatusMessage = DiskOptimizationService.SummarizeFragmentation(vol, pct, Config.TrimEnabled);
                }

                StatusBanner = "Análise de fragmentação concluída.";
                _log("[DiskOpt] Análise de fragmentação concluída.");
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Análise cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro na análise: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task OptimizeVolumeAsync(OptimizableVolumeModel volume)
        {
            if (IsBusy) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusy = true;
            StatusBanner = volume.IsSsd
                ? $"Executando ReTrim em {volume.DriveLetter}..."
                : $"Desfragmentando {volume.DriveLetter}...";

            try
            {
                var res = await _service.OptimizeVolumeAsync(volume, forceSsdDefrag: false, ct);
                volume.StatusMessage = res.Message;
                StatusBanner = res.Message;
                _log($"[DiskOpt] {res.Message}");
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Otimização cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro ao otimizar: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task EnableDefaultsAsync()
        {
            if (IsBusy) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusy = true;
            StatusBanner = "Restaurando configurações padrão recomendadas do Windows (TRIM / agendamento)...";

            try
            {
                var changed = await _service.EnableMissingDefaultsAsync(Config, ct);
                Config = await Task.Run(_service.ReadConfig, ct);
                StatusBanner = changed.Count > 0
                    ? $"Reativado(s): {string.Join(", ", changed)}."
                    : "Nenhuma alteração aplicada (verifique privilégios de administrador).";
                _log($"[DiskOpt] {StatusBanner}");
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Operação cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro ao restaurar padrões: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void CancelActiveTask()
        {
            try { _cts?.Cancel(); }
            catch { }
        }

        private void InvalidateCommands()
        {
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AnalyzeAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OptimizeVolumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EnableDefaultsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? prop = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(prop);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

