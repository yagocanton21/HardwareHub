using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public sealed class ProgramUninstallerService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static readonly (RegistryHive Hive, string SubKey)[] UninstallRoots =
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        private static readonly string[] RegistryScanRoots =
        {
            @"HKEY_CURRENT_USER\Software",
            @"HKEY_LOCAL_MACHINE\SOFTWARE",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node"
        };

        private static readonly HashSet<string> LargePublishers = new(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "microsoftcorporation", "google", "googlellc", "adobe", "adobesystems",
            "nvidia", "nvidiacorporation", "amd", "advancedmicrodevices", "intel", "intelcorporation",
            "valve", "apple", "appleinc", "oracle", "autodesk", "mozilla", "jetbrains",
            "realtek", "logitech", "asus", "lenovo", "dell", "hp", "hewlettpackard", "samsung", "acer", "razer"
        };

        private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "app", "apps", "application", "program", "programs", "software", "setup", "install", "installer",
            "uninstall", "uninstaller", "update", "updater", "win", "windows", "x64", "x86", "bin", "data",
            "cache", "temp", "tmp", "logs", "log", "config", "settings", "common", "shared", "files",
            "microsoft", "system", "runtime", "redistributable", "framework", "service", "services", "client",
            "desktop", "helper", "launcher", "tools", "tool", "edition", "pro", "free", "home", "version"
        };

        private static readonly HashSet<string> PersonalDataMarkers = new(StringComparer.OrdinalIgnoreCase)
        {
            "saves", "savegame", "savegames", "bookmarks", "profiles", "userdata", "wallet", "vault"
        };

        private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        private static readonly string ProgramFilesDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
        private static readonly string ProgramFilesX86Dir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\');
        private static readonly string ProgramDataDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\');
        private static readonly string LocalAppDataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
        private static readonly string RoamingAppDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\');
        private static readonly string HubDataDir = Path.Combine(LocalAppDataDir, "HardwareHub");

        private static readonly string[] ProtectedSystemRoots =
        {
            WindowsDir,
            ProgramFilesDir,
            ProgramFilesX86Dir,
            ProgramDataDir,
            LocalAppDataDir,
            RoamingAppDataDir,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\')
        };

        private static readonly string[] PersonalFolders =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory).TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments).TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures).TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos).TrimEnd('\\'),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic).TrimEnd('\\'),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads").TrimEnd('\\')
        };

        private readonly string _backupsDir;
        private readonly IRegistryBackend _registry;
        private readonly Func<string, string, (int ExitCode, string Output)> _exportReg;

        public static string DefaultBackupsDirectory { get; } = Path.Combine(HubDataDir, "backup-uninstaller");

        public ProgramUninstallerService(
            string? backupsDir = null,
            IRegistryBackend? registry = null,
            Func<string, string, (int ExitCode, string Output)>? exportRegistryHook = null)
        {
            _backupsDir = backupsDir ?? DefaultBackupsDirectory;
            _registry = registry ?? new WindowsRegistryBackend();
            _exportReg = exportRegistryHook ?? ExportRegistryKey;
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
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegQueryInfoKey(
            Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey,
            nint lpClass, nint lpcbClass, nint lpReserved, nint lpcSubKeys,
            nint lpcbMaxSubKeyLen, nint lpcbMaxClassLen, nint lpcValues,
            nint lpcbMaxValueNameLen, nint lpcbMaxValueLen, nint lpcbSecurityDescriptor,
            out long lpftLastWriteTime);

        public List<InstalledProgramItem> ListInstalledPrograms()
        {
            var items = new List<InstalledProgramItem>();

            foreach (var (hive, subPath) in UninstallRoots)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var root = baseKey.OpenSubKey(subPath);
                    if (root == null) continue;

                    foreach (string subName in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = root.OpenSubKey(subName);
                            if (sub == null) continue;
                            var parsed = ReadUninstallEntry(sub, hive, subPath, subName);
                            if (parsed != null) items.Add(parsed);
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            return items
                .GroupBy(p => (NormalizeToken(p.Name), p.Version.ToLowerInvariant()))
                .Select(g => g.OrderByDescending(p => !p.IsUserInstall).ThenByDescending(CompletenessScore).First())
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static InstalledProgramItem? ReadUninstallEntry(RegistryKey k, RegistryHive hive, string subPath, string keyName)
        {
            string? displayName = (k.GetValue("DisplayName") as string)?.Trim();
            if (string.IsNullOrWhiteSpace(displayName)) return null;
            if (k.GetValue("SystemComponent") is int sys && sys == 1) return null;
            if (k.GetValue("ParentKeyName") != null || k.GetValue("ParentDisplayName") != null) return null;

            string releaseType = (k.GetValue("ReleaseType") as string) ?? "";
            if (releaseType.Contains("update", StringComparison.OrdinalIgnoreCase) ||
                releaseType.Contains("hotfix", StringComparison.OrdinalIgnoreCase) ||
                releaseType.Contains("servicepack", StringComparison.OrdinalIgnoreCase))
                return null;

            if (Regex.IsMatch(displayName, @"\bKB\d{6,8}\b")) return null;

            string? uninstall = CleanOptional(k.GetValue("UninstallString") as string);
            string? quietUninstall = CleanOptional(k.GetValue("QuietUninstallString") as string);
            bool isWinInstaller = k.GetValue("WindowsInstaller") is int wi && wi == 1;

            string? productCode = null;
            if (keyName.StartsWith('{') && Guid.TryParse(keyName, out _) &&
                (isWinInstaller || (uninstall != null && uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))))
            {
                productCode = keyName.ToUpperInvariant();
            }
            else if (uninstall != null)
            {
                var m = Regex.Match(uninstall, @"msiexec(\.exe)?""?\s.*?/[IX]\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase);
                if (m.Success) productCode = m.Groups[2].Value.ToUpperInvariant();
            }

            if (uninstall == null && quietUninstall == null && productCode == null) return null;

            string hivePrefix = hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
            string fullKeyPath = $@"{hivePrefix}\{subPath}\{keyName}";
            long bytes = k.GetValue("EstimatedSize") is int kb && kb > 0 ? (long)kb * 1024L : -1L;

            return new InstalledProgramItem
            {
                KeyId = fullKeyPath,
                RegistryPath = fullKeyPath,
                Name = displayName,
                Publisher = (k.GetValue("Publisher") as string)?.Trim() ?? "",
                Version = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "",
                InstallDate = ParseInstallDate(k.GetValue("InstallDate") as string) ?? ReadLastWriteTime(k),
                EstimatedBytes = bytes,
                DisplayIcon = CleanOptional(k.GetValue("DisplayIcon") as string),
                UninstallString = uninstall,
                QuietUninstallString = quietUninstall,
                ProductCode = productCode,
                InstallLocation = CleanOptional(k.GetValue("InstallLocation") as string)?.Trim('"'),
                IsUserInstall = hive == RegistryHive.CurrentUser,
                IsMissingUninstaller = IsUninstallerExecutableMissing(uninstall ?? quietUninstall, productCode)
            };
        }

        public static (string Exe, string Args) SplitCommand(string command)
        {
            string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
            if (expanded.StartsWith('"'))
            {
                int closing = expanded.IndexOf('"', 1);
                if (closing > 0)
                {
                    return (expanded[1..closing], expanded[(closing + 1)..].Trim());
                }
            }

            var exeMatch = Regex.Match(expanded, @"^(.+?\.(?:exe|msi|bat|cmd|vbs))(?:\s+(.*))?$", RegexOptions.IgnoreCase);
            if (exeMatch.Success)
            {
                return (exeMatch.Groups[1].Value.Trim(), exeMatch.Groups[2].Value.Trim());
            }

            int space = expanded.IndexOf(' ');
            return space > 0 ? (expanded[..space], expanded[(space + 1)..].Trim()) : (expanded, "");
        }

        public bool EntryExists(InstalledProgramItem program) =>
            !string.IsNullOrWhiteSpace(program.RegistryPath) && _registry.KeyExists(ToShortHive(program.RegistryPath));

        public async Task<UninstallResult> UninstallProgramAsync(
            InstalledProgramItem program,
            bool preferSilent = true,
            Action<string>? statusCallback = null,
            CancellationToken ct = default)
        {
            var candidates = new List<(string Exe, string Args, bool Silent)>();

            if (preferSilent && !string.IsNullOrWhiteSpace(program.QuietUninstallString))
            {
                var (exe, args) = SplitCommand(program.QuietUninstallString);
                candidates.Add((exe, args, true));
            }
            if (!string.IsNullOrWhiteSpace(program.ProductCode))
            {
                string msiArgs = preferSilent ? $"/x {program.ProductCode} /qn /norestart" : $"/x {program.ProductCode} /norestart";
                candidates.Add((Path.Combine(Environment.SystemDirectory, "msiexec.exe"), msiArgs, preferSilent));
            }
            if (!string.IsNullOrWhiteSpace(program.UninstallString))
            {
                var (exe, args) = SplitCommand(program.UninstallString);
                if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
                    exe.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                {
                    args = Regex.Replace(args, @"/I(\s*\{)", "/X$1", RegexOptions.IgnoreCase);
                }
                candidates.Add((exe, args, false));
            }

            if (candidates.Count == 0)
            {
                return new UninstallResult(false, "O programa não informa comando de desinstalação. Use 'Remover Entrada Forçada'.");
            }

            bool ranAny = false;
            int lastExit = 0;

            foreach (var (exe, args, silent) in candidates)
            {
                if (Path.IsPathRooted(exe) && !File.Exists(exe)) continue;
                ranAny = true;

                statusCallback?.Invoke(silent
                    ? $"Desinstalando {program.Name} silenciosamente..."
                    : $"Aguardando o desinstalador de {program.Name} terminar...");

                try
                {
                    var psi = new ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = !silent
                    };
                    if (Path.IsPathRooted(exe))
                    {
                        string? wd = Path.GetDirectoryName(exe);
                        if (!string.IsNullOrEmpty(wd) && Directory.Exists(wd)) psi.WorkingDirectory = wd;
                    }

                    using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao abrir desinstalador.");
                    using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
                    await proc.WaitForExitAsync(ct);
                    lastExit = proc.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    return new UninstallResult(false, "Desinstalação interrompida pelo usuário.", Cancelled: true);
                }
                catch
                {
                    lastExit = -1;
                    continue;
                }

                await Task.Delay(700, CancellationToken.None);
                bool reboot = lastExit is 1641 or 3010;
                if (!EntryExists(program) || reboot)
                {
                    return new UninstallResult(
                        true,
                        reboot ? "Desinstalado (reinicie para concluir a remoção)." : $"{program.Name} desinstalado com sucesso.",
                        RebootRequired: reboot);
                }

                if (lastExit is 1223 or 1602)
                {
                    return new UninstallResult(false, "Desinstalação cancelada no assistente do programa.", Cancelled: true);
                }
                if (lastExit == 1605)
                {
                    return new UninstallResult(false, "O Windows indica que este programa já foi removido. Use a limpeza de entrada/sobras.");
                }
            }

            return !ranAny
                ? new UninstallResult(false, "O arquivo do desinstalador não existe mais. Você pode remover a entrada órfã e buscar sobras.")
                : new UninstallResult(false, $"Desinstalador terminou (código {lastExit}), mas a entrada ainda consta no registro.");
        }

        public List<ProgramLeftoverItem> FindLeftovers(
            InstalledProgramItem target,
            LeftoverLevel maxLevel,
            IEnumerable<InstalledProgramItem> allInstalled,
            IEnumerable<(string RootPath, string Label)>? customScanRoots = null)
        {
            var otherNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var otherPublishers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var otherFolders = new List<string>();

            foreach (var other in allInstalled)
            {
                if (string.Equals(other.KeyId, target.KeyId, StringComparison.OrdinalIgnoreCase)) continue;
                otherNames.UnionWith(GetCandidateTokens(other));
                string? op = NormalizePublisher(other.Publisher);
                if (op != null) otherPublishers.Add(op);
                otherFolders.AddRange(GetKnownFolders(other));
            }

            var targetNames = GetCandidateTokens(target);
            targetNames.ExceptWith(otherNames);

            string? publisher = NormalizePublisher(target.Publisher);
            bool largePub = publisher != null && LargePublishers.Contains(publisher);

            var roots = (customScanRoots ?? GetDefaultScanRoots()).ToList();
            var rootPathSet = roots.Select(r => r.RootPath.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var found = new Dictionary<string, ProgramLeftoverItem>(StringComparer.OrdinalIgnoreCase);

            void AddItem(ProgramLeftoverItem item)
            {
                if (item.Level > maxLevel) return;
                string key = $"{item.Kind}|{item.Path}|{item.ValueName}";
                if (!found.TryGetValue(key, out var prev) || prev.Level > item.Level)
                {
                    found[key] = item;
                }
            }

            bool IsFolderSafeToRemove(string path) =>
                IsSafeFolderPath(path, otherFolders, rootPathSet);

            void AddFolder(string path, LeftoverLevel lvl, string reason)
            {
                if (lvl > maxLevel || !Directory.Exists(path) || !IsFolderSafeToRemove(path)) return;
                string full = Path.GetFullPath(path).TrimEnd('\\');
                bool review = full.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) || HasPersonalMarkers(full);
                if (HasPersonalMarkers(full))
                {
                    lvl = LeftoverLevel.Advanced;
                    reason += " (pode conter perfis/dados pessoais — revise)";
                }
                if (lvl > maxLevel) return;

                AddItem(new ProgramLeftoverItem
                {
                    Kind = LeftoverKind.Folder,
                    Level = lvl,
                    Path = full,
                    Reason = reason,
                    SizeBytes = MeasureFolderSize(full, maxFiles: 2000),
                    RequiresReview = review
                });
            }

            void AddRegistryKey(string regPath, LeftoverLevel lvl, string reason)
            {
                if (lvl > maxLevel) return;
                string[] segs = regPath.Split('\\');
                if (segs.Length < 3) return;
                if (segs.Skip(1).Any(s =>
                    s.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("Classes", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("Policies", StringComparison.OrdinalIgnoreCase)))
                    return;

                string leaf = NormalizeToken(segs[^1]);
                if (leaf.Length < 3 || GenericTokens.Contains(leaf)) return;

                AddItem(new ProgramLeftoverItem
                {
                    Kind = LeftoverKind.RegistryKey,
                    Level = lvl,
                    Path = regPath,
                    Reason = reason
                });
            }

            foreach (string knownDir in GetKnownFolders(target))
            {
                AddFolder(knownDir, LeftoverLevel.Safe, "Pasta de instalação / desinstalador do programa");
            }

            if (targetNames.Count > 0 || publisher != null)
            {
                foreach (var (rootPath, label) in roots)
                {
                    if (!Directory.Exists(rootPath)) continue;
                    foreach (string subDir in SafeGetDirectories(rootPath))
                    {
                        string token = NormalizeToken(Path.GetFileName(subDir));
                        if (targetNames.Contains(token))
                        {
                            AddFolder(subDir, LeftoverLevel.Safe, $"Pasta com nome do programa ({label})");
                        }
                        else if (publisher != null && token == publisher)
                        {
                            var childDirs = SafeGetDirectories(subDir);
                            int matchedChildren = 0;
                            foreach (string child in childDirs)
                            {
                                string childToken = NormalizeToken(Path.GetFileName(child));
                                if (targetNames.Contains(childToken))
                                {
                                    AddFolder(child, LeftoverLevel.Safe, $"Subpasta do programa na editora ({label})");
                                    matchedChildren++;
                                }
                            }
                            if (childDirs.Count - matchedChildren == 0 && SafeGetFilesCount(subDir) == 0)
                            {
                                AddFolder(subDir, LeftoverLevel.Moderate, $"Pasta vazia da editora ({label})");
                            }
                            else if (!largePub && !otherPublishers.Contains(publisher))
                            {
                                AddFolder(subDir, LeftoverLevel.Advanced, $"Pasta da editora sem outros apps instalados ({label})");
                            }
                        }
                    }
                }

                if (customScanRoots == null)
                {
                    foreach (string regRoot in RegistryScanRoots)
                    {
                        foreach (string subName in _registry.GetSubKeyNames(ToShortHive(regRoot)))
                        {
                            string token = NormalizeToken(subName);
                            if (token.Length < 3 || GenericTokens.Contains(token)) continue;

                            string fullKey = $@"{regRoot}\{subName}";
                            if (targetNames.Contains(token))
                            {
                                AddRegistryKey(fullKey, LeftoverLevel.Safe, "Chave de configuração do programa no registro");
                            }
                            else if (publisher != null && token == publisher)
                            {
                                foreach (string childKey in _registry.GetSubKeyNames(ToShortHive(fullKey)))
                                {
                                    if (targetNames.Contains(NormalizeToken(childKey)))
                                    {
                                        AddRegistryKey($@"{fullKey}\{childKey}", LeftoverLevel.Safe, "Chave do programa dentro da editora");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(target.RegistryPath) && _registry.KeyExists(ToShortHive(target.RegistryPath)))
            {
                AddItem(new ProgramLeftoverItem
                {
                    Kind = LeftoverKind.RegistryKey,
                    Level = LeftoverLevel.Safe,
                    Path = target.RegistryPath,
                    Reason = "Entrada remanescente na lista de programas do Windows"
                });
            }

            return found.Values
                .OrderBy(l => l.IsRegistry ? 1 : 0)
                .ThenBy(l => l.Level)
                .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<LeftoverCleanResult> CleanLeftoversAsync(
            string title,
            IEnumerable<ProgramLeftoverItem> selectedItems,
            bool sendToRecycleBin = true)
        {
            var items = selectedItems.Where(x => x.IsSelected).ToList();
            if (items.Count == 0) return new LeftoverCleanResult(0, new List<string>(), null);

            return await Task.Run(() =>
            {
                var errors = new List<string>();
                int removed = 0;

                string safeName = SanitizeFileName(title);
                string backupDir = Path.Combine(_backupsDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{safeName}");
                var backup = new UninstallBackupInfo
                {
                    Title = title,
                    Date = DateTime.Now,
                    FolderPath = backupDir
                };

                int regIndex = 0;
                var deletedRegRoots = new List<string>();

                foreach (var reg in items.Where(x => x.IsRegistry).OrderBy(x => x.Path.Length))
                {
                    if (deletedRegRoots.Any(r => reg.Path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                                                 reg.Path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)))
                    {
                        removed++;
                        continue;
                    }

                    string shortPath = ToShortHive(reg.Path);
                    if (!_registry.KeyExists(shortPath))
                    {
                        removed++;
                        continue;
                    }

                    try
                    {
                        Directory.CreateDirectory(backupDir);
                        string regFile = Path.Combine(backupDir, $"{++regIndex:00}.reg");
                        var (expExit, expOut) = _exportReg(reg.Path, regFile);
                        if (expExit != 0 || !File.Exists(regFile))
                        {
                            errors.Add($"{reg.Path}: falha no backup (.reg) — nada apagado ({expOut.Trim()}).");
                            continue;
                        }

                        _registry.DeleteKeyTree(shortPath);
                        deletedRegRoots.Add(reg.Path);
                        backup.Items.Add(new UninstallBackupItem
                        {
                            Description = reg.DisplayPath,
                            FileName = Path.GetFileName(regFile)
                        });
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{reg.Path}: {ex.Message}");
                    }
                }

                foreach (var fs in items.Where(x => !x.IsRegistry).OrderBy(x => x.Path.Length))
                {
                    try
                    {
                        bool exists = fs.Kind == LeftoverKind.Folder ? Directory.Exists(fs.Path) : File.Exists(fs.Path);
                        if (!exists)
                        {
                            removed++;
                            continue;
                        }

                        if (!IsSafeFolderPath(fs.Path, Array.Empty<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                        {
                            errors.Add($"{fs.Path}: bloqueado por proteção contra exclusão de pasta crítica.");
                            continue;
                        }

                        if (sendToRecycleBin)
                        {
                            MoveToRecycleBin(fs.Path);
                        }
                        else if (fs.Kind == LeftoverKind.Folder)
                        {
                            Directory.Delete(fs.Path, recursive: true);
                        }
                        else
                        {
                            File.Delete(fs.Path);
                        }

                        bool stillExists = fs.Kind == LeftoverKind.Folder ? Directory.Exists(fs.Path) : File.Exists(fs.Path);
                        if (stillExists)
                        {
                            errors.Add($"{fs.Path}: alguns arquivos estão em uso. Feche o app ou reinicie.");
                        }
                        else
                        {
                            backup.RemovedPaths.Add(fs.Path);
                            removed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{fs.Path}: {ex.Message}");
                    }
                }

                if (backup.Items.Count > 0 || backup.RemovedPaths.Count > 0)
                {
                    try
                    {
                        Directory.CreateDirectory(backupDir);
                        File.WriteAllText(Path.Combine(backupDir, "info.json"), JsonSerializer.Serialize(backup, JsonOptions));
                    }
                    catch
                    {
                    }
                }
                else
                {
                    backup = null;
                }

                return new LeftoverCleanResult(removed, errors, backup);
            });
        }

        public List<UninstallBackupInfo> LoadBackups()
        {
            var list = new List<UninstallBackupInfo>();
            if (!Directory.Exists(_backupsDir)) return list;

            foreach (string dir in Directory.GetDirectories(_backupsDir))
            {
                try
                {
                    string infoFile = Path.Combine(dir, "info.json");
                    if (!File.Exists(infoFile)) continue;
                    var info = JsonSerializer.Deserialize<UninstallBackupInfo>(File.ReadAllText(infoFile));
                    if (info != null)
                    {
                        info.FolderPath = dir;
                        list.Add(info);
                    }
                }
                catch
                {
                }
            }

            return list.OrderByDescending(b => b.Date).ToList();
        }

        public Task<(bool Ok, string Message)> RestoreBackupAsync(UninstallBackupInfo backup)
        {
            return Task.Run(() =>
            {
                var errs = new List<string>();
                string baseDir = Path.GetFullPath(backup.FolderPath).TrimEnd('\\') + "\\";

                foreach (var item in backup.Items)
                {
                    string candidate = Path.GetFullPath(Path.Combine(baseDir, item.FileName ?? ""));
                    if (string.IsNullOrEmpty(item.FileName) ||
                        Path.IsPathRooted(item.FileName) ||
                        !candidate.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) ||
                        !candidate.EndsWith(".reg", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(candidate) ||
                        File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint))
                    {
                        errs.Add($"{item.Description}: arquivo de backup inválido ou ausente.");
                        continue;
                    }

                    var (exit, output) = RunRegExe($"import \"{candidate}\" /reg:64");
                    if (exit != 0) errs.Add($"{item.Description}: {output.Trim()}");
                }

                if (errs.Count < backup.Items.Count)
                {
                    backup.RestoredAt = DateTime.Now;
                    try
                    {
                        File.WriteAllText(Path.Combine(backup.FolderPath, "info.json"), JsonSerializer.Serialize(backup, JsonOptions));
                    }
                    catch
                    {
                    }
                }

                return errs.Count == 0
                    ? (true, $"{backup.Items.Count} entrada(s) do registro restaurada(s).")
                    : (false, "Alguns itens falharam ao restaurar:\n" + string.Join("\n", errs));
            });
        }

        public static bool IsSafeFolderPath(
            string candidatePath,
            IReadOnlyList<string> otherProgramFolders,
            IReadOnlySet<string> scanRoots)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidatePath).TrimEnd('\\');
            }
            catch
            {
                return false;
            }

            if (full.Length <= 3) return false;
            if (IsInsideOrEqual(full, WindowsDir)) return false;
            if (full.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) return false;
            if (IsInsideOrEqual(full, HubDataDir)) return false;

            if (ProtectedSystemRoots.Any(r => !string.IsNullOrEmpty(r) &&
                (full.Equals(r, StringComparison.OrdinalIgnoreCase) || IsInsideOrEqual(r, full))))
                return false;

            if (PersonalFolders.Any(p => !string.IsNullOrEmpty(p) && IsInsideOrEqual(full, p)))
                return false;

            string baseDir = AppContext.BaseDirectory.TrimEnd('\\');
            if (IsInsideOrEqual(full, baseDir) || IsInsideOrEqual(baseDir, full)) return false;

            string leaf = NormalizeToken(Path.GetFileName(full));
            if (leaf.Length < 3 || GenericTokens.Contains(leaf)) return false;

            foreach (string other in otherProgramFolders)
            {
                if (IsInsideOrEqual(full, other) || IsInsideOrEqual(other, full)) return false;
            }

            if (Directory.Exists(full))
            {
                try
                {
                    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
                }
                catch
                {
                    return false;
                }
            }

            int depth = full.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
            string? parent = Path.GetDirectoryName(full)?.TrimEnd('\\');
            if (depth < 3 && (parent == null || !scanRoots.Contains(parent))) return false;

            return true;
        }

        private static bool IsInsideOrEqual(string child, string parent) =>
            child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        private static string ToShortHive(string regPath)
        {
            if (regPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
                return "HKLM\\" + regPath["HKEY_LOCAL_MACHINE\\".Length..];
            if (regPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
                return "HKCU\\" + regPath["HKEY_CURRENT_USER\\".Length..];
            return regPath;
        }

        private static (int ExitCode, string Output) ExportRegistryKey(string regPath, string destFile) =>
            RunRegExe($"export \"{regPath}\" \"{destFile}\" /y /reg:64");

        private static (int ExitCode, string Output) RunRegExe(string args)
        {
            string regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
            var psi = new ProcessStartInfo(regExe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível executar reg.exe.");
            string output = proc.StandardOutput.ReadToEnd() + "\n" + proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);
            return (proc.ExitCode, output);
        }

        private static void MoveToRecycleBin(string path)
        {
            const uint FO_DELETE = 3;
            const ushort FOF_SILENT = 0x0004;
            const ushort FOF_NOCONFIRMATION = 0x0010;
            const ushort FOF_ALLOWUNDO = 0x0040;
            const ushort FOF_NOERRORUI = 0x0400;

            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };
            SHFileOperation(ref op);
        }

        private static IEnumerable<(string RootPath, string Label)> GetDefaultScanRoots()
        {
            yield return (ProgramFilesDir, "Arquivos de Programas");
            if (!string.Equals(ProgramFilesX86Dir, ProgramFilesDir, StringComparison.OrdinalIgnoreCase))
                yield return (ProgramFilesX86Dir, "Arquivos de Programas (x86)");
            yield return (LocalAppDataDir, "AppData\\Local");
            yield return (RoamingAppDataDir, "AppData\\Roaming");
            yield return (ProgramDataDir, "ProgramData");
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Menu Iniciar");
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Menu Iniciar (Todos)");
        }

        private static IEnumerable<string> GetKnownFolders(InstalledProgramItem p)
        {
            if (!string.IsNullOrWhiteSpace(p.InstallLocation)) yield return p.InstallLocation;

            foreach (string? cmd in new[] { p.QuietUninstallString, p.UninstallString, p.DisplayIcon })
            {
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                string candidate = cmd.Split(',')[0].Trim().Trim('"');
                if (candidate.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) continue;
                var (exe, _) = SplitCommand(candidate);
                if (Path.IsPathRooted(exe))
                {
                    string? dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir)) yield return dir;
                }
            }
        }

        private static HashSet<string> GetCandidateTokens(InstalledProgramItem p)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string baseName = Regex.Replace(p.Name, @"\s*(\([^)]*\)|v?\d+(\.\d+)+|x64|x86|64-bit|32-bit)\s*", " ", RegexOptions.IgnoreCase).Trim();
            string norm = NormalizeToken(baseName);
            if (norm.Length >= 4 && !GenericTokens.Contains(norm)) set.Add(norm);

            string fullNorm = NormalizeToken(p.Name);
            if (fullNorm.Length >= 4 && !GenericTokens.Contains(fullNorm)) set.Add(fullNorm);
            return set;
        }

        private static string? NormalizePublisher(string? publisher)
        {
            if (string.IsNullOrWhiteSpace(publisher)) return null;
            string clean = Regex.Replace(publisher, @",?\s*\b(Inc|LLC|Ltd|Corp|Corporation|GmbH|Co|SA|Ltda)\b\.?", "", RegexOptions.IgnoreCase).Trim();
            string norm = NormalizeToken(clean);
            return norm.Length >= 3 && !GenericTokens.Contains(norm) ? norm : null;
        }

        internal static string NormalizeToken(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static bool IsUninstallerExecutableMissing(string? command, string? productCode)
        {
            if (productCode != null || command == null) return false;
            var (exe, _) = SplitCommand(command);
            return Path.IsPathRooted(exe) && !File.Exists(exe);
        }

        private static int CompletenessScore(InstalledProgramItem p) =>
            (p.DisplayIcon != null ? 1 : 0) +
            (p.InstallLocation != null ? 1 : 0) +
            (p.EstimatedBytes > 0 ? 1 : 0) +
            (p.QuietUninstallString != null ? 1 : 0) +
            (p.IsMissingUninstaller ? -3 : 0);

        private static string? CleanOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static DateTime? ParseInstallDate(string? raw) =>
            raw != null && DateTime.TryParseExact(raw.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt
                : null;

        private static DateTime? ReadLastWriteTime(RegistryKey key)
        {
            try
            {
                if (RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out long ft) == 0 && ft > 0)
                    return DateTime.FromFileTime(ft);
            }
            catch
            {
            }
            return null;
        }

        private static bool HasPersonalMarkers(string dir)
        {
            string leaf = Path.GetFileName(dir);
            if (PersonalDataMarkers.Contains(leaf)) return true;
            try
            {
                return Directory.EnumerateDirectories(dir).Take(25).Any(d => PersonalDataMarkers.Contains(Path.GetFileName(d)));
            }
            catch
            {
                return false;
            }
        }

        private static long MeasureFolderSize(string path, int maxFiles)
        {
            long total = 0;
            int counted = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (++counted > maxFiles) break;
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch
            {
            }
            return total;
        }

        private static List<string> SafeGetDirectories(string dir)
        {
            try { return Directory.GetDirectories(dir).ToList(); } catch { return new List<string>(); }
        }

        private static int SafeGetFilesCount(string dir)
        {
            try { return Directory.GetFiles(dir).Length; } catch { return 1; }
        }

        private static string SanitizeFileName(string raw)
        {
            string clean = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            return clean.Length > 0 ? clean[..Math.Min(40, clean.Length)] : "programa";
        }
    }
}
