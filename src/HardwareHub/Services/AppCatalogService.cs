using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HardwareHub.Models;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HardwareHub.Services;

/// <summary>
/// Motor oficial de catalogo e instalacao de programas via Winget e Microsoft Store (baseado no fluxo oficial do HUB Pro).
/// </summary>
public sealed class AppCatalogService
{
    private const string WingetMutexName = @"Local\HardwareHub-winget-instalacao";
    private const string EmbeddedCatalogResourceName = "HardwareHub.Resources.Data.posformatacao.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly SemaphoreSlim WingetQueueSemaphore = new(1, 1);
    private static readonly SemaphoreSlim ShowConcurrencyGate = new(4, 4);
    private static readonly object SearchCacheLock = new();
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<string, (DateTime Timestamp, WingetSearchResponse Response)> SearchCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, WingetPackageDetails> DetailsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static AppCatalogDocument? _cachedDocument;

    private static readonly Regex ValidWingetIdRegex =
        new("^[A-Za-z0-9][A-Za-z0-9.\\-_+]{0,127}$", RegexOptions.CultureInvariant);

    private static readonly Regex ValidStoreIdRegex =
        new("^[A-Za-z0-9]{12}$", RegexOptions.CultureInvariant);

    private static readonly Regex LocaleSuffixRegex =
        new("^(?<b>.+)\\.(?<l>[a-z]{2,3}(-[A-Za-z0-9]{2,8})*)$", RegexOptions.CultureInvariant);

    private static readonly string[] PublisherLabels =
        { "Publisher", "Editor", "Editora", "Fabricante", "Herausgeber", "Éditeur" };

    private static readonly string[] DescriptionLabels =
        { "Description", "Descrição", "Descripción", "Beschreibung", "Descrizione", "Descriptif" };

    private static readonly string[] HomepageLabels =
        { "Homepage", "Home Page", "Página Inicial", "Página principal", "Startseite", "Page d'accueil" };

    /// <summary>
    /// Carrega e cacheia o catalogo embutido em posformatacao.json.
    /// </summary>
    public static AppCatalogDocument LoadCatalog()
    {
        if (_cachedDocument != null)
        {
            return _cachedDocument;
        }

        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedCatalogResourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Catalogo embutido '{EmbeddedCatalogResourceName}' nao encontrado no assembly.");
        }

