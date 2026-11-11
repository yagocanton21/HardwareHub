using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class FormatBackupViewModel : INotifyPropertyChanged
    {
        private readonly FormatBackupService _service;
        private readonly Action<string> _log;
        private CancellationTokenSource? _cts;

        private string _activeSubTab = "Create"; // "Create" | "Restore"
        private string _destinationFolder = "";
        private bool _includeDrivers = true;
        private bool _includeWifi = true;
        private bool _isBusyBackup;
        private bool _scannedOnce;
        private FoundFormatBackupModel? _selectedBackup;
        private string _statusBanner = "Salve seus drivers e senhas de Wi-Fi em um pendrive ou HD externo antes de formatar o PC.";
        private string _lastCreatedBackupPath = "";

        public ObservableCollection<FoundFormatBackupModel> FoundBackups { get; } = new();

        public FormatBackupViewModel(
            FormatBackupService? service = null,
            Action<string>? logCallback = null)
        {
            _service = service ?? new FormatBackupService();
            _log = logCallback ?? (_ => { });

            SetSubTabCommand = new RelayCommand(p => ActiveSubTab = p?.ToString() ?? "Create");
            CreateBackupCommand = new RelayCommand(
                _ => _ = CreateBackupAsync(DestinationFolder),
                _ => !IsBusyBackup && !string.IsNullOrWhiteSpace(DestinationFolder) && (IncludeDrivers || IncludeWifi));
            ScanBackupsCommand = new RelayCommand(_ => _ = ScanBackupsAsync(), _ => !IsBusyBackup);
            RestoreDriversCommand = new RelayCommand(
                _ => _ = RestoreDriversAsync(),
                _ => !IsBusyBackup && SelectedBackup != null && SelectedBackup.InfCount > 0);
            RestoreWifiCommand = new RelayCommand(
                _ => _ = RestoreWifiAsync(),
                _ => !IsBusyBackup && SelectedBackup != null && SelectedBackup.WifiProfiles.Count > 0);
            CancelTaskCommand = new RelayCommand(_ => CancelActiveTask(), _ => IsBusyBackup);
        }

        public string ActiveSubTab
        {
            get => _activeSubTab;
            set
            {
                if (SetField(ref _activeSubTab, value))
                {
                    OnPropertyChanged(nameof(IsCreateTabActive));
                    OnPropertyChanged(nameof(IsRestoreTabActive));
                    if (value == "Restore" && !_scannedOnce)
                        _ = ScanBackupsAsync();
                }
            }
        }

        public bool IsCreateTabActive => ActiveSubTab == "Create";
        public bool IsRestoreTabActive => ActiveSubTab == "Restore";

        public string DestinationFolder
        {
            get => _destinationFolder;
            set
            {
                if (SetField(ref _destinationFolder, value))
                {
                    OnPropertyChanged(nameof(IsOnWindowsDriveWarning));
                    InvalidateCommands();
                }
            }
        }

        public bool IsOnWindowsDriveWarning =>
            !string.IsNullOrWhiteSpace(DestinationFolder) && FormatBackupService.IsOnWindowsDrive(DestinationFolder);

        public bool IncludeDrivers
        {
            get => _includeDrivers;
            set
            {
                if (SetField(ref _includeDrivers, value))
                    InvalidateCommands();
            }
        }

        public bool IncludeWifi
        {
            get => _includeWifi;
            set
            {
                if (SetField(ref _includeWifi, value))
                    InvalidateCommands();
            }
        }

        public bool IsBusyBackup
        {
            get => _isBusyBackup;
            private set
            {
                if (SetField(ref _isBusyBackup, value))
                    InvalidateCommands();
            }
        }

        public string StatusBanner
        {
            get => _statusBanner;
            private set => SetField(ref _statusBanner, value);
        }

        public string LastCreatedBackupPath
        {
            get => _lastCreatedBackupPath;
            private set => SetField(ref _lastCreatedBackupPath, value);
        }

        public FoundFormatBackupModel? SelectedBackup
        {
            get => _selectedBackup;
            set
            {
                if (SetField(ref _selectedBackup, value))
                {
                    OnPropertyChanged(nameof(HasSelectedBackup));
                    OnPropertyChanged(nameof(HasHardwareWarning));
                    InvalidateCommands();
                }
            }
        }

        public bool HasSelectedBackup => SelectedBackup != null;
        public bool HasHardwareWarning => SelectedBackup?.IsDifferentHardware == true;
        public bool HasNoFoundBackups => FoundBackups.Count == 0;

        public ICommand SetSubTabCommand { get; }
        public ICommand CreateBackupCommand { get; }
        public ICommand ScanBackupsCommand { get; }
        public ICommand RestoreDriversCommand { get; }
        public ICommand RestoreWifiCommand { get; }
        public ICommand CancelTaskCommand { get; }

        public async Task CreateBackupAsync(string? destinationRoot = null)
        {
            var root = string.IsNullOrWhiteSpace(destinationRoot) ? DestinationFolder : destinationRoot;
            if (IsBusyBackup || string.IsNullOrWhiteSpace(root))
                return;

            DestinationFolder = root;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyBackup = true;
            StatusBanner = "Preparando pacote de backup pré-formatação...";
            bool completed = false;

            try
            {
                var res = await _service.CreateBackupAsync(
                    root,
                    IncludeDrivers,
                    IncludeWifi,
                    (msg, _) => UpdateOnUi(() => { if (!completed) StatusBanner = msg; }),
                    ct);

                completed = true;
                StatusBanner = res.Message;
                if (res.Success)
                {
                    LastCreatedBackupPath = res.BackupFolder;
                    _log($"[FormatBackup] Backup criado em {res.BackupFolder}");
                }
                else
                {
                    _log($"[FormatBackup] Aviso: {res.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                completed = true;
                StatusBanner = "Criação do backup cancelada.";
            }
            catch (Exception ex)
            {
                completed = true;
                StatusBanner = $"Erro ao criar backup: {ex.Message}";
            }
            finally
            {
                IsBusyBackup = false;
            }
        }

        public async Task ScanBackupsAsync()
        {
            if (IsBusyBackup) return;

            _scannedOnce = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyBackup = true;
            StatusBanner = "Procurando pastas HUB-Backup-* nos discos disponíveis...";

            try
            {
                var found = await Task.Run(() => _service.ScanForBackups(ct), ct);

                FoundBackups.Clear();
                foreach (var item in found)
                    FoundBackups.Add(item);

                if (SelectedBackup == null && FoundBackups.Count > 0)
                    SelectedBackup = FoundBackups[0];

                OnPropertyChanged(nameof(HasNoFoundBackups));
                StatusBanner = found.Count > 0
                    ? $"{found.Count} backup(s) encontrado(s). Selecione um abaixo para restaurar."
                    : "Nenhum backup automático encontrado. Use 'Selecionar Pasta' para abrir manualmente.";
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Varredura cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro na busca: {ex.Message}";
            }
            finally
            {
                IsBusyBackup = false;
            }
        }

        public bool LoadBackupFromFolder(string folderPath)
        {
            var loaded = _service.TryLoadBackup(folderPath, out var error);
            if (loaded == null)
            {
                StatusBanner = error;
                return false;
            }

            var existing = FoundBackups.FirstOrDefault(b =>
                string.Equals(b.FolderPath, loaded.FolderPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                FoundBackups.Remove(existing);

            FoundBackups.Insert(0, loaded);
            SelectedBackup = loaded;
            OnPropertyChanged(nameof(HasNoFoundBackups));
            StatusBanner = $"Backup carregado: {loaded.Title} ({loaded.ContentsSummary}).";
            return true;
        }

        public async Task RestoreDriversAsync()
        {
            if (IsBusyBackup || SelectedBackup == null) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyBackup = true;
            StatusBanner = "Restaurando pacote de drivers...";
            bool completed = false;

            try
            {
                var res = await _service.RestoreDriversAsync(
                    SelectedBackup.DriversPath,
                    (msg, _) => UpdateOnUi(() => { if (!completed) StatusBanner = msg; }),
                    ct);

                completed = true;
                StatusBanner = res.Message;
                _log($"[FormatBackup] Restauração de drivers: {res.Message}");
            }
            catch (OperationCanceledException)
            {
                completed = true;
                StatusBanner = "Restauração de drivers cancelada.";
            }
            catch (Exception ex)
            {
                completed = true;
                StatusBanner = $"Erro ao restaurar drivers: {ex.Message}";
            }
            finally
            {
                IsBusyBackup = false;
            }
        }

        public async Task RestoreWifiAsync()
        {
            if (IsBusyBackup || SelectedBackup == null) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyBackup = true;
            StatusBanner = "Importando perfis e senhas de Wi-Fi...";
            bool completed = false;

            try
            {
                var res = await _service.RestoreWifiAsync(
                    SelectedBackup.WifiProfiles,
                    (msg, _) => UpdateOnUi(() => { if (!completed) StatusBanner = msg; }),
                    ct);

                completed = true;
                StatusBanner = res.Message;
                _log($"[FormatBackup] Restauração Wi-Fi: {res.Message}");
            }
            catch (OperationCanceledException)
            {
                completed = true;
                StatusBanner = "Restauração de Wi-Fi cancelada.";
            }
            catch (Exception ex)
            {
                completed = true;
                StatusBanner = $"Erro ao restaurar Wi-Fi: {ex.Message}";
            }
            finally
            {
                IsBusyBackup = false;
            }
        }

        public void CancelActiveTask()
        {
            try { _cts?.Cancel(); }
            catch { }
        }

        private void InvalidateCommands()
        {
            (CreateBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ScanBackupsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RestoreDriversCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RestoreWifiCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

