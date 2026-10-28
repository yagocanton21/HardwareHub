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
        private readonly UpdateService _updateService;
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

        private UpdateInfo? _availableUpdate;
        private bool _isDownloadingUpdate;
        private double _updateDownloadProgress;
        private string _updateButtonText = "⚡ Atualizar Agora";

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
            set 
            { 
                _cpu = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(IsPawnIOMissing));
            }
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

        private bool _isPawnBannerDismissed = false;
        private bool _isInstallingPawn = false;
        public bool IsPawnIOMissing => !_isPawnBannerDismissed && _hardwareService.IsInitialized && !HardwareService.IsPawnIOInstalled && Cpu.Temperature <= 0;
        public bool IsInstallingPawn => _isInstallingPawn;
        public bool CanInstallPawn => !_isInstallingPawn;
        public string PawnButtonText => _isInstallingPawn ? "⏳ Instalando..." : "Instalar Driver ➔";

        public UpdateInfo? AvailableUpdate
        {
            get => _availableUpdate;
            set
            {
                _availableUpdate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsUpdateAvailable));
                OnPropertyChanged(nameof(LatestVersionText));
                OnPropertyChanged(nameof(UpdateChangelogSummary));
                OnPropertyChanged(nameof(CanApplyUpdate));
            }
        }

        public bool IsUpdateAvailable => _availableUpdate?.IsAvailable ?? false;
        public string LatestVersionText => _availableUpdate != null ? $"v{_availableUpdate.LatestVersion} ({_availableUpdate.FormattedSize})" : "";
        public string UpdateChangelogSummary => !string.IsNullOrWhiteSpace(_availableUpdate?.Changelog) ? _availableUpdate.Changelog.Trim() : "Melhorias de desempenho e correções de estabilidade.";

        public bool IsDownloadingUpdate
        {
            get => _isDownloadingUpdate;
            set
            {
                if (_isDownloadingUpdate != value)
                {
                    _isDownloadingUpdate = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanApplyUpdate));
                }
            }
        }

        public double UpdateDownloadProgress
        {
            get => _updateDownloadProgress;
            set
            {
                if (Math.Abs(_updateDownloadProgress - value) > 0.01)
                {
                    _updateDownloadProgress = value;
                    OnPropertyChanged();
                }
            }
        }

        public string UpdateButtonText
        {
            get => _updateButtonText;
            set
            {
                if (_updateButtonText != value)
                {
                    _updateButtonText = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool CanApplyUpdate => !_isDownloadingUpdate && IsUpdateAvailable;

        private bool _isBatchInstalling = false;
        public bool IsBatchInstalling
        {
            get => _isBatchInstalling;
            set
            {
                if (_isBatchInstalling != value)
                {
                    _isBatchInstalling = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanBatchInstall));
                }
            }
        }

        public bool HasImportedApps => AllSoftware.Any(x => x.Category == "Importados");
        public int ImportedCount => AllSoftware.Count(x => x.Category == "Importados");
        public bool CanBatchInstall => !_isBatchInstalling && HasImportedApps;

        public ICommand NavigateCommand { get; }
        public ICommand InstallAppCommand { get; }
        public ICommand ImportWingetFileCommand { get; }
        public ICommand InstallAllImportedCommand { get; }
        public ICommand RefreshSoftwareStatusCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand CheckForUpdatesCommand { get; }
        public ICommand ApplyUpdateCommand { get; }
        public ICommand InstallPawnIOCommand { get; }
        public ICommand DismissPawnBannerCommand { get; }
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
            _updateService = new UpdateService();

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
                _gpuRenderService.Start(AppendLog);
            });

            StopGpuRenderCommand = new RelayCommand(_ =>
            {
                _gpuRenderService.Stop(AppendLog);
            });

            InstallPawnIOCommand = new RelayCommand(async _ =>
            {
                if (_isInstallingPawn) return;
                _isInstallingPawn = true;
                OnPropertyChanged(nameof(IsInstallingPawn));
                OnPropertyChanged(nameof(CanInstallPawn));
                OnPropertyChanged(nameof(PawnButtonText));
                AppendLog("[DRIVER] Iniciando instalação do driver de sensores PawnIO...");

                try
                {
                    // 1. Tenta pelo winget via SoftwareService
                    var pawnItem = AllSoftware.FirstOrDefault(x => x.Id == "pawnio");
                    if (pawnItem != null)
                    {
                        await _softwareService.InstallPackageAsync(pawnItem, msg =>
                        {
                            System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendLog(msg));
                        });
                    }

                    // 2. Se falhar pelo winget, tenta instalar diretamente via GitHub Setup
                    if (!HardwareService.IsPawnIOInstalled)
                    {
                        AppendLog("[DRIVER] Tentando instalação alternativa direta (GitHub Releases)...");
                        await _softwareService.InstallPawnIODirectAsync(msg =>
                        {
                            System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendLog(msg));
                        });
                    }

                    if (HardwareService.IsPawnIOInstalled)
                    {
                        AppendLog("[DRIVER] Driver PawnIO instalado com sucesso! Reinicializando sensores da CPU...");
                        await _hardwareService.ReinitializeSensorsAsync();
                        UpdateHardwareMetrics();
                    }
                    else
                    {
                        AppendLog("[DRIVER] Não foi possível concluir a instalação automática. Você pode baixar em https://pawnio.eu");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[ERRO DRIVER] {ex.Message}");
                }
                finally
                {
                    _isInstallingPawn = false;
                    OnPropertyChanged(nameof(IsInstallingPawn));
                    OnPropertyChanged(nameof(CanInstallPawn));
                    OnPropertyChanged(nameof(PawnButtonText));
                    OnPropertyChanged(nameof(IsPawnIOMissing));
                }
            }, _ => !_isInstallingPawn);

            DismissPawnBannerCommand = new RelayCommand(_ =>
            {
                _isPawnBannerDismissed = true;
                OnPropertyChanged(nameof(IsPawnIOMissing));
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
            ImportWingetFileCommand = new RelayCommand(_ => ImportWingetFile());
            InstallAllImportedCommand = new RelayCommand(async _ => await InstallAllImportedAsync(), _ => CanBatchInstall);
            ClearLogsCommand = new RelayCommand(_ => LogOutput = "");
            CheckForUpdatesCommand = new RelayCommand(async _ => await CheckForUpdatesAsync(silent: false));
            ApplyUpdateCommand = new RelayCommand(async _ => await ApplyUpdateAsync(), _ => CanApplyUpdate);

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
            _metricsTimer.Tick += (s, e) =>
            {
                UpdateHardwareMetrics();
                if (IsGpuRenderRunning)
                {
                    OnPropertyChanged(nameof(GpuRenderElapsed));
                }
            };
            _metricsTimer.Start();

            // Leitura imediata de métricas locais rápidas (discos C:/D:, uptime, memória RAM)
            UpdateHardwareMetrics();

            // Inicialização profunda de hardware em background (não bloqueia nada na tela)
            _ = Task.Run(async () =>
            {
                await _hardwareService.InitializeAsync();

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateHardwareMetrics();
                });

                // O executável portátil não usa o inicializador. Sem temperatura
                // detectada e sem PawnIO, tenta instalar o driver já na primeira execução.
                if (!HardwareService.IsPawnIOInstalled && Cpu.Temperature <= 0)
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        AppendLog("[DRIVER] Sensores térmicos indisponíveis. Tentando instalar PawnIO automaticamente...");
                        InstallPawnIOCommand.Execute(null);
                    });
                }

                _ = RefreshDisksHealthAsync();
                _ = RefreshInstalledStatusAsync();
                _ = CheckForUpdatesAsync(silent: true);
            });
        }

        private void UpdateHardwareMetrics()
        {
            try
            {
                var (cpu, gpu, ram, disks, sys) = _hardwareService.GetCurrentMetrics();

                if (IsGpuRenderRunning && gpu.UsagePercentage <= 0)
                {
                    // Em GPUs legadas onde o driver não possui contador de telemetria WDDM,
                    // reflete a saturação total do motor de estresse Direct3D 11 ativo
                    gpu.UsagePercentage = 100.0;
                }

                // Em GPUs integradas onde não há sensor térmico separado do die do processador,
                // reflete a temperatura do pacote da CPU caso ela esteja disponível
                if (gpu.Temperature <= 0 && cpu.Temperature > 0 &&
                    (gpu.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                     gpu.Name.Contains("Vega", StringComparison.OrdinalIgnoreCase) ||
                     gpu.Name.Contains("Graphics", StringComparison.OrdinalIgnoreCase)))
                {
                    gpu.Temperature = cpu.Temperature;
                }

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

                // Atualiza sensores de discos físicos em tempo real se já carregados
                if (PhysicalDisks.Count > 0)
                {
                    _hardwareService.EnrichDisksWithSensors(PhysicalDisks);
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
                _hardwareService.EnrichDisksWithSensors(disks);

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

        private void ImportWingetFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importar Lista de Programas (Winget / UniGetUI)",
                Filter = "Todos os Formatos Suportados (*.ubundle;*.json;*.txt;*.yaml;*.yml)|*.ubundle;*.json;*.txt;*.yaml;*.yml|Pacotes UniGetUI (*.ubundle)|*.ubundle|Arquivos Winget (*.json)|*.json|Arquivos de Texto e YAML (*.txt;*.yaml;*.yml)|*.txt;*.yaml;*.yml|Todos os Arquivos (*.*)|*.*",
                DefaultExt = ".ubundle"
            };

            if (dialog.ShowDialog() == true)
            {
                AppendLog($"[IMPORTAÇÃO] Lendo arquivo '{Path.GetFileName(dialog.FileName)}'...");
                var items = _softwareService.ParseWingetFile(dialog.FileName);

                if (items.Count == 0)
                {
                    AppendLog("[AVISO] Nenhum pacote válido foi identificado no arquivo fornecido.");
                    return;
                }

                if (!Categories.Contains("Importados"))
                {
                    Categories.Add("Importados");
                }

                int added = 0;
                foreach (var item in items)
                {
                    var existing = AllSoftware.FirstOrDefault(x => x.WingetId.Equals(item.WingetId, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        AllSoftware.Add(item);
                        added++;
                    }
                    else
                    {
                        existing.Category = "Importados";
                    }
                }

                SelectedCategory = "Importados";
                ApplySoftwareFilters();
                OnPropertyChanged(nameof(HasImportedApps));
                OnPropertyChanged(nameof(ImportedCount));
                OnPropertyChanged(nameof(CanBatchInstall));

                AppendLog($"[IMPORTAÇÃO] {items.Count} programa(s) importado(s) com sucesso ({added} novos adicionados)! Categoria 'Importados' selecionada.");
                _ = RefreshInstalledStatusAsync();
            }
        }

        private async Task InstallAllImportedAsync()
        {
            var queue = AllSoftware.Where(x => x.Category == "Importados" && x.CanInstall).ToList();
            if (queue.Count == 0)
            {
                AppendLog("[INFO] Todos os programas importados já estão instalados ou em andamento.");
                return;
            }

            IsBatchInstalling = true;
            AppendLog($"\n[INSTALAÇÃO EM LOTE] Iniciando instalação sequencial de {queue.Count} programas da lista importada...");

            try
            {
                int current = 1;
                foreach (var item in queue)
                {
                    AppendLog($"\n[LOTE {current}/{queue.Count}] Instalando {item.Name} ({item.WingetId})...");
                    await InstallAppAsync(item);
                    current++;
                }
                AppendLog($"\n[INSTALAÇÃO EM LOTE] Processo concluído para todos os {queue.Count} programas!");
            }
            finally
            {
                IsBatchInstalling = false;
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

        public async Task CheckForUpdatesAsync(bool silent = false)
        {
            try
            {
                if (!silent)
                {
                    AppendLog("[ATUALIZAÇÃO] Verificando se há novas versões disponíveis no GitHub...");
                }

                var update = await _updateService.CheckForUpdatesAsync();
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    AvailableUpdate = update;
                    if (update.IsAvailable)
                    {
                        AppendLog($"[ATUALIZAÇÃO] Nova versão detectada: v{update.LatestVersion} ({update.FormattedSize}). Clique no banner 'Atualizar' para instalar.");
                    }
                    else if (!silent)
                    {
                        AppendLog($"[ATUALIZAÇÃO] Você já está executando a versão mais recente (v{update.CurrentVersion}).");
                    }
                });
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    AppendLog($"[ERRO AO VERIFICAR ATUALIZAÇÃO] {ex.Message}");
                }
            }
        }

        public async Task ApplyUpdateAsync()
        {
            if (_availableUpdate == null || string.IsNullOrEmpty(_availableUpdate.DownloadUrl) || IsDownloadingUpdate)
                return;

            IsDownloadingUpdate = true;
            UpdateDownloadProgress = 0;
            UpdateButtonText = "Baixando 0%...";

            var progress = new Progress<double>(percent =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateDownloadProgress = percent;
                    UpdateButtonText = $"Baixando {percent:F0}%...";
                });
            });

            try
            {
                AppendLog($"[ATUALIZAÇÃO] Baixando instalador da versão v{_availableUpdate.LatestVersion}...");
                await _updateService.DownloadAndInstallUpdateAsync(_availableUpdate.DownloadUrl, progress, msg =>
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendLog(msg));
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[ERRO NA ATUALIZAÇÃO] {ex.Message}");
                UpdateButtonText = "Tentar Novamente";
                IsDownloadingUpdate = false;
            }
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