        _cachedDocument = JsonSerializer.Deserialize<AppCatalogDocument>(stream, JsonOptions) ?? new AppCatalogDocument();
        return _cachedDocument;
    }

    /// <summary>
    /// Mapeia categorias longas para rotulos curtos nos chips de filtro (igual ao HUB Pro).
    /// </summary>
    public static string GetShortCategoryLabel(string category) => category switch
    {
        "Base para jogos e programas" => "Bibliotecas",
        "Inteligência artificial" => "IA",
        "Diagnóstico e disco" => "Diagnóstico",
        _ => category
    };

    /// <summary>
    /// Caminho oficial do alias do winget no perfil atual do usuario.
    /// </summary>
    public static string GetWingetExecutablePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string aliasPath = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(aliasPath) ? aliasPath : "winget.exe";
    }

    /// <summary>
    /// Verifica se o winget oficial da Microsoft (DesktopAppInstaller) esta presente e funcional.
    /// </summary>
    public static bool IsOfficialWingetAvailable()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string aliasPath = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(aliasPath))
        {
            return false;
        }

        try
        {
            string? target = ResolveAppExecutionAliasTarget(aliasPath);
            string windowsAppsPrefix = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps") + "\\";

            if (!string.IsNullOrWhiteSpace(target) &&
                Path.GetFullPath(target).StartsWith(windowsAppsPrefix + "Microsoft.DesktopAppInstaller_", StringComparison.OrdinalIgnoreCase) &&
                target.EndsWith("\\winget.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // Fallback abaixo verifica execucao direta caso o alias nao exponha reparse point legivel.
        }

        return File.Exists(aliasPath);
    }

    /// <summary>
    /// Tenta re-registrar o pacote Microsoft.DesktopAppInstaller caso o alias do winget esteja ausente ou quebrado.
    /// </summary>
    public static async Task<bool> TryRepairWingetAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
            }
        }
        catch
        {
            // Retorna verificacao atual abaixo.
        }

        return IsOfficialWingetAvailable();
    }

    /// <summary>
    /// Le os DisplayNames instalados nas tres arvores padrao de Uninstall do Registro do Windows.
    /// </summary>
    public static HashSet<string> ReadInstalledDisplayNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (RegistryHive Hive, RegistryView View)[] roots =
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default)
        };

        foreach (var (hive, view) in roots)
        {
            try
            {
                using RegistryKey? baseKey = RegistryKey.OpenBaseKey(hive, view)
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (baseKey == null)
                {
                    continue;
                }

                foreach (string subKeyName in baseKey.GetSubKeyNames())
                {
                    using RegistryKey? sub = baseKey.OpenSubKey(subKeyName);
                    if (sub?.GetValue("DisplayName") is string displayName && !string.IsNullOrWhiteSpace(displayName))
                    {
                        names.Add(displayName.Trim());
                    }
                }
            }
            catch
            {
                // Ignora chaves sem permissao de leitura.
            }
        }

        return names;
    }

    /// <summary>
    /// Verifica se um programa curado ja esta instalado por pacote Appx ou nome no Registro.
    /// </summary>
    public static bool IsProgramInstalled(
        AppPackageDefinition program,
        HashSet<string> installedRegistryNames,
        string? localAppDataPackagesPath = null)
    {
        if (program.IsLink)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(program.AppxPackage))
        {
            string appxPackagesDir = localAppDataPackagesPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");

            if (Directory.Exists(appxPackagesDir) &&
                Directory.EnumerateDirectories(appxPackagesDir, program.AppxPackage + "_*").Any())
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(program.InstalledName))
        {
            return false;
        }

        if (program.InstalledName.EndsWith('$'))
        {
            string exact = program.InstalledName[..^1];
            return installedRegistryNames.Contains(exact);
        }

        if (program.Id.Equals("vcredist86", StringComparison.OrdinalIgnoreCase))
        {
            return installedRegistryNames.Any(n =>
                n.StartsWith(program.InstalledName, StringComparison.OrdinalIgnoreCase) &&
                n.Contains("(x86)", StringComparison.OrdinalIgnoreCase));
        }

        if (program.Id.Equals("vcredist", StringComparison.OrdinalIgnoreCase))
        {
            return installedRegistryNames.Any(n =>
                n.StartsWith(program.InstalledName, StringComparison.OrdinalIgnoreCase) &&
                n.Contains("(x64)", StringComparison.OrdinalIgnoreCase));
        }

        if (installedRegistryNames.Any(n =>
            n.StartsWith(program.InstalledName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Programas portáteis instalados via Winget não gravam na chave Uninstall do Registro,
        // mas criam um executável ou link em %LOCALAPPDATA%\Microsoft\WinGet\Links ou Packages.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string linksDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        if (Directory.Exists(linksDir))
        {
            if (File.Exists(Path.Combine(linksDir, $"{program.Id}.exe"))) return true;
            if (Directory.EnumerateFiles(linksDir, "*.exe")
                .Any(f => Path.GetFileNameWithoutExtension(f).Contains(program.Id, StringComparison.OrdinalIgnoreCase) ||
                          (!string.IsNullOrWhiteSpace(program.Name) && Path.GetFileNameWithoutExtension(f).Contains(program.Name, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        string packagesDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packagesDir) && !string.IsNullOrWhiteSpace(program.WingetId))
        {
            if (Directory.EnumerateDirectories(packagesDir, $"{program.WingetId}_*").Any() ||
                Directory.EnumerateDirectories(packagesDir, $"*{program.WingetId}*").Any())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Monta a linha de argumentos oficial do winget para instalacao silenciosa e segura.
    /// </summary>
    public static string BuildInstallArguments(AppPackageDefinition program, string? locale, string? overrideId = null)
    {
        string targetId = overrideId ?? program.WingetId;
        string source = string.IsNullOrWhiteSpace(program.Source) ? "winget" : program.Source;
        string localeArg = !string.IsNullOrWhiteSpace(locale) ? $" --locale {locale}" : "";
        return $"install --id {targetId} -e --source {source} --silent --accept-package-agreements --accept-source-agreements --disable-interactivity{localeArg}";
    }

    /// <summary>
    /// Instala um programa via canal oficial (winget / msstore), com fallback automatico de locale e modo nao-administrador quando necessario.
    /// </summary>
    public async Task<AppInstallOutcome> InstallProgramAsync(
        AppPackageDefinition program,
        IProgress<string>? statusProgress,
        Action<string>? logCallback,
        CancellationToken ct = default)
    {
        if (program.IsLink)
        {
            return new AppInstallOutcome(false, -1, "Este item abre uma página oficial: não instala pelo winget.", "");
        }

        string? preferredLocale = string.IsNullOrWhiteSpace(program.Locale) ? null : program.Locale;
        string packageId = program.WingetId;

        var outcome = await RunSingleInstallAttemptAsync(program, preferredLocale, packageId, statusProgress, logCallback, ct);

        if (!outcome.Success && packageId.EndsWith(".pt-BR", StringComparison.OrdinalIgnoreCase) &&
            outcome.ShortMessage.Contains("0x8A150014", StringComparison.OrdinalIgnoreCase))
        {
            string baseId = packageId[..^".pt-BR".Length];
            logCallback?.Invoke($"[WINGET] '{packageId}' não encontrado; tentando pacote base '{baseId}'...");
            packageId = baseId;
            outcome = await RunSingleInstallAttemptAsync(program, preferredLocale, packageId, statusProgress, logCallback, ct);
        }

        if (!outcome.Success && preferredLocale != null &&
            outcome.ShortMessage.Contains("0x8A150010", StringComparison.OrdinalIgnoreCase))
        {
            logCallback?.Invoke($"[WINGET] Sem instalador no locale '{preferredLocale}' para {program.Name}; tentando instalador padrão...");
            outcome = await RunSingleInstallAttemptAsync(program, null, packageId, statusProgress, logCallback, ct);
        }

        if (outcome.Success)
        {
            EnsureStartMenuShortcutForPackage(program);
            string langSummary = ApplyPostInstallLanguageActions(program);
            if (!string.IsNullOrWhiteSpace(langSummary))
            {
                outcome = outcome with { ShortMessage = $"{outcome.ShortMessage} ({langSummary})" };
            }
        }

        return outcome;
    }

    private async Task<AppInstallOutcome> RunSingleInstallAttemptAsync(
        AppPackageDefinition program,
        string? locale,
        string packageId,
        IProgress<string>? statusProgress,
        Action<string>? logCallback,
        CancellationToken ct)
    {
        string args = BuildInstallArguments(program, locale, packageId);
        string wingetExe = GetWingetExecutablePath();

        await WingetQueueSemaphore.WaitAsync(ct);
        using Mutex? systemMutex = TryAcquireSystemMutex(statusProgress, logCallback);
        try
        {
            bool runAsStandardUser = program.UserScope && IsCurrentProcessElevated();
            logCallback?.Invoke($"[WINGET] Executando: winget {args}{(runAsStandardUser ? " (como usuário comum)" : "")}");

            var (exitCode, output) = runAsStandardUser
                ? await RunAsNonAdminAsync(wingetExe, args, statusProgress, ct)
                : await RunProcessAsync(wingetExe, args, statusProgress, logCallback, ct);

            return TranslateExitCode(exitCode, output);
        }
        finally
        {
            try { systemMutex?.ReleaseMutex(); } catch { }
            WingetQueueSemaphore.Release();
        }
    }

    private static Mutex? TryAcquireSystemMutex(IProgress<string>? statusProgress, Action<string>? logCallback)
    {
        try
        {
            var mutex = new Mutex(false, WingetMutexName);
            if (!mutex.WaitOne(0))
            {
                statusProgress?.Report("Aguardando outra instalação...");
                logCallback?.Invoke("[FILA] Aguardando outra instância do Winget concluir...");
                mutex.WaitOne();
            }
            return mutex;
        }
        catch (AbandonedMutexException abandoned)
        {
            return abandoned.Mutex;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Aplica ajustes opcionais de idioma em pt-BR apos instalar o pacote (ex: 7-Zip, VLC, Steam, Notepad++).
    /// </summary>
    public static string ApplyPostInstallLanguageActions(AppPackageDefinition program)
    {
        if (program.LanguageActions.Count == 0)
        {
            return "";
        }

        var errors = new List<string>();
        foreach (var action in program.LanguageActions)
        {
            try
            {
                if (action.Type.Equals("reg", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(action.Key) &&
                    !string.IsNullOrWhiteSpace(action.ValueName))
                {
                    WriteRegistryValue(action.Key, action.ValueName, action.Kind ?? "sz", action.Data ?? "");
                }
                else if (action.Type.Equals("copiar", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(action.FromPath) &&
                         !string.IsNullOrWhiteSpace(action.ToPath))
                {
                    string src = Environment.ExpandEnvironmentVariables(action.FromPath);
                    string dst = Environment.ExpandEnvironmentVariables(action.ToPath);
                    if (!File.Exists(src))
                    {
                        errors.Add($"arquivo '{Path.GetFileName(src)}' não encontrado");
                        continue;
                    }

                    string? dir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.Copy(src, dst, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        return errors.Count == 0 ? "em português" : $"idioma: {string.Join("; ", errors)}";
    }

    /// <summary>
    /// Garante que programas portáteis instalados pelo Winget (como Rufus) tenham um atalho no Menu Iniciar.
    /// </summary>
    public static bool EnsureStartMenuShortcutForPackage(AppPackageDefinition program)
    {
        try
        {
            string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            string safeName = string.IsNullOrWhiteSpace(program.Name) ? program.Id : program.Name;
            string targetLnk = Path.Combine(userPrograms, $"{safeName}.lnk");

            if (File.Exists(targetLnk) || File.Exists(Path.Combine(commonPrograms, $"{safeName}.lnk")))
            {
                return true;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string linksDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
            string packagesDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");

            string? targetExe = null;
            if (Directory.Exists(linksDir))
            {
                string candidate = Path.Combine(linksDir, $"{program.Id}.exe");
                if (File.Exists(candidate))
                {
                    targetExe = candidate;
                }
                else
                {
                    targetExe = Directory.EnumerateFiles(linksDir, "*.exe")
                        .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(program.Id, StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileNameWithoutExtension(f).Equals(safeName, StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileNameWithoutExtension(f).Contains(program.Id, StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileNameWithoutExtension(f).Contains(safeName, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (targetExe == null && Directory.Exists(packagesDir) && !string.IsNullOrWhiteSpace(program.WingetId))
            {
                string pkgPrefix = program.WingetId + "_";
                var pkgDir = Directory.EnumerateDirectories(packagesDir)
                    .FirstOrDefault(d => Path.GetFileName(d).StartsWith(pkgPrefix, StringComparison.OrdinalIgnoreCase) ||
                                         Path.GetFileName(d).Contains(program.WingetId, StringComparison.OrdinalIgnoreCase));
                if (pkgDir != null)
                {
                    var allExes = Directory.EnumerateFiles(pkgDir, "*.exe", SearchOption.AllDirectories)
                        .Where(e => !Path.GetFileName(e).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    targetExe = allExes.FirstOrDefault(e => Path.GetFileName(e).Contains(program.Id, StringComparison.OrdinalIgnoreCase) ||
                                                            Path.GetFileName(e).Contains(safeName, StringComparison.OrdinalIgnoreCase))
                                ?? allExes.FirstOrDefault();
                }
            }

            if (targetExe != null && File.Exists(targetExe))
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    object? shell = Activator.CreateInstance(shellType);
                    if (shell != null)
                    {
                        dynamic dynShell = shell;
                        dynamic shortcut = dynShell.CreateShortcut(targetLnk);
                        shortcut.TargetPath = targetExe;
                        shortcut.WorkingDirectory = Path.GetDirectoryName(targetExe);
                        shortcut.Description = program.Description ?? safeName;
                        shortcut.Save();
                        if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                        if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Tenta iniciar um programa instalado via atalho do Menu Iniciar ou executável direto.
    /// </summary>
    public static bool TryLaunchProgram(AppPackageDefinition program)
    {
        try
        {
            string safeName = string.IsNullOrWhiteSpace(program.Name) ? program.Id : program.Name;
            string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

            string userLnk = Path.Combine(userPrograms, $"{safeName}.lnk");
            if (File.Exists(userLnk))
            {
                Process.Start(new ProcessStartInfo(userLnk) { UseShellExecute = true });
                return true;
            }

            string commonLnk = Path.Combine(commonPrograms, $"{safeName}.lnk");
            if (File.Exists(commonLnk))
            {
                Process.Start(new ProcessStartInfo(commonLnk) { UseShellExecute = true });
                return true;
            }

            string? foundLnk = Directory.EnumerateFiles(userPrograms, "*.lnk", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(commonPrograms, "*.lnk", SearchOption.AllDirectories))
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(safeName, StringComparison.OrdinalIgnoreCase) ||
                                     Path.GetFileNameWithoutExtension(f).Contains(program.Id, StringComparison.OrdinalIgnoreCase));
            if (foundLnk != null)
            {
                Process.Start(new ProcessStartInfo(foundLnk) { UseShellExecute = true });
                return true;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string linksDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
            if (Directory.Exists(linksDir))
            {
                string cand = Path.Combine(linksDir, $"{program.Id}.exe");
                if (File.Exists(cand))
                {
                    Process.Start(new ProcessStartInfo(cand) { UseShellExecute = true });
                    return true;
                }
                var linkExe = Directory.EnumerateFiles(linksDir, "*.exe")
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(program.Id, StringComparison.OrdinalIgnoreCase) ||
                                         Path.GetFileNameWithoutExtension(f).Contains(safeName, StringComparison.OrdinalIgnoreCase));
                if (linkExe != null)
                {
                    Process.Start(new ProcessStartInfo(linkExe) { UseShellExecute = true });
                    return true;
                }
            }

            Process.Start(new ProcessStartInfo(program.Id) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteRegistryValue(string fullKey, string valueName, string kind, string rawData)
    {
        int slash = fullKey.IndexOf('\\');
        if (slash <= 0)
        {
            return;
        }

        string hiveToken = fullKey[..slash].ToUpperInvariant();
        string subKeyPath = fullKey[(slash + 1)..];
        RegistryKey baseHive = hiveToken switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            _ => throw new ArgumentException($"Hive de registro não suportado: {hiveToken}")
        };

        using RegistryKey? key = baseHive.CreateSubKey(subKeyPath, writable: true);
        if (key == null)
        {
            return;
        }

        if (kind.Equals("dword", StringComparison.OrdinalIgnoreCase) && int.TryParse(rawData, out int intVal))
        {
            key.SetValue(valueName, intVal, RegistryValueKind.DWord);
        }
        else
        {
            key.SetValue(valueName, rawData, RegistryValueKind.String);
        }
    }

    /// <summary>
    /// Interpreta codigos de retorno oficiais do Winget em mensagens claras em portugues.
    /// </summary>
    public static AppInstallOutcome TranslateExitCode(int exitCode, string output, string successText = "Instalado")
    {
        return unchecked((uint)exitCode) switch
        {
            0u => new AppInstallOutcome(true, exitCode, successText, output),
            0x8A15002Bu or 0x8A150061u => new AppInstallOutcome(true, exitCode, "Já estava instalado", output),
            0x8A150079u => new AppInstallOutcome(true, exitCode, "Instalado (conclui ao reiniciar)", output),
            0x8A150014u => new AppInstallOutcome(false, exitCode, "Não encontrado no catálogo do winget (0x8A150014)", output),
            0x8A150008u => new AppInstallOutcome(false, exitCode, "Falha no download: confira sua conexão com a internet", output),
            0x8A150010u => new AppInstallOutcome(false, exitCode, "Nenhum instalador compatível para este PC (0x8A150010)", output),
            0x80070020u => new AppInstallOutcome(false, exitCode, "Arquivo em uso: feche o programa e tente novamente (0x80070020)", output),
            0x8A150006u => new AppInstallOutcome(false, exitCode, "O instalador foi interrompido (feche o programa se estiver aberto) (0x8A150006)", output),
            _ => new AppInstallOutcome(false, exitCode, $"Falhou (código 0x{exitCode:X8}). {ExtractReadableReason(output)}".Trim(), output)
        };
    }

    /// <summary>
    /// Resume linhas da saida do winget em mensagens de progresso curtas para o usuario.
    /// </summary>
    public static string? SummarizeProgressLine(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length < 3 || trimmed.All(c => "-\\|/█▒ ".Contains(c)))
        {
            return null;
        }

        Match sizeMatch = Regex.Match(trimmed, "([\\d.,]+\\s*[KMG]B\\s*/\\s*[\\d.,]+\\s*[KMG]B)");
        if (sizeMatch.Success)
        {
            return $"Baixando… {sizeMatch.Groups[1].Value}";
        }

        Match pctMatch = Regex.Match(trimmed, "(\\d{1,3})\\s*%");
        if (pctMatch.Success)
        {
            return $"Instalando… {pctMatch.Groups[1].Value}%";
        }

        if (trimmed.Contains("hash", StringComparison.OrdinalIgnoreCase))
        {
            return "Instalador oficial verificado";
        }

        if (trimmed.Contains("instala", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("install", StringComparison.OrdinalIgnoreCase))
        {
            return "Instalando…";
        }

        if (trimmed.Contains("baix", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("download", StringComparison.OrdinalIgnoreCase))
        {
            return "Baixando…";
        }

        return null;
    }

    private static string ExtractReadableReason(string output)
    {
        return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l =>
                l.Length > 3 &&
                !l.Contains('\ufffd') &&
                !l.Contains("log is available", StringComparison.OrdinalIgnoreCase) &&
                !l.All(c => "-\\|/█▒ ".Contains(c)) &&
                !l.Contains("KB /", StringComparison.Ordinal) &&
                !l.Contains("MB /", StringComparison.Ordinal)) ?? "";
    }

    /// <summary>
    /// Sanitiza termo digitado pelo usuario antes de passar ao winget search.
    /// </summary>
    public static string? SanitizeSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (char c in query.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '#' or '+' or '-' or '.' or '_')
            {
                sb.Append(c);
            }
        }

        string clean = Regex.Replace(sb.ToString(), "\\s+", " ").Trim().TrimStart('-', '.', '+', ' ');
        if (clean.Length > 60)
        {
            clean = clean[..60].Trim();
        }

        return clean.Length >= 2 ? clean : null;
    }

    /// <summary>
    /// Executa busca online paralela no Winget e na Microsoft Store, igual ao CatalogoWinget do HUB Pro.
    /// </summary>
    public async Task<WingetSearchResponse> SearchOnlineCatalogAsync(string rawQuery, CancellationToken ct = default)
    {
        string? term = SanitizeSearchQuery(rawQuery);
        if (term == null)
        {
            return new WingetSearchResponse(new(), new(), 0, 0, "");
        }

        lock (SearchCacheLock)
        {
            if (SearchCache.TryGetValue(term, out var cached) && DateTime.UtcNow - cached.Timestamp < SearchCacheTtl)
            {
                return cached.Response;
            }
        }

        if (!IsOfficialWingetAvailable())
        {
            return new WingetSearchResponse(new(), new(), 0, 0, "winget indisponível neste PC");
        }

        var wingetTask = RunSearchSourceAsync(term, "winget", ct);
        var storeTask = RunSearchSourceAsync(term, "msstore", ct);
        await Task.WhenAll(wingetTask, storeTask);

        var (wingetRaw, wingetErr) = await wingetTask;
        var (storeRaw, _) = await storeTask;
        var (organizedWinget, hiddenCount) = OrganizeWingetResults(wingetRaw, "pt-BR", term);

        var response = new WingetSearchResponse(
            organizedWinget.Take(40).ToList(),
            storeRaw.Take(8).ToList(),
            organizedWinget.Count,
            hiddenCount,
            wingetRaw.Count == 0 ? wingetErr : "");

        if (response.Error.Length == 0)
        {
            lock (SearchCacheLock)
            {
                SearchCache[term] = (DateTime.UtcNow, response);
            }
        }

        return response;
    }

    private static async Task<(List<WingetSearchPackage> Packages, string Error)> RunSearchSourceAsync(
        string term,
        string source,
        CancellationToken ct)
    {
        string args = $"search --query \"{term}\" --source {source} --accept-source-agreements --disable-interactivity";
        try
        {
            var (exitCode, output) = await RunProcessAsync(GetWingetExecutablePath(), args, null, null, ct);
            var list = ParseWingetSearchOutput(output, source);
            if (exitCode == unchecked((int)0x8A150014) || exitCode == 0 || list.Count > 0)
            {
                return (list, "");
            }

            return (list, $"o winget respondeu com código 0x{exitCode:X8}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (new List<WingetSearchPackage>(), ex.Message);
        }
    }

    public static bool IsValidPackageId(string id, string source)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        return source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
            ? ValidStoreIdRegex.IsMatch(id)
            : ValidWingetIdRegex.IsMatch(id);
    }

    public static List<WingetSearchPackage> ParseWingetSearchOutput(string output, string source)
    {
        var list = new List<WingetSearchPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string[] row in ParseFixedWidthTable(output, 3))
        {
            string name = row[0].TrimEnd('…').Trim();
            string id = row[1].Trim();
            string version = row[2].TrimEnd('…').Trim();

            if (IsValidPackageId(id, source) && name.Length > 0 && seen.Add(id))
            {
                list.Add(new WingetSearchPackage(name, id, version, source));
            }
        }

        return list;
    }

    /// <summary>
    /// Colapsa variantes redundantes de idioma (.en-US, .fr-FR, etc.) priorizando o idioma desejado e relevancia.
    /// </summary>
    public static (List<WingetSearchPackage> Packages, int HiddenCount) OrganizeWingetResults(
        List<WingetSearchPackage> packages,
        string preferredLocale = "pt-BR",
        string query = "")
    {
        var allIds = packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var multiLocaleBases = packages
            .Select(p => (Package: p, Parsed: SplitLocaleSuffix(p.Id)))
            .Where(x => x.Parsed.Base != null && allIds.Contains(x.Parsed.Base))
            .GroupBy(x => x.Parsed.Base!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 3)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = new List<WingetSearchPackage>();
        int hidden = 0;

        foreach (var pkg in packages)
        {
            var (baseId, localeSuffix) = SplitLocaleSuffix(pkg.Id);
            if (baseId != null && multiLocaleBases.Contains(baseId) &&
                !string.Equals(localeSuffix, preferredLocale, StringComparison.OrdinalIgnoreCase))
            {
                hidden++;
            }
            else
            {
                filtered.Add(pkg);
            }
        }

        var ordered = filtered
            .Select((pkg, idx) => (Package: pkg, Index: idx))
            .OrderBy(x => HasLocaleMatch(x.Package, preferredLocale) ? 0 : 1)
            .ThenBy(x => ComputeSearchRelevance(x.Package, query))
            .ThenBy(x => x.Index)
            .Select(x => x.Package)
            .ToList();

        return (ordered, hidden);
    }

    private static (string? Base, string? Locale) SplitLocaleSuffix(string id)
    {
        var match = LocaleSuffixRegex.Match(id);
        return match.Success ? (match.Groups["b"].Value, match.Groups["l"].Value) : (null, null);
    }

    private static bool HasLocaleMatch(WingetSearchPackage pkg, string locale) =>
        pkg.Id.EndsWith("." + locale, StringComparison.OrdinalIgnoreCase) ||
        pkg.Name.Contains($"({locale})", StringComparison.OrdinalIgnoreCase);

    private static int ComputeSearchRelevance(WingetSearchPackage pkg, string term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return 0;
        }

        CompareInfo ci = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        string compactTerm = term.Replace(" ", "");

        if (ci.IsPrefix(pkg.Name, term, opts) ||
            pkg.Id.Split('.').Skip(1).Any(part => ci.Compare(part, compactTerm, opts) == 0))
        {
            return 0;
        }

        if (ci.IndexOf(pkg.Name, term, opts) >= 0 || ci.IndexOf(pkg.Id, compactTerm, opts) >= 0)
        {
            return 1;
        }

        return 2;
    }

    /// <summary>
    /// Consulta detalhes oficiais sob demanda (editora, descricao, site) usando winget show.
    /// </summary>
    public async Task<WingetPackageDetails?> GetPackageDetailsAsync(string id, string source, CancellationToken ct = default)
    {
        if (!IsValidPackageId(id, source) || !IsOfficialWingetAvailable())
        {
            return null;
        }

        string cacheKey = $"{source}:{id}";
        lock (SearchCacheLock)
        {
            if (DetailsCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        await ShowConcurrencyGate.WaitAsync(ct);
        try
        {
            lock (SearchCacheLock)
            {
                if (DetailsCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            string args = $"show --id {id} -e --source {source} --locale pt-BR --accept-source-agreements --disable-interactivity";
            var (_, output) = await RunProcessAsync(GetWingetExecutablePath(), args, null, null, ct);
            var details = ParsePackageDetails(output, id, "pt-BR");

            if (details != null)
            {
                lock (SearchCacheLock)
                {
                    DetailsCache[cacheKey] = details;
                }
            }

            return details;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            ShowConcurrencyGate.Release();
        }
    }

    public static WingetPackageDetails? ParsePackageDetails(string output, string id, string locale = "pt-BR")
    {
        var lines = SplitConsoleLines(output);
        int headerIndex = lines.FindIndex(l => l.Contains($"[{id}]", StringComparison.OrdinalIgnoreCase));
        if (headerIndex < 0)
        {
            return null;
        }

        var fields = new List<(string Label, string Value, List<string> Children)>();
        foreach (string line in lines.Skip(headerIndex + 1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    fields.Add((line[..colon].Trim(), line[(colon + 1)..].Trim(), new List<string>()));
                    continue;
                }
            }

            if (fields.Count > 0)
            {
                fields[^1].Children.Add(line.Trim());
            }
        }

        if (fields.Count < 2)
        {
            return null;
        }

        int pubIdx = fields.FindIndex(f => PublisherLabels.Contains(f.Label, StringComparer.OrdinalIgnoreCase));
        if (pubIdx < 0)
        {
            pubIdx = 1;
        }
        string publisher = fields[pubIdx].Value;

        int descIdx = fields.FindIndex(f => DescriptionLabels.Contains(f.Label, StringComparer.OrdinalIgnoreCase));
        if (descIdx < 0)
        {
            descIdx = fields.FindIndex(pubIdx + 1, f =>
            {
                string combined = CombineFieldText(f);
                return combined.Split(' ').Length >= 5 &&
                       !combined.Contains("://", StringComparison.Ordinal) &&
                       f.Children.All(c => c.Contains(' '));
            });
        }

        string description = descIdx >= 0 ? CombineFieldText(fields[descIdx]) : "";
        if (description.Length > 360)
        {
            description = description[..360].TrimEnd() + "…";
        }

        bool hasLocale = fields[^1].Children.Any(c =>
            !c.Contains("://", StringComparison.Ordinal) &&
            Regex.IsMatch(c, ":\\s*" + Regex.Escape(locale) + "\\s*$", RegexOptions.IgnoreCase));

        string? homepage = fields
            .Where(f => HomepageLabels.Contains(f.Label, StringComparer.OrdinalIgnoreCase))
            .Select(f => ValidHttpsLink(f.Value))
            .FirstOrDefault(v => v != null);

        string? publisherUrl = pubIdx + 1 < fields.Count ? ValidHttpsLink(fields[pubIdx + 1].Value) : null;
        publisherUrl ??= fields.Select(f => ValidHttpsLink(f.Value)).FirstOrDefault(v => v != null);

        return new WingetPackageDetails(publisher, description, hasLocale, homepage, publisherUrl);

        static string CombineFieldText((string Label, string Value, List<string> Children) field) =>
            string.Join(" ", new[] { field.Value }.Concat(field.Children).Where(s => s.Length > 0)).Trim();

        static string? ValidHttpsLink(string value) =>
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !value.Contains(' ') ? value : null;
    }

    /// <summary>
    /// Fatiador de tabela em largura visual (suporta emojis / caracteres largos / barras \r do winget).
    /// </summary>
    public static List<string[]> ParseFixedWidthTable(string output, int minColumns)
    {
        var lines = SplitConsoleLines(output);
        var rows = new List<string[]>();

        int separatorIdx = lines.FindIndex(l =>
        {
            string trimmed = l.Trim();
            return trimmed.Length > 20 && trimmed.All(c => c == '-');
        });
        if (separatorIdx < 1)
        {
            return rows;
        }

        string header = lines[separatorIdx - 1];
        var colStarts = new List<int>();
        int visualPos = 0;
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
            {
                colStarts.Add(visualPos);
            }
            visualPos += GetCharConsoleWidth(header, i);
        }

        if (colStarts.Count < minColumns)
        {
            return rows;
        }

        for (int rowIdx = separatorIdx + 1; rowIdx < lines.Count; rowIdx++)
        {
            string rowLine = lines[rowIdx].TrimEnd();
            if (rowLine.Length == 0)
            {
                break;
            }

            string[] sliced = SliceRowByColumns(rowLine, colStarts);
            if (sliced[1].Length > 0 && !sliced[1].Contains(' '))
            {
                rows.Add(sliced);
            }
        }

        return rows;
    }

    private static List<string> SplitConsoleLines(string raw) =>
        raw.Replace("\r\n", "\n")
           .Split('\n')
           .Select(l => l.TrimEnd('\r'))
           .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
           .ToList();

    private static string[] SliceRowByColumns(string line, List<int> starts)
    {
        var builders = Enumerable.Range(0, starts.Count).Select(_ => new StringBuilder()).ToArray();
        int visualPos = 0;
        int col = 0;

        for (int i = 0; i < line.Length; i++)
        {
            while (col + 1 < starts.Count && visualPos >= starts[col + 1])
            {
                col++;
            }

            builders[col].Append(line[i]);
            visualPos += GetCharConsoleWidth(line, i);
        }

        return builders.Select(b => b.ToString().Trim()).ToArray();
    }

    private static int GetCharConsoleWidth(string s, int index)
    {
        char c = s[index];
        if (char.IsLowSurrogate(c))
        {
            return 0;
        }
        if (char.IsHighSurrogate(c))
        {
            return 2;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        if ((c >= '\u1100' && c <= '\u115F') ||
            (c >= '\u2E80' && c <= '\u303E') ||
            (c >= '\u3041' && c <= '\uA4CF') ||
            (c >= '\uAC00' && c <= '\uD7AF') ||
            (c >= '\uF900' && c <= '\uFAFF') ||
            (c >= '\uFE30' && c <= '\uFE4F') ||
            (c >= '\uFF01' && c <= '\uFF60') ||
            (c >= '\uFFE0' && c <= '\uFFE6'))
        {
            return 2;
        }

        return 1;
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string exe,
        string args,
        IProgress<string>? progress,
        Action<string>? rawLogCallback,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o winget.");
        var fullOutput = new StringBuilder();
        Task readOut = ReadStreamProgressAsync(proc.StandardOutput, fullOutput, progress, rawLogCallback, ct);
        Task<string> readErr = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        await readOut;
        fullOutput.Append(await readErr);
        return (proc.ExitCode, fullOutput.ToString());
    }

    private static async Task ReadStreamProgressAsync(
        StreamReader reader,
        StringBuilder output,
        IProgress<string>? progress,
        Action<string>? rawLogCallback,
        CancellationToken ct)
    {
        char[] buffer = new char[1024];
        var currentLine = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char ch = buffer[i];
                output.Append(ch);
                if (ch is '\n' or '\r')
                {
                    string line = currentLine.ToString();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        rawLogCallback?.Invoke(line.Trim());
                        string? summary = SummarizeProgressLine(line);
                        if (summary != null)
                        {
                            progress?.Report(summary);
                        }
                    }
                    currentLine.Clear();
                }
                else
                {
                    currentLine.Append(ch);
                }
            }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsNonAdminAsync(
        string exe,
        string args,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        string tempDir = GetShortPath(Path.GetTempPath());
        string token = Guid.NewGuid().ToString("N")[..8];
        string cmdFile = Path.Combine(tempDir, $"hub-{token}.cmd");
        string outFile = Path.Combine(tempDir, $"hub-{token}.txt");
        string codeFile = Path.Combine(tempDir, $"hub-{token}.code");

        await File.WriteAllTextAsync(cmdFile,
            $"@echo off\r\n\"{exe}\" {args} > \"{outFile}\" 2>&1\r\necho %errorlevel% > \"{codeFile}\"\r\n", ct);

        try
        {
            string runasExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "runas.exe");
            using var runas = Process.Start(new ProcessStartInfo(runasExe, $"/trustlevel:0x20000 \"{cmdFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });

            if (runas == null)
            {
                return (-1, "Não foi possível iniciar o instalador sem elevação.");
            }

            Task<string> runasOut = runas.StandardOutput.ReadToEndAsync(ct);
            await Task.WhenAny(runas.WaitForExitAsync(ct), Task.Delay(15000, ct));
            if (runas.HasExited && runas.ExitCode != 0)
            {
                return (-1, $"Falha ao iniciar sem elevação (runas): {(await runasOut).Trim()}");
            }

            progress?.Report("Instalando como usuário comum…");
            DateTime deadline = DateTime.UtcNow.AddMinutes(30);
            while (!File.Exists(codeFile))
            {
                if (DateTime.UtcNow > deadline)
                {
                    return (-1, "Tempo limite excedido (30 min).");
                }
                await Task.Delay(800, ct);
            }

            await Task.Delay(250, ct);
            int code = int.TryParse((await File.ReadAllTextAsync(codeFile, ct)).Trim(), out int parsed) ? parsed : -1;
            string output = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, ct) : "";
            return (code, output);
        }
        finally
        {
            foreach (string file in new[] { cmdFile, outFile, codeFile })
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private static string? ResolveAppExecutionAliasTarget(string aliasPath)
    {
        using SafeFileHandle handle = CreateFileW(aliasPath, 0u, 7u, IntPtr.Zero, 3u, 0x02200000u, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        byte[] buffer = new byte[16384];
        if (!DeviceIoControl(handle, 0x000900A8u, IntPtr.Zero, 0, buffer, buffer.Length, out int bytesRead, IntPtr.Zero) || bytesRead < 12)
        {
            return null;
        }

        if (BitConverter.ToUInt32(buffer, 0) != 0x8000001Bu)
        {
            return null;
        }

        string[] parts = Encoding.Unicode.GetString(buffer, 12, bytesRead - 12).Split('\0');
        return parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : null;
    }

    private static string GetShortPath(string path)
    {
        var sb = new StringBuilder(260);
        return GetShortPathName(path, sb, (uint)sb.Capacity) == 0 ? path : sb.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, nint lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, nint lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferSize);
}

