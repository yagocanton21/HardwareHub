using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HardwareHub.Models;
using HardwareHub.Services;

namespace HardwareHub.ViewModels;

/// <summary>
/// Card de programa exibido na grade do catalogo (curado, importado ou resultado ao vivo do Winget/Store).
/// </summary>
public sealed class AppCardViewModel : INotifyPropertyChanged
{
    private bool _isInstalled;
    private bool _isSelected;
    private bool _isInstalling;
    private bool _hasFailed;
    private bool _couponCopied;
    private string _installStatusText = "Disponível";
    private string _description;
    private string _publisher;
    private string? _version;
    private ImageSource? _iconImage;

    public AppCardViewModel(AppPackageDefinition definition, bool isOnlineResult = false)
    {
        Definition = definition;
        IsOnlineResult = isOnlineResult;
        _description = definition.Description;
        _publisher = definition.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft Store"
            : "winget";
        _iconImage = isOnlineResult ? AppIconService.TryGetCachedOnlineIcon(definition.WingetId) : AppIconService.LoadEmbeddedIcon(definition.EffectiveIconName);
        Glyph = AppIconService.ResolveGlyph(definition.GlyphHex, AppIconService.GetCategoryGlyph(definition.Category));
    }

    public AppPackageDefinition Definition { get; }
    public bool IsOnlineResult { get; }

    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Category => Definition.Category;
    public string ShortCategory => AppCatalogService.GetShortCategoryLabel(Definition.Category);
    public string WingetId => Definition.WingetId;
    public string Source => Definition.Source;
    public double EstimatedMinutes => Definition.EstimatedMinutes;
    public bool IsLink => Definition.IsLink;
    public string? Url => Definition.Url;
    public string? Coupon => Definition.Coupon;
    public string? HighlightText => Definition.HighlightText;
    public string? Tip => Definition.Tip;
    public string Glyph { get; }

    public bool HasHighlight => !string.IsNullOrWhiteSpace(HighlightText);
    public bool HasCoupon => !string.IsNullOrWhiteSpace(Coupon);
    public bool HasTip => !string.IsNullOrWhiteSpace(Tip);
    public bool HasIconImage => IconImage != null;

    public string LanguageBadge => Definition.LanguageMode.Equals("ingles", StringComparison.OrdinalIgnoreCase) ? "En" : "";
    public bool HasLanguageBadge => !string.IsNullOrEmpty(LanguageBadge);

    public string LanguageTooltip => Definition.LanguageMode.ToLowerInvariant() switch
    {
        "nativo" => "Pacote oficial já em português",
        "ajuste" => "O HUB configura para português automaticamente após instalar",
        "windows" => "Usa o idioma configurado no Windows",
        "ingles" => "Este programa só existe em inglês",
        _ => "Usa o idioma do sistema"
    };

    public string TimeEstimateText => EstimatedMinutes < 1.0
        ? "~30 seg"
        : $"~{EstimatedMinutes:0.#} min";

    public string SourceBadgeText => Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
        ? "Microsoft Store"
        : Source.Equals("imported", StringComparison.OrdinalIgnoreCase)
            ? "Importado"
            : "winget";

    public string Description
    {
        get => _description;
        set
        {
            if (_description != value)
            {
                _description = value;
                OnPropertyChanged();
            }
        }
    }

    public string Publisher
    {
        get => _publisher;
        set
        {
            if (_publisher != value)
            {
                _publisher = value;
                OnPropertyChanged();
            }
        }
    }

    public string? Version
    {
        get => _version;
        set
        {
            if (_version != value)
            {
                _version = value;
                OnPropertyChanged();
            }
        }
    }

