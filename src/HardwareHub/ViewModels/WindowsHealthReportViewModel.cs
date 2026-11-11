using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class WindowsHealthReportViewModel : INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        private readonly PcInventoryService _inventoryService;
        private readonly WindowsHealthRepairService _healthRepairService;
        private readonly ReportExportService _reportService;
        private readonly MemoryRamService _memoryService;
        private readonly DiskOptimizationService _diskOptService;
        private readonly Action<string> _logCallback;
        private readonly Action<string> _navigateCallback;

        private CancellationTokenSource? _cts;
        private SystemInventoryReport? _lastInventory;
        private WindowsHealthReport? _lastHealthReport;

        private string _activeSubTab = "Health";
        private bool _isBusy;
        private string _statusMessage = "Pronto para diagnosticar saúde do Windows e inventário técnico.";
        private string _repairLogOutput = string.Empty;
        private string _reportSummaryPreview = string.Empty;
        private string _ramCleanupBanner = string.Empty;

        private string _systemSummary = "—";
        private string _motherboardSummary = "—";
        private string _cpuSummary = "—";
        private string _memorySummary = "—";

        public ObservableCollection<WindowsHealthCheckItem> HealthItems { get; } = new();
        public ObservableCollection<SystemSuggestionItem> Suggestions { get; } = new();
        public ObservableCollection<RepairStepDefinition> RepairSteps { get; } = new();

        public ObservableCollection<PcInfoLine> SystemLines { get; } = new();
        public ObservableCollection<PcInfoLine> MotherboardLines { get; } = new();
        public ObservableCollection<PcInfoLine> CpuLines { get; } = new();
        public ObservableCollection<PcInfoLine> MemoryLines { get; } = new();
        public ObservableCollection<RamModuleDetailModel> RamModules { get; } = new();
        public ObservableCollection<GpuInventoryDetailModel> VideoAdapters { get; } = new();

        public string ActiveSubTab
        {
            get => _activeSubTab;
            set
            {
                if (SetProperty(ref _activeSubTab, value))
                {
                    OnPropertyChanged(nameof(IsHealthTab));
                    OnPropertyChanged(nameof(IsRepairsTab));
                    OnPropertyChanged(nameof(IsInventoryTab));
                    OnPropertyChanged(nameof(IsReportTab));
                }
            }
        }

        public bool IsHealthTab => ActiveSubTab == "Health";
        public bool IsRepairsTab => ActiveSubTab == "Repairs";
        public bool IsInventoryTab => ActiveSubTab == "Inventory";
        public bool IsReportTab => ActiveSubTab == "Report";

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    OnPropertyChanged(nameof(CanExecuteActions));
            }
        }

        public bool CanExecuteActions => !IsBusy;

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public string RepairLogOutput
        {
            get => _repairLogOutput;
            private set => SetProperty(ref _repairLogOutput, value);
        }

        public string ReportSummaryPreview
        {
            get => _reportSummaryPreview;
            private set => SetProperty(ref _reportSummaryPreview, value);
        }

        public string RamCleanupBanner
        {
            get => _ramCleanupBanner;
            private set => SetProperty(ref _ramCleanupBanner, value);
        }

        public string SystemSummary { get => _systemSummary; private set => SetProperty(ref _systemSummary, value); }
        public string MotherboardSummary { get => _motherboardSummary; private set => SetProperty(ref _motherboardSummary, value); }
        public string CpuSummary { get => _cpuSummary; private set => SetProperty(ref _cpuSummary, value); }
        public string MemorySummary { get => _memorySummary; private set => SetProperty(ref _memorySummary, value); }

        public SystemInventoryReport? CurrentInventory => _lastInventory;
        public WindowsHealthReport? CurrentHealthReport => _lastHealthReport;

        public ICommand SelectSubTabCommand { get; }
        public ICommand RunQuickHealthCheckCommand { get; }
        public ICommand RunFullHealthCheckCommand { get; }
        public ICommand ExecuteSelectedRepairsCommand { get; }
        public ICommand RefreshInventoryCommand { get; }
        public ICommand TrimRamCommand { get; }
        public ICommand OpenSuggestionTargetCommand { get; }
        public ICommand CopySummaryCommand { get; }

        public WindowsHealthReportViewModel(
            PcInventoryService? inventoryService = null,
            WindowsHealthRepairService? healthRepairService = null,
            ReportExportService? reportService = null,
            MemoryRamService? memoryService = null,
            DiskOptimizationService? diskOptService = null,
            Action<string>? logCallback = null,
            Action<string>? navigateCallback = null)
        {
            _inventoryService = inventoryService ?? new PcInventoryService();
            _healthRepairService = healthRepairService ?? new WindowsHealthRepairService();
            _reportService = reportService ?? new ReportExportService();
            _memoryService = memoryService ?? new MemoryRamService();
            _diskOptService = diskOptService ?? new DiskOptimizationService();
            _logCallback = logCallback ?? (_ => { });
            _navigateCallback = navigateCallback ?? (_ => { });

            foreach (var step in WindowsHealthRepairService.CreateAdvancedRepairSteps())
                RepairSteps.Add(step);

            SelectSubTabCommand = new RelayCommand(p => { if (p is string tab) ActiveSubTab = tab; });
            RunQuickHealthCheckCommand = new RelayCommand(async _ => await RunHealthCheckAsync(fullScan: false), _ => CanExecuteActions);
            RunFullHealthCheckCommand = new RelayCommand(async _ => await RunHealthCheckAsync(fullScan: true), _ => CanExecuteActions);
            ExecuteSelectedRepairsCommand = new RelayCommand(async _ => await ExecuteSelectedRepairsAsync(), _ => CanExecuteActions);
            RefreshInventoryCommand = new RelayCommand(async _ => await LoadInventoryAsync(), _ => CanExecuteActions);
            TrimRamCommand = new RelayCommand(async _ => await TrimRamAsync(), _ => CanExecuteActions);
            OpenSuggestionTargetCommand = new RelayCommand(p =>
            {
                if (p is SystemSuggestionItem s && !string.IsNullOrWhiteSpace(s.NavigationTarget))
                {
                    if (s.NavigationTarget == "HealthReport:Repairs")
                        ActiveSubTab = "Repairs";
                    else if (s.NavigationTarget == "HealthReport")
                        ActiveSubTab = "Inventory";
                    else
                        _navigateCallback(s.NavigationTarget);
                }
            });
            CopySummaryCommand = new RelayCommand(_ => CopySummaryToClipboard());
        }

        public async Task EnsureInitializedAsync()
        {
            if (_lastInventory != null) return;
            await LoadInventoryAsync();
        }

        public async Task LoadInventoryAsync()
        {
            if (IsBusy) return;
            var cts = ReplaceCts();
            IsBusy = true;
            StatusMessage = "Coletando inventário completo de hardware e módulos RAM...";

            try
            {
                var inventory = await _inventoryService.CollectAsync(cts.Token);
                _lastInventory = inventory;

                SystemSummary = inventory.SystemSummary;
                MotherboardSummary = inventory.MotherboardSummary;
                CpuSummary = inventory.CpuSummary;
                MemorySummary = inventory.MemorySummary;

                ReplaceItems(SystemLines, inventory.SystemLines);
                ReplaceItems(MotherboardLines, inventory.MotherboardLines);
                ReplaceItems(CpuLines, inventory.CpuLines);
                ReplaceItems(MemoryLines, inventory.MemoryLines);
                ReplaceItems(RamModules, inventory.RamModules);
                ReplaceItems(VideoAdapters, inventory.VideoAdapters);

                RefreshSuggestionsAndPreview();
                StatusMessage = $"Inventário atualizado às {inventory.CollectedAt:HH:mm:ss}.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Coleta de inventário cancelada.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Erro ao ler inventário: {ex.Message}";
                _logCallback($"[SAÚDE/INVENTÁRIO] {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task RunHealthCheckAsync(bool fullScan)
        {
            if (IsBusy) return;
            var cts = ReplaceCts();
            IsBusy = true;
            StatusMessage = fullScan
                ? "Executando checagem completa (DISM /ScanHealth + SFC /verifyonly + WMI + NTP)..."
                : "Executando checagem rápida do Windows...";

            try
            {
                bool completed = false;
                var progress = new Progress<string>(msg =>
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (!completed) StatusMessage = msg;
                    });
                });

                var inventoryTask = _lastInventory == null ? _inventoryService.CollectAsync(cts.Token) : Task.FromResult(_lastInventory);
                var health = await _healthRepairService.RunHealthCheckAsync(fullScan, progress, cts.Token);
                var inventory = await inventoryTask;
                completed = true;

                _lastInventory = inventory;
                _lastHealthReport = health;

                SystemSummary = inventory.SystemSummary;
                MotherboardSummary = inventory.MotherboardSummary;
                CpuSummary = inventory.CpuSummary;
                MemorySummary = inventory.MemorySummary;
                ReplaceItems(SystemLines, inventory.SystemLines);
                ReplaceItems(MotherboardLines, inventory.MotherboardLines);
                ReplaceItems(CpuLines, inventory.CpuLines);
                ReplaceItems(MemoryLines, inventory.MemoryLines);
                ReplaceItems(RamModules, inventory.RamModules);
                ReplaceItems(VideoAdapters, inventory.VideoAdapters);

                ReplaceItems(HealthItems, health.Items);
                RefreshSuggestionsAndPreview();

                StatusMessage = health.SummaryMessage;
                _logCallback($"[SAÚDE WINDOWS] {health.SummaryMessage}");
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Checagem cancelada.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Erro na checagem do Windows: {ex.Message}";
                _logCallback($"[SAÚDE WINDOWS] Erro: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task ExecuteSelectedRepairsAsync()
        {
            if (IsBusy) return;
            var selected = RepairSteps.Where(s => s.IsSelected).ToList();
            if (selected.Count == 0)
            {
                StatusMessage = "Selecione ao menos um reparo extra antes de executar.";
                return;
            }

            var cts = ReplaceCts();
            IsBusy = true;
            var logBuilder = new StringBuilder();
            RepairLogOutput = string.Empty;

            try
            {
                foreach (var step in selected)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    step.StatusText = "Executando...";
                    step.StatusColor = "#818CF8";
                    StatusMessage = $"Executando: {step.Title}...";

                    var res = await _healthRepairService.ExecuteAdvancedStepAsync(step, line =>
                    {
                        lock (logBuilder)
                        {
                            logBuilder.AppendLine(line);
                            RepairLogOutput = logBuilder.ToString();
                        }
                        _logCallback($"[REPARO] {line}");
                    }, cts.Token);

                    step.StatusText = res.Status switch
                    {
                        WindowsHealthStatus.Healthy => "✓ Concluído",
                        WindowsHealthStatus.Attention => "⚠ Atenção",
                        WindowsHealthStatus.Critical => "✕ Erro",
                        _ => "Info"
                    };
                    step.StatusColor = res.Status switch
                    {
                        WindowsHealthStatus.Healthy => "#10B981",
                        WindowsHealthStatus.Attention => "#F59E0B",
                        WindowsHealthStatus.Critical => "#EF4444",
                        _ => "#A1A1AA"
                    };

                    lock (logBuilder)
                    {
                        logBuilder.AppendLine($"Resultado: {res.Detail}");
                        if (!string.IsNullOrWhiteSpace(res.SuggestedAction))
                            logBuilder.AppendLine($"Próximo passo: {res.SuggestedAction}");
                        logBuilder.AppendLine();
                        RepairLogOutput = logBuilder.ToString();
                    }
                }

                StatusMessage = $"Reparos selecionados concluídos ({selected.Count} executado(s)).";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Execução de reparos cancelada.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Erro ao executar reparo: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<RamCleanupResult> TrimRamAsync()
        {
            IsBusy = true;
            StatusMessage = "Liberando memória RAM ociosa via EmptyWorkingSet...";
            try
            {
                var res = await _memoryService.TrimIdleMemoryAsync();
                RamCleanupBanner = res.Message;
                StatusMessage = res.Message;
                _logCallback($"[MEMÓRIA RAM] {res.Message}");
                return res;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void CopySummaryToClipboard()
        {
            if (_lastInventory == null)
                _lastInventory = _inventoryService.Collect();

            string summary = _reportService.BuildTextSummary(_lastInventory, _lastHealthReport);
            ReportSummaryPreview = summary;
            try
            {
                System.Windows.Clipboard.SetText(summary);
                StatusMessage = "Resumo técnico anonimizado copiado para a Área de Transferência!";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Resumo atualizado na prévia (falha no Clipboard: {ex.Message}).";
            }
        }

        public void SaveReportFile(string filePath)
        {
            if (_lastInventory == null)
                _lastInventory = _inventoryService.Collect();

            _reportService.SaveReportToFile(_lastInventory, filePath, _lastHealthReport);
            StatusMessage = $"Relatório salvo em: {filePath}";
            _logCallback($"[RELATÓRIO] Salvo em {filePath}");
        }

        private void RefreshSuggestionsAndPreview()
        {
            DiskOptimizationConfig? diskConfig = null;
            try { diskConfig = _diskOptService.ReadConfig(); } catch { }

            var suggestions = _reportService.EvaluateSuggestions(_lastInventory, _lastHealthReport, diskConfig);
            // Aponta reparos de WMI/Relógio diretamente para a sub-aba Reparos Extras
            for (int i = 0; i < suggestions.Count; i++)
            {
                if (suggestions[i].Id is "wmi_repair_needed" or "clock_out_of_sync")
                {
                    suggestions[i] = new SystemSuggestionItem
                    {
                        Id = suggestions[i].Id,
                        Severity = suggestions[i].Severity,
                        Icon = suggestions[i].Icon,
                        Title = suggestions[i].Title,
                        Description = suggestions[i].Description,
                        ActionLabel = suggestions[i].ActionLabel,
                        NavigationTarget = "HealthReport:Repairs"
                    };
                }
            }

            ReplaceItems(Suggestions, suggestions);

            if (_lastInventory != null)
                ReportSummaryPreview = _reportService.BuildTextSummary(_lastInventory, _lastHealthReport);
        }

        private CancellationTokenSource ReplaceCts()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return _cts;
        }

        private static void ReplaceItems<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T> items)
        {
            target.Clear();
            foreach (var item in items) target.Add(item);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
