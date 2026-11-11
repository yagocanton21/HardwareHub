using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class Windows11ViewModel : INotifyPropertyChanged
    {
        private readonly Windows11Service _service;
        private readonly Action<string> _log;
        private CancellationTokenSource? _cts;

        private bool _loadedOnce;
        private bool _isBusy;
        private Windows11Report? _report;
        private string _statusBanner = "Clique em 'Verificar Compatibilidade' para checar TPM 2.0, UEFI, Secure Boot, CPU e instruções 24H2.";

        public ObservableCollection<Windows11RequirementItem> Requirements { get; } = new();

        public Windows11ViewModel(
            Windows11Service? service = null,
            Action<string>? logCallback = null)
        {
            _service = service ?? new Windows11Service();
            _log = logCallback ?? (_ => { });

            VerifyCommand = new RelayCommand(_ => _ = VerifyAsync(), _ => !IsBusy);
            CancelCommand = new RelayCommand(_ => CancelActiveTask(), _ => IsBusy);
            OpenMsInfoCommand = new RelayCommand(_ => LaunchSystemTool("msinfo32.exe"));
            OpenTpmConsoleCommand = new RelayCommand(_ => LaunchSystemTool("tpm.msc"));
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

        public Windows11Report? Report
        {
            get => _report;
            private set
            {
                if (SetField(ref _report, value))
                {
                    OnPropertyChanged(nameof(HasReport));
                    OnPropertyChanged(nameof(SummarySubtitle));
                }
            }
        }

        public bool HasReport => Report != null;

        public string SummarySubtitle => Report == null
            ? ""
            : $"{Report.WindowsEdition} • {Report.PassedCount}/{Report.Items.Count} requisitos atendidos";

        public string StatusBanner
        {
            get => _statusBanner;
            private set => SetField(ref _statusBanner, value);
        }

        public ICommand VerifyCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenMsInfoCommand { get; }
        public ICommand OpenTpmConsoleCommand { get; }

        public void EnsureLoaded()
        {
            if (_loadedOnce || IsBusy) return;
            _loadedOnce = true;
            _ = VerifyAsync();
        }

        public async Task VerifyAsync()
        {
            if (IsBusy) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusy = true;
            StatusBanner = "Consultando CPU, firmware UEFI, Secure Boot, TPM 2.0, RAM, disco e DirectX 12...";

            try
            {
                var rep = await _service.VerifyAsync(ct);

                Requirements.Clear();
                foreach (var item in rep.Items)
                    Requirements.Add(item);

                Report = rep;
                StatusBanner = rep.VerdictTitle;
                _log($"[Win11] {rep.VerdictTitle} ({rep.PassedCount}/{rep.Items.Count}).");
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Verificação do Windows 11 cancelada.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Erro ao verificar requisitos: {ex.Message}";
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

        private static void LaunchSystemTool(string exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe))
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private void InvalidateCommands()
        {
            (VerifyCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

