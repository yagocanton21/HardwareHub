using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class ProgramsStartupViewModel : INotifyPropertyChanged
    {
        private readonly StartupManagerService _startupService;
        private readonly WingetUpdaterService _updaterService;
        private readonly ProgramUninstallerService _uninstallerService;
        private readonly Action<string> _log;

        private string _activeProgramsSubTab = "Updates"; // "Updates" | "Uninstaller" | "Backups"
        private bool _isBusyUpdates;
        private bool _isBusyUninstaller;
        private bool _isBusyStartup;
        private string _programsStatusBanner = "";
        private string _startupStatusBanner = "";

        private string _startupSearch = "";
        private string _startupFilter = "Todos"; // "Todos" | "Pode" | "Ativos" | "Desativados"
        private string _programSearch = "";
        private string _updateSearch = "";
        private LeftoverLevel _selectedLeftoverLevel = LeftoverLevel.Moderate;
        private InstalledProgramItem? _selectedProgram;
        private string _leftoversTitle = "Nenhum programa selecionado para busca de sobras.";

        private bool _startupLoadedOnce;
        private bool _programsLoadedOnce;
        private CancellationTokenSource? _programsCts;

        public ObservableCollection<StartupEntryViewModel> AllStartupEntries { get; } = new();
        public ObservableCollection<StartupEntryViewModel> FilteredStartupEntries { get; } = new();

        public ObservableCollection<ProgramUpdateItemViewModel> AllUpdates { get; } = new();
        public ObservableCollection<ProgramUpdateItemViewModel> FilteredUpdates { get; } = new();

        public ObservableCollection<InstalledProgramItem> AllPrograms { get; } = new();
        public ObservableCollection<InstalledProgramItem> FilteredPrograms { get; } = new();
        public ObservableCollection<ProgramLeftoverItem> FoundLeftovers { get; } = new();
        public ObservableCollection<UninstallBackupInfo> UninstallBackups { get; } = new();

        public IReadOnlyList<LeftoverLevel> LeftoverLevels { get; } = new[]
        {
            LeftoverLevel.Safe,
            LeftoverLevel.Moderate,
            LeftoverLevel.Advanced
        };

        public ProgramsStartupViewModel(
            StartupManagerService? startupService = null,
            WingetUpdaterService? updaterService = null,
            ProgramUninstallerService? uninstallerService = null,
            Action<string>? logCallback = null)
        {
            _startupService = startupService ?? new StartupManagerService();
            _updaterService = updaterService ?? new WingetUpdaterService();
            _uninstallerService = uninstallerService ?? new ProgramUninstallerService();
            _log = logCallback ?? (_ => { });

            AllUpdates.CollectionChanged += AllUpdates_CollectionChanged;

            SwitchProgramsTabCommand = new RelayCommand(p => { if (p is string tab) ActiveProgramsSubTab = tab; });

            CheckUpdatesCommand = new RelayCommand(async _ => await CheckUpdatesAsync(), _ => !IsBusyUpdates);
            UpgradeSelectedCommand = new RelayCommand(async _ => await UpgradeSelectedAsync(), _ => !IsBusyUpdates && AllUpdates.Any(u => u.IsSelected && u.CanInteract));
            UpgradeSingleCommand = new RelayCommand(async p => { if (p is ProgramUpdateItemViewModel u) await UpgradeSingleAsync(u); }, _ => !IsBusyUpdates);
            IgnoreUpdateCommand = new RelayCommand(p => { if (p is ProgramUpdateItemViewModel u) IgnoreUpdate(u); });
            SelectAllUpdatesCommand = new RelayCommand(_ => ToggleAllUpdatesSelection(), _ => !IsBusyUpdates && FilteredUpdates.Any(u => u.CanInteract));
            ClearIgnoredUpdatesCommand = new RelayCommand(async _ => { _updaterService.UnignoreAll(); await CheckUpdatesAsync(); });

            RefreshProgramsCommand = new RelayCommand(async _ => await LoadInstalledProgramsAsync(), _ => !IsBusyUninstaller);
            UninstallSelectedProgramCommand = new RelayCommand(async _ => await UninstallSelectedAsync(silent: true), _ => !IsBusyUninstaller && SelectedProgram != null);
            ScanLeftoversCommand = new RelayCommand(async _ => await ScanLeftoversForSelectedAsync(), _ => !IsBusyUninstaller && SelectedProgram != null);
            CleanLeftoversCommand = new RelayCommand(async _ => await CleanSelectedLeftoversAsync(), _ => !IsBusyUninstaller && FoundLeftovers.Any(l => l.IsSelected));
            ForceRemoveProgramEntryCommand = new RelayCommand(async _ => await ForceRemoveSelectedEntryAsync(), _ => !IsBusyUninstaller && SelectedProgram != null);
            RestoreBackupCommand = new RelayCommand(async p => { if (p is UninstallBackupInfo b) await RestoreBackupAsync(b); }, _ => !IsBusyUninstaller);
            CancelProgramsTaskCommand = new RelayCommand(_ => CancelProgramsTask(), _ => IsBusyPrograms);

            RefreshStartupCommand = new RelayCommand(async _ => await LoadStartupAsync(), _ => !IsBusyStartup);
            ToggleStartupCommand = new RelayCommand(p => { if (p is StartupEntryViewModel e) ToggleStartupEntry(e); }, _ => !IsBusyStartup);
            DisableRecommendedStartupCommand = new RelayCommand(_ => DisableAllSafeStartup(), _ => !IsBusyStartup);
            SetStartupFilterCommand = new RelayCommand(p => { if (p is string f) StartupFilter = f; });
        }

        public string ActiveProgramsSubTab
        {
            get => _activeProgramsSubTab;
            set
            {
                if (_activeProgramsSubTab != value)
                {
                    _activeProgramsSubTab = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsUpdatesTab));
                    OnPropertyChanged(nameof(IsUninstallerTab));
                    OnPropertyChanged(nameof(IsBackupsTab));
                    if (value is "Uninstaller" or "Backups" && !_programsLoadedOnce)
                    {
                        _ = LoadInstalledProgramsAsync();
                    }
                }
            }
        }

        public bool IsUpdatesTab => ActiveProgramsSubTab == "Updates";
        public bool IsUninstallerTab => ActiveProgramsSubTab == "Uninstaller";
        public bool IsBackupsTab => ActiveProgramsSubTab == "Backups";

        public bool IsBusyUpdates
        {
            get => _isBusyUpdates;
            private set
            {
                if (_isBusyUpdates != value)
                {
                    _isBusyUpdates = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBusyPrograms));
                    InvalidateCommands();
                }
            }
        }

        public bool IsBusyUninstaller
        {
            get => _isBusyUninstaller;
            private set
            {
                if (_isBusyUninstaller != value)
                {
                    _isBusyUninstaller = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBusyPrograms));
                    InvalidateCommands();
                }
            }
        }

        public bool IsBusyPrograms => IsBusyUpdates || IsBusyUninstaller;

        public bool IsBusyStartup
        {
            get => _isBusyStartup;
            private set
            {
                if (_isBusyStartup != value)
                {
                    _isBusyStartup = value;
                    OnPropertyChanged();
                    InvalidateCommands();
                }
            }
        }

        public string ProgramsStatusBanner
        {
            get => _programsStatusBanner;
            set
            {
                if (_programsStatusBanner != value)
                {
                    _programsStatusBanner = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasProgramsBanner));
                }
            }
        }

        public bool HasProgramsBanner => !string.IsNullOrWhiteSpace(ProgramsStatusBanner);

        public string StartupStatusBanner
        {
            get => _startupStatusBanner;
            set
            {
                if (_startupStatusBanner != value)
                {
                    _startupStatusBanner = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasStartupBanner));
                }
            }
        }

        public bool HasStartupBanner => !string.IsNullOrWhiteSpace(StartupStatusBanner);

        public string StartupSearch
        {
            get => _startupSearch;
            set { if (_startupSearch != value) { _startupSearch = value; OnPropertyChanged(); ApplyStartupFilter(); } }
        }

        public string StartupFilter
        {
            get => _startupFilter;
            set { if (_startupFilter != value) { _startupFilter = value; OnPropertyChanged(); ApplyStartupFilter(); } }
        }

        public string ProgramSearch
        {
            get => _programSearch;
            set { if (_programSearch != value) { _programSearch = value; OnPropertyChanged(); ApplyProgramFilter(); } }
        }

        public string UpdateSearch
        {
            get => _updateSearch;
            set { if (_updateSearch != value) { _updateSearch = value; OnPropertyChanged(); ApplyUpdateFilter(); } }
        }

        public LeftoverLevel SelectedLeftoverLevel
        {
            get => _selectedLeftoverLevel;
            set { if (_selectedLeftoverLevel != value) { _selectedLeftoverLevel = value; OnPropertyChanged(); } }
        }

        public InstalledProgramItem? SelectedProgram
        {
            get => _selectedProgram;
            set
            {
                if (_selectedProgram != value)
                {
                    _selectedProgram = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelectedProgram));
                    InvalidateCommands();
                }
            }
        }

        public bool HasSelectedProgram => SelectedProgram != null;

        public string LeftoversTitle
        {
            get => _leftoversTitle;
            set { if (_leftoversTitle != value) { _leftoversTitle = value; OnPropertyChanged(); } }
        }

        public int SafeDisableStartupCount => AllStartupEntries.Count(e => e.IsEnabled && e.Advice == StartupAdvice.CanDisable);
        public int ActiveStartupCount => AllStartupEntries.Count(e => e.IsEnabled);
        public int UpdatesCount => AllUpdates.Count(u => !u.IsUpdated);
        public int ProgramsCount => AllPrograms.Count;
        public int LeftoversCount => FoundLeftovers.Count;
        public bool HasLeftovers => FoundLeftovers.Count > 0;

        public ICommand SwitchProgramsTabCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand UpgradeSelectedCommand { get; }
        public ICommand UpgradeSingleCommand { get; }
        public ICommand IgnoreUpdateCommand { get; }
        public ICommand SelectAllUpdatesCommand { get; }
        public ICommand ClearIgnoredUpdatesCommand { get; }
        public ICommand RefreshProgramsCommand { get; }
        public ICommand UninstallSelectedProgramCommand { get; }
        public ICommand ScanLeftoversCommand { get; }
        public ICommand CleanLeftoversCommand { get; }
        public ICommand ForceRemoveProgramEntryCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand CancelProgramsTaskCommand { get; }
        public ICommand RefreshStartupCommand { get; }
        public ICommand ToggleStartupCommand { get; }
        public ICommand DisableRecommendedStartupCommand { get; }
        public ICommand SetStartupFilterCommand { get; }

        public void EnsureStartupLoaded()
        {
            if (!_startupLoadedOnce) _ = LoadStartupAsync();
        }

        public void EnsureProgramsLoaded()
        {
            if (ActiveProgramsSubTab is "Uninstaller" or "Backups" && !_programsLoadedOnce)
            {
                _ = LoadInstalledProgramsAsync();
            }
            else if (ActiveProgramsSubTab == "Updates" && AllUpdates.Count == 0 && !IsBusyUpdates)
            {
                _ = CheckUpdatesAsync();
            }
        }

        public void CancelProgramsTask()
        {
            _programsCts?.Cancel();
            ProgramsStatusBanner = "Cancelando operação...";
        }

        public async Task LoadStartupAsync()
        {
            if (IsBusyStartup) return;
            IsBusyStartup = true;
            _startupLoadedOnce = true;
            StartupStatusBanner = "Lendo itens de inicialização do Windows...";
            try
            {
                var list = await Task.Run(() => _startupService.ListEntries());
                AllStartupEntries.Clear();
                foreach (var entry in list) AllStartupEntries.Add(entry);
                ApplyStartupFilter();

                StartupStatusBanner = $"{list.Count} item(ns) encontrados ({ActiveStartupCount} ativo(s), {SafeDisableStartupCount} podem desligar). Analisando detalhes...";
                await _startupService.EnrichDetailsAsync(list);
                ApplyStartupFilter();

                StartupStatusBanner = $"{list.Count} item(ns) analisados ({ActiveStartupCount} ativo(s), {SafeDisableStartupCount} seguro(s) para desligar).";
                _log($"[INICIALIZAÇÃO] {StartupStatusBanner}");
            }
            catch (Exception ex)
            {
                StartupStatusBanner = $"Erro ao ler inicialização: {ex.Message}";
                _log($"[INICIALIZAÇÃO ERRO] {ex.Message}");
            }
            finally
            {
                IsBusyStartup = false;
            }
        }

        public void ToggleStartupEntry(StartupEntryViewModel entry)
        {
            try
            {
                bool targetState = !entry.IsEnabled;
                _startupService.SetEnabled(entry, targetState);
                ApplyStartupFilter();
                StartupStatusBanner = $"{entry.DisplayName}: {(targetState ? "reativado" : "desligado")} na inicialização.";
                _log($"[INICIALIZAÇÃO] {StartupStatusBanner}");
            }
            catch (Exception ex)
            {
                StartupStatusBanner = $"Falha ao alterar '{entry.DisplayName}' (pode exigir Administrador): {ex.Message}";
                _log($"[INICIALIZAÇÃO ERRO] {entry.DisplayName}: {ex.Message}");
            }
        }

        public void DisableAllSafeStartup()
        {
            var targets = AllStartupEntries.Where(e => e.IsEnabled && e.Advice == StartupAdvice.CanDisable).ToList();
            if (targets.Count == 0)
            {
                StartupStatusBanner = "Nenhum item classificado como 'Pode desligar' está ativo no momento.";
                return;
            }

            int ok = 0;
            int fail = 0;
            foreach (var entry in targets)
            {
                try
                {
                    _startupService.SetEnabled(entry, false);
                    ok++;
                }
                catch
                {
                    fail++;
                }
            }

            ApplyStartupFilter();
            StartupStatusBanner = fail == 0
                ? $"{ok} programa(s) desnecessário(s) desligado(s) da inicialização."
                : $"{ok} desligado(s); {fail} precisaram de permissão de Administrador.";
            _log($"[INICIALIZAÇÃO] Desligamento recomendado: {StartupStatusBanner}");
        }

        public void ApplyStartupFilter()
        {
            string q = (StartupSearch ?? "").Trim();
            FilteredStartupEntries.Clear();

            foreach (var item in AllStartupEntries)
            {
                switch (StartupFilter)
                {
                    case "Pode" when item.Advice != StartupAdvice.CanDisable:
                    case "Ativos" when !item.IsEnabled:
                    case "Desativados" when item.IsEnabled:
                        continue;
                }

                if (!string.IsNullOrEmpty(q) &&
                    !item.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !item.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !item.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !item.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;

                FilteredStartupEntries.Add(item);
            }

            OnPropertyChanged(nameof(ActiveStartupCount));
            OnPropertyChanged(nameof(SafeDisableStartupCount));
        }

        public async Task CheckUpdatesAsync()
        {
            if (IsBusyUpdates) return;
            IsBusyUpdates = true;
            _programsCts = new CancellationTokenSource();
            CancellationToken ct = _programsCts.Token;
            ProgramsStatusBanner = "Consultando Winget para verificar programas desatualizados...";
            try
            {
                var updates = await _updaterService.ListAvailableUpgradesAsync(includeIgnored: false, ct);
                AllUpdates.Clear();
                foreach (var u in updates) AllUpdates.Add(u);
                ApplyUpdateFilter();

                ProgramsStatusBanner = updates.Count == 0
                    ? "Todos os seus programas verificados pelo Winget estão atualizados."
                    : $"{updates.Count} atualização(ões) disponível(eis) encontrada(s).";
                _log($"[ATUALIZADOR] {ProgramsStatusBanner}");
            }
            catch (OperationCanceledException)
            {
                ProgramsStatusBanner = "Consulta de atualizações cancelada.";
            }
            catch (Exception ex)
            {
                ProgramsStatusBanner = $"Erro ao consultar Winget: {ex.Message}";
                _log($"[ATUALIZADOR ERRO] {ex.Message}");
            }
            finally
            {
                _programsCts?.Dispose();
                _programsCts = null;
                IsBusyUpdates = false;
            }
        }

        public async Task UpgradeSelectedAsync()
        {
            if (IsBusyUpdates) return;
            var targets = AllUpdates.Where(u => u.IsSelected && u.CanInteract).ToList();
            if (targets.Count == 0)
            {
                ProgramsStatusBanner = "Nenhum programa selecionado para atualizar.";
                return;
            }

            IsBusyUpdates = true;
            _programsCts = new CancellationTokenSource();
            CancellationToken ct = _programsCts.Token;
            int ok = 0;
            int fail = 0;
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = targets[i];
                    ProgramsStatusBanner = $"Atualizando ({i + 1}/{targets.Count}): {item.Name} ({item.InstalledVersion} → {item.AvailableVersion})...";
                    var (success, msg) = await _updaterService.UpgradeAsync(item, line => _log($"[WINGET] {line}"), ct);
                    if (success) ok++; else fail++;
                    _log($"[ATUALIZADOR] {item.Name}: {msg}");
                }

                OnPropertyChanged(nameof(UpdatesCount));
                ProgramsStatusBanner = fail == 0
                    ? $"{ok} programa(s) atualizado(s) com sucesso!"
                    : $"{ok} atualizado(s), {fail} falha(s) (verifique o Console para detalhes).";
            }
            catch (OperationCanceledException)
            {
                ProgramsStatusBanner = $"Atualização cancelada ({ok} concluído(s)).";
            }
            finally
            {
                _programsCts?.Dispose();
                _programsCts = null;
                IsBusyUpdates = false;
            }
        }

        public async Task UpgradeSingleAsync(ProgramUpdateItemViewModel item)
        {
            if (IsBusyUpdates || !item.CanInteract) return;
            IsBusyUpdates = true;
            _programsCts = new CancellationTokenSource();
            try
            {
                ProgramsStatusBanner = $"Atualizando {item.Name} para {item.AvailableVersion}...";
                var (ok, msg) = await _updaterService.UpgradeAsync(item, line => _log($"[WINGET] {line}"), _programsCts.Token);
                OnPropertyChanged(nameof(UpdatesCount));
                ProgramsStatusBanner = ok ? $"{item.Name}: {msg}" : $"{item.Name} — {msg}";
                _log($"[ATUALIZADOR] {ProgramsStatusBanner}");
            }
            catch (OperationCanceledException)
            {
                ProgramsStatusBanner = $"Atualização de {item.Name} cancelada.";
            }
            finally
            {
                _programsCts?.Dispose();
                _programsCts = null;
                IsBusyUpdates = false;
            }
        }

        public void IgnoreUpdate(ProgramUpdateItemViewModel item)
        {
            _updaterService.IgnoreUpdate(item.Id);
            AllUpdates.Remove(item);
            ApplyUpdateFilter();
            ProgramsStatusBanner = $"Atualização de '{item.Name}' ignorada (use 'Reexibir Ignoradas' se mudar de ideia).";
        }

        public bool AreAllFilteredUpdatesSelected =>
            FilteredUpdates.Any(u => u.CanInteract) &&
            FilteredUpdates.Where(u => u.CanInteract).All(u => u.IsSelected);

        public string SelectAllUpdatesLabel =>
            AreAllFilteredUpdatesSelected ? "☐ Desmarcar Todos" : "☑️ Selecionar Todos";

        public void ToggleAllUpdatesSelection() =>
            SetAllUpdatesSelection(!AreAllFilteredUpdatesSelected);

        public void SetAllUpdatesSelection(bool selected)
        {
            foreach (var u in FilteredUpdates.Where(u => u.CanInteract)) u.IsSelected = selected;
            OnPropertyChanged(nameof(AreAllFilteredUpdatesSelected));
            OnPropertyChanged(nameof(SelectAllUpdatesLabel));
            InvalidateCommands();
        }

        private readonly List<ProgramUpdateItemViewModel> _subscribedUpdates = new();

        private void AllUpdates_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                foreach (var item in _subscribedUpdates)
                    item.PropertyChanged -= UpdateItem_PropertyChanged;
                _subscribedUpdates.Clear();
                OnPropertyChanged(nameof(AreAllFilteredUpdatesSelected));
                OnPropertyChanged(nameof(SelectAllUpdatesLabel));
                InvalidateCommands();
                return;
            }

            if (e.OldItems != null)
            {
                foreach (ProgramUpdateItemViewModel item in e.OldItems)
                {
                    item.PropertyChanged -= UpdateItem_PropertyChanged;
                    _subscribedUpdates.Remove(item);
                }
            }
            if (e.NewItems != null)
            {
                foreach (ProgramUpdateItemViewModel item in e.NewItems)
                {
                    item.PropertyChanged += UpdateItem_PropertyChanged;
                    _subscribedUpdates.Add(item);
                }
            }
        }

        private void UpdateItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ProgramUpdateItemViewModel.IsSelected)
                or nameof(ProgramUpdateItemViewModel.IsUpdated)
                or nameof(ProgramUpdateItemViewModel.IsUpdating))
            {
                OnPropertyChanged(nameof(AreAllFilteredUpdatesSelected));
                OnPropertyChanged(nameof(SelectAllUpdatesLabel));
                InvalidateCommands();
            }
        }

        public void ApplyUpdateFilter()
        {
            string q = (UpdateSearch ?? "").Trim();
            FilteredUpdates.Clear();
            foreach (var u in AllUpdates)
            {
                if (!string.IsNullOrEmpty(q) &&
                    !u.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !u.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
                FilteredUpdates.Add(u);
            }
            OnPropertyChanged(nameof(UpdatesCount));
            OnPropertyChanged(nameof(AreAllFilteredUpdatesSelected));
            OnPropertyChanged(nameof(SelectAllUpdatesLabel));
            InvalidateCommands();
        }

        public async Task LoadInstalledProgramsAsync()
        {
            if (IsBusyUninstaller) return;
            IsBusyUninstaller = true;
            _programsLoadedOnce = true;
            ProgramsStatusBanner = "Carregando lista de programas instalados e backups...";
            try
            {
                var (programs, backups) = await Task.Run(() =>
                    (_uninstallerService.ListInstalledPrograms(), _uninstallerService.LoadBackups()));

                AllPrograms.Clear();
                foreach (var p in programs) AllPrograms.Add(p);
                ApplyProgramFilter();

                UninstallBackups.Clear();
                foreach (var b in backups) UninstallBackups.Add(b);

                ProgramsStatusBanner = $"{programs.Count} programa(s) instalado(s) carregado(s). Selecione um programa para desinstalar ou buscar sobras.";
            }
            catch (Exception ex)
            {
                ProgramsStatusBanner = $"Erro ao listar programas: {ex.Message}";
            }
            finally
            {
                IsBusyUninstaller = false;
            }
        }

        public void ApplyProgramFilter()
        {
            string q = (ProgramSearch ?? "").Trim();
            FilteredPrograms.Clear();
            foreach (var p in AllPrograms)
            {
                if (!string.IsNullOrEmpty(q) &&
                    !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !p.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
                FilteredPrograms.Add(p);
            }
            OnPropertyChanged(nameof(ProgramsCount));
        }

        public async Task UninstallSelectedAsync(bool silent = true)
        {
            var target = SelectedProgram;
            if (target == null || IsBusyUninstaller) return;

            IsBusyUninstaller = true;
            _programsCts = new CancellationTokenSource();
            CancellationToken ct = _programsCts.Token;
            try
            {
                var snapshot = AllPrograms.ToList();
                var res = await _uninstallerService.UninstallProgramAsync(
                    target,
                    preferSilent: silent,
                    msg => SetProgramsBannerThreadSafe(msg),
                    ct);

                _log($"[DESINSTALADOR] {target.Name}: {res.Message}");
                if (!res.Ok)
                {
                    ProgramsStatusBanner = res.Message;
                    return;
                }

                ProgramsStatusBanner = $"{res.Message} Procurando sobras de {target.Name}...";
                var leftovers = await Task.Run(() =>
                    _uninstallerService.FindLeftovers(target, SelectedLeftoverLevel, snapshot), ct);

                FoundLeftovers.Clear();
                foreach (var l in leftovers) FoundLeftovers.Add(l);
                OnPropertyChanged(nameof(LeftoversCount));
                OnPropertyChanged(nameof(HasLeftovers));

                LeftoversTitle = leftovers.Count == 0
                    ? $"{target.Name}: nenhuma sobra encontrada!"
                    : $"{target.Name}: {leftovers.Count} sobra(s) encontrada(s) — revise e clique em Limpar Sobras.";
                ProgramsStatusBanner = LeftoversTitle;

                AllPrograms.Remove(target);
                ApplyProgramFilter();
                SelectedProgram = null;
            }
            finally
            {
                _programsCts?.Dispose();
                _programsCts = null;
                IsBusyUninstaller = false;
            }
        }

        public async Task ScanLeftoversForSelectedAsync()
        {
            var target = SelectedProgram;
            if (target == null || IsBusyUninstaller) return;

            IsBusyUninstaller = true;
            ProgramsStatusBanner = $"Analisando pastas e chaves de {target.Name}...";
            try
            {
                var snapshot = AllPrograms.ToList();
                var leftovers = await Task.Run(() =>
                    _uninstallerService.FindLeftovers(target, SelectedLeftoverLevel, snapshot));

                FoundLeftovers.Clear();
                foreach (var l in leftovers) FoundLeftovers.Add(l);
                OnPropertyChanged(nameof(LeftoversCount));
                OnPropertyChanged(nameof(HasLeftovers));

                LeftoversTitle = leftovers.Count == 0
                    ? $"Nenhuma sobra detectada para '{target.Name}' no nível atual."
                    : $"Sobras de '{target.Name}': {leftovers.Count} item(ns) encontrados.";
                ProgramsStatusBanner = LeftoversTitle;
            }
            finally
            {
                IsBusyUninstaller = false;
            }
        }

        public async Task CleanSelectedLeftoversAsync()
        {
            if (IsBusyUninstaller) return;
            var selected = FoundLeftovers.Where(l => l.IsSelected).ToList();
            if (selected.Count == 0)
            {
                ProgramsStatusBanner = "Nenhuma sobra marcada para limpeza.";
                return;
            }

            IsBusyUninstaller = true;
            ProgramsStatusBanner = $"Limpando {selected.Count} sobra(s) (backup .reg + Lixeira)...";
            try
            {
                var result = await _uninstallerService.CleanLeftoversAsync(LeftoversTitle, selected, sendToRecycleBin: true);
                foreach (var item in selected)
                {
                    if (!result.Errors.Any(e => e.StartsWith(item.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        FoundLeftovers.Remove(item);
                    }
                }
                OnPropertyChanged(nameof(LeftoversCount));
                OnPropertyChanged(nameof(HasLeftovers));

                var backups = await Task.Run(() => _uninstallerService.LoadBackups());
                UninstallBackups.Clear();
                foreach (var b in backups) UninstallBackups.Add(b);

                ProgramsStatusBanner = result.Errors.Count == 0
                    ? $"{result.RemovedCount} sobra(s) limpa(s) com segurança (pastas na Lixeira e chaves salvas em Backup)."
                    : $"{result.RemovedCount} limpa(s), {result.Errors.Count} aviso(s): {result.Errors.First()}";
                _log($"[DESINSTALADOR SOBRAS] {ProgramsStatusBanner}");
            }
            finally
            {
                IsBusyUninstaller = false;
            }
        }

        public async Task ForceRemoveSelectedEntryAsync()
        {
            var target = SelectedProgram;
            if (target == null || IsBusyUninstaller) return;

            IsBusyUninstaller = true;
            ProgramsStatusBanner = $"Removendo entrada órfã de '{target.Name}' com backup...";
            try
            {
                var regItem = new ProgramLeftoverItem
                {
                    Kind = LeftoverKind.RegistryKey,
                    Level = LeftoverLevel.Safe,
                    Path = target.RegistryPath,
                    Reason = "Entrada na lista de programas",
                    IsSelected = true
                };
                var res = await _uninstallerService.CleanLeftoversAsync($"Entrada_{target.Name}", new[] { regItem });
                if (res.Errors.Count == 0)
                {
                    AllPrograms.Remove(target);
                    ApplyProgramFilter();
                    SelectedProgram = null;
                    ProgramsStatusBanner = $"Entrada de '{target.Name}' removida da lista (backup salvo).";
                }
                else
                {
                    ProgramsStatusBanner = $"Não foi possível remover a entrada: {res.Errors.First()}";
                }
            }
            finally
            {
                IsBusyUninstaller = false;
            }
        }

        public async Task RestoreBackupAsync(UninstallBackupInfo backup)
        {
            if (IsBusyUninstaller) return;
            IsBusyUninstaller = true;
            ProgramsStatusBanner = $"Restaurando chaves de '{backup.Title}'...";
            try
            {
                var (_, msg) = await _uninstallerService.RestoreBackupAsync(backup);
                ProgramsStatusBanner = msg;
                _log($"[DESINSTALADOR BACKUP] {msg}");
            }
            finally
            {
                IsBusyUninstaller = false;
            }
        }

        private void SetProgramsBannerThreadSafe(string text)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(() => ProgramsStatusBanner = text);
                return;
            }
            ProgramsStatusBanner = text;
        }

        private void InvalidateCommands()
        {
            (CheckUpdatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UpgradeSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UpgradeSingleCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SelectAllUpdatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshProgramsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UninstallSelectedProgramCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ScanLeftoversCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CleanLeftoversCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ForceRemoveProgramEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RestoreBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelProgramsTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshStartupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleStartupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisableRecommendedStartupCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

