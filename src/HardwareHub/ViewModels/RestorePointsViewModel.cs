using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class RestorePointsViewModel : INotifyPropertyChanged
    {
        private readonly RestorePointsService _service;
        private readonly Action<string> _log;
        private CancellationTokenSource? _cts;

        private bool _loadedOnce;
        private bool _isBusy;
        private string _newPointDescription = "Ponto antes de alterações (Hardware HUB)";
        private SystemProtectionStatus _protectionStatus = new();
        private string _statusBanner = "Crie pontos de segurança antes de instalar drivers/tweaks ou remova cópias antigas.";

        public ObservableCollection<RestorePointModel> Points { get; } = new();

        public RestorePointsViewModel(
            RestorePointsService? service = null,
            Action<string>? logCallback = null)
        {
            _service = service ?? new RestorePointsService();
            _log = logCallback ?? (_ => { });

            RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
            CreatePointCommand = new RelayCommand(_ => _ = CreatePointAsync(), _ => !IsBusy);
            EnableProtectionCommand = new RelayCommand(_ => _ = EnableProtectionAsync(), _ => !IsBusy);
            DeletePointCommand = new RelayCommand(
                p => { if (p is RestorePointModel rp) DeletePoint(rp); },
                p => !IsBusy && p is RestorePointModel rp && !rp.IsLatest);
            OpenRestoreWizardCommand = new RelayCommand(_ => RestorePointsService.OpenSystemRestoreWizard());
            OpenProtectionSettingsCommand = new RelayCommand(_ => RestorePointsService.OpenProtectionSettings());
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

        public string NewPointDescription
        {
            get => _newPointDescription;
            set => SetField(ref _newPointDescription, value);
        }

        public SystemProtectionStatus ProtectionStatus
        {
            get => _protectionStatus;
            private set
            {
                if (SetField(ref _protectionStatus, value))
                    OnPropertyChanged(nameof(ShowEnableProtectionButton));
            }
        }

        public bool ShowEnableProtectionButton => ProtectionStatus.IsEnabled == false;
        public bool HasNoPoints => Points.Count == 0;

        public string StatusBanner
        {
            get => _statusBanner;
            private set => SetField(ref _statusBanner, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand CreatePointCommand { get; }
        public ICommand EnableProtectionCommand { get; }
        public ICommand DeletePointCommand { get; }
        public ICommand OpenRestoreWizardCommand { get; }
        public ICommand OpenProtectionSettingsCommand { get; }
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
            StatusBanner = "Consultando pontos de restauração do Windows e uso em Volume Shadow Copy...";

            try
            {
                var res = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var prot = _service.ReadProtectionStatus();
                    var (pts, err) = _service.ReadPoints();
                    return (prot, pts, err);
                }, ct);

                ProtectionStatus = res.prot;

                Points.Clear();
                foreach (var pt in res.pts)
                    Points.Add(pt);
                OnPropertyChanged(nameof(HasNoPoints));

                if (!string.IsNullOrWhiteSpace(res.err))
                {
                    StatusBanner = res.err;
                }
                else
                {
                    StatusBanner = Points.Count > 0
                        ? $"{Points.Count} ponto(s) de restauração disponível(is)."
                        : "Nenhum ponto de restauração salvo no momento.";
                }
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Leitura cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro ao consultar pontos: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task CreatePointAsync()
        {
            if (IsBusy) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusy = true;
            StatusBanner = "Criando ponto de restauração no Windows (pode levar alguns segundos)...";

            try
            {
                var res = await _service.CreatePointAsync(NewPointDescription, ct);
                StatusBanner = res.Message;
                _log($"[RestorePoints] {res.Message}");

                if (res.Success)
                {
                    IsBusy = false;
                    await RefreshAsync();
                }
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Criação do ponto cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro ao criar ponto: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task EnableProtectionAsync()
        {
            if (IsBusy) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusy = true;
            StatusBanner = "Habilitando a Proteção do Sistema na unidade do Windows...";

            try
            {
                var res = await _service.EnableProtectionAsync(ct);
                StatusBanner = res.Message;
                _log($"[RestorePoints] {res.Message}");
                IsBusy = false;
                await RefreshAsync();
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Operação cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro ao ativar proteção: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void DeletePoint(RestorePointModel point)
        {
            if (IsBusy) return;
            var res = _service.DeletePoint(point);
            StatusBanner = res.Message;
            _log($"[RestorePoints] {res.Message}");

            if (res.Success)
            {
                Points.Remove(point);
                OnPropertyChanged(nameof(HasNoPoints));
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
            (CreatePointCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EnableProtectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeletePointCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

