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
    public sealed class DuplicateFolderOption : ObservableModel
    {
        private bool _isSelected = true;

        public string Label { get; init; } = "";
        public string FolderPath { get; init; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }
    }

    public sealed class DiskCategorySummaryItem
    {
        public string Label { get; init; } = "";
        public long Bytes { get; init; }
        public double Percent { get; init; }
        public double BarRatio => Math.Clamp(Percent / 100.0, 0.02, 1.0);
        public string ColorHex { get; init; } = "#4F46E5";
        public string FormattedSize => InstalledProgramItem.FormatBytes(Bytes);
        public string FormattedPercent => $"{Percent:0.0}%";
    }

    public sealed class StorageToolsViewModel : INotifyPropertyChanged
    {
        private readonly StorageCleanerService _cleanerService;
        private readonly Action<string> _log;

        private string _activeSubTab = "Cleaner"; // "Cleaner" | "Duplicates" | "DiskMap"
        private bool _isBusyStorage;
        private string _storageStatusBanner = "Pronto para analisar limpeza segura, arquivos duplicados ou mapa de disco.";
        private CancellationTokenSource? _cts;
        private bool _cleanerScannedOnce;

        // Cleaner filters
        private string _cleanupSearchText = "";
        private string _selectedCleanupCategory = "Todas";

        // Duplicates state
        private long _minDuplicateSizeBytes = 1024;
        private bool _sendDuplicatesToRecycleBin = true;
        private string _duplicatesSummaryText = "Selecione as pastas e clique em \"Buscar Duplicados\".";

        // DiskMap state
        private string _selectedDriveRoot = "C:\\";
        private DiskMapScanResult? _lastDiskMapResult;
        private DiskFolderNode? _currentFolderNode;
        private string _diskMapSummaryText = "Selecione uma unidade ou pasta para mapear o uso de espaço.";

        public ObservableCollection<CleanupItemViewModel> AllCleanupItems { get; } = new();
        public ObservableCollection<CleanupItemViewModel> FilteredCleanupItems { get; } = new();
        public ObservableCollection<string> CleanupCategories { get; } = new() { "Todas" };

        public ObservableCollection<DuplicateFolderOption> DuplicateFolders { get; } = new();
        public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; } = new();

        public ObservableCollection<string> AvailableDrives { get; } = new();
        public ObservableCollection<DiskFolderRowViewModel> DiskFolderRows { get; } = new();
        public ObservableCollection<DiskLargestFileItem> TopLargestFiles { get; } = new();
        public ObservableCollection<DiskCategorySummaryItem> DiskCategorySummaries { get; } = new();

        public StorageToolsViewModel(
            StorageCleanerService? cleanerService = null,
            Action<string>? logCallback = null)
        {
            _cleanerService = cleanerService ?? new StorageCleanerService();
            _log = logCallback ?? (_ => { });

            foreach (var rule in _cleanerService.Catalog)
            {
                var item = new CleanupItemViewModel(rule);
                item.PropertyChanged += OnCleanupItemPropertyChanged;
                AllCleanupItems.Add(item);

                if (!string.IsNullOrWhiteSpace(rule.Categoria) && !CleanupCategories.Contains(rule.Categoria))
                    CleanupCategories.Add(rule.Categoria);
            }

            foreach (var (label, folder) in StorageCleanerService.GetDefaultPersonalFolders())
            {
                DuplicateFolders.Add(new DuplicateFolderOption
                {
                    Label = label,
                    FolderPath = folder,
                    IsSelected = true
                });
            }

            RefreshAvailableDrives();
            ApplyCleanupFilter();

            SetSubTabCommand = new RelayCommand(p => ActiveSubTab = p?.ToString() ?? "Cleaner");
            SetCleanupCategoryCommand = new RelayCommand(p => SelectedCleanupCategory = p?.ToString() ?? "Todas");

            ScanCleanCommand = new RelayCommand(_ => _ = ScanCleanAsync(), _ => !IsBusyStorage);
            CleanSelectedCommand = new RelayCommand(_ => _ = CleanSelectedAsync(), _ => !IsBusyStorage && AllCleanupItems.Any(i => i.IsSelected));
            SelectSafeCleanCommand = new RelayCommand(_ => SelectRecommendedCleanup(), _ => !IsBusyStorage && AllCleanupItems.Count > 0);
            ToggleSelectAllCleanCommand = new RelayCommand(_ => ToggleSelectAllCleanup(), _ => !IsBusyStorage && FilteredCleanupItems.Count > 0);

            FindDuplicatesCommand = new RelayCommand(_ => _ = FindDuplicatesAsync(), _ => !IsBusyStorage);
            KeepOldestDuplicatesCommand = new RelayCommand(_ => MarkDuplicatesExcept(keepNewest: false), _ => !IsBusyStorage && DuplicateGroups.Count > 0);
            KeepNewestDuplicatesCommand = new RelayCommand(_ => MarkDuplicatesExcept(keepNewest: true), _ => !IsBusyStorage && DuplicateGroups.Count > 0);
            ClearDuplicateSelectionsCommand = new RelayCommand(_ => ClearDuplicateSelections(), _ => !IsBusyStorage && DuplicateGroups.Count > 0);
            DeleteDuplicatesCommand = new RelayCommand(_ => DeleteSelectedDuplicates(), _ => !IsBusyStorage && DuplicateGroups.Any(g => g.Files.Any(f => f.IsSelected)));

            ScanDiskMapCommand = new RelayCommand(_ => _ = ScanDiskMapAsync(), _ => !IsBusyStorage && !string.IsNullOrWhiteSpace(SelectedDriveRoot));
            DrillDownFolderCommand = new RelayCommand(p =>
            {
                if (p is DiskFolderRowViewModel row)
                    NavigateToFolderNode(row.Node);
            });
            NavigateUpFolderCommand = new RelayCommand(_ =>
            {
                if (CurrentFolderNode?.Parent != null)
                    NavigateToFolderNode(CurrentFolderNode.Parent);
            }, _ => CanNavigateUpDiskMap);
            OpenFolderInExplorerCommand = new RelayCommand(p => OpenInExplorer(p?.ToString() ?? CurrentFolderPath));

            CancelStorageTaskCommand = new RelayCommand(_ => CancelActiveTask(), _ => IsBusyStorage);
        }

        public string ActiveSubTab
        {
            get => _activeSubTab;
            set
            {
                if (SetField(ref _activeSubTab, value))
                {
                    OnPropertyChanged(nameof(IsCleanerTabActive));
                    OnPropertyChanged(nameof(IsDuplicatesTabActive));
                    OnPropertyChanged(nameof(IsDiskMapTabActive));
                }
            }
        }

        public bool IsCleanerTabActive => ActiveSubTab == "Cleaner";
        public bool IsDuplicatesTabActive => ActiveSubTab == "Duplicates";
        public bool IsDiskMapTabActive => ActiveSubTab == "DiskMap";

        public bool IsBusyStorage
        {
            get => _isBusyStorage;
            private set
            {
                if (SetField(ref _isBusyStorage, value))
                    InvalidateCommands();
            }
        }

        public string StorageStatusBanner
        {
            get => _storageStatusBanner;
            private set => SetField(ref _storageStatusBanner, value);
        }

        public string CleanupSearchText
        {
            get => _cleanupSearchText;
            set
            {
                if (SetField(ref _cleanupSearchText, value))
                    ApplyCleanupFilter();
            }
        }

        public string SelectedCleanupCategory
        {
            get => _selectedCleanupCategory;
            set
            {
                if (SetField(ref _selectedCleanupCategory, value))
                    ApplyCleanupFilter();
            }
        }

        public string SelectAllCleanLabel =>
            FilteredCleanupItems.Count > 0 && FilteredCleanupItems.All(i => i.IsSelected)
                ? "Desmarcar Visíveis"
                : "Marcar Visíveis";

        public string SelectedCleanSummary
        {
            get
            {
                var selected = AllCleanupItems.Where(i => i.IsSelected).ToList();
                var knownBytes = selected.Where(i => i.EstimatedBytes > 0).Sum(i => i.EstimatedBytes);
                return $"{selected.Count} selecionado(s) • {InstalledProgramItem.FormatBytes(knownBytes)} estimados";
            }
        }

        public string TotalEstimatedCleanText
        {
            get
            {
                var scanned = AllCleanupItems.Where(i => i.EstimatedBytes >= 0).ToList();
                if (scanned.Count == 0)
                    return "Clique em \"Analisar Espaço\" para calcular quanto pode ser liberado.";
                var totalBytes = scanned.Sum(i => Math.Max(0, i.EstimatedBytes));
                var totalFiles = scanned.Sum(i => i.FileCount);
                return $"Total analisado recuperável: {InstalledProgramItem.FormatBytes(totalBytes)} ({totalFiles:N0} arquivo(s))";
            }
        }

        public long MinDuplicateSizeBytes
        {
            get => _minDuplicateSizeBytes;
            set => SetField(ref _minDuplicateSizeBytes, value);
        }

        public bool SendDuplicatesToRecycleBin
        {
            get => _sendDuplicatesToRecycleBin;
            set => SetField(ref _sendDuplicatesToRecycleBin, value);
        }

        public string DuplicatesSummaryText
        {
            get => _duplicatesSummaryText;
            private set => SetField(ref _duplicatesSummaryText, value);
        }

        public string SelectedDuplicatesSummary
        {
            get
            {
                var selected = DuplicateGroups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
                var bytes = selected.Sum(f => f.SizeBytes);
                return $"{selected.Count} arquivo(s) marcado(s) ({InstalledProgramItem.FormatBytes(bytes)})";
            }
        }

        public string SelectedDriveRoot
        {
            get => _selectedDriveRoot;
            set
            {
                if (SetField(ref _selectedDriveRoot, value))
                    InvalidateCommands();
            }
        }

        public DiskFolderNode? CurrentFolderNode
        {
            get => _currentFolderNode;
            private set
            {
                if (SetField(ref _currentFolderNode, value))
                {
                    OnPropertyChanged(nameof(CurrentFolderPath));
                    OnPropertyChanged(nameof(CurrentFolderSizeText));
                    OnPropertyChanged(nameof(CanNavigateUpDiskMap));
                    InvalidateCommands();
                }
            }
        }

        public string CurrentFolderPath => CurrentFolderNode?.FullPath ?? SelectedDriveRoot;
        public string CurrentFolderSizeText => CurrentFolderNode != null
            ? $"{InstalledProgramItem.FormatBytes(CurrentFolderNode.TotalBytes)} • {CurrentFolderNode.FileCount:N0} arquivo(s)"
            : "";
        public bool CanNavigateUpDiskMap => CurrentFolderNode?.Parent != null;

        public string DiskMapSummaryText
        {
            get => _diskMapSummaryText;
            private set => SetField(ref _diskMapSummaryText, value);
        }

        public ICommand SetSubTabCommand { get; }
        public ICommand SetCleanupCategoryCommand { get; }
        public ICommand ScanCleanCommand { get; }
        public ICommand CleanSelectedCommand { get; }
        public ICommand SelectSafeCleanCommand { get; }
        public ICommand ToggleSelectAllCleanCommand { get; }
        public ICommand FindDuplicatesCommand { get; }
        public ICommand KeepOldestDuplicatesCommand { get; }
        public ICommand KeepNewestDuplicatesCommand { get; }
        public ICommand ClearDuplicateSelectionsCommand { get; }
        public ICommand DeleteDuplicatesCommand { get; }
        public ICommand ScanDiskMapCommand { get; }
        public ICommand DrillDownFolderCommand { get; }
        public ICommand NavigateUpFolderCommand { get; }
        public ICommand OpenFolderInExplorerCommand { get; }
        public ICommand CancelStorageTaskCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void EnsureLoaded()
        {
            if (!_cleanerScannedOnce && !IsBusyStorage && ActiveSubTab == "Cleaner")
            {
                _cleanerScannedOnce = true;
                _ = ScanCleanAsync();
            }
        }

        public void AddCustomDuplicateFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return;

            var normalized = Path.GetFullPath(folderPath);
            if (DuplicateFolders.Any(f => string.Equals(f.FolderPath, normalized, StringComparison.OrdinalIgnoreCase)))
                return;

            var label = Path.GetFileName(normalized.TrimEnd('\\'));
            if (string.IsNullOrWhiteSpace(label))
                label = normalized;

            DuplicateFolders.Add(new DuplicateFolderOption
            {
                Label = label,
                FolderPath = normalized,
                IsSelected = true
            });
        }

        public void RefreshAvailableDrives()
        {
            AvailableDrives.Clear();
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)))
                    AvailableDrives.Add(d.RootDirectory.FullName);
            }
            catch
            {
            }

            if (AvailableDrives.Count == 0)
                AvailableDrives.Add("C:\\");

            if (!AvailableDrives.Contains(SelectedDriveRoot))
                SelectedDriveRoot = AvailableDrives[0];
        }

        public async Task ScanCleanAsync()
        {
            if (IsBusyStorage)
                return;

            var ct = BeginTask();
            IsBusyStorage = true;
            StorageStatusBanner = "Analisando pastas temporárias, caches e Lixeira...";

            try
            {
                var items = AllCleanupItems.ToList();
                var scannedCount = 0;

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    var res = await Task.Run(() => _cleanerService.ScanRule(item.Definition), ct);
                    var blocker = await Task.Run(() => StorageCleanerService.GetRunningBlockerApp(item.Definition), ct);

                    item.EstimatedBytes = res.Bytes;
                    item.FileCount = res.FileCount;
                    item.Note = res.Note ?? "";
                    item.RunningBlockerApp = blocker;
                    scannedCount++;

                    if (scannedCount % 4 == 0)
                        StorageStatusBanner = $"Analisando limpeza segura ({scannedCount}/{items.Count})...";
                }

                OnPropertyChanged(nameof(SelectedCleanSummary));
                OnPropertyChanged(nameof(TotalEstimatedCleanText));
                var total = items.Where(i => i.EstimatedBytes > 0).Sum(i => i.EstimatedBytes);
                StorageStatusBanner = $"Análise concluída: {InstalledProgramItem.FormatBytes(total)} recuperáveis encontrados.";
                _log($"[LIMPEZA] Análise concluída: {InstalledProgramItem.FormatBytes(total)} recuperáveis.");
            }
            catch (OperationCanceledException)
            {
                StorageStatusBanner = "Análise de limpeza cancelada.";
            }
            finally
            {
                IsBusyStorage = false;
                InvalidateCommands();
            }
        }

        public async Task CleanSelectedAsync()
        {
            var selected = AllCleanupItems.Where(i => i.IsSelected).ToList();
            if (IsBusyStorage || selected.Count == 0)
                return;

            var ct = BeginTask();
            IsBusyStorage = true;
            StorageStatusBanner = $"Executando limpeza em {selected.Count} item(ns)...";

            try
            {
                var result = await _cleanerService.CleanRulesAsync(
                    selected,
                    msg => StorageStatusBanner = msg,
                    ct);

                // Re-scan affected rules to show remaining locked files accurately
                foreach (var item in selected)
                {
                    if (ct.IsCancellationRequested) break;
                    var res = await Task.Run(() => _cleanerService.ScanRule(item.Definition), ct);
                    item.EstimatedBytes = res.Bytes;
                    item.FileCount = res.FileCount;
                    item.Note = res.Note ?? "";
                }

                OnPropertyChanged(nameof(SelectedCleanSummary));
                OnPropertyChanged(nameof(TotalEstimatedCleanText));

                var skippedText = result.SkippedRunningApps.Count > 0
                    ? $" • Pulado(s) em uso: {string.Join(", ", result.SkippedRunningApps)}"
                    : "";
                StorageStatusBanner =
                    $"Limpeza concluída: {InstalledProgramItem.FormatBytes(result.BytesFreed)} liberados ({result.FilesDeleted:N0} arquivo(s)){skippedText}.";
                _log($"[LIMPEZA] {StorageStatusBanner}");
            }
            catch (OperationCanceledException)
            {
                StorageStatusBanner = "Limpeza cancelada pelo usuário.";
            }
            finally
            {
                IsBusyStorage = false;
                InvalidateCommands();
            }
        }

        public void SelectRecommendedCleanup()
        {
            foreach (var item in AllCleanupItems)
                item.IsSelected = item.Recomendado;

            OnPropertyChanged(nameof(SelectedCleanSummary));
            OnPropertyChanged(nameof(SelectAllCleanLabel));
            InvalidateCommands();
        }

        public void ToggleSelectAllCleanup()
        {
            bool selectAll = !(FilteredCleanupItems.Count > 0 && FilteredCleanupItems.All(i => i.IsSelected));
            foreach (var item in FilteredCleanupItems)
                item.IsSelected = selectAll;

            OnPropertyChanged(nameof(SelectedCleanSummary));
            OnPropertyChanged(nameof(SelectAllCleanLabel));
            InvalidateCommands();
        }

        public async Task FindDuplicatesAsync()
        {
            if (IsBusyStorage)
                return;

            var targetFolders = DuplicateFolders
                .Where(f => f.IsSelected && Directory.Exists(f.FolderPath))
                .Select(f => f.FolderPath)
                .ToList();

            if (targetFolders.Count == 0)
            {
                StorageStatusBanner = "Selecione ao menos uma pasta existente para buscar duplicados.";
                return;
            }

            var ct = BeginTask();
            IsBusyStorage = true;
            DuplicateGroups.Clear();
            DuplicatesSummaryText = "Varrendo pastas selecionadas (tamanho -> hash parcial -> SHA-256)...";
            StorageStatusBanner = DuplicatesSummaryText;

            var progress = new Progress<string>(msg =>
            {
                DuplicatesSummaryText = msg;
                StorageStatusBanner = msg;
            });

            try
            {
                var res = await _cleanerService.FindDuplicatesAsync(targetFolders, MinDuplicateSizeBytes, progress, ct);
                foreach (var g in res.Groups)
                {
                    foreach (var f in g.Files)
                        f.PropertyChanged += OnDuplicateFilePropertyChanged;
                    DuplicateGroups.Add(g);
                }

                var waste = res.Groups.Sum(g => g.WasteBytes);
                DuplicatesSummaryText = res.Cancelled
                    ? "Busca de duplicados cancelada."
                    : $"{res.Groups.Count} grupo(s) encontrado(s) • {InstalledProgramItem.FormatBytes(waste)} recuperáveis • {res.FilesScanned:N0} arquivos lidos ({res.Elapsed.TotalSeconds:0.0}s)";
                StorageStatusBanner = DuplicatesSummaryText;
                _log($"[DUPLICADOS] {DuplicatesSummaryText}");
            }
            catch (OperationCanceledException)
            {
                DuplicatesSummaryText = "Busca de duplicados cancelada.";
                StorageStatusBanner = DuplicatesSummaryText;
            }
            finally
            {
                OnPropertyChanged(nameof(SelectedDuplicatesSummary));
                IsBusyStorage = false;
                InvalidateCommands();
            }
        }

        public void MarkDuplicatesExcept(bool keepNewest)
        {
            foreach (var group in DuplicateGroups)
            {
                if (group.Files.Count <= 1)
                    continue;

                var keeper = keepNewest
                    ? group.Files.OrderByDescending(f => f.ModifiedUtc).ThenBy(f => f.FilePath.Length).First()
                    : group.Files.OrderBy(f => f.CreatedUtc < f.ModifiedUtc ? f.CreatedUtc : f.ModifiedUtc).ThenBy(f => f.FilePath.Length).First();

                foreach (var file in group.Files)
                    file.IsSelected = !ReferenceEquals(file, keeper);
            }

            OnPropertyChanged(nameof(SelectedDuplicatesSummary));
            InvalidateCommands();
        }

        public void ClearDuplicateSelections()
        {
            foreach (var file in DuplicateGroups.SelectMany(g => g.Files))
                file.IsSelected = false;

            OnPropertyChanged(nameof(SelectedDuplicatesSummary));
            InvalidateCommands();
        }

        public void DeleteSelectedDuplicates()
        {
            if (IsBusyStorage || DuplicateGroups.Count == 0)
                return;

            var (deletedCount, bytesFreed, skipped) = StorageCleanerService.DeleteDuplicatesSafely(
                DuplicateGroups,
                SendDuplicatesToRecycleBin);

            // Prune deleted files & singleton groups from UI
            var emptyGroups = new List<DuplicateGroupViewModel>();
            foreach (var group in DuplicateGroups)
            {
                for (int i = group.Files.Count - 1; i >= 0; i--)
                {
                    if (!File.Exists(group.Files[i].FilePath))
                    {
                        group.Files[i].PropertyChanged -= OnDuplicateFilePropertyChanged;
                        group.Files.RemoveAt(i);
                    }
                }
                if (group.Files.Count < 2)
                    emptyGroups.Add(group);
            }

            foreach (var eg in emptyGroups)
                DuplicateGroups.Remove(eg);

            var skippedText = skipped.Count > 0 ? $" • {skipped.Count} protegido(s)/ignorado(s)" : "";
            var dest = SendDuplicatesToRecycleBin ? "para a Lixeira" : "permanentemente";
            StorageStatusBanner = $"Removido(s) {deletedCount} arquivo(s) {dest} ({InstalledProgramItem.FormatBytes(bytesFreed)} liberados){skippedText}.";
            DuplicatesSummaryText = $"{DuplicateGroups.Count} grupo(s) restante(s) • {StorageStatusBanner}";
            _log($"[DUPLICADOS] {StorageStatusBanner}");
            OnPropertyChanged(nameof(SelectedDuplicatesSummary));
            InvalidateCommands();
        }

        public async Task ScanDiskMapAsync(string? overridePath = null)
        {
            if (IsBusyStorage)
                return;

            var targetPath = string.IsNullOrWhiteSpace(overridePath) ? SelectedDriveRoot : overridePath;
            if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
            {
                StorageStatusBanner = $"Caminho não encontrado: {targetPath}";
                return;
            }

            SelectedDriveRoot = targetPath;
            var ct = BeginTask();
            IsBusyStorage = true;
            DiskFolderRows.Clear();
            TopLargestFiles.Clear();
            DiskCategorySummaries.Clear();
            DiskMapSummaryText = $"Mapeando {targetPath}...";
            StorageStatusBanner = DiskMapSummaryText;

            var progress = new Progress<string>(msg =>
            {
                DiskMapSummaryText = msg;
                StorageStatusBanner = msg;
            });

            try
            {
                _lastDiskMapResult = await _cleanerService.ScanDiskMapAsync(targetPath, progress, ct);
                NavigateToFolderNode(_lastDiskMapResult.Root);

                TopLargestFiles.Clear();
                foreach (var f in _lastDiskMapResult.TopFiles.Take(40))
                    TopLargestFiles.Add(f);

                DiskMapSummaryText = _lastDiskMapResult.Cancelled
                    ? "Mapeamento cancelado (exibindo resultados parciais)."
                    : $"{InstalledProgramItem.FormatBytes(_lastDiskMapResult.Root.TotalBytes)} • {_lastDiskMapResult.FolderCount:N0} pastas • {_lastDiskMapResult.FileCount:N0} arquivos ({_lastDiskMapResult.Elapsed.TotalSeconds:0.0}s)";
                StorageStatusBanner = DiskMapSummaryText;
                _log($"[MAPA DISCO] {targetPath}: {DiskMapSummaryText}");
            }
            catch (OperationCanceledException)
            {
                DiskMapSummaryText = "Mapeamento cancelado.";
                StorageStatusBanner = DiskMapSummaryText;
            }
            finally
            {
                IsBusyStorage = false;
                InvalidateCommands();
            }
        }

        public void NavigateToFolderNode(DiskFolderNode node)
        {
            CurrentFolderNode = node;
            DiskFolderRows.Clear();
            DiskCategorySummaries.Clear();

            var parentTotal = Math.Max(1L, node.TotalBytes);
            foreach (var child in node.Children.OrderByDescending(c => c.TotalBytes).Take(100))
            {
                DiskFolderRows.Add(new DiskFolderRowViewModel
                {
                    Node = child,
                    SharePercent = child.TotalBytes * 100.0 / parentTotal
                });
            }

            var categoryMeta = new (DiskFileCategory Cat, string Label, string Color)[]
            {
                (DiskFileCategory.Videos, "Vídeos", "#EC4899"),
                (DiskFileCategory.Programs, "Programas / Jogos", "#6366F1"),
                (DiskFileCategory.Archives, "Compactados", "#F59E0B"),
                (DiskFileCategory.Images, "Imagens", "#10B981"),
                (DiskFileCategory.Documents, "Documentos", "#3B82F6"),
                (DiskFileCategory.Music, "Músicas", "#8B5CF6"),
                (DiskFileCategory.System, "Sistema", "#EF4444"),
                (DiskFileCategory.Other, "Outros", "#71717A")
            };

            foreach (var (cat, label, color) in categoryMeta)
            {
                long bytes = node.BytesByCategory[(int)cat];
                if (bytes <= 0)
                    continue;

                DiskCategorySummaries.Add(new DiskCategorySummaryItem
                {
                    Label = label,
                    Bytes = bytes,
                    Percent = bytes * 100.0 / parentTotal,
                    ColorHex = color
                });
            }
        }

        public void CancelActiveTask() => _cts?.Cancel();

        private CancellationToken BeginTask()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }

        private void ApplyCleanupFilter()
        {
            FilteredCleanupItems.Clear();
            var search = (CleanupSearchText ?? "").Trim();
            var cat = SelectedCleanupCategory;

            foreach (var item in AllCleanupItems)
            {
                bool catMatch = string.IsNullOrWhiteSpace(cat) || cat == "Todas" ||
                                string.Equals(item.Categoria, cat, StringComparison.OrdinalIgnoreCase);
                if (!catMatch)
                    continue;

                if (!string.IsNullOrEmpty(search))
                {
                    bool hit = item.Titulo.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                               item.Descricao.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                               item.Categoria.Contains(search, StringComparison.OrdinalIgnoreCase);
                    if (!hit)
                        continue;
                }

                FilteredCleanupItems.Add(item);
            }

            OnPropertyChanged(nameof(SelectAllCleanLabel));
            InvalidateCommands();
        }

        private void OnCleanupItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CleanupItemViewModel.IsSelected))
            {
                OnPropertyChanged(nameof(SelectedCleanSummary));
                OnPropertyChanged(nameof(SelectAllCleanLabel));
                InvalidateCommands();
            }
        }

        private void OnDuplicateFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DuplicateFileItemViewModel.IsSelected))
            {
                OnPropertyChanged(nameof(SelectedDuplicatesSummary));
                InvalidateCommands();
            }
        }

        private static void OpenInExplorer(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch
            {
            }
        }

        private void InvalidateCommands()
        {
            (ScanCleanCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CleanSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SelectSafeCleanCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleSelectAllCleanCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (FindDuplicatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (KeepOldestDuplicatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (KeepNewestDuplicatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ClearDuplicateSelectionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteDuplicatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ScanDiskMapCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (NavigateUpFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelStorageTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? prop = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

