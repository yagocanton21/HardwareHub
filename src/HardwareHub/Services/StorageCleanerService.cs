using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public sealed class StorageCleanerService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        private static readonly string ProgramFilesDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
        private static readonly string ProgramFilesX86Dir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\');
        private static readonly string ProgramDataDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\');
        private static readonly string UserProfileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        private static readonly string AppDataRoamingDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\');
        private static readonly string AppDataLocalDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');

        private static readonly string[] DangerousRoots = new[]
        {
            WindowsDir,
            ProgramFilesDir,
            ProgramFilesX86Dir,
            ProgramDataDir,
            UserProfileDir,
            AppDataRoamingDir,
            AppDataLocalDir,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory).TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments).TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures).TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos).TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic).TrimEnd('\\'),
            Path.Combine(UserProfileDir, "Downloads").TrimEnd('\\')
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        private static readonly HashSet<string> ForbiddenDuplicateDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "AppData", "Program Files", "Program Files (x86)", "ProgramData", "Windows", "Windows.old",
            "$Recycle.Bin", "System Volume Information", "$WinREAgent", "$WINDOWS.~BT", "$Windows.~WS",
            "Recovery", "WindowsApps", "steamapps", "Epic Games", "XboxGames", "Riot Games", "GOG Games",
            "EA Games", "Ubisoft Game Launcher", "node_modules", ".git"
        };

        private static readonly HashSet<string> ProgramBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "dll", "sys", "ocx", "pyd", "node", "so", "drv", "cpl", "mui"
        };

        private const FileAttributes CloudOnlyAttributes = (FileAttributes)4460544; // RecallOnOpen / Offline / Unpinned

        private static readonly Dictionary<string, DiskFileCategory> DiskCategoryByExtension = BuildDiskExtensionMap();

        private readonly IRegistryBackend _registry;

        public IReadOnlyList<CleanupRuleDef> Catalog { get; }

        public StorageCleanerService(
            IRegistryBackend? registry = null,
            IReadOnlyList<CleanupRuleDef>? customRules = null)
        {
            _registry = registry ?? new WindowsRegistryBackend();
            Catalog = customRules ?? LoadEmbeddedCleanupRules();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public nint hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public nint hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBinW(nint hwnd, string? pszRootPath, uint dwFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT FileOp);

        public static IReadOnlyList<CleanupRuleDef> LoadEmbeddedCleanupRules()
        {
            Assembly asm = typeof(StorageCleanerService).Assembly;
            string? resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Resources.Data.cleanup.json", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return Array.Empty<CleanupRuleDef>();

            using Stream stream = asm.GetManifestResourceStream(resName)!;
            return JsonSerializer.Deserialize<List<CleanupRuleDef>>(stream, JsonOptions) ?? new List<CleanupRuleDef>();
        }

        public static string? GetRunningBlockerApp(CleanupRuleDef rule)
        {
            foreach (string procName in rule.Processos)
            {
                if (string.IsNullOrWhiteSpace(procName)) continue;
                var procs = Process.GetProcessesByName(procName);
                bool any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (any) return string.IsNullOrWhiteSpace(rule.Programa) ? procName : rule.Programa;
            }
            return null;
        }

        public static bool IsDangerousCleanRoot(string candidate)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate).TrimEnd('\\');
            }
            catch
            {
                return true;
            }

            if (full.Length <= 3 || !Path.IsPathRooted(full)) return true;
            string? root = Path.GetPathRoot(full)?.TrimEnd('\\');
            if (root != null && full.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;

            if (full.Equals(WindowsDir, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(WindowsDir + @"\System32", StringComparison.OrdinalIgnoreCase) &&
                !full.Contains(@"\config\systemprofile\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return DangerousRoots.Any(r => full.Equals(r, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<string> ExpandTargetPaths(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return Array.Empty<string>();

            if (pattern.Contains("{Steam}", StringComparison.OrdinalIgnoreCase))
            {
                string? steamPath = _registry.Read(@"HKCU\Software\Valve\Steam", "SteamPath").Data as string;
                if (string.IsNullOrWhiteSpace(steamPath)) return Array.Empty<string>();
                try
                {
                    string normalized = Path.GetFullPath(steamPath.Replace('/', '\\')).TrimEnd('\\');
                    if (!Directory.Exists(normalized) || normalized.Length <= 3) return Array.Empty<string>();
                    pattern = pattern.Replace("{Steam}", normalized, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return Array.Empty<string>();
                }
            }

            string expanded = Environment.ExpandEnvironmentVariables(pattern).TrimEnd('\\');
            if (expanded.Contains('%')) return Array.Empty<string>();

            if (!expanded.Contains('*'))
            {
                return Directory.Exists(expanded) || File.Exists(expanded)
                    ? new[] { expanded }
                    : Array.Empty<string>();
            }

            string[] segments = expanded.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return Array.Empty<string>();

            IEnumerable<string> current = new[] { segments[0] + "\\" };
            foreach (string part in segments.Skip(1))
            {
                if (part.Contains('*'))
                {
                    current = current.SelectMany(dir =>
                    {
                        try
                        {
                            return new DirectoryInfo(dir)
                                .GetDirectories(part)
                                .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                .Select(d => d.FullName)
                                .ToList();
                        }
                        catch
                        {
                            return Enumerable.Empty<string>();
                        }
                    }).ToList();
                }
                else
                {
                    current = current
                        .Select(dir => Path.Combine(dir, part))
                        .Where(p => Directory.Exists(p) || File.Exists(p))
                        .ToList();
                }
            }

            return current;
        }

        private IEnumerable<(string Root, CleanupTargetDef Target)> GetSafeTargets(CleanupRuleDef rule)
        {
            foreach (var target in rule.Alvos)
            {
                foreach (string resolved in ExpandTargetPaths(target.Caminho))
                {
                    bool isReparse;
                    try
                    {
                        isReparse = File.GetAttributes(resolved).HasFlag(FileAttributes.ReparsePoint);
                    }
                    catch
                    {
                        isReparse = true;
                    }

                    if (!isReparse && !IsDangerousCleanRoot(resolved))
                    {
                        yield return (resolved, target);
                    }
                }
            }
        }

        private IEnumerable<FileInfo> EnumerateRuleFiles(CleanupRuleDef rule)
        {
            foreach (var (root, target) in GetSafeTargets(rule))
            {
                if (File.Exists(root))
                {
                    FileInfo? single = null;
                    try { single = new FileInfo(root); } catch { }
                    if (single != null && IsOlderThan(single, target.IdadeMinimaHoras))
                        yield return single;
                    continue;
                }

                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = target.Recursivo,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                IEnumerable<FileInfo> files;
                try
                {
                    string searchPattern = string.IsNullOrWhiteSpace(target.Padrao) ? "*" : target.Padrao;
                    files = new DirectoryInfo(root).EnumerateFiles(searchPattern, opts);
                }
                catch
                {
                    continue;
                }

                foreach (var f in files)
                {
                    if (IsOlderThan(f, target.IdadeMinimaHoras))
                        yield return f;
                }
            }
        }

        private static bool IsOlderThan(FileInfo f, double minHours)
        {
            if (minHours <= 0) return true;
            try
            {
                return f.LastWriteTime < DateTime.Now.AddHours(-minHours);
            }
            catch
            {
                return false;
            }
        }

        public CleanScanResult ScanRule(CleanupRuleDef rule)
        {
            if (rule.Especial == "lixeira")
            {
                return QueryRecycleBin();
            }

            if (rule.Especial == "dism-componentes")
            {
                return new CleanScanResult(-1, 0, 0, "O espaço liberado pelo WinSxS aparece após executar o DISM.");
            }

            if (rule.Especial is "restauracao-antigos" or "drivers-antigos")
            {
                return new CleanScanResult(-1, 0, 0, "Limpeza sob demanda no sistema.");
            }

            long bytes = 0;
            int count = 0;
            foreach (var file in EnumerateRuleFiles(rule))
            {
                try
                {
                    bytes += file.Length;
                    count++;
                }
                catch
                {
                }
            }

            string? note = null;
            if (rule.Especial == "cleanmgr:Previous Installations" && bytes > 0)
            {
                string winOldUsers = Path.Combine(Path.GetPathRoot(WindowsDir) ?? @"C:\", "Windows.old", "Users");
                if (Directory.Exists(winOldUsers) && Directory.EnumerateFiles(winOldUsers, "*", SearchOption.AllDirectories).Any())
                {
                    note = "Detectados arquivos em Windows.old\\Users — o HUB preserva seus dados pessoais e pula esta limpeza.";
                }
            }

            return new CleanScanResult(bytes, count, 0, note);
        }

        public async Task<CleanExecutionResult> CleanRulesAsync(
            IEnumerable<CleanupItemViewModel> selectedItems,
            Action<string>? onProgress = null,
            CancellationToken ct = default)
        {
            var items = selectedItems.Where(x => x.IsSelected).ToList();
            return await Task.Run(() =>
            {
                long totalBytes = 0;
                int totalFiles = 0;
                int executed = 0;
                var errors = new List<string>();
                var blockedApps = new List<string>();

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();

                    string? blocker = GetRunningBlockerApp(item.Definition);
                    if (blocker != null)
                    {
                        item.RunningBlockerApp = blocker;
                        item.Note = $"Feche '{blocker}' para limpar.";
                        blockedApps.Add(blocker);
                        continue;
                    }
                    item.RunningBlockerApp = null;

                    onProgress?.Invoke($"Limpando: {item.Titulo}...");

                    if (item.Definition.Especial == "lixeira")
                    {
                        var before = QueryRecycleBin();
                        int hr = SHEmptyRecycleBinW(nint.Zero, null, 0x00000001 | 0x00000002 | 0x00000004);
                        if (hr == 0 || hr == unchecked((int)0x8000FFFF))
                        {
                            totalBytes += Math.Max(0, before.Bytes);
                            totalFiles += before.FileCount;
                            executed++;
                            item.EstimatedBytes = 0;
                            item.FileCount = 0;
                        }
                        else
                        {
                            errors.Add($"{item.Titulo}: código 0x{hr:X8}");
                        }
                        continue;
                    }

                    if (item.Definition.Especial == "dism-componentes")
                    {
                        var (exit, output) = RunTool("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", ct);
                        if (exit == 0) executed++;
                        else errors.Add($"{item.Titulo}: {output}");
                        continue;
                    }

                    if (item.Definition.Especial != null && item.Definition.Especial.StartsWith("cleanmgr:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Segurança: não remove Windows.old se houver dados em Users
                        if (item.Definition.Especial == "cleanmgr:Previous Installations")
                        {
                            var check = ScanRule(item.Definition);
                            if (check.Note != null && check.Note.Contains("Windows.old\\Users", StringComparison.OrdinalIgnoreCase))
                            {
                                item.Note = check.Note;
                                continue;
                            }
                        }

                        long bytesHere = 0;
                        int filesHere = 0;
                        DeleteFilesForRule(item.Definition, ref bytesHere, ref filesHere, ct);
                        totalBytes += bytesHere;
                        totalFiles += filesHere;
                        executed++;
                        item.EstimatedBytes = 0;
                        item.FileCount = 0;
                        continue;
                    }

                    if (item.Definition.Alvos.Count > 0)
                    {
                        long bytesHere = 0;
                        int filesHere = 0;
                        DeleteFilesForRule(item.Definition, ref bytesHere, ref filesHere, ct);
                        totalBytes += bytesHere;
                        totalFiles += filesHere;
                        executed++;
                        var after = ScanRule(item.Definition);
                        item.EstimatedBytes = after.Bytes;
                        item.FileCount = after.FileCount;
                    }
                }

                return new CleanExecutionResult(executed, totalBytes, totalFiles, errors, blockedApps.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }, ct);
        }

        private void DeleteFilesForRule(CleanupRuleDef rule, ref long bytesFreed, ref int filesDeleted, CancellationToken ct)
        {
            foreach (var file in EnumerateRuleFiles(rule))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long len = file.Length;
                    file.IsReadOnly = false;
                    file.Delete();
                    bytesFreed += len;
                    filesDeleted++;
                }
                catch
                {
                }
            }

            foreach (var (root, target) in GetSafeTargets(rule))
            {
                if (!target.Recursivo || !Directory.Exists(root)) continue;
                DeleteEmptySubfolders(root, removeSelf: false);
            }
        }

        private static void DeleteEmptySubfolders(string dir, bool removeSelf)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    try
                    {
                        if (File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint)) continue;
                        DeleteEmptySubfolders(sub, removeSelf: true);
                    }
                    catch
                    {
                    }
                }

                if (removeSelf && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, false);
                }
            }
            catch
            {
            }
        }

        private static CleanScanResult QueryRecycleBin()
        {
            try
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                int hr = SHQueryRecycleBinW(null, ref info);
                return hr == 0
                    ? new CleanScanResult(info.i64Size, (int)Math.Min(int.MaxValue, info.i64NumItems), 0, null)
                    : new CleanScanResult(-1, 0, 0, null);
            }
            catch
            {
                return new CleanScanResult(-1, 0, 0, null);
            }
        }

        private static (int ExitCode, string Output) RunTool(string exe, string args, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (-1, "Não foi possível iniciar o processo.");
                using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        // ===================== DUPLICADOS =====================

        public static List<(string Label, string FolderPath)> GetDefaultPersonalFolders()
        {
            var candidates = new (string Label, Environment.SpecialFolder Folder, string? Fallback)[]
            {
                ("Downloads", Environment.SpecialFolder.UserProfile, "Downloads"),
                ("Documentos", Environment.SpecialFolder.Personal, null),
                ("Imagens", Environment.SpecialFolder.MyPictures, null),
                ("Vídeos", Environment.SpecialFolder.MyVideos, null),
                ("Músicas", Environment.SpecialFolder.MyMusic, null),
                ("Área de Trabalho", Environment.SpecialFolder.DesktopDirectory, null)
            };

            var list = new List<(string Label, string FolderPath)>();
            foreach (var (label, folder, sub) in candidates)
            {
                try
                {
                    string basePath = Environment.GetFolderPath(folder);
                    string path = sub == null ? basePath : Path.Combine(basePath, sub);
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                    string full = Path.GetFullPath(path).TrimEnd('\\');
                    if (list.All(x => !x.FolderPath.Equals(full, StringComparison.OrdinalIgnoreCase)))
                    {
                        list.Add((label, full));
                    }
                }
                catch
                {
                }
            }
            return list;
        }

        public static bool IsForbiddenDuplicateFolder(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path).TrimEnd('\\');
            }
            catch
            {
                return true;
            }

            foreach (string sysRoot in new[]
                     {
                         WindowsDir, ProgramFilesDir, ProgramFilesX86Dir,
                         ProgramDataDir, AppDataRoamingDir, AppDataLocalDir
                     }.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                if (full.Equals(sysRoot, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(sysRoot + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string root = Path.GetPathRoot(full)?.TrimEnd('\\') ?? "";
            string rel = full.Length > root.Length ? full[root.Length..] : "";
            string[] segments = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                if (ForbiddenDuplicateDirNames.Contains(segments[i]))
                {
                    if (i == 0 || (!segments[i].Equals("Windows", StringComparison.OrdinalIgnoreCase) &&
                                   !segments[i].Equals("ProgramData", StringComparison.OrdinalIgnoreCase) &&
                                   !segments[i].Equals("Recovery", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public async Task<DuplicateScanResult> FindDuplicatesAsync(
            IReadOnlyList<string> folders,
            long minSizeBytes = 1024,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                minSizeBytes = Math.Max(1, minSizeBytes);

                long filesScanned = 0;
                long cloudSkipped = 0;
                long unreadableSkipped = 0;
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var bySize = new Dictionary<long, List<DuplicateFileEntry>>();

                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false
                };

                foreach (string folder in folders)
                {
                    if (!Directory.Exists(folder) || IsForbiddenDuplicateFolder(folder)) continue;
                    progress?.Report($"Listando arquivos em {folder}...");

                    IEnumerable<FileInfo> infos;
                    try
                    {
                        infos = new DirectoryInfo(folder).EnumerateFiles("*", opts);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var fi in infos)
                    {
                        ct.ThrowIfCancellationRequested();
                        if ((fi.Attributes & CloudOnlyAttributes) != 0)
                        {
                            cloudSkipped++;
                            continue;
                        }

                        string ext = fi.Extension.TrimStart('.');
                        if (ext.Length > 0 && ProgramBinaryExtensions.Contains(ext)) continue;

                        long len;
                        try { len = fi.Length; }
                        catch { unreadableSkipped++; continue; }

                        if (len < minSizeBytes) continue;
                        if (!seenPaths.Add(fi.FullName)) continue;

                        filesScanned++;
                        if (!bySize.TryGetValue(len, out var bucket))
                        {
                            bucket = new List<DuplicateFileEntry>();
                            bySize[len] = bucket;
                        }
                        bucket.Add(new DuplicateFileEntry(fi.FullName, len, fi.LastWriteTimeUtc, fi.CreationTimeUtc));
                    }
                }

                var groups = new List<DuplicateGroupViewModel>();
                var candidateBuckets = bySize.Values.Where(b => b.Count > 1).ToList();
                int doneBuckets = 0;

                foreach (var bucket in candidateBuckets)
                {
                    ct.ThrowIfCancellationRequested();
                    doneBuckets++;
                    if (doneBuckets % 10 == 0 || candidateBuckets.Count < 15)
                        progress?.Report($"Comparando conteúdo ({doneBuckets}/{candidateBuckets.Count})...");

                    // Fase 1: hash parcial (64 KB início + 64 KB fim)
                    var byPartial = new Dictionary<string, List<DuplicateFileEntry>>(StringComparer.Ordinal);
                    foreach (var entry in bucket)
                    {
                        ct.ThrowIfCancellationRequested();
                        string? pHash = ComputePartialHashHex(entry.FilePath, entry.SizeBytes);
                        if (pHash == null)
                        {
                            unreadableSkipped++;
                            continue;
                        }
                        if (!byPartial.TryGetValue(pHash, out var pList))
                        {
                            pList = new List<DuplicateFileEntry>();
                            byPartial[pHash] = pList;
                        }
                        pList.Add(entry);
                    }

                    // Fase 2: hash SHA-256 completo apenas nos colisões parciais
                    foreach (var pList in byPartial.Values.Where(x => x.Count > 1))
                    {
                        var byFull = new Dictionary<string, List<DuplicateFileEntry>>(StringComparer.Ordinal);
                        foreach (var entry in pList)
                        {
                            ct.ThrowIfCancellationRequested();
                            string? fullHash = ComputeFullSha256Hex(entry.FilePath);
                            if (fullHash == null)
                            {
                                unreadableSkipped++;
                                continue;
                            }
                            if (!byFull.TryGetValue(fullHash, out var fList))
                            {
                                fList = new List<DuplicateFileEntry>();
                                byFull[fullHash] = fList;
                            }
                            fList.Add(entry);
                        }

                        foreach (var (hashHex, copies) in byFull)
                        {
                            if (copies.Count < 2) continue;
                            var ordered = copies.OrderBy(c => c.ModifiedUtc).ToList();
                            var grp = new DuplicateGroupViewModel
                            {
                                FileSizeBytes = ordered[0].SizeBytes,
                                HashHex = hashHex
                            };
                            // Mantém a cópia mais antiga desmarcada e sugere remover as cópias extras
                            for (int i = 0; i < ordered.Count; i++)
                            {
                                grp.Files.Add(new DuplicateFileItemViewModel(ordered[i], initialSelected: i > 0));
                            }
                            groups.Add(grp);
                        }
                    }
                }

                return new DuplicateScanResult(
                    groups.OrderByDescending(g => g.WasteBytes).ToList(),
                    filesScanned,
                    cloudSkipped,
                    unreadableSkipped,
                    sw.Elapsed,
                    false);
            }, ct);
        }

        public static (int DeletedCount, long BytesFreed, List<string> Skipped) DeleteDuplicatesSafely(
            IEnumerable<DuplicateGroupViewModel> groups,
            bool sendToRecycleBin = true)
        {
            int deleted = 0;
            long freed = 0;
            var skipped = new List<string>();

            foreach (var group in groups)
            {
                var selected = group.Files.Where(f => f.IsSelected).ToList();
                if (selected.Count == 0) continue;

                // Segurança absoluta: nunca remove todas as cópias de um grupo
                if (selected.Count >= group.Files.Count)
                {
                    skipped.Add($"{group.GroupTitle}: mantida 1 cópia original por segurança.");
                    selected = selected.Skip(1).ToList();
                }

                foreach (var copy in selected)
                {
                    try
                    {
                        if (!File.Exists(copy.FilePath)) continue;
                        if (sendToRecycleBin)
                        {
                            var op = new SHFILEOPSTRUCT
                            {
                                wFunc = 3, // FO_DELETE
                                pFrom = copy.FilePath + "\0\0",
                                fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400 // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
                            };
                            SHFileOperationW(ref op);
                        }
                        else
                        {
                            File.Delete(copy.FilePath);
                        }

                        if (!File.Exists(copy.FilePath))
                        {
                            deleted++;
                            freed += copy.SizeBytes;
                            copy.IsSelected = false;
                            group.Files.Remove(copy);
                        }
                        else
                        {
                            skipped.Add($"{copy.FileName}: em uso ou protegido.");
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped.Add($"{copy.FileName}: {ex.Message}");
                    }
                }
            }

            return (deleted, freed, skipped);
        }

        private static string? ComputePartialHashHex(string path, long size)
        {
            const int chunk = 65536;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sha = SHA256.Create();
                byte[] buffer = new byte[chunk];
                int readHead = fs.Read(buffer, 0, (int)Math.Min(chunk, size));
                sha.TransformBlock(buffer, 0, readHead, null, 0);

                if (size > chunk)
                {
                    long tailOffset = Math.Max(chunk, size - chunk);
                    fs.Seek(tailOffset, SeekOrigin.Begin);
                    int readTail = fs.Read(buffer, 0, chunk);
                    sha.TransformBlock(buffer, 0, readTail, null, 0);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Convert.ToHexString(sha.Hash!);
            }
            catch
            {
                return null;
            }
        }

        private static string? ComputeFullSha256Hex(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(fs));
            }
            catch
            {
                return null;
            }
        }

        // ===================== MAPA DE DISCO =====================

        public async Task<DiskMapScanResult> ScanDiskMapAsync(
            string rootPath,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                string normalizedRoot = Path.GetFullPath(rootPath);
                var rootNode = new DiskFolderNode(normalizedRoot, normalizedRoot, null);
                var topFiles = new List<DiskLargestFileItem>(64);

                long totalFolders = 0;
                long totalFiles = 0;
                long denied = 0;

                void ScanNode(DiskFolderNode node, int depth)
                {
                    ct.ThrowIfCancellationRequested();
                    totalFolders++;
                    if (totalFolders % 150 == 0)
                        progress?.Report($"Mapeando {totalFolders:N0} pastas, {totalFiles:N0} arquivos...");

                    var di = new DirectoryInfo(node.FullPath);
                    try
                    {
                        foreach (var fi in di.EnumerateFiles())
                        {
                            ct.ThrowIfCancellationRequested();
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((fi.Attributes & CloudOnlyAttributes) != 0) continue;

                            long len;
                            try { len = fi.Length; }
                            catch { continue; }

                            DiskFileCategory cat = ClassifyFile(fi.Extension, node.FullPath);
                            node.OwnBytes += len;
                            node.TotalBytes += len;
                            node.FileCount++;
                            node.BytesByCategory[(int)cat] += len;
                            totalFiles++;

                            if (len >= 5 * 1024 * 1024 || topFiles.Count < 40)
                            {
                                TrackTopFile(topFiles, new DiskLargestFileItem(fi.Name, fi.FullName, len, cat), maxCount: 40);
                            }
                        }

                        foreach (var sub in di.EnumerateDirectories())
                        {
                            ct.ThrowIfCancellationRequested();
                            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            var child = new DiskFolderNode(sub.Name, sub.FullName, node);
                            ScanNode(child, depth + 1);
                            node.Children.Add(child);
                            node.TotalBytes += child.TotalBytes;
                            node.FileCount += child.FileCount;
                            for (int i = 0; i < 8; i++)
                                node.BytesByCategory[i] += child.BytesByCategory[i];
                        }

                        node.Children.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
                    }
                    catch (UnauthorizedAccessException)
                    {
                        node.AccessDenied = true;
                        denied++;
                    }
                    catch
                    {
                        node.AccessDenied = true;
                    }
                }

                ScanNode(rootNode, 0);
                topFiles.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

                return new DiskMapScanResult(rootNode, topFiles, totalFolders, totalFiles, denied, sw.Elapsed, false);
            }, ct);
        }

        private static void TrackTopFile(List<DiskLargestFileItem> list, DiskLargestFileItem item, int maxCount)
        {
            if (list.Count < maxCount)
            {
                list.Add(item);
                return;
            }

            int minIdx = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].SizeBytes < list[minIdx].SizeBytes) minIdx = i;
            }

            if (item.SizeBytes > list[minIdx].SizeBytes)
            {
                list[minIdx] = item;
            }
        }

        public static DiskFileCategory ClassifyFile(string extension, string folderPath)
        {
            if (folderPath.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase))
                return DiskFileCategory.System;

            string ext = extension.TrimStart('.');
            if (ext.Length > 0 && DiskCategoryByExtension.TryGetValue(ext, out var cat))
                return cat;

            return DiskFileCategory.Other;
        }

        private static Dictionary<string, DiskFileCategory> BuildDiskExtensionMap()
        {
            var map = new Dictionary<string, DiskFileCategory>(StringComparer.OrdinalIgnoreCase);
            void Add(DiskFileCategory cat, string exts)
            {
                foreach (string e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    map[e] = cat;
            }
            Add(DiskFileCategory.Videos, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts mts m2ts");
            Add(DiskFileCategory.Images, "jpg jpeg png gif bmp tif tiff webp heic raw cr2 nef arw dng psd ai svg ico");
            Add(DiskFileCategory.Music, "mp3 wav flac aac m4a ogg wma opus aiff mid midi");
            Add(DiskFileCategory.Documents, "pdf doc docx xls xlsx xlsm ppt pptx odt ods odp rtf txt csv md epub");
            Add(DiskFileCategory.Programs, "exe msi msix appx dll sys ocx pak vpk uasset ubulk cab");
            Add(DiskFileCategory.Archives, "zip rar 7z tar gz tgz bz2 xz zst iso img vhd vhdx wim esd");
            return map;
        }
    }
}