    public ImageSource? IconImage
    {
        get => _iconImage;
        set
        {
            if (!Equals(_iconImage, value))
            {
                _iconImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasIconImage));
            }
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled != value)
            {
                _isInstalled = value;
                if (value)
                {
                    _isSelected = false;
                    OnPropertyChanged(nameof(IsSelected));
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSelect));
                OnPropertyChanged(nameof(ShowCheckbox));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            bool normalized = value && CanSelect;
            if (_isSelected != normalized)
            {
                _isSelected = normalized;
                OnPropertyChanged();
            }
        }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (_isInstalling != value)
            {
                _isInstalling = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSelect));
                OnPropertyChanged(nameof(ShowCheckbox));
            }
        }
    }

    public bool HasFailed
    {
        get => _hasFailed;
        set
        {
            if (_hasFailed != value)
            {
                _hasFailed = value;
                OnPropertyChanged();
            }
        }
    }

    public string InstallStatusText
    {
        get => _installStatusText;
        set
        {
            if (_installStatusText != value)
            {
                _installStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CouponCopied
    {
        get => _couponCopied;
        set
        {
            if (_couponCopied != value)
            {
                _couponCopied = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CouponButtonText));
            }
        }
    }

    public string CouponButtonText => CouponCopied
        ? $"✔ Cupom {Coupon} copiado!"
        : $"Copiar cupom {Coupon}";

    public bool CanSelect => !IsInstalled && !IsLink && !IsInstalling;
    public bool ShowCheckbox => !IsLink && !IsInstalled;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Card de kit pronto exibido no topo da tela Programas.
/// </summary>
public sealed class AppKitCardViewModel : INotifyPropertyChanged
{
    private bool _isActive;

    public AppKitCardViewModel(AppKitDefinition definition, IReadOnlyDictionary<string, string>? namesById = null)
    {
        Definition = definition;
        IconGlyph = AppIconService.ResolveGlyph(definition.IconGlyphHex, "\uE71D");
        var names = definition.ProgramIds
            .Select(id => namesById != null && namesById.TryGetValue(id, out var n) ? n : id);
        ProgramNamesSummary = "Inclui: " + string.Join(", ", names);
    }

    public AppKitDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string IconGlyph { get; }
    public string ProgramNamesSummary { get; }
    public IReadOnlyList<string> ProgramIds => Definition.ProgramIds;
    public string ProgramCountText => $"{ProgramIds.Count} programas";

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Pill/chip de categoria para filtragem rapida do catalogo.
/// </summary>
public sealed class CategoryChipViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public CategoryChipViewModel(string key, string label, bool isSelected = false)
    {
        Key = key;
        Label = label;
        _isSelected = isSelected;
    }

    public string Key { get; }
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// ViewModel completa da tela Programas no estilo oficial HUB Pro (kits, categorias, busca ao vivo Winget/Store, icones e fila de instalacao).
/// </summary>
public sealed class AppsCatalogViewModel : INotifyPropertyChanged
{
    private readonly AppCatalogService _catalogService;
    private readonly SoftwareService _softwareService;
    private readonly Action<string> _log;

    private readonly List<AppCardViewModel> _allCatalogApps = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _installCts;

    private string _searchQuery = "";
    private string _selectedCategoryKey = "Todos";
    private bool _isWingetReady;
    private bool _isRefreshingStatus;
    private bool _isSearchingOnline;
    private bool _isInstallingQueue;
    private double _queueProgressPercent;
    private string _queueStatusText = "";
    private string _onlineSearchBanner = "";
    private bool _hasOnlineResults;

    public AppsCatalogViewModel(
        AppCatalogService? catalogService = null,
        SoftwareService? softwareService = null,
        Action<string>? logCallback = null)
    {
        _catalogService = catalogService ?? new AppCatalogService();
        _softwareService = softwareService ?? new SoftwareService();
        _log = logCallback ?? (_ => { });

        ToggleProgramCommand = new RelayCommand(p => { if (p is AppCardViewModel card) ToggleProgram(card); });
        ToggleKitCommand = new RelayCommand(p => { if (p is AppKitCardViewModel kit) ToggleKit(kit); });
        SelectCategoryCommand = new RelayCommand(p => { if (p is CategoryChipViewModel chip) SelectCategory(chip); });
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => SelectedCount > 0 && !IsInstallingQueue);
        ClearSearchCommand = new RelayCommand(_ => SearchQuery = "");
        InstallSelectedCommand = new RelayCommand(async _ => await InstallSelectedQueueAsync(), _ => SelectedCount > 0 && !IsInstallingQueue);
        CancelQueueCommand = new RelayCommand(_ => CancelQueue(), _ => IsInstallingQueue);
        OpenLinkCommand = new RelayCommand(p => { if (p is AppCardViewModel card) OpenExternalLink(card); });
        LaunchProgramCommand = new RelayCommand(p => { if (p is AppCardViewModel card) LaunchProgram(card); });
        CopyCouponCommand = new RelayCommand(async p => { if (p is AppCardViewModel card) await CopyCouponAsync(card); });
        RefreshStatusCommand = new RelayCommand(async _ => await RefreshInstalledStatusAsync(), _ => !IsRefreshingStatus);
        RepairWingetCommand = new RelayCommand(async _ => await RepairWingetAsync());
        ImportWingetFileCommand = new RelayCommand(_ => ImportWingetFile());

        LoadInitialCatalog();
    }

    public ObservableCollection<AppKitCardViewModel> Kits { get; } = new();
    public ObservableCollection<CategoryChipViewModel> CategoryChips { get; } = new();
    public ObservableCollection<AppCardViewModel> VisibleApps { get; } = new();
    public ObservableCollection<AppCardViewModel> OnlineWingetApps { get; } = new();
    public ObservableCollection<AppCardViewModel> OnlineStoreApps { get; } = new();

    public ICommand ToggleProgramCommand { get; }
    public ICommand ToggleKitCommand { get; }
    public ICommand SelectCategoryCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand InstallSelectedCommand { get; }
    public ICommand CancelQueueCommand { get; }
    public ICommand OpenLinkCommand { get; }
    public ICommand LaunchProgramCommand { get; }
    public ICommand CopyCouponCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand RepairWingetCommand { get; }
    public ICommand ImportWingetFileCommand { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery != value)
            {
                _searchQuery = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSearchQuery));
                ApplyFilter();
                ScheduleOnlineSearch(_searchQuery);
            }
        }
    }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    public bool IsWingetReady
    {
        get => _isWingetReady;
        private set
        {
            if (_isWingetReady != value)
            {
                _isWingetReady = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WingetBadgeText));
            }
        }
    }

    public string WingetBadgeText => IsWingetReady ? "✔ winget pronto" : "⚠ winget indisponível";

    public bool IsRefreshingStatus
    {
        get => _isRefreshingStatus;
        private set
        {
            if (_isRefreshingStatus != value)
            {
                _isRefreshingStatus = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsSearchingOnline
    {
        get => _isSearchingOnline;
        private set
        {
            if (_isSearchingOnline != value)
            {
                _isSearchingOnline = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowOnlinePanel));
            }
        }
    }

    public bool HasOnlineResults
    {
        get => _hasOnlineResults;
        private set
        {
            if (_hasOnlineResults != value)
            {
                _hasOnlineResults = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowOnlinePanel));
            }
        }
    }

    public string OnlineSearchBanner
    {
        get => _onlineSearchBanner;
        private set
        {
            if (_onlineSearchBanner != value)
            {
                _onlineSearchBanner = value;
                OnPropertyChanged();
            }
        }
    }

    public bool ShowOnlinePanel =>
        (HasSearchQuery && AppCatalogService.SanitizeSearchQuery(SearchQuery) != null) &&
        (IsSearchingOnline || HasOnlineResults || !string.IsNullOrEmpty(OnlineSearchBanner));

    public bool HasLocalEmptyState => VisibleApps.Count == 0;

    public bool IsInstallingQueue
    {
        get => _isInstallingQueue;
        private set
        {
            if (_isInstallingQueue != value)
            {
                _isInstallingQueue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowFooterProgress));
            }
        }
    }

    public double QueueProgressPercent
    {
        get => _queueProgressPercent;
        private set
        {
            if (Math.Abs(_queueProgressPercent - value) > 0.01)
            {
                _queueProgressPercent = value;
                OnPropertyChanged();
            }
        }
    }

    public string QueueStatusText
    {
        get => _queueStatusText;
        private set
        {
            if (_queueStatusText != value)
            {
                _queueStatusText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowFooterProgress));
            }
        }
    }

    public bool ShowFooterProgress => IsInstallingQueue || !string.IsNullOrEmpty(QueueStatusText);

    public int InstalledCount => _allCatalogApps.Count(a => !a.IsLink && a.IsInstalled);
    public int InstallableTotalCount => _allCatalogApps.Count(a => !a.IsLink);
    public string InstalledCounterText => $"{InstalledCount} de {InstallableTotalCount} já instalados";

    private IEnumerable<AppCardViewModel> GetAllSelectedCards() =>
        _allCatalogApps.Concat(OnlineWingetApps).Concat(OnlineStoreApps)
            .Where(a => a.IsSelected && a.CanSelect)
            .GroupBy(a => a.WingetId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

    public int SelectedCount => GetAllSelectedCards().Count();

    public string SelectionSummaryText
    {
        get
        {
            var selected = GetAllSelectedCards().ToList();
            if (selected.Count == 0)
            {
                return "Nenhum programa marcado";
            }

            double minutes = Math.Max(0.5, selected.Sum(x => x.EstimatedMinutes));
            return $"{selected.Count} marcado(s) · ~{minutes:0.#} min";
        }
    }

    public string InstallButtonLabel => SelectedCount > 0 ? $"Instalar ({SelectedCount})" : "Instalar";

    public bool HasSelection => SelectedCount > 0;

    public bool HasArchiveConflictWarning
    {
        get
        {
            var selectedIds = GetAllSelectedCards().Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return selectedIds.Contains("7zip") && selectedIds.Contains("winrar");
        }
    }

    public bool HasOnlineStoreApps => OnlineStoreApps.Count > 0;
    public bool HasOnlineWingetApps => OnlineWingetApps.Count > 0;

    private void LoadInitialCatalog()
    {
        var doc = AppCatalogService.LoadCatalog();

        var namesById = doc.Programs.ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);

        Kits.Clear();
        foreach (var kit in doc.Kits)
        {
            Kits.Add(new AppKitCardViewModel(kit, namesById));
        }

        _allCatalogApps.Clear();
        CategoryChips.Clear();
        CategoryChips.Add(new CategoryChipViewModel("Todos", "Todos", isSelected: true));

        var seenCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in doc.Programs)
        {
            var card = new AppCardViewModel(def);
            card.PropertyChanged += OnAppCardPropertyChanged;
            _allCatalogApps.Add(card);

            if (!string.IsNullOrWhiteSpace(def.Category) && seenCategories.Add(def.Category))
            {
                CategoryChips.Add(new CategoryChipViewModel(def.Category, AppCatalogService.GetShortCategoryLabel(def.Category)));
            }
        }

        ApplyFilter();
        NotifySelectionMetricsChanged();
    }

    private void OnAppCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppCardViewModel.IsSelected) or nameof(AppCardViewModel.IsInstalled))
        {
            UpdateKitActiveStates();
            NotifySelectionMetricsChanged();
        }
    }

    public async Task RefreshInstalledStatusAsync()
    {
        if (IsRefreshingStatus)
        {
            return;
        }

        IsRefreshingStatus = true;
        try
        {
            var (wingetOk, installedNames) = await Task.Run(() =>
            {
                bool w = AppCatalogService.IsOfficialWingetAvailable();
                var names = AppCatalogService.ReadInstalledDisplayNames();
                return (w, names);
            });

            IsWingetReady = wingetOk;
            string packagesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");

            foreach (var card in _allCatalogApps.Concat(OnlineWingetApps).Concat(OnlineStoreApps))
            {
                if (card.IsInstalling)
                {
                    continue;
                }

                bool installed = AppCatalogService.IsProgramInstalled(card.Definition, installedNames, packagesDir);
                if (!installed && card.IsOnlineResult)
                {
                    installed = installedNames.Any(n => n.StartsWith(card.Name, StringComparison.OrdinalIgnoreCase));
                }

                card.IsInstalled = installed;
                if (installed && !card.IsLink)
                {
                    AppCatalogService.EnsureStartMenuShortcutForPackage(card.Definition);
                }

                if (!card.HasFailed)
                {
                    card.InstallStatusText = installed ? "Já instalado" : "Disponível";
                }
            }

            OnPropertyChanged(nameof(InstalledCount));
            OnPropertyChanged(nameof(InstallableTotalCount));
            OnPropertyChanged(nameof(InstalledCounterText));
            UpdateKitActiveStates();
            NotifySelectionMetricsChanged();
        }
        finally
        {
            IsRefreshingStatus = false;
        }
    }

    private async Task RepairWingetAsync()
    {
        _log("[WINGET] Tentando re-registrar o pacote oficial Microsoft.DesktopAppInstaller...");
        QueueStatusText = "Tentando reparar o winget...";
        bool ok = await AppCatalogService.TryRepairWingetAsync();
        IsWingetReady = ok;
        QueueStatusText = ok
            ? "✔ winget reparado e pronto para uso."
            : "⚠ Não foi possível reparar o winget automaticamente. Instale 'Instalador de Aplicativo' na Microsoft Store.";
    }

    public void ToggleProgram(AppCardViewModel card)
    {
        if (!card.CanSelect)
        {
            return;
        }

        card.IsSelected = !card.IsSelected;
    }

    public void LaunchProgram(AppCardViewModel card)
    {
        if (card.IsLink)
        {
            OpenExternalLink(card);
            return;
        }

        AppCatalogService.TryLaunchProgram(card.Definition);
    }

    public void ToggleKit(AppKitCardViewModel kit)
    {
        var targets = _allCatalogApps
            .Where(a => kit.ProgramIds.Contains(a.Id, StringComparer.OrdinalIgnoreCase) && !a.IsLink && !a.IsInstalled && !a.IsInstalling)
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        bool markAll = targets.Any(a => !a.IsSelected);
        foreach (var card in targets)
        {
            card.IsSelected = markAll;
        }

        UpdateKitActiveStates();
        NotifySelectionMetricsChanged();
    }

    public void SelectCategory(CategoryChipViewModel chip)
    {
        _selectedCategoryKey = chip.Key;
        foreach (var c in CategoryChips)
        {
            c.IsSelected = ReferenceEquals(c, chip);
        }

        ApplyFilter();
    }

    public void ClearSelection()
    {
        foreach (var card in _allCatalogApps.Concat(OnlineWingetApps).Concat(OnlineStoreApps))
        {
            card.IsSelected = false;
        }

        UpdateKitActiveStates();
        NotifySelectionMetricsChanged();
    }

    private void ApplyFilter()
    {
        VisibleApps.Clear();
        IEnumerable<AppCardViewModel> query = _allCatalogApps;

        if (!_selectedCategoryKey.Equals("Todos", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(a => a.Category.Equals(_selectedCategoryKey, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            string term = SearchQuery.Trim();
            query = query.Where(a =>
                a.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                a.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                a.WingetId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                a.Category.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var card in query)
        {
            VisibleApps.Add(card);
        }

        OnPropertyChanged(nameof(HasLocalEmptyState));
    }

    private void UpdateKitActiveStates()
    {
        foreach (var kit in Kits)
        {
            var selectable = _allCatalogApps
                .Where(a => kit.ProgramIds.Contains(a.Id, StringComparer.OrdinalIgnoreCase) && !a.IsLink && !a.IsInstalled)
                .ToList();

            kit.IsActive = selectable.Count > 0 && selectable.All(a => a.IsSelected);
        }
    }

    private void NotifySelectionMetricsChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(InstallButtonLabel));
        OnPropertyChanged(nameof(HasArchiveConflictWarning));
    }

    private void ScheduleOnlineSearch(string rawQuery)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        string? sanitized = AppCatalogService.SanitizeSearchQuery(rawQuery);
        if (sanitized == null)
        {
            OnlineWingetApps.Clear();
            OnlineStoreApps.Clear();
            HasOnlineResults = false;
            IsSearchingOnline = false;
            OnlineSearchBanner = "";
            OnPropertyChanged(nameof(HasOnlineStoreApps));
            OnPropertyChanged(nameof(HasOnlineWingetApps));
            OnPropertyChanged(nameof(ShowOnlinePanel));
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = RunDebouncedOnlineSearchAsync(sanitized, cts.Token);
    }

    private async Task RunDebouncedOnlineSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            IsSearchingOnline = true;
            OnlineSearchBanner = $"Pesquisando \"{query}\" no Winget e na Microsoft Store…";
            await Task.Delay(450, ct);

            var response = await _catalogService.SearchOnlineCatalogAsync(query, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            OnlineWingetApps.Clear();
            OnlineStoreApps.Clear();

            var curatedIds = _allCatalogApps
                .Where(a => !string.IsNullOrWhiteSpace(a.WingetId))
                .Select(a => a.WingetId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var pkg in response.Store.Where(p => !curatedIds.Contains(p.Id)))
            {
                var card = CreateOnlineCard(pkg);
                OnlineStoreApps.Add(card);
                _ = HydrateOnlineCardAsync(card, ct);
            }

            foreach (var pkg in response.Winget.Where(p => !curatedIds.Contains(p.Id)))
            {
                var card = CreateOnlineCard(pkg);
                OnlineWingetApps.Add(card);
                _ = HydrateOnlineCardAsync(card, ct);
            }

            HasOnlineResults = OnlineWingetApps.Count > 0 || OnlineStoreApps.Count > 0;
            if (!string.IsNullOrEmpty(response.Error))
            {
                OnlineSearchBanner = $"Não foi possível consultar online agora: {response.Error}";
            }
            else if (!HasOnlineResults)
            {
                OnlineSearchBanner = $"Nenhum resultado adicional no winget/Store para \"{query}\".";
            }
            else
            {
                string hiddenNote = response.HiddenOtherLocales > 0
                    ? $" ({response.HiddenOtherLocales} variantes em outros idiomas ocultas)"
                    : "";
                OnlineSearchBanner = $"{OnlineWingetApps.Count + OnlineStoreApps.Count} pacotes encontrados online{hiddenNote}";
            }

            OnPropertyChanged(nameof(HasOnlineStoreApps));
            OnPropertyChanged(nameof(HasOnlineWingetApps));
        }
        catch (OperationCanceledException)
        {
            // Digitação continua.
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsSearchingOnline = false;
                OnPropertyChanged(nameof(ShowOnlinePanel));
            }
        }
    }

    private AppCardViewModel CreateOnlineCard(WingetSearchPackage pkg)
    {
        var def = new AppPackageDefinition
        {
            Id = pkg.Id,
            Name = pkg.Name,
            Category = pkg.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? "Microsoft Store" : "Utilitários",
            Description = $"Pacote oficial ({pkg.Source}) • Versão {pkg.Version} • ID: {pkg.Id}",
            WingetId = pkg.Id,
            Source = pkg.Source,
            InstalledName = pkg.Name,
            LanguageMode = pkg.Id.EndsWith(".pt-BR", StringComparison.OrdinalIgnoreCase) ? "nativo" : "windows",
            EstimatedMinutes = 1.5
        };

        var card = new AppCardViewModel(def, isOnlineResult: true)
        {
            Version = pkg.Version,
            Publisher = pkg.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? "Microsoft Store" : pkg.Id
        };
        card.PropertyChanged += OnAppCardPropertyChanged;
        return card;
    }

    private async Task HydrateOnlineCardAsync(AppCardViewModel card, CancellationToken ct)
    {
        try
        {
            string? homepage = null;
            string? publisherUrl = null;
            if (!card.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
            {
                var details = await _catalogService.GetPackageDetailsAsync(card.WingetId, card.Source, ct);
                if (details != null && !ct.IsCancellationRequested)
                {
                    if (!string.IsNullOrWhiteSpace(details.Publisher))
                    {
                        card.Publisher = details.Publisher;
                    }
                    if (!string.IsNullOrWhiteSpace(details.Description))
                    {
                        card.Description = details.Description;
                    }
                    homepage = details.Homepage;
                    publisherUrl = details.PublisherUrl;
                }
            }

            var icon = await AppIconService.FetchOnlineIconAsync(card.WingetId, card.Source, homepage, publisherUrl, ct);
            if (icon != null && !ct.IsCancellationRequested)
            {
                card.IconImage = icon;
            }
        }
        catch
        {
            // Falha ao hidratar icone/detalhes online mantem o card funcional com glifo fallback.
        }
    }

    private async Task InstallSelectedQueueAsync()
    {
        var queue = GetAllSelectedCards().ToList();
        if (queue.Count == 0 || IsInstallingQueue)
        {
            return;
        }

        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;

        IsInstallingQueue = true;
        QueueProgressPercent = 0;

        if (!AppCatalogService.IsOfficialWingetAvailable())
        {
            QueueStatusText = "Ativando o winget oficial antes de instalar…";
            bool repaired = await AppCatalogService.TryRepairWingetAsync(ct);
            IsWingetReady = repaired;
            if (!repaired)
            {
                QueueStatusText = "⚠ winget indisponível neste Windows. Instale/atualize 'Instalador de Aplicativo' na Microsoft Store.";
                IsInstallingQueue = false;
                return;
            }
        }

        int successCount = 0;
        int failedCount = 0;

        foreach (var item in queue)
        {
            item.HasFailed = false;
            item.InstallStatusText = "Na fila…";
        }

        try
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var card = queue[i];
                card.IsInstalling = true;
                card.InstallStatusText = "Preparando instalação…";
                QueueStatusText = $"Instalando {i + 1} de {queue.Count}: {card.Name}…";
                _log($"\n>>> [{i + 1}/{queue.Count}] Instalando {card.Name} ({card.WingetId})...");

                var progress = new Progress<string>(status => card.InstallStatusText = status);
                AppInstallOutcome outcome;
                try
                {
                    outcome = await _catalogService.InstallProgramAsync(card.Definition, progress, line => _log(line), ct);
                }
                catch (OperationCanceledException)
                {
                    card.IsInstalling = false;
                    card.InstallStatusText = "Cancelado";
                    break;
                }

                card.IsInstalling = false;
                if (outcome.Success)
                {
                    successCount++;
                    card.HasFailed = false;
                    card.IsInstalled = true;
                    card.IsSelected = false;
                    card.InstallStatusText = outcome.ShortMessage;
                    _log($"[OK] {card.Name}: {outcome.ShortMessage}");
                }
                else
                {
                    failedCount++;
                    card.HasFailed = true;
                    card.InstallStatusText = outcome.ShortMessage;
                    _log($"[FALHA] {card.Name}: {outcome.ShortMessage}");
                }

                QueueProgressPercent = ((double)(i + 1) / queue.Count) * 100.0;
            }

            if (ct.IsCancellationRequested)
            {
                QueueStatusText = $"Instalação interrompida ({successCount} concluído(s)).";
            }
            else if (failedCount == 0)
            {
                QueueStatusText = $"✔ {successCount} programa(s) instalado(s) com sucesso!";
            }
            else
            {
                QueueStatusText = $"Concluído: {successCount} instalado(s), {failedCount} falha(s). Veja os detalhes no card ou log.";
            }

            await RefreshInstalledStatusAsync();
        }
        finally
        {
            IsInstallingQueue = false;
            NotifySelectionMetricsChanged();
        }
    }

    private void CancelQueue()
    {
        try
        {
            _installCts?.Cancel();
            QueueStatusText = "Cancelando fila após a operação atual…";
        }
        catch { }
    }

    private void OpenExternalLink(AppCardViewModel card)
    {
        if (string.IsNullOrWhiteSpace(card.Url) ||
            !Uri.TryCreate(card.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log($"[ERRO] Não foi possível abrir '{card.Url}': {ex.Message}");
        }
    }

    private async Task CopyCouponAsync(AppCardViewModel card)
    {
        if (string.IsNullOrWhiteSpace(card.Coupon))
        {
            return;
        }

        try
        {
            Clipboard.SetText(card.Coupon);
            card.CouponCopied = true;
            await Task.Delay(2000);
            card.CouponCopied = false;
        }
        catch { }
    }

    private void ImportWingetFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar Lista de Programas (Winget / UniGetUI)",
            Filter = "Todos os Formatos Suportados (*.ubundle;*.json;*.txt;*.yaml;*.yml)|*.ubundle;*.json;*.txt;*.yaml;*.yml|Pacotes UniGetUI (*.ubundle)|*.ubundle|Arquivos Winget (*.json)|*.json|Todos os Arquivos (*.*)|*.*",
            DefaultExt = ".ubundle"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var items = _softwareService.ParseWingetFile(dialog.FileName);
        if (items.Count == 0)
        {
            QueueStatusText = "Nenhum pacote válido foi encontrado no arquivo importado.";
            return;
        }

        if (!CategoryChips.Any(c => c.Key.Equals("Importados", StringComparison.OrdinalIgnoreCase)))
        {
            CategoryChips.Add(new CategoryChipViewModel("Importados", "Importados"));
        }

        int added = 0;
        foreach (var sw in items)
        {
            var existing = _allCatalogApps.FirstOrDefault(x => x.WingetId.Equals(sw.WingetId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!existing.IsInstalled && !existing.IsLink)
                {
                    existing.IsSelected = true;
                }
                continue;
            }

            var def = new AppPackageDefinition
            {
                Id = sw.Id,
                Name = sw.Name,
                Category = "Importados",
                Description = sw.Description,
                WingetId = sw.WingetId,
                Source = sw.WingetId.StartsWith("9", StringComparison.OrdinalIgnoreCase) && sw.WingetId.Length == 12 && !sw.WingetId.Contains('.')
                    ? "msstore"
                    : "winget",
                InstalledName = sw.Name,
                EstimatedMinutes = 1.5
            };

            var card = new AppCardViewModel(def) { IsSelected = true };
            card.PropertyChanged += OnAppCardPropertyChanged;
            _allCatalogApps.Add(card);
            added++;
        }

        var importedChip = CategoryChips.First(c => c.Key.Equals("Importados", StringComparison.OrdinalIgnoreCase));
        SelectCategory(importedChip);
        QueueStatusText = $"✔ {items.Count} programa(s) importado(s) ({added} novo(s) na categoria Importados).";
        _ = RefreshInstalledStatusAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

