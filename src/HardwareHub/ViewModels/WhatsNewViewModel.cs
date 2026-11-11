using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public class WhatsNewViewModel : INotifyPropertyChanged
    {
        private readonly WhatsNewService _service;
        private readonly Func<Task>? _checkUpdatesAsync;
        private readonly Func<Task>? _applyUpdateAsync;
        private readonly Action<string>? _logCallback;
        private readonly Version _currentVersion;

        public ObservableCollection<VersionReleaseNotes> EmbeddedVersions { get; } = new();

        public WhatsNewViewModel(
            WhatsNewService? service = null,
            Func<Task>? checkUpdatesAsync = null,
            Func<Task>? applyUpdateAsync = null,
            Action<string>? logCallback = null)
        {
            _service = service ?? new WhatsNewService();
            _checkUpdatesAsync = checkUpdatesAsync;
            _applyUpdateAsync = applyUpdateAsync;
            _logCallback = logCallback;
            _currentVersion = _service.GetCurrentVersion();

            foreach (var item in _service.LoadEmbeddedNotes())
            {
                EmbeddedVersions.Add(item);
            }

            CheckForUpdatesCommand = new RelayCommand(async _ => await CheckForUpdatesAsync(), _ => !IsCheckingUpdates);
            ApplyUpdateCommand = new RelayCommand(async _ => await ApplyUpdateInternalAsync(), _ => CanApplyUpdate);
            OpenReleasesPageCommand = new RelayCommand(_ => OpenReleasesPage());
        }

        public string CurrentVersionLabel => $"v{_currentVersion.ToString(3)}";

        private VersionReleaseNotes? _pendingUpdateNotes;
        public VersionReleaseNotes? PendingUpdateNotes
        {
            get => _pendingUpdateNotes;
            private set
            {
                _pendingUpdateNotes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPendingUpdate));
                OnPropertyChanged(nameof(CanApplyUpdate));
                OnPropertyChanged(nameof(PendingVersionTitle));
            }
        }

        public bool HasPendingUpdate => PendingUpdateNotes != null;

        public string PendingVersionTitle =>
            PendingUpdateNotes != null
                ? $"Nova versão v{PendingUpdateNotes.Version} disponível!"
                : string.Empty;

        private string _pendingUpdateSizeText = string.Empty;
        public string PendingUpdateSizeText
        {
            get => _pendingUpdateSizeText;
            private set { _pendingUpdateSizeText = value; OnPropertyChanged(); }
        }

        private bool _isCheckingUpdates;
        public bool IsCheckingUpdates
        {
            get => _isCheckingUpdates;
            private set
            {
                if (_isCheckingUpdates != value)
                {
                    _isCheckingUpdates = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CheckButtonText));
                }
            }
        }

        public string CheckButtonText => IsCheckingUpdates ? "⏳ Verificando..." : "🔄 Verificar Atualizações";

        private bool _isDownloadingUpdate;
        public bool IsDownloadingUpdate
        {
            get => _isDownloadingUpdate;
            private set
            {
                if (_isDownloadingUpdate != value)
                {
                    _isDownloadingUpdate = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanApplyUpdate));
                }
            }
        }

        private double _downloadProgress;
        public double DownloadProgress
        {
            get => _downloadProgress;
            private set { _downloadProgress = value; OnPropertyChanged(); }
        }

        private string _applyUpdateButtonText = "⚡ Atualizar Agora";
        public string ApplyUpdateButtonText
        {
            get => _applyUpdateButtonText;
            private set { _applyUpdateButtonText = value; OnPropertyChanged(); }
        }

        public bool CanApplyUpdate => HasPendingUpdate && !IsDownloadingUpdate && _applyUpdateAsync != null;

        private string _updateStatusMessage = string.Empty;
        public string UpdateStatusMessage
        {
            get => _updateStatusMessage;
            private set
            {
                _updateStatusMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUpdateStatusMessage));
            }
        }

        public bool HasUpdateStatusMessage => !string.IsNullOrWhiteSpace(UpdateStatusMessage) && !HasPendingUpdate;

        public void SyncPendingUpdate(
            UpdateInfo? update,
            bool isDownloading = false,
            double downloadProgress = 0,
            string? applyButtonText = null)
        {
            IsDownloadingUpdate = isDownloading;
            DownloadProgress = downloadProgress;
            ApplyUpdateButtonText = string.IsNullOrWhiteSpace(applyButtonText) ? "⚡ Atualizar Agora" : applyButtonText;

            if (update != null && update.IsAvailable && !string.IsNullOrWhiteSpace(update.LatestVersion))
            {
                PendingUpdateNotes = _service.ParseGitHubReleaseBody(
                    update.LatestVersion,
                    DateTime.Now.ToString("dd/MM/yyyy"),
                    update.Changelog);
                PendingUpdateSizeText = update.FormattedSize;
                UpdateStatusMessage = string.Empty;
            }
            else
            {
                PendingUpdateNotes = null;
                PendingUpdateSizeText = string.Empty;
                if (update != null && !string.IsNullOrWhiteSpace(update.ErrorMessage))
                {
                    UpdateStatusMessage = update.ErrorMessage;
                }
                else if (update != null)
                {
                    UpdateStatusMessage = $"Você já está executando a versão mais recente ({CurrentVersionLabel}).";
                }
            }
        }

        public PostUpdateCheckResult EvaluatePostUpdate(
            string? markerPathOverride = null,
            Version? currentVersionOverride = null)
        {
            return _service.CheckAndMarkPostUpdate(markerPathOverride, currentVersionOverride);
        }

        public async Task CheckForUpdatesAsync()
        {
            if (IsCheckingUpdates || _checkUpdatesAsync == null)
                return;

            try
            {
                IsCheckingUpdates = true;
                UpdateStatusMessage = "Consultando GitHub Releases...";
                await _checkUpdatesAsync();
            }
            catch (Exception ex)
            {
                UpdateStatusMessage = $"Erro ao verificar atualizações: {ex.Message}";
                _logCallback?.Invoke($"[NOVIDADES] Erro ao consultar atualizações: {ex.Message}");
            }
            finally
            {
                IsCheckingUpdates = false;
            }
        }

        private async Task ApplyUpdateInternalAsync()
        {
            if (_applyUpdateAsync == null || !CanApplyUpdate)
                return;

            await _applyUpdateAsync();
        }

        private void OpenReleasesPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/yagocanton21/HardwareHub/releases",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logCallback?.Invoke($"[NOVIDADES] Não foi possível abrir o navegador: {ex.Message}");
            }
        }

        public ICommand CheckForUpdatesCommand { get; }
        public ICommand ApplyUpdateCommand { get; }
        public ICommand OpenReleasesPageCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

