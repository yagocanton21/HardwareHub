using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class DriversUpdateViewModel : INotifyPropertyChanged
    {
        private readonly DriversUpdateService _service;
        private readonly Action<string> _log;
        private CancellationTokenSource? _cts;

        private string _activeSubTab = "Overview"; // "Overview" | "Updates" | "Inventory"
        private bool _isBusyDrivers;
        private bool _isBusyUpdates;
        private bool _loadedOnce;

        private string _statusBanner = "Analise dispositivos com problema, faça backup dos drivers atuais ou verifique atualizações do Windows.";
        private string _driverSearchText = "";
        private string _driverClassFilter = "Todas";
        private bool _showOnlyUnsigned;

        private DriverBoardInfo _boardInfo = new();
        private readonly List<DriverItemModel> _allDrivers = new();
        private bool _isRebootPending;
        private string _lastBackupFolder = "";
        private string _updateStageText = "";

        public ObservableCollection<DriverProblemItem> DriverProblems { get; } = new();
        public ObservableCollection<DriverGpuInfo> Gpus { get; } = new();
        public ObservableCollection<DriverItemModel> FilteredDrivers { get; } = new();
        public ObservableCollection<string> ClassFilters { get; } = new() { "Todas" };
        public ObservableCollection<WindowsUpdateItemModel> WindowsUpdates { get; } = new();

        public DriversUpdateViewModel(
            DriversUpdateService? service = null,
            Action<string>? logCallback = null)
        {
            _service = service ?? new DriversUpdateService();
            _log = logCallback ?? (_ => { });

            SetSubTabCommand = new RelayCommand(p => ActiveSubTab = p?.ToString() ?? "Overview");
            RefreshHardwareAndDriversCommand = new RelayCommand(_ => _ = RefreshHardwareAndDriversAsync(), _ => !IsAnyBusy);
            SearchUpdatesCommand = new RelayCommand(_ => _ = SearchUpdatesAsync(), _ => !IsAnyBusy);
            InstallSelectedUpdatesCommand = new RelayCommand(_ => _ = InstallSelectedUpdatesAsync(), _ => !IsAnyBusy && SelectedUpdatesCount > 0);
            ToggleSelectAllUpdatesCommand = new RelayCommand(_ => ToggleSelectAllUpdates(), _ => WindowsUpdates.Count > 0 && !IsAnyBusy);
            OpenDeviceManagerCommand = new RelayCommand(_ => DriversUpdateService.OpenDeviceManager());
            OpenWindowsUpdateDriversCommand = new RelayCommand(_ => DriversUpdateService.OpenWindowsUpdateDrivers());
            OpenBoardSupportCommand = new RelayCommand(_ => DriversUpdateService.OpenUrl(BoardInfo.SupportUrl), _ => !string.IsNullOrWhiteSpace(BoardInfo.SupportUrl));
            OpenGpuSupportCommand = new RelayCommand(p => DriversUpdateService.OpenUrl(p?.ToString()));
            CancelTaskCommand = new RelayCommand(_ => CancelActiveTask(), _ => IsAnyBusy);
        }

        public string ActiveSubTab
        {
            get => _activeSubTab;
            set
            {
                if (SetField(ref _activeSubTab, value))
                {
                    OnPropertyChanged(nameof(IsOverviewTabActive));
                    OnPropertyChanged(nameof(IsUpdatesTabActive));
                    OnPropertyChanged(nameof(IsInventoryTabActive));
                }
            }
        }

        public bool IsOverviewTabActive => ActiveSubTab == "Overview";
        public bool IsUpdatesTabActive => ActiveSubTab == "Updates";
        public bool IsInventoryTabActive => ActiveSubTab == "Inventory";

        public bool IsBusyDrivers
        {
            get => _isBusyDrivers;
            private set
            {
                if (SetField(ref _isBusyDrivers, value))
                {
                    OnPropertyChanged(nameof(IsAnyBusy));
                    OnPropertyChanged(nameof(CanRunDriverBackup));
                    InvalidateCommands();
                }
            }
        }

        public bool IsBusyUpdates
        {
            get => _isBusyUpdates;
            private set
            {
                if (SetField(ref _isBusyUpdates, value))
                {
                    OnPropertyChanged(nameof(IsAnyBusy));
                    OnPropertyChanged(nameof(CanRunDriverBackup));
                    InvalidateCommands();
                }
            }
        }

        public bool IsAnyBusy => IsBusyDrivers || IsBusyUpdates;
        public bool CanRunDriverBackup => !IsAnyBusy;

        public string StatusBanner
        {
            get => _statusBanner;
            private set => SetField(ref _statusBanner, value);
        }

        public string UpdateStageText
        {
            get => _updateStageText;
            private set => SetField(ref _updateStageText, value);
        }

        public DriverBoardInfo BoardInfo
        {
            get => _boardInfo;
            private set
            {
                if (SetField(ref _boardInfo, value))
                {
                    OnPropertyChanged(nameof(BoardSummaryText));
                    OnPropertyChanged(nameof(HasBoardSupportUrl));
                    InvalidateCommands();
                }
            }
        }

        public string BoardSummaryText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(BoardInfo.DisplayTitle))
                    return "Placa-mãe não identificada";
                return string.IsNullOrWhiteSpace(BoardInfo.Bios)
                    ? BoardInfo.DisplayTitle
                    : $"{BoardInfo.DisplayTitle} • BIOS {BoardInfo.Bios}";
            }
        }

        public bool HasBoardSupportUrl => !string.IsNullOrWhiteSpace(BoardInfo.SupportUrl);

        public string DriverSearchText
        {
            get => _driverSearchText;
            set
            {
                if (SetField(ref _driverSearchText, value))
                    ApplyDriverFilter();
            }
        }

        public string DriverClassFilter
        {
            get => _driverClassFilter;
            set
            {
                if (SetField(ref _driverClassFilter, value))
                    ApplyDriverFilter();
            }
        }

        public bool ShowOnlyUnsigned
        {
            get => _showOnlyUnsigned;
            set
            {
                if (SetField(ref _showOnlyUnsigned, value))
                    ApplyDriverFilter();
            }
        }

        public bool IsRebootPending
        {
            get => _isRebootPending;
            private set => SetField(ref _isRebootPending, value);
        }

        public string LastBackupFolder
        {
            get => _lastBackupFolder;
            private set => SetField(ref _lastBackupFolder, value);
        }

        public int TotalDriversCount => _allDrivers.Count;
        public int ProblemsCount => DriverProblems.Count;
        public bool HasNoProblems => DriverProblems.Count == 0;
        public int SelectedUpdatesCount => WindowsUpdates.Count(u => u.IsSelected);
        public bool AreAllUpdatesSelected => WindowsUpdates.Count > 0 && WindowsUpdates.All(u => u.IsSelected);
        public string SelectAllUpdatesLabel => AreAllUpdatesSelected ? "Desmarcar Todos" : "Selecionar Todos";
        public string InstallUpdatesButtonLabel => SelectedUpdatesCount > 0
            ? $"⬇ Instalar Selecionadas ({SelectedUpdatesCount})"
            : "⬇ Instalar Selecionadas";

        public ICommand SetSubTabCommand { get; }
        public ICommand RefreshHardwareAndDriversCommand { get; }
        public ICommand SearchUpdatesCommand { get; }
        public ICommand InstallSelectedUpdatesCommand { get; }
        public ICommand ToggleSelectAllUpdatesCommand { get; }
        public ICommand OpenDeviceManagerCommand { get; }
        public ICommand OpenWindowsUpdateDriversCommand { get; }
        public ICommand OpenBoardSupportCommand { get; }
        public ICommand OpenGpuSupportCommand { get; }
        public ICommand CancelTaskCommand { get; }

        public void EnsureLoaded()
        {
            if (_loadedOnce || IsAnyBusy) return;
            _loadedOnce = true;
            _ = RefreshHardwareAndDriversAsync();
        }

        public async Task RefreshHardwareAndDriversAsync()
        {
            if (IsAnyBusy) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyDrivers = true;
            StatusBanner = "Lendo inventário de drivers assinados, dispositivos com erro, placa-mãe e GPU...";

            try
            {
                var snapshot = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var board = _service.ReadBoardInfo();
                    ct.ThrowIfCancellationRequested();
                    var gpus = _service.ReadGpus();
                    ct.ThrowIfCancellationRequested();
                    var problems = _service.ReadDriverProblems();
                    ct.ThrowIfCancellationRequested();
                    var drivers = _service.ReadInstalledDrivers();
                    bool reboot = _service.IsRebootPending();
                    return (board, gpus, problems, drivers, reboot);
                }, ct);

                BoardInfo = snapshot.board;
                IsRebootPending = snapshot.reboot;

                Gpus.Clear();
                foreach (var gpu in snapshot.gpus)
                    Gpus.Add(gpu);

                DriverProblems.Clear();
                foreach (var p in snapshot.problems)
                    DriverProblems.Add(p);

                _allDrivers.Clear();
                _allDrivers.AddRange(snapshot.drivers);

                var currentFilter = DriverClassFilter;
                ClassFilters.Clear();
                ClassFilters.Add("Todas");
                foreach (var cls in _allDrivers.Select(d => d.ClassLabel).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c))
                    ClassFilters.Add(cls);
                _driverClassFilter = ClassFilters.Contains(currentFilter) ? currentFilter : "Todas";
                OnPropertyChanged(nameof(DriverClassFilter));

                ApplyDriverFilter();
                OnPropertyChanged(nameof(TotalDriversCount));
                OnPropertyChanged(nameof(ProblemsCount));
                OnPropertyChanged(nameof(HasNoProblems));

                StatusBanner = snapshot.problems.Count > 0
                    ? $"Diagnóstico concluído: {snapshot.problems.Count} dispositivo(s) com alerta e {snapshot.drivers.Count} drivers catalogados."
                    : $"Tudo certo! Nenhum dispositivo com erro detectado ({snapshot.drivers.Count} drivers catalogados).";

                _log($"[Drivers] Inventário atualizado ({snapshot.drivers.Count} drivers, {snapshot.problems.Count} alertas).");
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Leitura de drivers cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro ao ler drivers: {ex.Message}";
                _log($"[Drivers] Erro: {ex.Message}");
            }
            finally
            {
                IsBusyDrivers = false;
            }
        }

        public async Task BackupDriversAsync(string destinationBaseFolder)
        {
            if (IsAnyBusy || string.IsNullOrWhiteSpace(destinationBaseFolder))
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyDrivers = true;
            StatusBanner = "Exportando drivers de terceiros via pnputil...";
            bool completed = false;

            try
            {
                var res = await _service.BackupDriversAsync(
                    destinationBaseFolder,
                    (msg, _) => UpdateOnUi(() => { if (!completed) StatusBanner = msg; }),
                    ct);

                completed = true;
                if (res.Success)
                {
                    LastBackupFolder = res.ExportFolder;
                    StatusBanner = res.Message;
                    _log($"[Drivers] Backup salvo em {res.ExportFolder}");
                }
                else
                {
                    StatusBanner = res.Message;
                    _log($"[Drivers] Falha no backup: {res.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                completed = true;
                StatusBanner = "Backup de drivers cancelado.";
            }
            catch (Exception ex)
            {
                completed = true;
                StatusBanner = $"Erro no backup de drivers: {ex.Message}";
            }
            finally
            {
                IsBusyDrivers = false;
            }
        }

        public async Task ImportDriversAsync(string driversFolder)
        {
            if (IsAnyBusy || string.IsNullOrWhiteSpace(driversFolder))
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyDrivers = true;
            StatusBanner = "Importando e instalando drivers (.inf) da pasta selecionada...";
            bool completed = false;

            try
            {
                var res = await _service.ImportDriversAsync(
                    driversFolder,
                    (msg, _) => UpdateOnUi(() => { if (!completed) StatusBanner = msg; }),
                    ct);

                completed = true;
                IsRebootPending = _service.IsRebootPending() || res.RebootRequired;
                StatusBanner = res.Message;
                _log($"[Drivers] Importação: {res.Message}");
            }
            catch (OperationCanceledException)
            {
                completed = true;
                StatusBanner = "Importação de drivers cancelada.";
            }
            catch (Exception ex)
            {
                completed = true;
                StatusBanner = $"Erro ao importar drivers: {ex.Message}";
            }
            finally
            {
                IsBusyDrivers = false;
            }
        }

        public async Task SearchUpdatesAsync()
        {
            if (IsAnyBusy) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyUpdates = true;
            UpdateStageText = "Consultando servidor do Windows Update...";
            StatusBanner = "Buscando atualizações pendentes no Windows Update...";

            try
            {
                var items = await _service.SearchWindowsUpdatesAsync(ct);

                foreach (var existing in WindowsUpdates)
                    existing.PropertyChanged -= OnUpdateItemPropertyChanged;
                WindowsUpdates.Clear();

                foreach (var item in items)
                {
                    item.PropertyChanged += OnUpdateItemPropertyChanged;
                    WindowsUpdates.Add(item);
                }

                IsRebootPending = _service.IsRebootPending();
                RaiseUpdateSelectionProperties();

                StatusBanner = items.Count == 0
                    ? "Seu Windows já está atualizado! Nenhuma atualização pendente encontrada."
                    : $"{items.Count} atualização(ões) disponível(is) no Windows Update.";
                UpdateStageText = items.Count == 0 ? "Nenhuma pendência." : "Pronto para instalar.";

                _log($"[WindowsUpdate] {items.Count} atualização(ões) encontrada(s).");
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Busca no Windows Update cancelada.";
                UpdateStageText = "Cancelado.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Falha ao consultar Windows Update: {ex.Message}";
                UpdateStageText = "Erro na consulta.";
                _log($"[WindowsUpdate] Erro: {ex.Message}");
            }
            finally
            {
                IsBusyUpdates = false;
            }
        }

        public async Task InstallSelectedUpdatesAsync()
        {
            var selected = WindowsUpdates.Where(u => u.IsSelected).ToList();
            if (IsAnyBusy || selected.Count == 0) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyUpdates = true;
            StatusBanner = $"Iniciando download e instalação de {selected.Count} atualização(ões)...";
            bool completed = false;

            try
            {
                var result = await _service.DownloadAndInstallUpdatesAsync(
                    selected,
                    stage => UpdateOnUi(() =>
                    {
                        if (completed) return;
                        UpdateStageText = stage;
                        StatusBanner = stage;
                    }),
                    ct);

                completed = true;
                IsRebootPending = _service.IsRebootPending() || result.RebootRequired;
                StatusBanner = result.Message;
                UpdateStageText = result.RebootRequired ? "Reinicialização recomendada." : "Concluído.";
                _log($"[WindowsUpdate] {result.Message}");

                if (result.Success)
                {
                    foreach (var s in selected)
                    {
                        s.PropertyChanged -= OnUpdateItemPropertyChanged;
                        WindowsUpdates.Remove(s);
                    }
                    RaiseUpdateSelectionProperties();
                }
            }
            catch (OperationCanceledException)
            {
                completed = true;
                StatusBanner = "Instalação de atualizações cancelada.";
                UpdateStageText = "Cancelado.";
            }
            catch (Exception ex)
            {
                completed = true;
                StatusBanner = $"Erro ao instalar atualizações: {ex.Message}";
                UpdateStageText = "Erro.";
            }
            finally
            {
                IsBusyUpdates = false;
            }
        }

        private void ToggleSelectAllUpdates()
        {
            bool target = !AreAllUpdatesSelected;
            foreach (var item in WindowsUpdates)
                item.IsSelected = target;
            RaiseUpdateSelectionProperties();
        }

        private void OnUpdateItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WindowsUpdateItemModel.IsSelected))
                RaiseUpdateSelectionProperties();
        }

        private void RaiseUpdateSelectionProperties()
        {
            OnPropertyChanged(nameof(SelectedUpdatesCount));
            OnPropertyChanged(nameof(AreAllUpdatesSelected));
            OnPropertyChanged(nameof(SelectAllUpdatesLabel));
            OnPropertyChanged(nameof(InstallUpdatesButtonLabel));
            InvalidateCommands();
        }

        private void ApplyDriverFilter()
        {
            FilteredDrivers.Clear();
            var query = DriverSearchText?.Trim() ?? "";
            var cls = DriverClassFilter;

            foreach (var d in _allDrivers)
            {
                if (ShowOnlyUnsigned && d.IsSigned)
                    continue;
                if (!string.IsNullOrWhiteSpace(cls) && cls != "Todas" && !string.Equals(d.ClassLabel, cls, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(query) &&
                    !d.Device.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !d.Vendor.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !d.Version.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                FilteredDrivers.Add(d);
            }
        }

        public void CancelActiveTask()
        {
            try { _cts?.Cancel(); }
            catch { }
        }

        private void InvalidateCommands()
        {
            (RefreshHardwareAndDriversCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SearchUpdatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (InstallSelectedUpdatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleSelectAllUpdatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenBoardSupportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private static void UpdateOnUi(Action action)
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null || disp.CheckAccess())
                action();
            else
                disp.BeginInvoke(action);
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
