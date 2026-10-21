using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly HardwareService _hardwareService;
        private readonly SoftwareService _softwareService;
        private readonly DiskHealthService _diskHealthService;
        private readonly MaintenanceService _maintenanceService;
        private readonly StressTestService _stressTestService;
        private readonly GpuRenderService _gpuRenderService;
        private readonly DispatcherTimer _metricsTimer;

        private string _currentSection = "Dashboard";
        private CpuMetrics _cpu = new();
        private GpuMetrics _gpu = new();
        private RamMetrics _ram = new();
        private SystemOverview _systemInfo = new();
        private string _searchFilter = "";
        private string _selectedCategory = "Todos";
        private string _logOutput = "Console de atividades iniciado...\n";
        private bool _isUpdatingSoftware = false;

        public ObservableCollection<DiskMetrics> Disks { get; } = new();
        public ObservableCollection<DiskHealthInfo> PhysicalDisks { get; } = new();
        public ObservableCollection<MaintenanceTool> MaintenanceTools { get; } = new();
        public ObservableCollection<SoftwareItem> AllSoftware { get; } = new();
        public ObservableCollection<SoftwareItem> FilteredSoftware { get; } = new();
        public ObservableCollection<string> Categories { get; } = new()
        {
            "Todos",
            "Navegadores",
            "Comunicação",
            "Produtividade",
            "Desenvolvimento",
            "Jogos & Mídia",
            "Utilitários"
        };

        public string CurrentSection
        {
            get => _currentSection;
            set
            {
                if (_currentSection != value)
                {
                    _currentSection = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsDashboardActive));
                    OnPropertyChanged(nameof(IsHardwareActive));
                    OnPropertyChanged(nameof(IsDisksActive));
                    OnPropertyChanged(nameof(IsMaintenanceActive));
                    OnPropertyChanged(nameof(IsAppsActive));
                    OnPropertyChanged(nameof(IsSettingsActive));
                    OnPropertyChanged(nameof(IsStressTestActive));
                }
            }
        }

        public bool IsDashboardActive => CurrentSection == "Dashboard";
        public bool IsHardwareActive => CurrentSection == "Hardware";
        public bool IsDisksActive => CurrentSection == "Disks";
        public bool IsMaintenanceActive => CurrentSection == "Maintenance";
        public bool IsAppsActive => CurrentSection == "Apps";
        public bool IsSettingsActive => CurrentSection == "Settings";
        public bool IsStressTestActive => CurrentSection == "StressTest";

        public CpuMetrics Cpu
        {
            get => _cpu;
            set { _cpu = value; OnPropertyChanged(); }
        }

        public GpuMetrics Gpu
        {
            get => _gpu;
            set { _gpu = value; OnPropertyChanged(); }
        }

        public RamMetrics Ram
        {
            get => _ram;
            set { _ram = value; OnPropertyChanged(); }
        }

        public SystemOverview SystemInfo
        {
            get => _systemInfo;
            set { _systemInfo = value; OnPropertyChanged(); }
        }

        public string SearchFilter
        {
            get => _searchFilter;
            set
            {
                if (_searchFilter != value)
                {
                    _searchFilter = value;
                    OnPropertyChanged();
                    ApplySoftwareFilters();
                }
            }
        }

        public string SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (_selectedCategory != value)
                {
                    _selectedCategory = value;
                    OnPropertyChanged();
                    ApplySoftwareFilters();
                }
            }
        }

        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        public bool IsUpdatingSoftware
        {
            get => _isUpdatingSoftware;
            set { _isUpdatingSoftware = value; OnPropertyChanged(); }
        }

        private string _refreshButtonText = "🔄 Verificar Instalados";
        public string RefreshButtonText
        {
            get => _refreshButtonText;
            set { _refreshButtonText = value; OnPropertyChanged(); }
        }

        private string _refreshDisksButtonText = "🔄 Atualizar S.M.A.R.T.";
        public string RefreshDisksButtonText
        {
            get => _refreshDisksButtonText;
            set { _refreshDisksButtonText = value; OnPropertyChanged(); }
        }

        private bool _isRefreshingDisks = false;
        public bool IsRefreshingDisks
        {
            get => _isRefreshingDisks;
            set
            {
                if (_isRefreshingDisks != value)
                {
                    _isRefreshingDisks = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanRefreshDisks));
                }
            }
        }
        public bool CanRefreshDisks => !IsRefreshingDisks;

        public bool IsPawnIOMissing => !HardwareService.IsPawnIOInstalled;

        public ICommand NavigateCommand { get; }
        public ICommand InstallAppCommand { get; }
        public ICommand RefreshSoftwareStatusCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand InstallPawnIOCommand { get; }
        public ICommand RefreshDisksCommand { get; }
        public ICommand BenchmarkDiskCommand { get; }
        public ICommand RunMaintenanceCommand { get; }
        public ICommand RunMaintenanceInCmdCommand { get; }
        public ICommand SearchOnlineCommand { get; }

        public ICommand StartCpuStressCommand { get; }
        public ICommand StartCpuBenchmarkCommand { get; }
        public ICommand StopCpuTestCommand { get; }
        public ICommand StartGpuRenderCommand { get; }
        public ICommand StopGpuRenderCommand { get; }

        public bool IsCpuStressRunning => _stressTestService.IsCpuStressRunning;
        public bool IsCpuBenchRunning => _stressTestService.IsCpuBenchRunning;
        public bool IsGpuRenderRunning => _gpuRenderService.IsRunning;
        public double SingleCoreScore => _stressTestService.SingleCoreScore;
        public double MultiCoreScore => _stressTestService.MultiCoreScore;
        public double MultiThreadRatio => _stressTestService.MultiThreadRatio;
        public double CpuMaxObservedTemp => _stressTestService.MaxObservedTemp;
        public double CpuStartTemp => _stressTestService.StartTemp;
        public string CpuStressElapsed => _stressTestService.ElapsedTime.ToString(@"mm\:ss");
        public int GpuRenderFps => _gpuRenderService.CurrentFps;
        public string GpuRenderElapsed => _gpuRenderService.ElapsedTime.ToString(@"mm\:ss");
        public bool IsThermalWarning => Cpu.Temperature >= 85.0;
        public System.Windows.Media.Media3D.MeshGeometry3D TorusMesh { get; } = GpuRenderService.CreateTorusMesh();

        private bool _isSearchingOnline = false;
        public bool IsSearchingOnline
        {
            get => _isSearchingOnline;
            set
            {
                if (_isSearchingOnline != value)
                {
                    _isSearchingOnline = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasNoLocalResults));
                }
            }
        }

        public bool HasNoLocalResults => FilteredSoftware.Count == 0 && !string.IsNullOrWhiteSpace(SearchFilter) && !IsSearchingOnline;

        public MainViewModel()
        {
            _hardwareService = new HardwareService();
            _softwareService = new SoftwareService();
            _diskHealthService = new DiskHealthService();
            _maintenanceService = new MaintenanceService();
            _stressTestService = new StressTestService();
            _gpuRenderService = new GpuRenderService();

            _stressTestService.StateChanged += () =>
            {
                OnPropertyChanged(nameof(IsCpuStressRunning));
                OnPropertyChanged(nameof(IsCpuBenchRunning));
                OnPropertyChanged(nameof(SingleCoreScore));
                OnPropertyChanged(nameof(MultiCoreScore));
                OnPropertyChanged(nameof(MultiThreadRatio));
                OnPropertyChanged(nameof(CpuMaxObservedTemp));
                OnPropertyChanged(nameof(CpuStartTemp));
                OnPropertyChanged(nameof(CpuStressElapsed));
                OnPropertyChanged(nameof(IsThermalWarning));
            };

            _gpuRenderService.StateChanged += () =>
            {
                OnPropertyChanged(nameof(IsGpuRenderRunning));
                OnPropertyChanged(nameof(GpuRenderFps));
                OnPropertyChanged(nameof(GpuRenderElapsed));
            };

            StartCpuStressCommand = new RelayCommand(async _ =>
            {
                await _stressTestService.StartCpuStressAsync(() => Cpu.Temperature, AppendLog);
            });

            StartCpuBenchmarkCommand = new RelayCommand(async _ =>
            {
                await _stressTestService.StartCpuBenchmarkAsync(() => Cpu.Temperature, AppendLog);
            });

            StopCpuTestCommand = new RelayCommand(_ =>
            {
                _stressTestService.StopCpuTest();
            });

            StartGpuRenderCommand = new RelayCommand(_ =>
            {
                _gpuRenderService.Start();
                AppendLog("[GPU 3D RENDER] Teste de renderização gráfica 3D iniciado.");
            });

            StopGpuRenderCommand = new RelayCommand(_ =>
            {
                _gpuRenderService.Stop();
                AppendLog("[GPU 3D RENDER] Teste de renderização gráfica 3D finalizado.");
            });

            InstallPawnIOCommand = new RelayCommand(async _ =>
            {
                var pawnItem = AllSoftware.FirstOrDefault(x => x.Id == "pawnio");
                if (pawnItem != null)
                {
                    await InstallAppAsync(pawnItem);
                    OnPropertyChanged(nameof(IsPawnIOMissing));
                }
            });

            NavigateCommand = new RelayCommand(p =>
            {
                if (p is string section)
                    CurrentSection = section;
            });

            InstallAppCommand = new RelayCommand(async p =>
            {
                if (p is SoftwareItem item)
                {
                    await InstallAppAsync(item);
                }
            }, p => p is SoftwareItem item && item.CanInstall);

            RefreshSoftwareStatusCommand = new RelayCommand(async _ => await RefreshInstalledStatusAsync());
            ClearLogsCommand = new RelayCommand(_ => LogOutput = "");

            RefreshDisksCommand = new RelayCommand(async _ => await RefreshDisksHealthAsync());
            BenchmarkDiskCommand = new RelayCommand(async p =>
            {
                if (p is DiskHealthInfo disk)
                {
                    await _diskHealthService.RunSpeedBenchmarkAsync(disk, msg =>
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendLog($"[DISCO {disk.Index}] {msg}"));
                    });
                }
            });

            RunMaintenanceCommand = new RelayCommand(async p =>
            {
                if (p is MaintenanceTool tool && tool.CanExecute)
                {
                    await _maintenanceService.RunToolAsync(tool, msg =>
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendLog(msg));
                    });
                }
            });

            RunMaintenanceInCmdCommand = new RelayCommand(p =>
            {
                if (p is MaintenanceTool tool && tool.CanExecute)
                {
                    _maintenanceService.RunToolInCmd(tool, msg =>
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendLog(msg));
                    });
                }
            });

            SearchOnlineCommand = new RelayCommand(async _ =>
            {
                if (string.IsNullOrWhiteSpace(SearchFilter) || SearchFilter.Trim().Length < 2) return;

                IsSearchingOnline = true;
                AppendLog($"[INFO] Pesquisando '{SearchFilter}' no catálogo global do Windows (Winget)...");
                try
                {
                    var onlineItems = await _softwareService.SearchWingetOnlineAsync(SearchFilter);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (onlineItems.Count > 0)
                        {
                            FilteredSoftware.Clear();
                            foreach (var item in onlineItems)
                            {
                                FilteredSoftware.Add(item);
                            }
                            AppendLog($"[INFO] {onlineItems.Count} pacotes encontrados no Winget para '{SearchFilter}'.");
                        }
                        else
                        {
                            AppendLog($"[AVISO] Nenhum pacote adicional encontrado no Winget para '{SearchFilter}'.");
                        }
                    });
                }
                catch (Exception ex)
                {
                    AppendLog($"[ERRO BUSCA ONLINE] {ex.Message}");
                }
                finally
                {
                    IsSearchingOnline = false;
                    OnPropertyChanged(nameof(HasNoLocalResults));
                }
            });

            // Carrega catálogo inicial de programas
            foreach (var item in _softwareService.GetCatalog())
            {
                AllSoftware.Add(item);
            }
            ApplySoftwareFilters();

            // Carrega ferramentas de manutenção
            foreach (var t in _maintenanceService.GetTools())
            {
                MaintenanceTools.Add(t);
            }

            // Timer para atualização periódica de métricas de Hardware
            _metricsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.0)
            };
            _metricsTimer.Tick += (s, e) => UpdateHardwareMetrics();

            // Inicialização em background: a janela renderiza imediatamente (< 100ms) sem congelar a UI
            _ = Task.Run(async () =>
            {
                await _hardwareService.InitializeAsync();

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateHardwareMetrics();
                    _metricsTimer.Start();
                });

                _ = RefreshDisksHealthAsync();
                _ = RefreshInstalledStatusAsync();
            });
        }

        private void UpdateHardwareMetrics()
        {
            try
            {
                var (cpu, gpu, ram, disks, sys) = _hardwareService.GetCurrentMetrics();
                Cpu = cpu;
                Gpu = gpu;
                Ram = ram;
                SystemInfo = sys;

                // Atualiza coleção de discos se mudou
                if (Disks.Count != disks.Count)
                {
                    Disks.Clear();
                    foreach (var d in disks) Disks.Add(d);
                }
                else
                {
                    for (int i = 0; i < disks.Count; i++)
                    {
                        Disks[i] = disks[i];
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[ERRO SENSOR] {ex.Message}");
            }
        }

        private async Task RefreshInstalledStatusAsync()
        {
            if (IsUpdatingSoftware) return;
            IsUpdatingSoftware = true;
            RefreshButtonText = "⏳ Verificando...";
            AppendLog("[INFO] Verificando aplicativos instalados no Windows...");

            try
            {
                await _softwareService.CheckAllInstalledStatusAsync(AllSoftware);
                int count = AllSoftware.Count(x => x.IsInstalled);
                AppendLog($"[INFO] Verificação de aplicativos concluída. {count} programas instalados.");
                RefreshButtonText = $"✓ Verificado ({count} instalados)";
            }
            catch (Exception ex)
            {
                AppendLog($"[ERRO VERIFICAÇÃO] {ex.Message}");
                RefreshButtonText = "⚠️ Falha ao verificar";
            }
            finally
            {
                IsUpdatingSoftware = false;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (!IsUpdatingSoftware)
                        {
                            RefreshButtonText = "🔄 Verificar Instalados";
                        }
                    });
                });
            }
        }

        public async Task RefreshDisksHealthAsync()
        {
            if (IsRefreshingDisks) return;

            IsRefreshingDisks = true;
            RefreshDisksButtonText = "⏳ Lendo Sensores S.M.A.R.T....";
            AppendLog("[INFO] Lendo sensores S.M.A.R.T. e integridade dos discos físicos...");

            try
            {
                // Garante pelo menos 600ms de feedback visual caso o WMI responda instantaneamente
                var fetchTask = Task.Run(() => _diskHealthService.GetDisksHealth());
                var minWaitTask = Task.Delay(600);
                await Task.WhenAll(fetchTask, minWaitTask);

                var disks = await fetchTask;
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    PhysicalDisks.Clear();
                    foreach (var d in disks)
                    {
                        PhysicalDisks.Add(d);
                    }
                });

                RefreshDisksButtonText = "✓ S.M.A.R.T. Atualizado!";
                AppendLog($"[SUCESSO] {disks.Count} discos físicos analisados com sucesso.");

                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        RefreshDisksButtonText = "🔄 Atualizar S.M.A.R.T.";
                        IsRefreshingDisks = false;
                    });
                });
            }
            catch (Exception ex)
            {
                RefreshDisksButtonText = "❌ Falha ao Atualizar";
                AppendLog($"[ERRO S.M.A.R.T.] Falha ao analisar discos: {ex.Message}");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        RefreshDisksButtonText = "🔄 Atualizar S.M.A.R.T.";
                        IsRefreshingDisks = false;
                    });
                });
            }
        }

        private async Task InstallAppAsync(SoftwareItem item)
        {
            AppendLog($"\n>>> Solicitando instalação: {item.Name} ({item.WingetId})");
            bool success = await _softwareService.InstallPackageAsync(item, line =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    AppendLog(line);
                });
            });

            if (success)
            {
                AppendLog($"[SUCESSO] Instalação de {item.Name} finalizada com êxito!");
            }
            else
            {
                AppendLog($"[FALHA] Não foi possível completar a instalação de {item.Name}.");
            }
        }

        private void ApplySoftwareFilters()
        {
            FilteredSoftware.Clear();
            var query = AllSoftware.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SelectedCategory) && SelectedCategory != "Todos")
            {
                query = query.Where(x => x.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(SearchFilter))
            {
                query = query.Where(x =>
                    x.Name.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) ||
                    x.Description.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) ||
                    x.Publisher.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var item in query)
            {
                FilteredSoftware.Add(item);
            }
            OnPropertyChanged(nameof(HasNoLocalResults));
        }

        private void AppendLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{time}] {message}\n";
        }

        public void Dispose()
        {
            _metricsTimer.Stop();
            _stressTestService?.StopCpuTest();
            _gpuRenderService?.Stop();
            _hardwareService.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
