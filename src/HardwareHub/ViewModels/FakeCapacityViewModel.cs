using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class CapacityPointVisualItem
    {
        public int Index { get; init; }
        public CapacityPointState State { get; init; }

        public string Color => State switch
        {
            CapacityPointState.Ok => "#10B981",
            CapacityPointState.Written => "#6366F1",
            CapacityPointState.Wrapped => "#EF4444",
            CapacityPointState.Lost => "#F59E0B",
            CapacityPointState.Error => "#DC2626",
            _ => "#27272A"
        };

        public string Tooltip => State switch
        {
            CapacityPointState.Ok => $"Bloco #{Index + 1}: Íntegro",
            CapacityPointState.Written => $"Bloco #{Index + 1}: Gravado",
            CapacityPointState.Wrapped => $"Bloco #{Index + 1}: Sobreposição detectada (Falso)",
            CapacityPointState.Lost => $"Bloco #{Index + 1}: Dados perdidos",
            CapacityPointState.Error => $"Bloco #{Index + 1}: Erro de leitura/gravação",
            _ => $"Bloco #{Index + 1}: Aguardando"
        };
    }

    public sealed class FakeCapacityViewModel : INotifyPropertyChanged
    {
        private readonly FakeCapacityService _service;
        private readonly Func<List<RemovableDriveCandidate>> _listDrivesHook;
        private readonly Func<RemovableDriveCandidate, int, IProgress<FakeCapacityProgress>, CancellationToken, Task<FakeCapacityReport>>? _customProbeHook;
        private readonly Action<string> _log;
        private CancellationTokenSource? _cts;

        private bool _loadedOnce;
        private bool _isBusyProbe;
        private RemovableDriveCandidate? _selectedDrive;
        private int _sampleSizeMb = 128;
        private double _progressPercent;
        private string _progressStage = "Selecione um pendrive, cartão SD ou SSD USB externo e clique em Iniciar Verificação.";
        private FakeCapacityReport? _lastReport;

        public static readonly int[] SampleSizePresetsMb = { 32, 128, 512 };

        public ObservableCollection<RemovableDriveCandidate> AvailableDrives { get; } = new();
        public ObservableCollection<CapacityPointVisualItem> PointVisuals { get; } = new();

        public FakeCapacityViewModel(
            FakeCapacityService? service = null,
            Func<List<RemovableDriveCandidate>>? listDrivesHook = null,
            Func<RemovableDriveCandidate, int, IProgress<FakeCapacityProgress>, CancellationToken, Task<FakeCapacityReport>>? customProbeHook = null,
            Action<string>? logCallback = null)
        {
            _service = service ?? new FakeCapacityService();
            _listDrivesHook = listDrivesHook ?? (() => _service.ListRemovableDrives());
            _customProbeHook = customProbeHook;
            _log = logCallback ?? (_ => { });

            RefreshDrivesCommand = new RelayCommand(_ => _ = RefreshDrivesAsync(), _ => !IsBusyProbe);
            StartProbeCommand = new RelayCommand(_ => _ = StartProbeAsync(), _ => !IsBusyProbe && SelectedDrive?.CanTest == true);
            CancelProbeCommand = new RelayCommand(_ => CancelProbe(), _ => IsBusyProbe);
            SetSampleSizeCommand = new RelayCommand(p =>
            {
                if (int.TryParse(p?.ToString(), out var mb) && mb > 0)
                    SampleSizeMb = mb;
            }, _ => !IsBusyProbe);
        }

        public bool IsBusyProbe
        {
            get => _isBusyProbe;
            private set
            {
                if (SetField(ref _isBusyProbe, value))
                    InvalidateCommands();
            }
        }

        public RemovableDriveCandidate? SelectedDrive
        {
            get => _selectedDrive;
            set
            {
                if (SetField(ref _selectedDrive, value))
                {
                    OnPropertyChanged(nameof(HasSelectedDrive));
                    OnPropertyChanged(nameof(SelectedDriveBlockReason));
                    OnPropertyChanged(nameof(HasSelectedDriveBlockReason));
                    InvalidateCommands();
                }
            }
        }

        public bool HasSelectedDrive => SelectedDrive != null;
        public bool HasNoDrives => AvailableDrives.Count == 0;
        public string SelectedDriveBlockReason => SelectedDrive?.BlockReason ?? "";
        public bool HasSelectedDriveBlockReason => !string.IsNullOrWhiteSpace(SelectedDrive?.BlockReason);

        public int SampleSizeMb
        {
            get => _sampleSizeMb;
            set
            {
                if (SetField(ref _sampleSizeMb, value))
                {
                    OnPropertyChanged(nameof(IsPreset32Selected));
                    OnPropertyChanged(nameof(IsPreset128Selected));
                    OnPropertyChanged(nameof(IsPreset512Selected));
                }
            }
        }

        public bool IsPreset32Selected => SampleSizeMb == 32;
        public bool IsPreset128Selected => SampleSizeMb == 128;
        public bool IsPreset512Selected => SampleSizeMb == 512;

        public double ProgressPercent
        {
            get => _progressPercent;
            private set => SetField(ref _progressPercent, value);
        }

        public string ProgressStage
        {
            get => _progressStage;
            private set => SetField(ref _progressStage, value);
        }

        public FakeCapacityReport? LastReport
        {
            get => _lastReport;
            private set
            {
                if (SetField(ref _lastReport, value))
                {
                    OnPropertyChanged(nameof(HasReport));
                    OnPropertyChanged(nameof(SpeedSummaryText));
                    OnPropertyChanged(nameof(StatsSummaryText));
                    OnPropertyChanged(nameof(RealCapacityFormatted));
                    RebuildPointVisuals(value?.PointStates);
                }
            }
        }

        public bool HasReport => LastReport != null;

        public string RealCapacityFormatted
        {
            get
            {
                if (LastReport == null) return "—";
                if (LastReport.Verdict == FakeCapacityVerdict.Inconclusive) return "Não avaliada (interrompido)";
                if (LastReport.Verdict == FakeCapacityVerdict.Defective && LastReport.RealBytes == 0) return "Não avaliada (falha de hardware/E/S)";
                return LastReport.RealBytes > 0 ? InstalledProgramItem.FormatBytes(LastReport.RealBytes) : "—";
            }
        }

        public string SpeedSummaryText => LastReport == null
            ? ""
            : $"Escrita: {LastReport.WriteSpeedMBps:0.0} MB/s • Leitura: {LastReport.ReadSpeedMBps:0.0} MB/s • Duração: {FormatDuration(LastReport.Duration)}";

        public static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes >= 1)
                return $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s";
            return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))}s";
        }

        public string StatsSummaryText => LastReport == null
            ? ""
            : $"{LastReport.PointsOk} blocos íntegros • {LastReport.PointsBad} falhas/sobreposições • {(LastReport.OriginalDataRestored ? "Dados intactos" : "Verifique integridade")}";

        public ICommand RefreshDrivesCommand { get; }
        public ICommand StartProbeCommand { get; }
        public ICommand CancelProbeCommand { get; }
        public ICommand SetSampleSizeCommand { get; }

        public void EnsureLoaded()
        {
            if (_loadedOnce || IsBusyProbe) return;
            _loadedOnce = true;
            _ = RefreshDrivesAsync();
        }

        public async Task RefreshDrivesAsync()
        {
            if (IsBusyProbe) return;

            ProgressStage = "Identificando unidades USB e cartões conectados...";
            try
            {
                var list = await Task.Run(_listDrivesHook);
                AvailableDrives.Clear();
                foreach (var d in list)
                    AvailableDrives.Add(d);

                SelectedDrive = AvailableDrives.FirstOrDefault(d => d.CanTest) ?? AvailableDrives.FirstOrDefault();
                OnPropertyChanged(nameof(HasNoDrives));

                ProgressStage = list.Count > 0
                    ? $"{list.Count} unidade(s) USB/removível(is) encontrada(s)."
                    : "Nenhum pendrive ou disco USB externo conectado.";
            }
            catch (Exception ex)
            {
                ProgressStage = $"Erro ao listar discos removíveis: {ex.Message}";
            }
        }

        public async Task StartProbeAsync()
        {
            if (IsBusyProbe || SelectedDrive == null || !SelectedDrive.CanTest)
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsBusyProbe = true;
            ProgressPercent = 0;
            LastReport = null;
            ProgressStage = $"Iniciando verificação em {SelectedDrive.Model}...";
            bool completed = false;

            var progress = new Progress<FakeCapacityProgress>(p =>
            {
                UpdateOnUi(() =>
                {
                    if (completed) return;
                    ProgressStage = p.Stage;
                    if (p.Ratio.HasValue)
                        ProgressPercent = Math.Clamp(p.Ratio.Value * 100.0, 0, 100);
                });
            });

            try
            {
                FakeCapacityReport report;
                if (_customProbeHook != null)
                {
                    report = await _customProbeHook(SelectedDrive, SampleSizeMb, progress, ct);
                }
                else if (SelectedDrive.MountLetters.Length > 0)
                {
                    report = await _service.ExecuteMountedVolumeProbeAsync(
                        SelectedDrive,
                        SelectedDrive.MountLetters[0],
                        SampleSizeMb,
                        progress,
                        ct);
                }
                else
                {
                    report = await Task.Run(
                        () => _service.ExecuteBlockCapacityTest(SelectedDrive, new FakeCapacityOptions(), progress, ct),
                        ct);
                }

                completed = true;
                LastReport = report;
                ProgressPercent = 100;
                ProgressStage = report.ErrorMessage ?? report.VerdictLabel;
                _log($"[FakeCapacity] {SelectedDrive.Model}: {report.VerdictLabel}");
            }
            catch (OperationCanceledException)
            {
                completed = true;
                ProgressStage = "Teste de capacidade cancelado pelo usuário.";
            }
            catch (Exception ex)
            {
                completed = true;
                ProgressStage = $"Falha no teste: {ex.Message}";
            }
            finally
            {
                IsBusyProbe = false;
            }
        }

        public void CancelProbe()
        {
            try { _cts?.Cancel(); }
            catch { }
        }

        private void RebuildPointVisuals(CapacityPointState[]? states)
        {
            PointVisuals.Clear();
            if (states == null || states.Length == 0)
                return;

            for (int i = 0; i < states.Length; i++)
            {
                PointVisuals.Add(new CapacityPointVisualItem
                {
                    Index = i,
                    State = states[i]
                });
            }
        }

        private void InvalidateCommands()
        {
            (RefreshDrivesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartProbeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelProbeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SetSampleSizeCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

