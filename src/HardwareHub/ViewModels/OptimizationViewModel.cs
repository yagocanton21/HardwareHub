using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels
{
    public sealed class OptimizationViewModel : INotifyPropertyChanged
    {
        private readonly TweakEngine _tweakEngine;
        private readonly BloatwareService _bloatwareService;
        private readonly PostFormatService _postFormatService;
        private readonly SoftwareService _softwareService;
        private readonly Action<string> _log;

        private string _activeSubTab = "Perfis";
        private string _tweakSearch = "";
        private string _selectedTweakCategory = "Todas";
        private bool _showOnlyActiveTweaks;
        private string _bloatwareSearch = "";
        private bool _alsoRemoveForNewUsers = true;
        private bool _showProtectedBloatware;
        private bool _isScanningBloatware;
        private bool _isApplyingProfile;
        private string _optimizationStatusBanner = "";
        private CancellationTokenSource? _optCts;

        private string _selectedProfileId = "equilibrado";
        private string _selectedKitId = "essencial";
        private bool _isRunningPostFormat;
        private string _postFormatSummary = "Selecione as etapas, o perfil e o kit desejado e clique em Iniciar Pós-Formatação.";
        private WindowsActivationStatus? _activationStatus;
        private CancellationTokenSource? _postFormatCts;

        public ObservableCollection<OptimizationProfile> Profiles { get; } = new();
        public ObservableCollection<TweakItemViewModel> AllTweaks { get; } = new();
        public ObservableCollection<TweakItemViewModel> FilteredTweaks { get; } = new();
        public ObservableCollection<TweakItemViewModel> UndoableTweaks { get; } = new();
        public ObservableCollection<string> TweakCategories { get; } = new() { "Todas" };

        public ObservableCollection<BloatwareItemViewModel> ScannedBloatware { get; } = new();
        public ObservableCollection<BloatwareItemViewModel> FilteredBloatware { get; } = new();
        public ObservableCollection<RemovedBloatwareRecord> RemovedHistory { get; } = new();

        public ObservableCollection<SoftwareKit> SoftwareKits { get; } = new();
        public ObservableCollection<PostFormatStepViewModel> PostFormatSteps { get; } = new();

        public OptimizationViewModel(
            TweakEngine? tweakEngine = null,
            BloatwareService? bloatwareService = null,
            PostFormatService? postFormatService = null,
            SoftwareService? softwareService = null,
            Action<string>? logCallback = null)
        {
            _tweakEngine = tweakEngine ?? new TweakEngine();
            _bloatwareService = bloatwareService ?? new BloatwareService();
            _postFormatService = postFormatService ?? new PostFormatService();
            _softwareService = softwareService ?? new SoftwareService();
            _log = logCallback ?? (_ => { });

            foreach (var profile in OptimizationProfiles.BuildProfiles(_tweakEngine.Tweaks, _bloatwareService.Catalog))
            {
                Profiles.Add(profile);
            }

            foreach (var kit in PostFormatService.GetSoftwareKits())
            {
                SoftwareKits.Add(kit);
            }

            foreach (var step in PostFormatService.CreateDefaultSteps())
            {
                PostFormatSteps.Add(step);
            }

            LoadTweaksCatalog();
            RefreshUndoableTweaks();
            RefreshRemovedHistory();

            SwitchSubTabCommand = new RelayCommand(p => { if (p is string tab) ActiveSubTab = tab; });
            ToggleTweakCommand = new RelayCommand(async p => { if (p is TweakItemViewModel item) await ToggleTweakAsync(item); });
            ApplyRecommendedTweaksCommand = new RelayCommand(async _ => await ApplyRecommendedTweaksAsync(), _ => !IsApplyingProfile);
            ApplyProfileCommand = new RelayCommand(async p => { if (p is OptimizationProfile profile) await ApplyProfileAsync(profile); }, _ => !IsApplyingProfile);
            CancelOptimizationCommand = new RelayCommand(_ => CancelOptimization(), _ => IsBusyOptimization);
            RevertSingleBackupCommand = new RelayCommand(async p => { if (p is TweakItemViewModel item) await RevertTweakAsync(item); });
            RevertAllBackupsCommand = new RelayCommand(async _ => await RevertAllTweaksAsync(), _ => UndoableTweaks.Count > 0 && !IsApplyingProfile);
            RestartExplorerCommand = new RelayCommand(_ => RestartExplorer());

            ScanBloatwareCommand = new RelayCommand(async _ => await ScanBloatwareAsync(), _ => !IsScanningBloatware);
            RemoveSelectedBloatwareCommand = new RelayCommand(async _ => await RemoveSelectedBloatwareAsync(), _ => !IsScanningBloatware);
            SelectRecommendedBloatwareCommand = new RelayCommand(_ => SelectRecommendedBloatware());
            ReinstallBloatwareCommand = new RelayCommand(async p => { if (p is RemovedBloatwareRecord rec) await ReinstallBloatwareAsync(rec); });

            RefreshActivationCommand = new RelayCommand(async _ => await RefreshActivationStatusAsync());
            RunPostFormatCommand = new RelayCommand(async _ => await RunPostFormatAsync(), _ => !IsRunningPostFormat);
            CancelPostFormatCommand = new RelayCommand(_ => CancelPostFormat(), _ => IsRunningPostFormat);
            OpenWindowsUpdateCommand = new RelayCommand(_ => PostFormatService.OpenWindowsUpdateSettings());
        }

        public string ActiveSubTab
        {
            get => _activeSubTab;
            set
            {
                if (_activeSubTab != value)
                {
                    _activeSubTab = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPerfisTab));
                    OnPropertyChanged(nameof(IsTweaksTab));
                    OnPropertyChanged(nameof(IsBloatwareTab));
                    OnPropertyChanged(nameof(IsUndoTab));
                }
            }
        }

        public bool IsPerfisTab => ActiveSubTab == "Perfis";
        public bool IsTweaksTab => ActiveSubTab == "Tweaks";
        public bool IsBloatwareTab => ActiveSubTab == "Bloatware";
        public bool IsUndoTab => ActiveSubTab == "Desfazer";

        public string TweakSearch
        {
            get => _tweakSearch;
            set { if (_tweakSearch != value) { _tweakSearch = value; OnPropertyChanged(); ApplyTweakFilter(); } }
        }

        public string SelectedTweakCategory
        {
            get => _selectedTweakCategory;
            set { if (_selectedTweakCategory != value) { _selectedTweakCategory = value; OnPropertyChanged(); ApplyTweakFilter(); } }
        }

        public bool ShowOnlyActiveTweaks
        {
            get => _showOnlyActiveTweaks;
            set { if (_showOnlyActiveTweaks != value) { _showOnlyActiveTweaks = value; OnPropertyChanged(); ApplyTweakFilter(); } }
        }

        public string BloatwareSearch
        {
            get => _bloatwareSearch;
            set { if (_bloatwareSearch != value) { _bloatwareSearch = value; OnPropertyChanged(); ApplyBloatwareFilter(); } }
        }

        public bool AlsoRemoveForNewUsers
        {
            get => _alsoRemoveForNewUsers;
            set { if (_alsoRemoveForNewUsers != value) { _alsoRemoveForNewUsers = value; OnPropertyChanged(); } }
        }

        public bool ShowProtectedBloatware
        {
            get => _showProtectedBloatware;
            set { if (_showProtectedBloatware != value) { _showProtectedBloatware = value; OnPropertyChanged(); ApplyBloatwareFilter(); } }
        }

        public bool IsScanningBloatware
        {
            get => _isScanningBloatware;
            set
            {
                if (_isScanningBloatware != value)
                {
                    _isScanningBloatware = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBusyOptimization));
                    InvalidateCommands();
                }
            }
        }

        public bool IsApplyingProfile
        {
            get => _isApplyingProfile;
            set
            {
                if (_isApplyingProfile != value)
                {
                    _isApplyingProfile = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBusyOptimization));
                    InvalidateCommands();
                }
            }
        }

        public bool IsBusyOptimization => IsApplyingProfile || IsScanningBloatware;

        public string OptimizationStatusBanner
        {
            get => _optimizationStatusBanner;
            set
            {
                if (_optimizationStatusBanner != value)
                {
                    _optimizationStatusBanner = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasStatusBanner));
                }
            }
        }

        public bool HasStatusBanner => !string.IsNullOrWhiteSpace(OptimizationStatusBanner);

        public string SelectedProfileId
        {
            get => _selectedProfileId;
            set
            {
                if (_selectedProfileId != value)
                {
                    _selectedProfileId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedProfileSummary));
                }
            }
        }

        public string SelectedKitId
        {
            get => _selectedKitId;
            set
            {
                if (_selectedKitId != value)
                {
                    _selectedKitId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedKitDescription));
                    OnPropertyChanged(nameof(SelectedKitProgramsHeader));
                    OnPropertyChanged(nameof(SelectedKitPrograms));
                }
            }
        }

        public string SelectedProfileSummary
        {
            get
            {
                var p = Profiles.FirstOrDefault(x => x.Id == SelectedProfileId);
                return p == null
                    ? "Todos os ajustes salvam backup antes de alterar o registro."
                    : $"{p.Resumo} ({p.TweakIds.Count} ajustes · {p.BloatwareIds.Count} bloatwares removíveis)";
            }
        }

        public string SelectedKitDescription
        {
            get
            {
                var kit = SoftwareKits.FirstOrDefault(x => x.Id == SelectedKitId);
                return kit?.Description ?? "Instala apenas os programas do kit que ainda faltam no computador.";
            }
        }

        public string SelectedKitProgramsHeader
        {
            get
            {
                var kit = SoftwareKits.FirstOrDefault(x => x.Id == SelectedKitId);
                return kit == null
                    ? "Programas incluídos:"
                    : $"Programas incluídos ({kit.SoftwareIds.Count}) — apenas os que faltam serão instalados:";
            }
        }

        public IReadOnlyList<string> SelectedKitPrograms
        {
            get
            {
                var kit = SoftwareKits.FirstOrDefault(x => x.Id == SelectedKitId);
                if (kit == null)
                {
                    return Array.Empty<string>();
                }

                var byId = _softwareService.GetCatalog()
                    .ToDictionary(s => s.Id, s => s.Name, StringComparer.OrdinalIgnoreCase);

                return kit.SoftwareIds
                    .Select(id => byId.TryGetValue(id, out var name) ? name : id)
                    .ToList();
            }
        }

        public bool IsRunningPostFormat
        {
            get => _isRunningPostFormat;
            set
            {
                if (_isRunningPostFormat != value)
                {
                    _isRunningPostFormat = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanStartPostFormat));
                    InvalidateCommands();
                }
            }
        }

        public bool CanStartPostFormat => !IsRunningPostFormat;

        public string PostFormatSummary
        {
            get => _postFormatSummary;
            set { if (_postFormatSummary != value) { _postFormatSummary = value; OnPropertyChanged(); } }
        }

        public WindowsActivationStatus? ActivationStatus
        {
            get => _activationStatus;
            private set
            {
                _activationStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActivationSummaryText));
            }
        }

        public string ActivationSummaryText => ActivationStatus?.Summary ?? "Clique em 'Checar Licença' para consultar a ativação.";

        public int ActiveTweaksCount => AllTweaks.Count(t => t.IsActive);
        public int UndoableCount => UndoableTweaks.Count;

        public ICommand SwitchSubTabCommand { get; }
        public ICommand ToggleTweakCommand { get; }
        public ICommand ApplyRecommendedTweaksCommand { get; }
        public ICommand ApplyProfileCommand { get; }
        public ICommand CancelOptimizationCommand { get; }
        public ICommand RevertSingleBackupCommand { get; }
        public ICommand RevertAllBackupsCommand { get; }
        public ICommand RestartExplorerCommand { get; }
        public ICommand ScanBloatwareCommand { get; }
        public ICommand RemoveSelectedBloatwareCommand { get; }
        public ICommand SelectRecommendedBloatwareCommand { get; }
        public ICommand ReinstallBloatwareCommand { get; }
        public ICommand RefreshActivationCommand { get; }
        public ICommand RunPostFormatCommand { get; }
        public ICommand CancelPostFormatCommand { get; }
        public ICommand OpenWindowsUpdateCommand { get; }

        private void LoadTweaksCatalog()
        {
            AllTweaks.Clear();
            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (TweakDefinition def in _tweakEngine.Tweaks)
            {
                if (!_tweakEngine.IsApplicable(def)) continue;
                var vm = new TweakItemViewModel(def)
                {
                    IsActive = _tweakEngine.IsActive(def),
                    HasBackup = _tweakEngine.HasBackup(def.Id),
                    AppliedAt = _tweakEngine.GetAppliedAt(def.Id)
                };
                AllTweaks.Add(vm);
                if (!string.IsNullOrWhiteSpace(def.Categoria))
                {
                    categories.Add(def.Categoria);
                }
            }

            TweakCategories.Clear();
            TweakCategories.Add("Todas");
            foreach (string cat in categories.OrderBy(c => c))
            {
                TweakCategories.Add(cat);
            }

            ApplyTweakFilter();
        }

        public void ApplyTweakFilter()
        {
            string query = (TweakSearch ?? "").Trim();
            FilteredTweaks.Clear();

            foreach (var item in AllTweaks)
            {
                if (SelectedTweakCategory != "Todas" &&
                    !string.Equals(item.Categoria, SelectedTweakCategory, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ShowOnlyActiveTweaks && !item.IsActive)
                    continue;

                if (!string.IsNullOrEmpty(query) &&
                    !item.Titulo.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !item.Descricao.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !item.Categoria.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                FilteredTweaks.Add(item);
            }

            OnPropertyChanged(nameof(ActiveTweaksCount));
        }

        public void ApplyBloatwareFilter()
        {
            string query = (BloatwareSearch ?? "").Trim();
            FilteredBloatware.Clear();

            foreach (var item in ScannedBloatware)
            {
                if (!ShowProtectedBloatware && item.IsProtected) continue;
                if (!string.IsNullOrEmpty(query) &&
                    !item.Nome.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !item.Descricao.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !item.Categoria.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                FilteredBloatware.Add(item);
            }
        }

        public async Task ToggleTweakAsync(TweakItemViewModel item)
        {
            if (item.IsBusy) return;
            item.IsBusy = true;
            try
            {
                bool wasActive = item.IsActive;
                await Task.Run(() =>
                {
                    if (wasActive)
                        _tweakEngine.Revert(item.Definition);
                    else
                        _tweakEngine.Apply(item.Definition);
                });

                RefreshTweakStates();
                string verb = wasActive ? "Revertido" : "Aplicado";
                OptimizationStatusBanner = $"{verb}: {item.Titulo}";
                _log($"[TWEAK] {verb}: {item.Titulo}");
            }
            catch (Exception ex)
            {
                OptimizationStatusBanner = $"Erro em '{item.Titulo}': {ex.Message}";
                _log($"[TWEAK ERRO] {item.Titulo}: {ex.Message}");
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        public async Task RevertTweakAsync(TweakItemViewModel item)
        {
            if (item.IsBusy) return;
            item.IsBusy = true;
            try
            {
                await Task.Run(() => _tweakEngine.Revert(item.Definition));
                RefreshTweakStates();
                OptimizationStatusBanner = $"Backup restaurado: {item.Titulo}";
                _log($"[DESFAZER] Restaurado: {item.Titulo}");
            }
            catch (Exception ex)
            {
                OptimizationStatusBanner = $"Erro ao restaurar '{item.Titulo}': {ex.Message}";
                _log($"[DESFAZER ERRO] {item.Titulo}: {ex.Message}");
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        public void CancelOptimization()
        {
            _optCts?.Cancel();
            OptimizationStatusBanner = "Cancelando operação...";
        }

        private void InvalidateCommands()
        {
            (ApplyRecommendedTweaksCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelOptimizationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RevertAllBackupsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ScanBloatwareCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveSelectedBloatwareCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RunPostFormatCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelPostFormatCommand as RelayCommand)?.RaiseCanExecuteChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        private void SetBannerThreadSafe(string text)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(() => OptimizationStatusBanner = text);
                return;
            }
            OptimizationStatusBanner = text;
        }

        public async Task ApplyRecommendedTweaksAsync()
        {
            if (IsApplyingProfile) return;
            var recommended = AllTweaks.Where(t => t.Recomendado && !t.IsActive).ToList();
            if (recommended.Count == 0)
            {
                OptimizationStatusBanner = "Todos os ajustes recomendados já estão ativos.";
                return;
            }

            IsApplyingProfile = true;
            _optCts = new CancellationTokenSource();
            CancellationToken ct = _optCts.Token;
            OptimizationStatusBanner = $"Aplicando {recommended.Count} ajustes recomendados...";
            int applied = 0;
            int failed = 0;
            try
            {
                await Task.Run(() =>
                {
                    foreach (var vm in recommended)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            _tweakEngine.Apply(vm.Definition);
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            _log($"[TWEAK ERRO] {vm.Titulo}: {ex.Message}");
                        }
                    }
                }, ct);

                RefreshTweakStates();
                OptimizationStatusBanner = failed == 0
                    ? $"{applied} ajustes recomendados aplicados com backup para reversão."
                    : $"{applied} ajustes aplicados; {failed} precisaram de privilégios de Administrador.";
                _log($"[TWEAK] Recomendados: {applied} aplicado(s), {failed} falha(s).");
            }
            catch (OperationCanceledException)
            {
                RefreshTweakStates();
                OptimizationStatusBanner = $"Operação cancelada ({applied} ajustes já salvos na Central Desfazer).";
            }
            finally
            {
                _optCts.Dispose();
                _optCts = null;
                IsApplyingProfile = false;
            }
        }

        public async Task<(int TweaksApplied, int TweaksFailed, int BloatRemoved)> ApplyProfileAsync(
            OptimizationProfile profile,
            CancellationToken ct = default)
        {
            if (IsApplyingProfile) return (0, 0, 0);
            IsApplyingProfile = true;
            _optCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            CancellationToken effectiveCt = _optCts.Token;
            OptimizationStatusBanner = $"Aplicando perfil '{profile.Nome}' (ajustes do Windows)...";

            int tweaksOk = 0;
            int tweaksFail = 0;
            int bloatOk = 0;
            try
            {
                _log($"[PERFIL] Aplicando perfil '{profile.Nome}'...");
                var tweakSet = profile.TweakIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

                await Task.Run(() =>
                {
                    foreach (var vm in AllTweaks.Where(t => tweakSet.Contains(t.Id)))
                    {
                        effectiveCt.ThrowIfCancellationRequested();
                        if (vm.IsActive)
                        {
                            tweaksOk++;
                            continue;
                        }

                        try
                        {
                            _tweakEngine.Apply(vm.Definition);
                            tweaksOk++;
                        }
                        catch (Exception ex)
                        {
                            tweaksFail++;
                            _log($"[PERFIL AVISO] Tweak '{vm.Titulo}' falhou: {ex.Message}");
                        }
                    }
                }, effectiveCt);

                // Atualiza imediatamente os contadores (Ajustes ativos e Central Desfazer)
                RefreshTweakStates();

                if (profile.BloatwareIds.Count > 0)
                {
                    if (ScannedBloatware.Count == 0)
                    {
                        OptimizationStatusBanner = $"Perfil '{profile.Nome}': {tweaksOk} ajustes aplicados. Varrendo aplicativos...";
                        await ScanBloatwareAsync(effectiveCt);
                    }

                    var bloatSet = profile.BloatwareIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var targets = ScannedBloatware
                        .Where(b => bloatSet.Contains(b.Id) && b.CanRemove && (b.IsInstalled || (AlsoRemoveForNewUsers && b.IsProvisioned)))
                        .ToList();

                    if (targets.Count > 0)
                    {
                        OptimizationStatusBanner = $"Perfil '{profile.Nome}': removendo {targets.Count} aplicativos em lote...";
                        bloatOk = await _bloatwareService.RemoveBatchAsync(
                            targets,
                            AlsoRemoveForNewUsers,
                            (done, total, app) =>
                            {
                                SetBannerThreadSafe($"Perfil '{profile.Nome}': removendo aplicativos ({done}/{total}) — {app.Nome}");
                                _log($"[BLOATWARE] {app.Nome}: {app.StatusText}");
                            },
                            effectiveCt);
                    }
                }

                RefreshRemovedHistory();
                OptimizationStatusBanner = $"Perfil '{profile.Nome}' concluído: {tweaksOk} ajustes aplicados" +
                                           (bloatOk > 0 ? $", {bloatOk} apps removidos" : "") +
                                           (tweaksFail > 0 ? $" ({tweaksFail} falharam)" : ".");
                _log($"[PERFIL] {OptimizationStatusBanner}");
                return (tweaksOk, tweaksFail, bloatOk);
            }
            catch (OperationCanceledException)
            {
                RefreshTweakStates();
                RefreshRemovedHistory();
                OptimizationStatusBanner = $"Perfil '{profile.Nome}' cancelado ({tweaksOk} ajustes salvos na Central Desfazer).";
                _log($"[PERFIL] {OptimizationStatusBanner}");
                return (tweaksOk, tweaksFail, bloatOk);
            }
            finally
            {
                _optCts?.Dispose();
                _optCts = null;
                IsApplyingProfile = false;
            }
        }

        public async Task RevertAllTweaksAsync()
        {
            IsApplyingProfile = true;
            try
            {
                int count = await Task.Run(() => _tweakEngine.RevertAll());
                RefreshTweakStates();
                OptimizationStatusBanner = count > 0
                    ? $"{count} ajuste(s) revertido(s) ao estado original salvo no backup."
                    : "Nenhum ajuste pendente para reverter.";
                _log($"[DESFAZER] {OptimizationStatusBanner}");
            }
            catch (Exception ex)
            {
                OptimizationStatusBanner = $"Erro ao desfazer tudo: {ex.Message}";
                _log($"[DESFAZER ERRO] {ex.Message}");
            }
            finally
            {
                IsApplyingProfile = false;
            }
        }

        public void RefreshTweakStates()
        {
            foreach (var vm in AllTweaks)
            {
                vm.IsActive = _tweakEngine.IsActive(vm.Definition);
                vm.HasBackup = _tweakEngine.HasBackup(vm.Id);
                vm.AppliedAt = _tweakEngine.GetAppliedAt(vm.Id);
            }
            RefreshUndoableTweaks();
            ApplyTweakFilter();
        }

        private void RefreshUndoableTweaks()
        {
            UndoableTweaks.Clear();
            var byId = AllTweaks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var (tweak, _) in _tweakEngine.GetAppliedBackups())
            {
                if (byId.TryGetValue(tweak.Id, out var vm))
                {
                    UndoableTweaks.Add(vm);
                }
            }
            OnPropertyChanged(nameof(UndoableCount));
        }

        public async Task ScanBloatwareAsync(CancellationToken ct = default)
        {
            if (IsScanningBloatware) return;
            IsScanningBloatware = true;
            try
            {
                var found = await _bloatwareService.ScanAsync(includeProtected: true, ct);
                ScannedBloatware.Clear();
                foreach (var item in found)
                {
                    ScannedBloatware.Add(item);
                }
                ApplyBloatwareFilter();
                int removableCount = found.Count(x => x.CanRemove);
                OptimizationStatusBanner = $"Varredura concluída: {found.Count} apps encontrados ({removableCount} removíveis).";
                _log($"[BLOATWARE] {OptimizationStatusBanner}");
            }
            catch (Exception ex)
            {
                OptimizationStatusBanner = $"Erro na varredura de bloatware: {ex.Message}";
                _log($"[BLOATWARE ERRO] {ex.Message}");
            }
            finally
            {
                IsScanningBloatware = false;
            }
        }

        public void SelectRecommendedBloatware()
        {
            foreach (var item in ScannedBloatware)
            {
                item.IsSelected = item.Level == BloatwareRecommendation.CanRemove;
            }
        }

        public async Task RemoveSelectedBloatwareAsync()
        {
            if (IsScanningBloatware) return;
            var targets = ScannedBloatware.Where(x => x.IsSelected && x.CanRemove && x.IsPresent).ToList();
            if (targets.Count == 0)
            {
                OptimizationStatusBanner = "Nenhum aplicativo removível selecionado.";
                return;
            }

            IsScanningBloatware = true;
            _optCts = new CancellationTokenSource();
            CancellationToken ct = _optCts.Token;
            OptimizationStatusBanner = $"Removendo {targets.Count} aplicativo(s)...";
            int okCount = 0;
            try
            {
                okCount = await _bloatwareService.RemoveBatchAsync(
                    targets,
                    AlsoRemoveForNewUsers,
                    (done, total, app) =>
                    {
                        SetBannerThreadSafe($"Removendo aplicativos ({done}/{total}) — {app.Nome}");
                        _log($"[BLOATWARE] {app.Nome}: {app.StatusText}");
                    },
                    ct);

                RefreshRemovedHistory();
                OptimizationStatusBanner = $"{okCount} de {targets.Count} app(s) removidos.";
            }
            catch (OperationCanceledException)
            {
                RefreshRemovedHistory();
                OptimizationStatusBanner = "Remoção de bloatware cancelada.";
            }
            finally
            {
                _optCts.Dispose();
                _optCts = null;
                IsScanningBloatware = false;
            }
        }

        public async Task ReinstallBloatwareAsync(RemovedBloatwareRecord record)
        {
            var (ok, msg) = await _bloatwareService.ReinstallAsync(record);
            RefreshRemovedHistory();
            OptimizationStatusBanner = msg;
            _log($"[BLOATWARE] {(ok ? "Reinstalado" : "Aviso")}: {msg}");
        }

        private void RefreshRemovedHistory()
        {
            RemovedHistory.Clear();
            foreach (var item in _bloatwareService.LoadHistory())
            {
                RemovedHistory.Add(item);
            }
        }

        private void RestartExplorer()
        {
            _log("[SISTEMA] Reiniciando Windows Explorer para aplicar alterações visuais...");
            Task.Run(TweakEngine.RestartExplorer);
        }

        public async Task RefreshActivationStatusAsync()
        {
            var status = await Task.Run(() => _postFormatService.GetWindowsActivationStatus());
            ActivationStatus = status;
            _log($"[ATIVAÇÃO] {status.Summary}");
        }

        public void CancelPostFormat()
        {
            _postFormatCts?.Cancel();
            PostFormatSummary = "Cancelando etapa atual...";
        }

        public async Task RunPostFormatAsync()
        {
            if (IsRunningPostFormat) return;
            IsRunningPostFormat = true;
            _postFormatCts = new CancellationTokenSource();
            CancellationToken ct = _postFormatCts.Token;

            foreach (var step in PostFormatSteps)
            {
                step.State = step.IsEnabled ? PostFormatStepState.Pending : PostFormatStepState.Skipped;
                step.StatusText = step.IsEnabled ? "Aguardando" : "Ignorada";
                step.Detail = "";
            }

            int completed = 0;
            int warnings = 0;
            try
            {
                foreach (var step in PostFormatSteps)
                {
                    if (!step.IsEnabled) continue;
                    ct.ThrowIfCancellationRequested();

                    step.State = PostFormatStepState.Running;
                    step.StatusText = "Executando...";
                    PostFormatSummary = $"Etapa {step.Order}/6: {step.Title}...";

                    try
                    {
                        switch (step.Id)
                        {
                            case "restore_point":
                            {
                                var (ok, msg) = await PostFormatService.CreateRestorePointAsync("Hardware HUB Pos-Formatacao", ct);
                                step.State = ok ? PostFormatStepState.Success : PostFormatStepState.Warning;
                                step.StatusText = ok ? "Criado" : "Aviso";
                                step.Detail = msg;
                                if (!ok) warnings++; else completed++;
                                _log($"[PÓS-FORMATAÇÃO] {msg}");
                                break;
                            }
                            case "activation_check":
                            {
                                await RefreshActivationStatusAsync();
                                var act = ActivationStatus!;
                                step.State = act.IsActivated ? PostFormatStepState.Success : PostFormatStepState.Warning;
                                step.StatusText = act.IsActivated ? "Ativado" : "Não ativado";
                                step.Detail = act.Summary;
                                if (!act.IsActivated) warnings++; else completed++;
                                break;
                            }
                            case "windows_update":
                            {
                                var (pending, reboot, summary) = await _postFormatService.CheckWindowsUpdateAsync(ct);
                                step.State = reboot ? PostFormatStepState.Warning : PostFormatStepState.Success;
                                step.StatusText = pending > 0 ? $"{pending} pendente(s)" : "Verificado";
                                step.Detail = summary;
                                if (reboot) warnings++; else completed++;
                                _log($"[PÓS-FORMATAÇÃO] Windows Update: {summary}");
                                break;
                            }
                            case "remove_bloat":
                            {
                                await ScanBloatwareAsync(ct);
                                var removable = ScannedBloatware.Where(x => x.Level == BloatwareRecommendation.CanRemove && x.IsPresent).ToList();
                                int removed = removable.Count == 0
                                    ? 0
                                    : await _bloatwareService.RemoveBatchAsync(
                                        removable,
                                        AlsoRemoveForNewUsers,
                                        (done, total, app) =>
                                        {
                                            var dispatcher = System.Windows.Application.Current?.Dispatcher;
                                            if (dispatcher != null && !dispatcher.CheckAccess())
                                                dispatcher.BeginInvoke(() => step.Detail = $"Removendo {done}/{total}: {app.Nome}...");
                                            else
                                                step.Detail = $"Removendo {done}/{total}: {app.Nome}...";
                                        },
                                        ct);
                                RefreshRemovedHistory();
                                step.State = PostFormatStepState.Success;
                                step.StatusText = $"{removed} removidos";
                                step.Detail = removable.Count == 0
                                    ? "Nenhum bloatware recomendado encontrado."
                                    : $"{removed} de {removable.Count} apps removidos com segurança.";
                                completed++;
                                break;
                            }
                            case "apply_profile":
                            {
                                var profile = Profiles.FirstOrDefault(p => p.Id == SelectedProfileId) ?? Profiles.FirstOrDefault();
                                if (profile == null)
                                {
                                    step.State = PostFormatStepState.Skipped;
                                    step.StatusText = "Sem perfil";
                                    break;
                                }

                                var tweakSet = profile.TweakIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                                int okCount = 0;
                                int failCount = 0;
                                await Task.Run(() =>
                                {
                                    foreach (var vm in AllTweaks.Where(t => tweakSet.Contains(t.Id)))
                                    {
                                        ct.ThrowIfCancellationRequested();
                                        if (vm.IsActive) { okCount++; continue; }
                                        try { _tweakEngine.Apply(vm.Definition); okCount++; }
                                        catch { failCount++; }
                                    }
                                }, ct);
                                RefreshTweakStates();

                                step.State = failCount == 0 ? PostFormatStepState.Success : PostFormatStepState.Warning;
                                step.StatusText = $"{okCount} ajustes";
                                step.Detail = $"Perfil '{profile.Nome}' aplicado" + (failCount > 0 ? $" ({failCount} exigem Admin)." : " com backup.");
                                if (failCount > 0) warnings++; else completed++;
                                break;
                            }
                            case "install_kit":
                            {
                                var kit = SoftwareKits.FirstOrDefault(k => k.Id == SelectedKitId) ?? SoftwareKits.FirstOrDefault();
                                if (kit == null)
                                {
                                    step.State = PostFormatStepState.Skipped;
                                    step.StatusText = "Sem kit";
                                    break;
                                }

                                var catalog = _softwareService.GetCatalog();
                                await _softwareService.CheckAllInstalledStatusAsync(catalog);
                                var missing = catalog
                                    .Where(s => kit.SoftwareIds.Contains(s.Id, StringComparer.OrdinalIgnoreCase) && !s.IsInstalled)
                                    .ToList();

                                int installed = 0;
                                int failed = 0;
                                foreach (var sw in missing)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    step.Detail = $"Instalando {sw.Name}...";
                                    bool ok = await _softwareService.InstallPackageAsync(sw, msg => _log($"[KIT] {msg}"));
                                    if (ok) installed++; else failed++;
                                }

                                step.State = failed == 0 ? PostFormatStepState.Success : PostFormatStepState.Warning;
                                step.StatusText = missing.Count == 0 ? "Já instalado" : $"{installed}/{missing.Count} instalados";
                                step.Detail = missing.Count == 0
                                    ? $"Todos os apps do {kit.Name} já estão instalados."
                                    : $"{kit.Name}: {installed} instalado(s), {failed} falha(s).";
                                if (failed > 0) warnings++; else completed++;
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        step.State = PostFormatStepState.Skipped;
                        step.StatusText = "Cancelado";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        step.State = PostFormatStepState.Error;
                        step.StatusText = "Erro";
                        step.Detail = ex.Message;
                        warnings++;
                        _log($"[PÓS-FORMATAÇÃO ERRO] {step.Title}: {ex.Message}");
                    }
                }

                PostFormatSummary = warnings == 0
                    ? $"Pós-Formatação concluída com sucesso ({completed} etapa(s))."
                    : $"Pós-Formatação finalizada: {completed} OK, {warnings} atenção/aviso(s).";
            }
            catch (OperationCanceledException)
            {
                PostFormatSummary = "Pós-Formatação interrompida pelo usuário.";
                _log("[PÓS-FORMATAÇÃO] Processo cancelado pelo usuário.");
            }
            finally
            {
                _postFormatCts.Dispose();
                _postFormatCts = null;
                IsRunningPostFormat = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

