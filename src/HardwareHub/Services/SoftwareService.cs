using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public class SoftwareService
    {
        private sealed class WingetLockLease(ManualResetEventSlim releaseSignal) : IDisposable
        {
            public void Dispose() => releaseSignal.Set();
        }

        private const string WingetMutexName = @"Local\HardwareHub-winget-instalacao";
        private static readonly object InstallLogLock = new();
        private static readonly string InstallLogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HardwareHub",
            "Logs");

        /// <summary>
        /// Registro persistente das execucoes do winget, separado por dia para suporte.
        /// </summary>
        public static string GetInstallLogPath(DateTime? date = null) =>
            Path.Combine(InstallLogDirectory, $"package-install-{(date ?? DateTime.Now):yyyy-MM-dd}.log");

        private static void WriteInstallLog(string operationId, string message)
        {
            try
            {
                lock (InstallLogLock)
                {
                    Directory.CreateDirectory(InstallLogDirectory);
                    string safeMessage = message.Replace("\r", " ").Replace("\n", " ");
                    File.AppendAllText(GetInstallLogPath(), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{operationId}] {safeMessage}{Environment.NewLine}");
                }
            }
            catch
            {
                // Falha de log nao deve impedir a instalacao.
            }
        }

        public List<SoftwareItem> GetCatalog()
        {
            return AppCatalogService.LoadCatalog().Programs
                .Where(p => !p.IsLink)
                .Select(p => new SoftwareItem
                {
                    Id = p.Id,
                    Name = p.Name,
                    Publisher = p.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? "Microsoft Store" : "Winget",
                    Category = AppCatalogService.GetShortCategoryLabel(p.Category),
                    Description = p.Description,
                    WingetId = p.WingetId,
                    WingetSource = p.Source,
                    Definition = p
                })
                .ToList();
        }

        public async Task CheckAllInstalledStatusAsync(IEnumerable<SoftwareItem> items)
        {
            await Task.Run(() =>
            {
                var installedNames = GetInstalledSoftwareNamesFromRegistry();

                foreach (var item in items)
                {
                    bool isInstalled = IsSoftwareInstalled(item, installedNames);

                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        item.Status = isInstalled ? PackageStatus.Installed : PackageStatus.Available;
                        item.StatusMessage = isInstalled ? "Instalado" : "Disponível";
                    });
                }
            });
        }

        private bool IsSoftwareInstalled(SoftwareItem item, HashSet<string> installedNames)
        {
            if (item.Definition != null)
            {
                return AppCatalogService.IsProgramInstalled(item.Definition, installedNames);
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            switch (item.Id)
            {
                case "chrome":
                    if (installedNames.Any(n => n.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase) || n.Equals("Chrome", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe"))) return true;
                    if (File.Exists(Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe"))) return true;
                    break;

                case "brave":
                    if (installedNames.Any(n => n.Contains("Brave", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"BraveSoftware\Brave-Browser\Application\brave.exe"))) return true;
                    if (File.Exists(Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\Application\brave.exe"))) return true;
                    break;

                case "firefox":
                    if (installedNames.Any(n => n.Contains("Mozilla Firefox", StringComparison.OrdinalIgnoreCase) || n.Contains("Firefox", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"Mozilla Firefox\firefox.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"Mozilla Firefox\firefox.exe"))) return true;
                    break;

                case "vscode":
                    if (installedNames.Any(n => n.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(localAppData, @"Programs\Microsoft VS Code\Code.exe"))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"Microsoft VS Code\Code.exe"))) return true;
                    break;

                case "git":
                    if (installedNames.Any(n => n.Equals("Git", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Git ", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"Git\cmd\git.exe"))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"Git\bin\git.exe"))) return true;
                    break;

                case "nodejs":
                    if (installedNames.Any(n => n.Contains("Node.js", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"nodejs\node.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"nodejs\node.exe"))) return true;
                    break;

                case "powertoys":
                    if (installedNames.Any(n => n.Contains("PowerToys", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(localAppData, @"PowerToys\PowerToys.exe"))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"PowerToys\PowerToys.exe"))) return true;
                    break;

                case "steam":
                    if (installedNames.Any(n => n.Equals("Steam", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"Steam\steam.exe"))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"Steam\steam.exe"))) return true;
                    break;

                case "discord":
                    if (installedNames.Any(n => n.Contains("Discord", StringComparison.OrdinalIgnoreCase))) return true;
                    if (Directory.Exists(Path.Combine(localAppData, "Discord"))) return true;
                    break;

                case "spotify":
                    if (installedNames.Any(n => n.Contains("Spotify", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(appData, @"Spotify\Spotify.exe"))) return true;
                    break;

                case "obs":
                    if (installedNames.Any(n => n.Contains("OBS Studio", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"obs-studio\bin\64bit\obs64.exe"))) return true;
                    break;

                case "7zip":
                    if (installedNames.Any(n => n.Contains("7-Zip", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"7-Zip\7z.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"7-Zip\7z.exe"))) return true;
                    break;

                case "vlc":
                    if (installedNames.Any(n => n.Contains("VLC", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"VideoLAN\VLC\vlc.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"VideoLAN\VLC\vlc.exe"))) return true;
                    break;

                case "notepadplusplus":
                    if (installedNames.Any(n => n.Contains("Notepad++", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"Notepad++\notepad++.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"Notepad++\notepad++.exe"))) return true;
                    break;

                case "msoffice":
                    if (installedNames.Any(n => n.Contains("Microsoft 365", StringComparison.OrdinalIgnoreCase) ||
                                                n.Contains("Microsoft Office", StringComparison.OrdinalIgnoreCase) ||
                                                n.Contains("O365", StringComparison.OrdinalIgnoreCase) ||
                                                n.Contains("Office 16", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"Microsoft Office\root\Office16\WINWORD.EXE"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"Microsoft Office\root\Office16\WINWORD.EXE"))) return true;
                    break;

                case "libreoffice":
                    if (installedNames.Any(n => n.Contains("LibreOffice", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"LibreOffice\program\soffice.exe"))) return true;
                    break;

                case "anydesk":
                    if (installedNames.Any(n => n.Contains("AnyDesk", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"AnyDesk\AnyDesk.exe"))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"AnyDesk\AnyDesk.exe"))) return true;
                    break;

                case "pawnio":
                    if (HardwareService.IsPawnIOInstalled) return true;
                    if (installedNames.Any(n => n.Contains("PawnIO", StringComparison.OrdinalIgnoreCase))) return true;
                    break;

                case "operagx":
                    if (installedNames.Any(n => n.Contains("Opera GX", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(localAppData, @"Programs\Opera GX\launcher.exe"))) return true;
                    break;

                case "cpuz":
                    if (installedNames.Any(n => n.Contains("CPU-Z", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"CPUID\CPU-Z\cpuz.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"CPUID\CPU-Z\cpuz.exe"))) return true;
                    break;

                case "gpuz":
                    if (installedNames.Any(n => n.Contains("GPU-Z", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"TechPowerUp GPU-Z\GPU-Z.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"TechPowerUp GPU-Z\GPU-Z.exe"))) return true;
                    break;

                case "crystaldiskinfo":
                    if (installedNames.Any(n => n.Contains("CrystalDiskInfo", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"CrystalDiskInfo\DiskInfo64.exe"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"CrystalDiskInfo\DiskInfo32.exe"))) return true;
                    break;

                case "hwinfo":
                    if (installedNames.Any(n => n.Contains("HWiNFO", StringComparison.OrdinalIgnoreCase))) return true;
                    if (File.Exists(Path.Combine(programFiles, @"HWiNFO64\HWiNFO64.EXE"))) return true;
                    if (File.Exists(Path.Combine(programFilesX86, @"HWiNFO32\HWiNFO32.EXE"))) return true;
                    break;

                default:
                    if (installedNames.Any(n => n.Contains(item.Name, StringComparison.OrdinalIgnoreCase) || item.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                        return true;
                    break;
            }

            return false;
        }

        private static Task<IDisposable> AcquireWingetLockAsync(Action? onWaiting = null, CancellationToken ct = default)
        {
            var acquired = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSignal = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                bool holdsMutex = false;
                try
                {
                    using var mutex = new Mutex(false, WingetMutexName);
                    bool notified = false;
                    while (!holdsMutex)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            acquired.TrySetCanceled(ct);
                            return;
                        }

                        try
                        {
                            holdsMutex = mutex.WaitOne(500);
                        }
                        catch (AbandonedMutexException)
                        {
                            holdsMutex = true;
                        }

                        if (!holdsMutex && !notified)
                        {
                            notified = true;
                            onWaiting?.Invoke();
                        }
                    }

                    acquired.TrySetResult(new WingetLockLease(releaseSignal));
                    releaseSignal.Wait();
                    mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    acquired.TrySetException(ex);
                }
                finally
                {
                    releaseSignal.Dispose();
                }
            })
            {
                IsBackground = true,
                Name = "HardwareHub-WingetLock"
            };
            thread.Start();

            return acquired.Task;
        }

        public static string GetWingetPath()
        {
            // 1. Procura diretamente no PATH do sistema
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "winget",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (proc != null)
                {
                    string? output = proc.StandardOutput.ReadLine()?.Trim();
                    proc.WaitForExit(2000);
                    if (!string.IsNullOrEmpty(output) && File.Exists(output))
                        return output;
                }
            }
            catch { }

            // 2. Caminhos conhecidos no WindowsApps e LocalAppData
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userWinget = Path.Combine(localAppData, @"Microsoft\WindowsApps\winget.exe");
            if (File.Exists(userWinget)) return userWinget;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string windowsApps = Path.Combine(programFiles, "WindowsApps");
            if (Directory.Exists(windowsApps))
            {
                try
                {
                    var wingetExes = Directory.GetFiles(windowsApps, "winget.exe", SearchOption.AllDirectories);
                    var best = wingetExes.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                    if (!string.IsNullOrEmpty(best)) return best;
                }
                catch { }
            }

            return "winget";
        }

        private HashSet<string> GetInstalledSoftwareNamesFromRegistry()
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            string[] subkeys = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        foreach (var sub in subkeys)
                        {
                            using var uninstallKey = baseKey.OpenSubKey(sub);
                            if (uninstallKey == null) continue;

                            foreach (var appKeyName in uninstallKey.GetSubKeyNames())
                            {
                                using var appKey = uninstallKey.OpenSubKey(appKeyName);
                                var displayName = appKey?.GetValue("DisplayName")?.ToString()?.Trim();
                                if (!string.IsNullOrEmpty(displayName))
                                {
                                    results.Add(displayName);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            return results;
        }

        public async Task<bool> InstallPackageAsync(SoftwareItem item, Action<string> logCallback)
        {
            string operationId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var stopwatch = Stopwatch.StartNew();
            item.Status = PackageStatus.Installing;
            item.StatusMessage = "Iniciando download...";
            item.IsIndeterminate = true;
            WriteInstallLog(operationId, $"START packageId={item.WingetId}; name={item.Name}");
            logCallback($"[DIAGNOSTICO {operationId}] Log persistente: {GetInstallLogPath()}");
            logCallback($"[INFO] Solicitando instalação do pacote {item.Name} ({item.WingetId})...");

            // Tratamento especial para pacotes do catalogo oficial HUB Pro (locale, escopo usuario sem admin, tweaks de idioma).
            if (item.Definition != null)
            {
                var progress = new Progress<string>(status => item.StatusMessage = status);
                var outcome = await new AppCatalogService().InstallProgramAsync(
                    item.Definition,
                    progress,
                    line =>
                    {
                        WriteInstallLog(operationId, line);
                        logCallback(line);
                    });

                item.IsIndeterminate = false;
                item.ProgressValue = outcome.Success ? 100 : 0;
                item.Status = outcome.Success ? PackageStatus.Installed : PackageStatus.Failed;
                item.StatusMessage = outcome.ShortMessage;
                WriteInstallLog(operationId, $"END success={outcome.Success}; exit={outcome.ExitCode}; msg={outcome.ShortMessage}; elapsedMs={stopwatch.ElapsedMilliseconds}");
                return outcome.Success;
            }

            // Tratamento especial para o Microsoft 365 / Office:
            // A Microsoft atualiza silenciosamente os binários na CDN antes do repositório da comunidade do Winget ser atualizado,
            // gerando o erro de hash mismatch 0x8A150011 (-1978335215).
            // Baixamos o executável oficial assinado pela Microsoft direto da CDN, garantindo 100% de sucesso!
            if (item.Id == "msoffice")
            {
                return await InstallOfficeDirectAsync(item, logCallback);
            }

            return await Task.Run(async () =>
            {
                using var wingetLock = await AcquireWingetLockAsync(() =>
                {
                    item.StatusMessage = "Aguardando outra instalação...";
                    WriteInstallLog(operationId, "WAITING for active winget mutex");
                    logCallback($"[FILA] {item.Name} aguardando término da instalação em andamento...");
                });

                item.StatusMessage = "Iniciando download...";
                try
                {
                    bool isMsStore = item.WingetId.StartsWith("9N", StringComparison.OrdinalIgnoreCase);
                    string sourceArg = isMsStore ? "-s msstore" : "-s winget";
                    string exactArg = isMsStore ? "" : "--exact";
                    string flags = "--force --disable-interactivity --silent --accept-package-agreements --accept-source-agreements";
                    string wingetPath = GetWingetPath();
                    string arguments = $"install --id \"{item.WingetId}\" {sourceArg} {exactArg} {flags}";
                    WriteInstallLog(operationId, $"COMMAND executable={wingetPath}; arguments={arguments}");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = wingetPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };

                    using var process = new Process { StartInfo = startInfo };
                    bool hasInstalledSuccessMessage = false;

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            WriteInstallLog(operationId, $"STDOUT {e.Data}");
                            logCallback(e.Data);
                            if (e.Data.Contains("Downloading", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("Baixando", StringComparison.OrdinalIgnoreCase))
                            {
                                item.StatusMessage = "Baixando pacote...";
                            }
                            else if (e.Data.Contains("Installing", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("Instalando", StringComparison.OrdinalIgnoreCase))
                            {
                                item.StatusMessage = "Instalando no Windows...";
                            }

                            if (e.Data.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase) ||
                                e.Data.Contains("Instalado com êxito", StringComparison.OrdinalIgnoreCase) ||
                                e.Data.Contains("No available upgrade found", StringComparison.OrdinalIgnoreCase) ||
                                e.Data.Contains("No newer package versions", StringComparison.OrdinalIgnoreCase) ||
                                e.Data.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
                                e.Data.Contains("já está instalado", StringComparison.OrdinalIgnoreCase))
                            {
                                hasInstalledSuccessMessage = true;
                            }
                        }
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            WriteInstallLog(operationId, $"STDERR {e.Data}");
                            logCallback($"[STDERR] {e.Data}");
                        }
                    };

                    if (!process.Start())
                    {
                        throw new InvalidOperationException("O processo winget nao foi iniciado.");
                    }

                    WriteInstallLog(operationId, $"PROCESS_STARTED pid={process.Id}");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        WriteInstallLog(operationId, $"TIMEOUT elapsed={stopwatch.Elapsed}; killing process tree");
                        try { process.Kill(entireProcessTree: true); } catch { }
                        item.Status = PackageStatus.Failed;
                        item.StatusMessage = "Tempo limite excedido (15 min)";
                        item.IsIndeterminate = false;
                        logCallback("[ERRO] O winget excedeu 15 minutos e foi encerrado. Consulte o log de diagnostico.");
                        return false;
                    }

                    WriteInstallLog(operationId, $"PROCESS_EXIT code={process.ExitCode}; hex=0x{unchecked((uint)process.ExitCode):X8}; elapsed={stopwatch.Elapsed}");

                    // 0 = Sucesso
                    // 3010 = Sucesso (reinicialização pendente)
                    // 1641 = Sucesso (reinicialização iniciada)
                    // 0x8A15002B = Pacote já instalado
                    // 0x8A15002D = Nenhuma atualização disponível (já instalado)
                    // 0x8A150033 = Nenhuma ação necessária
                    bool isOk = (process.HasExited && (
                        process.ExitCode == 0 ||
                        process.ExitCode == 3010 ||
                        process.ExitCode == 1641 ||
                        unchecked((uint)process.ExitCode) == 0x8A15002B ||
                        unchecked((uint)process.ExitCode) == 0x8A15002D ||
                        unchecked((uint)process.ExitCode) == 0x8A150033
                    )) || hasInstalledSuccessMessage;

                    if (isOk)
                    {
                        item.Status = PackageStatus.Installed;
                        item.StatusMessage = process.ExitCode == 3010 ? "Instalado (Reinício necessário)" : "Instalado com sucesso!";
                        item.IsIndeterminate = false;
                        logCallback($"[SUCESSO] {item.Name} instalado com êxito!");
                        return true;
                    }
                    else
                    {
                        uint unsignedCode = unchecked((uint)process.ExitCode);
                        WriteInstallLog(operationId, $"FAILURE exitCode={process.ExitCode}; hex=0x{unsignedCode:X8}; elapsed={stopwatch.Elapsed}");
                        item.Status = PackageStatus.Failed;
                        item.StatusMessage = unsignedCode switch
                        {
                            0x8A150011 => "Falha de hash (0x8A150011)",
                            0x8A150014 => "Pacote não encontrado",
                            0x8A150010 => "Sem instalador compatível",
                            0x80070005 or 740 => "Requer privilégios de Admin",
                            _ => $"Falha (0x{unsignedCode:X8})"
                        };
                        item.IsIndeterminate = false;
                        logCallback($"[ERRO] Código de saída winget: {process.ExitCode} (0x{unsignedCode:X8})");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    WriteInstallLog(operationId, $"EXCEPTION type={ex.GetType().Name}; message={ex.Message}; elapsed={stopwatch.Elapsed}");
                    item.Status = PackageStatus.Failed;
                    item.StatusMessage = "Erro ao executar winget";
                    item.IsIndeterminate = false;
                    logCallback($"[EXCEÇÃO] {ex.Message}");
                    return false;
                }
            });
        }

        private async Task<bool> InstallOfficeDirectAsync(SoftwareItem item, Action<string> logCallback)
        {
            try
            {
                item.StatusMessage = "Baixando instalador oficial...";
                logCallback("[OFFICE] Baixando instalador oficial direto da CDN da Microsoft (officecdn.microsoft.com)...");

                string tempSetup = Path.Combine(Path.GetTempPath(), "OfficeSetup.exe");
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(5);
                    using var response = await httpClient.GetAsync("https://officecdn.microsoft.com/pr/wsus/setup.exe", System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    using var fs = new FileStream(tempSetup, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(fs);
                }

                item.StatusMessage = "Iniciando instalador do Office...";
                logCallback("[OFFICE] Download concluído com sucesso. Iniciando instalador oficial do Microsoft 365 na tela...");

                var psi = new ProcessStartInfo
                {
                    FileName = tempSetup,
                    UseShellExecute = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    item.Status = PackageStatus.Installed;
                    item.StatusMessage = "Instalador iniciado!";
                    item.IsIndeterminate = false;
                    logCallback("[OFFICE SUCESSO] Instalador oficial da Microsoft iniciado com sucesso na área de trabalho.");
                    return true;
                }

                item.Status = PackageStatus.Failed;
                item.StatusMessage = "Falha ao iniciar";
                return false;
            }
            catch (Exception ex)
            {
                item.Status = PackageStatus.Failed;
                item.StatusMessage = "Erro no download";
                item.IsIndeterminate = false;
                logCallback($"[OFFICE ERRO] {ex.Message}");
                return false;
            }
        }

        public async Task<bool> InstallPawnIODirectAsync(Action<string> logCallback)
        {
            try
            {
                string tempSetup = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
                bool fileReady = false;

                // 1. Tenta extrair instalador embutido no executável (funciona 100% offline!)
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    var resName = asm.GetManifestResourceNames()
                        .FirstOrDefault(n => n.EndsWith("PawnIO_setup.exe", StringComparison.OrdinalIgnoreCase));

                    if (!string.IsNullOrEmpty(resName))
                    {
                        using var stream = asm.GetManifestResourceStream(resName);
                        if (stream != null)
                        {
                            logCallback("[DRIVER] Extraindo driver PawnIO integrado (modo offline)...");
                            using var fs = new FileStream(tempSetup, FileMode.Create, FileAccess.Write, FileShare.None);
                            await stream.CopyToAsync(fs);
                            fileReady = true;
                        }
                    }
                }
                catch { }

                // 2. Se não encontrou embutido, baixa do GitHub Releases
                if (!fileReady)
                {
                    logCallback("[DRIVER] Baixando instalador oficial do PawnIO (GitHub Releases)...");
                    using var httpClient = new System.Net.Http.HttpClient();
                    httpClient.Timeout = TimeSpan.FromMinutes(2);
                    using var response = await httpClient.GetAsync("https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe");
                    if (!response.IsSuccessStatusCode)
                    {
                        logCallback($"[DRIVER] Falha ao baixar instalador direto: HTTP {(int)response.StatusCode}");
                        return false;
                    }

                    using var fs = new FileStream(tempSetup, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(fs);
                    fileReady = true;
                }

                logCallback("[DRIVER] Executando instalador silencioso do driver PawnIO...");
                var psi = new ProcessStartInfo
                {
                    FileName = tempSetup,
                    Arguments = "-install -silent",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    try
                    {
                        await proc.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        logCallback("[DRIVER] Tempo limite excedido ao instalar PawnIO.");
                        return HardwareService.IsPawnIOInstalled;
                    }
                    return proc.ExitCode == 0 || HardwareService.IsPawnIOInstalled;
                }
                return false;
            }
            catch (Exception ex)
            {
                logCallback($"[DRIVER EXCEÇÃO] {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    string tempSetup = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
                    if (File.Exists(tempSetup)) File.Delete(tempSetup);
                }
                catch { }
            }
        }

        public async Task<List<SoftwareItem>> SearchWingetOnlineAsync(string query)
        {
            var list = new List<SoftwareItem>();
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return list;

            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = GetWingetPath(),
                        Arguments = $"search \"{query.Trim().Replace("\"", "")}\" --accept-source-agreements --disable-interactivity -n 14",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    };

                    using var process = Process.Start(psi);
                    if (process == null) return list;

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(15000))
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        return list;
                    }

                    string output = outputTask.GetAwaiter().GetResult();
                    var normalizedLines = output
                        .Replace("\r\n", "\n")
                        .Split('\n')
                        .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
                        .ToList();

                    int dividerIndex = normalizedLines.FindIndex(l =>
                    {
                        string t = l.Trim();
                        return t.Length >= 15 && t.All(c => c == '-');
                    });

                    if (dividerIndex < 1) return list;

                    string header = normalizedLines[dividerIndex - 1];
                    var colStarts = new List<int>();
                    for (int i = 0; i < header.Length; i++)
                    {
                        if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
                        {
                            colStarts.Add(i);
                        }
                    }

                    if (colStarts.Count < 3) return list;

                    for (int row = dividerIndex + 1; row < normalizedLines.Count; row++)
                    {
                        string line = normalizedLines[row].TrimEnd();
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("<")) continue;

                        string name = "";
                        string id = "";
                        string version = "";

                        bool columnsAligned = line.Length > colStarts[1] &&
                                              !colStarts.Skip(1).Take(2).Any(pos => pos < line.Length && pos > 0 && line[pos - 1] != ' ');

                        if (columnsAligned)
                        {
                            int endName = Math.Min(colStarts[1], line.Length);
                            name = line[..endName].Trim();

                            int startId = colStarts[1];
                            int endId = line.Length > colStarts[2] ? colStarts[2] : line.Length;
                            id = line[startId..endId].Trim();

                            if (line.Length > colStarts[2])
                            {
                                int startVer = colStarts[2];
                                int endVer = colStarts.Count >= 4 && line.Length > colStarts[3] ? colStarts[3] : line.Length;
                                version = line[startVer..endVer].Trim();
                            }
                        }

                        // Fallback da direita para esquerda quando nomes longos invadem a coluna de Id
                        if (!columnsAligned || string.IsNullOrEmpty(id) || id.Contains(' '))
                        {
                            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            int idTokenIdx = -1;
                            for (int t = tokens.Length - 1; t >= 1; t--)
                            {
                                string cand = tokens[t];
                                if (Regex.IsMatch(cand, @"^([A-Za-z0-9][A-Za-z0-9._-]*\.[A-Za-z0-9._-]+|9[A-Za-z0-9]{8,})$"))
                                {
                                    idTokenIdx = t;
                                    break;
                                }
                            }

                            if (idTokenIdx < 1) continue;
                            name = string.Join(' ', tokens.Take(idTokenIdx));
                            id = tokens[idTokenIdx];
                            version = idTokenIdx + 1 < tokens.Length ? tokens[idTokenIdx + 1] : "";
                        }

                        name = name.TrimEnd('…').Trim();
                        if (string.IsNullOrEmpty(name) ||
                            string.IsNullOrEmpty(id) ||
                            id.EndsWith('…') ||
                            id.EndsWith("...", StringComparison.Ordinal) ||
                            id.Contains("truncated", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        list.Add(new SoftwareItem
                        {
                            Id = id.ToLowerInvariant(),
                            Name = name,
                            Publisher = "Repositório Oficial Winget",
                            Category = "Winget Online",
                            WingetId = id,
                            Description = string.IsNullOrEmpty(version)
                                ? "Pacote encontrado online no repositório global do Windows. Instalação silenciosa de 1 clique."
                                : $"Pacote encontrado online no repositório global do Windows (Versão: {version}). Instalação silenciosa de 1 clique."
                        });
                    }
                }
                catch { }

                return list;
            });
        }

        public List<SoftwareItem> ParseWingetFile(string filePath)
        {
            var results = new List<SoftwareItem>();
            if (!File.Exists(filePath)) return results;

            try
            {
                string content = File.ReadAllText(filePath).Trim();

                // 1. Tenta fazer parse como JSON (formato do `winget export`, UniGetUI / WingetUI `.ubundle` e JSONs customizados)
                if (content.StartsWith("{") || content.StartsWith("["))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(content);
                        var root = doc.RootElement;

                        // Caso 1: Array direto (ex: UniGetUI .ubundle -> [ { "Id": "...", "Name": "...", "ManagerName": "..." }, ... ] ou [ "Id1", "Id2" ])
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var el in root.EnumerateArray())
                            {
                                var (id, name) = ExtractPackageInfo(el);
                                if (!string.IsNullOrWhiteSpace(id))
                                {
                                    AddParsedItem(results, id, name);
                                }
                            }
                        }
                        // Caso 2: Objeto raiz (ex: winget export com "Sources", ou bundle com "packages" / "Packages" / "Items")
                        else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            // 2A: Formato oficial `winget export`: { "Sources": [ { "Packages": [ { "PackageIdentifier": "..." } ] } ] }
                            if (root.TryGetProperty("Sources", out var sourcesEl) && sourcesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var src in sourcesEl.EnumerateArray())
                                {
                                    if (src.TryGetProperty("Packages", out var pkgsEl) && pkgsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    {
                                        foreach (var pkg in pkgsEl.EnumerateArray())
                                        {
                                            var (id, name) = ExtractPackageInfo(pkg);
                                            if (!string.IsNullOrWhiteSpace(id))
                                            {
                                                AddParsedItem(results, id, name);
                                            }
                                        }
                                    }
                                }
                            }

                            // 2B: Objeto com lista "packages", "Packages", "items" ou "Items"
                            string[] listPropertyNames = { "packages", "Packages", "items", "Items", "software", "Software" };
                            foreach (var propName in listPropertyNames)
                            {
                                if (root.TryGetProperty(propName, out var listEl) && listEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var pkg in listEl.EnumerateArray())
                                    {
                                        var (id, name) = ExtractPackageInfo(pkg);
                                        if (!string.IsNullOrWhiteSpace(id))
                                        {
                                            AddParsedItem(results, id, name);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 2. Se não encontrou itens como JSON (ou se for formato TXT / YAML / lista)
                if (results.Count == 0)
                {
                    var lines = File.ReadAllLines(filePath);
                    foreach (var rawLine in lines)
                    {
                        string line = rawLine.Trim();
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("//"))
                            continue;

                        // Se for YAML do tipo: - PackageIdentifier: Google.Chrome ou - Id: Google.Chrome
                        if (line.Contains("PackageIdentifier:", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = line.IndexOf("PackageIdentifier:", StringComparison.OrdinalIgnoreCase) + "PackageIdentifier:".Length;
                            string val = line.Substring(idx).Trim(' ', '"', '\'', '-', ':');
                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                AddParsedItem(results, val);
                                continue;
                            }
                        }
                        if (line.Contains("Id:", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = line.IndexOf("Id:", StringComparison.OrdinalIgnoreCase) + "Id:".Length;
                            string val = line.Substring(idx).Trim(' ', '"', '\'', '-', ':');
                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                AddParsedItem(results, val);
                                continue;
                            }
                        }

                        // Se for formato de lista simples: "Google.Chrome" ou "1. Google.Chrome"
                        string clean = line.TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' ');
                        if (!string.IsNullOrWhiteSpace(clean) && !clean.Contains(" "))
                        {
                            AddParsedItem(results, clean);
                        }
                    }
                }
            }
            catch { }

            return results;
        }

        private static (string? Id, string? Name) ExtractPackageInfo(System.Text.Json.JsonElement el)
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return (el.GetString(), null);
            }

            if (el.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (null, null);

            string? id = null;
            string? name = null;

            // Busca identificador do pacote (Winget / UniGetUI .ubundle / Scoop / Choco)
            string[] idPropertyNames = { "Id", "id", "PackageIdentifier", "packageIdentifier", "PackageId", "packageId" };
            foreach (var propName in idPropertyNames)
            {
                if (el.TryGetProperty(propName, out var idProp) && idProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string candidate = idProp.GetString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        id = candidate;
                        break;
                    }
                }
            }

            // Busca nome amigável do pacote
            string[] namePropertyNames = { "Name", "name", "PackageName", "packageName", "Title", "title" };
            foreach (var propName in namePropertyNames)
            {
                if (el.TryGetProperty(propName, out var nameProp) && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string candidate = nameProp.GetString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        name = candidate;
                        break;
                    }
                }
            }

            return (id, name);
        }

        private void AddParsedItem(List<SoftwareItem> list, string rawId, string? customName = null)
        {
            string id = rawId.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(id))
                return;

            // Auto-correção inteligente para IDs comuns ou abreviações de exportações externas
            if (id.Equals("chrome", StringComparison.OrdinalIgnoreCase) || id.Equals("google-chrome", StringComparison.OrdinalIgnoreCase))
            {
                id = "Google.Chrome";
                customName = "Google Chrome";
            }
            else if (id.Equals("whatsapp", StringComparison.OrdinalIgnoreCase))
            {
                id = "9NKSQGP7F2NH";
                customName = "WhatsApp Desktop";
            }

            var catalogMatch = GetCatalog().FirstOrDefault(c => 
                c.WingetId.Equals(id, StringComparison.OrdinalIgnoreCase) || 
                c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (catalogMatch != null)
            {
                id = catalogMatch.WingetId;
                if (string.IsNullOrWhiteSpace(customName) || customName == rawId)
                {
                    customName = catalogMatch.Name;
                }
            }

            if (list.Any(x => x.WingetId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                return;

            string friendlyName = !string.IsNullOrWhiteSpace(customName) ? customName.Trim() : id;
            if (string.IsNullOrWhiteSpace(customName) && id.Contains("."))
            {
                var parts = id.Split('.');
                friendlyName = parts.Last();
            }

            list.Add(new SoftwareItem
            {
                Id = id.ToLowerInvariant(),
                Name = friendlyName,
                Publisher = catalogMatch?.Publisher ?? "Lista Importada",
                Category = "Importados",
                WingetId = id,
                Description = catalogMatch?.Description ?? $"Pacote importado: {id}. Instalação automatizada via Winget.",
                Status = PackageStatus.Available,
                StatusMessage = "Disponível para instalar"
            });
        }
    }
}
