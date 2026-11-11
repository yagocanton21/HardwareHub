using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    public sealed class FormatBackupService
    {
        public const string ManifestFileName = "hub-backup.json";
        private const string BackupPrefix = "HUB-Backup-";

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
        private static readonly Regex OemInfRegex = new(@"oem\d+\.inf", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TrailingNumberRegex = new(@":\s*(\d+)\s*$", RegexOptions.Compiled);

        private readonly Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>> _runProcess;
        private readonly Func<bool> _isAdmin;
        private readonly Func<bool> _hasWifiAdapter;

        public FormatBackupService(
            Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>>? processHook = null,
            Func<bool>? isAdminHook = null,
            Func<bool>? hasWifiHook = null)
        {
            _runProcess = processHook ?? DefaultRunProcessAsync;
            _isAdmin = isAdminHook ?? DriversUpdateService.IsAdministrator;
            _hasWifiAdapter = hasWifiHook ?? DefaultHasWifiAdapter;
        }

        public static bool IsOnWindowsDrive(string folder)
        {
            try
            {
                string winRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
                return string.Equals(Path.GetPathRoot(Path.GetFullPath(folder)), winRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return true;
            }
        }

        public async Task<(bool Success, string Message, string BackupFolder)> CreateBackupAsync(
            string destinationRoot,
            bool includeDrivers,
            bool includeWifi,
            Action<string, double?>? progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(destinationRoot) || !Directory.Exists(destinationRoot))
                return (false, "Pasta de destino não encontrada.", "");

            if (!_isAdmin())
                return (false, "Execute o Hardware HUB como Administrador para exportar drivers e chaves Wi-Fi.", "");

            if (IsReparsePoint(destinationRoot))
                return (false, "Por segurança, o destino não pode ser um link simbólico ou junção.", "");

            string folder = Path.Combine(destinationRoot, BackupPrefix + DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture));
            if (Directory.Exists(folder))
                folder += "-" + DateTime.Now.ToString("ss", CultureInfo.InvariantCulture);

            Directory.CreateDirectory(folder);

            var manifest = new FormatBackupManifest
            {
                HubVersion = "1.0.0",
                CreatedAt = DateTime.Now,
                WindowsVersion = Environment.OSVersion.VersionString,
                MachineName = Environment.MachineName,
                Motherboard = ReadBoardSummary()
            };

            var warnings = new List<string>();

            if (includeDrivers)
            {
                progress?.Invoke("Exportando drivers instalados (pnputil)...", null);
                string driversDir = Directory.CreateDirectory(Path.Combine(folder, manifest.DriversFolder)).FullName;
                string pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
                var (exit, output) = await _runProcess(pnputil, $"/export-driver * \"{driversDir}\"", ct);

                int exported = 0;
                try { exported = Directory.EnumerateFiles(driversDir, "*.inf", SearchOption.AllDirectories).Count(); } catch { }
                manifest.DriversCount = exported;

                if (exported == 0)
                {
                    warnings.Add(exit == 0
                        ? "Nenhum driver de terceiros encontrado para exportar."
                        : $"Falha ao exportar drivers: {output.Trim()}");
                }
            }
            else
            {
                manifest.DriversFolder = "";
            }

            ct.ThrowIfCancellationRequested();

            if (includeWifi)
            {
                progress?.Invoke("Exportando perfis e senhas Wi-Fi...", null);
                if (!_hasWifiAdapter())
                {
                    warnings.Add("Nenhum adaptador Wi-Fi ativo encontrado neste PC.");
                    manifest.WifiFolder = "";
                }
                else
                {
                    string wifiDir = Directory.CreateDirectory(Path.Combine(folder, manifest.WifiFolder)).FullName;
                    string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
                    var (wExit, wOut) = await _runProcess(netsh, $"wlan export profile key=clear folder=\"{wifiDir.TrimEnd('\\')}\"", ct);
                    if (wExit != 0)
                        warnings.Add($"netsh wlan retornou código {wExit}: {wOut.Trim()}");

                    var profiles = ReadWifiProfiles(wifiDir);
                    foreach (var (_, ssid) in profiles)
                        manifest.SsidList.Add(ssid);
                    manifest.WifiCount = profiles.Count;

                    if (profiles.Count == 0)
                    {
                        manifest.WifiFolder = "";
                        try { if (!Directory.EnumerateFileSystemEntries(wifiDir).Any()) Directory.Delete(wifiDir); } catch { }
                    }
                }
            }
            else
            {
                manifest.WifiFolder = "";
            }

            bool anythingSaved = manifest.DriversCount > 0 || manifest.WifiCount > 0;
            if (anythingSaved)
            {
                File.WriteAllText(
                    Path.Combine(folder, ManifestFileName),
                    JsonSerializer.Serialize(manifest, JsonOpts),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            else
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }

            string summary = anythingSaved
                ? $"Backup criado: {manifest.DriversCount} arquivo(s) .inf de driver e {manifest.WifiCount} rede(s) Wi-Fi em {folder}."
                : "Nada foi salvo neste backup.";

            if (warnings.Count > 0)
                summary += "\n" + string.Join("\n", warnings);

            return (anythingSaved, summary, anythingSaved ? folder : "");
        }

        public List<FoundFormatBackupModel> ScanForBackups(CancellationToken ct = default)
        {
            var found = new List<FoundFormatBackupModel>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); } catch { return found; }

            foreach (var d in drives)
            {
                ct.ThrowIfCancellationRequested();
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Network or DriveType.Unknown)
                    continue;

                string root = d.RootDirectory.FullName;
                TryAdd(root);

                foreach (string sub in SafeSubdirs(root))
                {
                    ct.ThrowIfCancellationRequested();
                    TryAdd(sub);
                    if (!Path.GetFileName(sub).StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string nested in SafeSubdirs(sub, BackupPrefix + "*"))
                            TryAdd(nested);
                    }
                }
            }

            return found.OrderByDescending(b => b.Manifest.CreatedAt).ToList();

            void TryAdd(string path)
            {
                if (!File.Exists(Path.Combine(path, ManifestFileName))) return;
                var loaded = TryLoadBackup(path, out _);
                if (loaded != null) found.Add(loaded);
            }
        }

        public FoundFormatBackupModel? TryLoadBackup(string path, out string error)
        {
            error = "";
            try
            {
                if (File.Exists(path) && Path.GetFileName(path).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    path = Path.GetDirectoryName(path) ?? "";

                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    error = "A pasta de backup não existe.";
                    return null;
                }

                if (IsReparsePoint(path))
                {
                    error = "A pasta de backup não pode ser um link simbólico.";
                    return null;
                }

                string manifestPath = Path.Combine(path, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    string? latest = SafeSubdirs(path, BackupPrefix + "*")
                        .Where(p => File.Exists(Path.Combine(p, ManifestFileName)))
                        .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    if (latest == null)
                    {
                        error = $"Nenhum arquivo {ManifestFileName} encontrado na pasta selecionada.";
                        return null;
                    }

                    path = latest;
                    manifestPath = Path.Combine(path, ManifestFileName);
                }

                if (new FileInfo(manifestPath).Length > 1_000_000)
                {
                    error = "Arquivo de manifesto inválido ou grande demais.";
                    return null;
                }

                var manifest = JsonSerializer.Deserialize<FormatBackupManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || manifest.FormatVersion < 1)
                {
                    error = "Arquivo de manifesto corrompido.";
                    return null;
                }
                if (manifest.FormatVersion > 1)
                {
                    error = "Backup criado por uma versão mais nova do HUB.";
                    return null;
                }

                string driversDir = ResolveSafeSubfolder(path, manifest.DriversFolder);
                int infCount = driversDir.Length > 0 ? CountSafeInfs(driversDir, out _) : 0;

                string wifiDir = ResolveSafeSubfolder(path, manifest.WifiFolder);
                var wifiProfiles = wifiDir.Length > 0 ? ReadWifiProfiles(wifiDir) : new List<(string XmlPath, string Ssid)>();

                string currentBoard = ReadBoardSummary();
                bool diffHw = manifest.Motherboard.Length > 0 && currentBoard.Length > 0 &&
                              !manifest.Motherboard.Equals(currentBoard, StringComparison.OrdinalIgnoreCase);

                return new FoundFormatBackupModel
                {
                    FolderPath = path,
                    Manifest = manifest,
                    DriversPath = infCount > 0 ? driversDir : "",
                    InfCount = infCount,
                    WifiProfiles = wifiProfiles,
                    IsDifferentHardware = diffHw
                };
            }
            catch (Exception ex)
            {
                error = $"Erro ao ler backup: {ex.Message}";
                return null;
            }
        }

        public static List<(string XmlPath, string Ssid)> ReadWifiProfiles(string folder)
        {
            var result = new List<(string, string)>();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.xml", new EnumerationOptions { IgnoreInaccessible = true }).ToList();
            }
            catch
            {
                return result;
            }

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_000_000
            };

            foreach (string file in files)
            {
                try
                {
                    if (IsReparsePoint(file)) continue;
                    using var reader = XmlReader.Create(file, settings);
                    if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "WLANProfile")
                        continue;

                    string ssid = "";
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "name")
                        {
                            ssid = reader.ReadElementContentAsString().Trim();
                            break;
                        }
                    }

                    if (ssid.Length > 0)
                        result.Add((file, ssid));
                }
                catch
                {
                }
            }

            return result.OrderBy(p => p.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public async Task<RestoreDriversResult> RestoreDriversAsync(
            string driversFolder,
            Action<string, double?>? progress = null,
            CancellationToken ct = default)
        {
            int total = CountSafeInfs(driversFolder, out bool hasLink);
            if (hasLink)
                return new RestoreDriversResult(false, 0, 0, 0, 0, false, "Pasta contém atalho/junção simbólica bloqueada por segurança.");

            if (total == 0)
                return new RestoreDriversResult(false, 0, 0, 0, 0, false, "Nenhum arquivo .inf encontrado na pasta de drivers.");

            if (!_isAdmin())
                return new RestoreDriversResult(false, total, 0, 0, 0, false, "Execute como Administrador para instalar drivers.");

            progress?.Invoke($"Instalando {total} driver(s) do backup...", null);
            string pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
            string args = $"/add-driver \"{Path.Combine(driversFolder, "*.inf")}\" /subdirs /install";
            var (exit, output) = await _runProcess(pnputil, args, ct);

            var counts = output.Split('\n')
                .Select(l => TrailingNumberRegex.Match(l.TrimEnd('\r')))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .TakeLast(2)
                .ToList();

            int seenTotal = counts.Count == 2 ? counts[0] : total;
            int added = counts.Count == 2 ? counts[1] : ((exit is 0 or 259 or 3010) ? total : 0);
            int failed = Math.Max(0, seenTotal - added);
            bool reboot = exit == 3010 ||
                          output.Contains("necessário reiniciar", StringComparison.OrdinalIgnoreCase) ||
                          output.Contains("reboot", StringComparison.OrdinalIgnoreCase);
            bool ok = added > 0 || exit is 0 or 259 or 3010;

            string msg = $"Drivers restaurados: {added} aceito(s) de {seenTotal}" +
                         (failed > 0 ? $" • {failed} incompatível(is)" : "") +
                         (reboot ? " (reinicie o PC para concluir)." : ".");
            return new RestoreDriversResult(ok, seenTotal, added, 0, failed, reboot, msg);
        }

        public async Task<RestoreWifiResult> RestoreWifiAsync(
            IReadOnlyList<(string XmlPath, string Ssid)> profiles,
            Action<string, double?>? progress = null,
            CancellationToken ct = default)
        {
            if (profiles.Count == 0)
                return new RestoreWifiResult(false, 0, 0, 0, "Nenhuma rede Wi-Fi no backup.");

            if (!_isAdmin())
                return new RestoreWifiResult(false, profiles.Count, 0, 0, "Execute como Administrador para restaurar perfis Wi-Fi.");

            if (!_hasWifiAdapter())
                return new RestoreWifiResult(false, profiles.Count, 0, profiles.Count, "Nenhum adaptador Wi-Fi ativo encontrado neste PC (instale o driver Wi-Fi antes).");

            string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            int ok = 0;

            for (int i = 0; i < profiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (xmlPath, ssid) = profiles[i];
                progress?.Invoke($"Restaurando rede Wi-Fi '{ssid}' ({i + 1}/{profiles.Count})...", (double)(i + 1) / profiles.Count);
                var (exit, _) = await _runProcess(netsh, $"wlan add profile filename=\"{xmlPath}\" user=all", ct);
                if (exit == 0) ok++;
            }

            int failed = profiles.Count - ok;
            string msg = $"Wi-Fi: {ok} rede(s) restaurada(s)" + (failed > 0 ? $" • {failed} falharam." : ".");
            return new RestoreWifiResult(ok > 0, profiles.Count, ok, failed, msg);
        }

        public static int CountSafeInfs(string folder, out bool hasReparsePoint)
        {
            hasReparsePoint = false;
            try
            {
                if (IsReparsePoint(folder))
                {
                    hasReparsePoint = true;
                    return 0;
                }

                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None
                };

                if (Directory.EnumerateDirectories(folder, "*", opts).Any(IsReparsePoint))
                {
                    hasReparsePoint = true;
                    return 0;
                }

                return Directory.EnumerateFiles(folder, "*.inf", opts).Count();
            }
            catch
            {
                return 0;
            }
        }

        private static string ResolveSafeSubfolder(string root, string? childName)
        {
            if (string.IsNullOrWhiteSpace(childName) || childName is "." or "..")
                return "";
            if (childName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || childName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "";

            string full = Path.Combine(root, childName);
            if (!Directory.Exists(full) || IsReparsePoint(full))
                return "";
            return full;
        }

        private static IEnumerable<string> SafeSubdirs(string folder, string pattern = "*")
        {
            try
            {
                return Directory.EnumerateDirectories(folder, pattern, new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                }).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string ReadBoardSummary()
        {
            try
            {
                var info = new DriversUpdateService().ReadBoardInfo();
                return info.DisplayTitle;
            }
            catch
            {
                return "";
            }
        }

        private static bool DefaultHasWifiAdapter()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            }
            catch
            {
                return false;
            }
        }

        private static async Task<(int ExitCode, string Output)> DefaultRunProcessAsync(string exe, string args, CancellationToken ct)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Falha ao iniciar {exe}.");
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            var stdout = await proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            await proc.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            return (proc.ExitCode, stdout + stderr);
        }
    }
}

