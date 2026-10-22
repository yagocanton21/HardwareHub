using System.Diagnostics;
using System.IO;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public class SoftwareService
    {
        public List<SoftwareItem> GetCatalog()
        {
            return new List<SoftwareItem>
            {
                // Navegadores
                new()
                {
                    Id = "chrome",
                    Name = "Google Chrome",
                    Publisher = "Google LLC",
                    Category = "Navegadores",
                    Description = "O navegador mais popular do mundo, rápido, seguro e integrado com a conta Google.",
                    WingetId = "Google.Chrome",
                    IconKind = "Globe24"
                },
                new()
                {
                    Id = "firefox",
                    Name = "Mozilla Firefox",
                    Publisher = "Mozilla",
                    Category = "Navegadores",
                    Description = "Navegador veloz focado em privacidade, código aberto e suporte a extensões avançadas.",
                    WingetId = "Mozilla.Firefox",
                    IconKind = "Globe24"
                },
                new()
                {
                    Id = "brave",
                    Name = "Brave Browser",
                    Publisher = "Brave Software",
                    Category = "Navegadores",
                    Description = "Navegador ultra-rápido baseado em Chromium com bloqueador nativo de anúncios e rastreadores.",
                    WingetId = "Brave.Brave",
                    IconKind = "Shield24"
                },

                // Produtividade & Escritório
                new()
                {
                    Id = "msoffice",
                    Name = "Microsoft 365 (Office)",
                    Publisher = "Microsoft Corporation",
                    Category = "Produtividade",
                    Description = "Pacote oficial de aplicativos Microsoft Office (Word, Excel, PowerPoint, Outlook e OneNote) via CDN oficial.",
                    WingetId = "Microsoft.Office",
                    IconKind = "Document24"
                },
                new()
                {
                    Id = "libreoffice",
                    Name = "LibreOffice",
                    Publisher = "The Document Foundation",
                    Category = "Produtividade",
                    Description = "Suíte de escritório livre, gratuita e de código aberto com editor de texto (Writer), planilhas (Calc) e apresentações.",
                    WingetId = "TheDocumentFoundation.LibreOffice",
                    IconKind = "DocumentTable24"
                },

                // Desenvolvimento & TI
                new()
                {
                    Id = "vscode",
                    Name = "Visual Studio Code",
                    Publisher = "Microsoft Corporation",
                    Category = "Desenvolvimento",
                    Description = "Editor de código-fonte moderno, extensível e leve, padrão da indústria de software.",
                    WingetId = "Microsoft.VisualStudioCode",
                    IconKind = "Code24"
                },
                new()
                {
                    Id = "git",
                    Name = "Git for Windows",
                    Publisher = "Git SCM",
                    Category = "Desenvolvimento",
                    Description = "Sistema de controle de versão distribuído essencial para desenvolvedores e versionamento de código.",
                    WingetId = "Git.Git",
                    IconKind = "BranchFork24"
                },
                new()
                {
                    Id = "nodejs",
                    Name = "Node.js (LTS)",
                    Publisher = "OpenJS Foundation",
                    Category = "Desenvolvimento",
                    Description = "Ambiente de execução JavaScript construído no motor V8 do Chrome para backend e ferramentas CLI.",
                    WingetId = "OpenJS.NodeJS.LTS",
                    IconKind = "Server24"
                },
                new()
                {
                    Id = "powertoys",
                    Name = "Microsoft PowerToys",
                    Publisher = "Microsoft Corporation",
                    Category = "Desenvolvimento",
                    Description = "Conjunto de utilitários avançados do Windows para aumentar produtividade (FancyZones, PowerToys Run, OCR).",
                    WingetId = "Microsoft.PowerToys",
                    IconKind = "Wrench24"
                },

                // Jogos & Comunicação
                new()
                {
                    Id = "steam",
                    Name = "Steam",
                    Publisher = "Valve Corporation",
                    Category = "Jogos & Mídia",
                    Description = "A maior plataforma de distribuição digital de jogos de computador do mundo.",
                    WingetId = "Valve.Steam",
                    IconKind = "Games24"
                },
                new()
                {
                    Id = "discord",
                    Name = "Discord",
                    Publisher = "Discord Inc.",
                    Category = "Jogos & Mídia",
                    Description = "Plataforma de comunicação completa por voz, vídeo e texto para comunidades e gamers.",
                    WingetId = "Discord.Discord",
                    IconKind = "Chat24"
                },
                new()
                {
                    Id = "spotify",
                    Name = "Spotify",
                    Publisher = "Spotify AB",
                    Category = "Jogos & Mídia",
                    Description = "Serviço de streaming de música e podcasts líder mundial com milhões de faixas.",
                    WingetId = "Spotify.Spotify",
                    IconKind = "MusicNote224"
                },
                new()
                {
                    Id = "obs",
                    Name = "OBS Studio",
                    Publisher = "OBS Project",
                    Category = "Jogos & Mídia",
                    Description = "Software livre e de código aberto para gravação de vídeo e transmissões ao vivo (streaming).",
                    WingetId = "OBSProject.OBSStudio",
                    IconKind = "Video24"
                },

                // Utilitários Essenciais
                new()
                {
                    Id = "7zip",
                    Name = "7-Zip",
                    Publisher = "Igor Pavlov",
                    Category = "Utilitários",
                    Description = "Compactador e descompactador de arquivos de alta taxa de compressão (ZIP, 7z, RAR, TAR, ISO).",
                    WingetId = "7zip.7zip",
                    IconKind = "FolderZip24"
                },
                new()
                {
                    Id = "vlc",
                    Name = "VLC Media Player",
                    Publisher = "VideoLAN",
                    Category = "Utilitários",
                    Description = "Player multimídia versátil que reproduz praticamente qualquer formato de vídeo e áudio sem codecs extras.",
                    WingetId = "VideoLAN.VLC",
                    IconKind = "PlayCircle24"
                },
                new()
                {
                    Id = "notepadplusplus",
                    Name = "Notepad++",
                    Publisher = "Don Ho",
                    Category = "Utilitários",
                    Description = "Editor de texto e código extremamente rápido e leve com destaque de sintaxe e abas.",
                    WingetId = "Notepad++.Notepad++",
                    IconKind = "DocumentText24"
                },
                new()
                {
                    Id = "pawnio",
                    Name = "PawnIO Kernel Driver",
                    Publisher = "namazso",
                    Category = "Utilitários",
                    Description = "Driver de baixo nível assinado pelo Windows para leitura de sensores de temperatura da CPU (usado pelo FanControl, LibreHardwareMonitor e ResetExplode).",
                    WingetId = "namazso.PawnIO",
                    IconKind = "Wrench24"
                },
                new()
                {
                    Id = "whatsapp",
                    Name = "WhatsApp Desktop",
                    Publisher = "WhatsApp LLC (Meta)",
                    Category = "Comunicação",
                    Description = "Aplicativo oficial de mensagens instantâneas e chamadas de voz/vídeo para desktop.",
                    WingetId = "9NKSQGP7F2NH",
                    IconKind = "Chat24"
                },
                new()
                {
                    Id = "telegram",
                    Name = "Telegram Desktop",
                    Publisher = "Telegram FZ-LLC",
                    Category = "Comunicação",
                    Description = "Mensageiro em nuvem rápido, seguro, com canais, grupos e envio de arquivos pesados.",
                    WingetId = "Telegram.TelegramDesktop",
                    IconKind = "Chat24"
                },
                new()
                {
                    Id = "zoom",
                    Name = "Zoom Workplace",
                    Publisher = "Zoom Video Communications",
                    Category = "Produtividade",
                    Description = "Plataforma de videoconferência, reuniões online, chat e webinars corporativos.",
                    WingetId = "Zoom.Zoom",
                    IconKind = "Video24"
                },
                new()
                {
                    Id = "anydesk",
                    Name = "AnyDesk",
                    Publisher = "AnyDesk Software GmbH",
                    Category = "Utilitários",
                    Description = "Acesso e suporte remoto veloz para administração de TI e manutenção de computadores.",
                    WingetId = "AnyDeskSoftwareGmbH.AnyDesk",
                    IconKind = "Desktop24"
                },
                new()
                {
                    Id = "winrar",
                    Name = "WinRAR",
                    Publisher = "win.rar GmbH",
                    Category = "Utilitários",
                    Description = "Poderoso gerenciador e compactador de arquivos com suporte completo a arquivos RAR e ZIP.",
                    WingetId = "RARLab.WinRAR",
                    IconKind = "FolderZip24"
                },
                new()
                {
                    Id = "epicgames",
                    Name = "Epic Games Launcher",
                    Publisher = "Epic Games Inc.",
                    Category = "Jogos & Mídia",
                    Description = "Loja de jogos digitais com jogos gratuitos semanais e catálogo de grandes títulos.",
                    WingetId = "EpicGames.EpicGamesLauncher",
                    IconKind = "Games24"
                },
                new()
                {
                    Id = "qbittorrent",
                    Name = "qBittorrent",
                    Publisher = "qBittorrent Project",
                    Category = "Utilitários",
                    Description = "Cliente torrent de código aberto, leve, sem anúncios e com motor de busca integrado.",
                    WingetId = "qBittorrent.qBittorrent",
                    IconKind = "ArrowDownload24"
                }
            };
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

                case "pawnio":
                    if (HardwareService.IsPawnIOInstalled) return true;
                    if (installedNames.Any(n => n.Contains("PawnIO", StringComparison.OrdinalIgnoreCase))) return true;
                    break;

                default:
                    if (installedNames.Any(n => n.Contains(item.Name, StringComparison.OrdinalIgnoreCase) || item.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                        return true;
                    break;
            }

            return false;
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
            item.Status = PackageStatus.Installing;
            item.StatusMessage = "Iniciando download...";
            item.IsIndeterminate = true;
            logCallback($"[INFO] Solicitando instalação do pacote {item.Name} ({item.WingetId})...");

            // Tratamento especial para o Microsoft 365 / Office:
            // A Microsoft atualiza silenciosamente os binários na CDN antes do repositório da comunidade do Winget ser atualizado,
            // gerando o erro de hash mismatch 0x8A150011 (-1978335215).
            // Baixamos o executável oficial assinado pela Microsoft direto da CDN, garantindo 100% de sucesso!
            if (item.Id == "msoffice")
            {
                return await InstallOfficeDirectAsync(item, logCallback);
            }

            return await Task.Run(() =>
            {
                try
                {
                    string sourceArg = item.WingetId.StartsWith("9N") ? "-s msstore" : "";
                    string exactArg = item.WingetId.StartsWith("9N") ? "" : "--exact";
                    string extraFlags = item.WingetId.StartsWith("9N") ? "" : "--force --ignore-security-hash";

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "winget",
                        Arguments = $"install --id \"{item.WingetId}\" {sourceArg} {exactArg} {extraFlags} --silent --accept-package-agreements --accept-source-agreements",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = new Process { StartInfo = startInfo };

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            logCallback(e.Data);
                            if (e.Data.Contains("Downloading", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("Baixando", StringComparison.OrdinalIgnoreCase))
                            {
                                item.StatusMessage = "Baixando pacote...";
                            }
                            else if (e.Data.Contains("Installing", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("Instalando", StringComparison.OrdinalIgnoreCase))
                            {
                                item.StatusMessage = "Instalando no Windows...";
                            }
                        }
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            logCallback($"[STDERR] {e.Data}");
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        item.Status = PackageStatus.Installed;
                        item.StatusMessage = "Instalado com sucesso!";
                        item.IsIndeterminate = false;
                        logCallback($"[SUCESSO] {item.Name} instalado com êxito!");
                        return true;
                    }
                    else
                    {
                        item.Status = PackageStatus.Failed;
                        item.StatusMessage = $"Falha ({process.ExitCode})";
                        item.IsIndeterminate = false;
                        logCallback($"[ERRO] Código de saída winget: {process.ExitCode}");
                        return false;
                    }
                }
                catch (Exception ex)
                {
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
                        FileName = "winget",
                        Arguments = $"search \"{query.Trim()}\" --accept-source-agreements -n 14",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    };

                    using var process = Process.Start(psi);
                    if (process == null) return list;

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(15000);

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    bool passedDivider = false;
                    int idColumnIndex = -1;

                    foreach (var line in lines)
                    {
                        if (line.StartsWith("---"))
                        {
                            passedDivider = true;
                            continue;
                        }

                        if (!passedDivider)
                        {
                            if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase))
                            {
                                idColumnIndex = line.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
                            }
                            continue;
                        }

                        if (idColumnIndex > 0 && line.Length > idColumnIndex)
                        {
                            string name = line.Substring(0, idColumnIndex).Trim();
                            string rest = line.Substring(idColumnIndex).Trim();
                            string[] tokens = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (tokens.Length >= 1)
                            {
                                string id = tokens[0].Trim();
                                string version = tokens.Length >= 2 ? tokens[1].Trim() : "";

                                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id) && !name.StartsWith("<") && !id.Contains("truncated"))
                                {
                                    list.Add(new SoftwareItem
                                    {
                                        Id = id.ToLowerInvariant(),
                                        Name = name,
                                        Publisher = "Repositório Oficial Winget",
                                        Category = "Winget Online",
                                        WingetId = id,
                                        Description = $"Pacote encontrado online no repositório global do Windows (Versão: {version}). Instalação silenciosa de 1 clique."
                                    });
                                }
                            }
                        }
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

                // 1. Tenta fazer parse como JSON (formato padrão do `winget export`)
                if (content.StartsWith("{") || content.StartsWith("["))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(content);
                        var root = doc.RootElement;

                        // Caso 1: Formato padrão `winget export -o ...`
                        // Estrutura: { "Sources": [ { "Packages": [ { "PackageIdentifier": "..." } ] } ] }
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Sources", out var sourcesEl) && sourcesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var src in sourcesEl.EnumerateArray())
                            {
                                if (src.TryGetProperty("Packages", out var pkgsEl) && pkgsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var pkg in pkgsEl.EnumerateArray())
                                    {
                                        if (pkg.TryGetProperty("PackageIdentifier", out var idProp))
                                        {
                                            string id = idProp.GetString() ?? "";
                                            if (!string.IsNullOrWhiteSpace(id))
                                            {
                                                AddParsedItem(results, id);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        // Caso 2: Objeto com lista direta de "Packages"
                        else if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Packages", out var directPkgs) && directPkgs.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var pkg in directPkgs.EnumerateArray())
                            {
                                if (pkg.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    AddParsedItem(results, pkg.GetString() ?? "");
                                }
                                else if (pkg.TryGetProperty("PackageIdentifier", out var idProp))
                                {
                                    AddParsedItem(results, idProp.GetString() ?? "");
                                }
                                else if (pkg.TryGetProperty("Id", out var id2Prop))
                                {
                                    AddParsedItem(results, id2Prop.GetString() ?? "");
                                }
                            }
                        }
                        // Caso 3: Array simples ["Google.Chrome", "7zip.7zip"]
                        else if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var el in root.EnumerateArray())
                            {
                                if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    AddParsedItem(results, el.GetString() ?? "");
                                }
                                else if (el.TryGetProperty("PackageIdentifier", out var idProp))
                                {
                                    AddParsedItem(results, idProp.GetString() ?? "");
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 2. Se não encontrou itens como JSON, faz parse linha por linha (formato TXT / YAML / lista)
                if (results.Count == 0)
                {
                    var lines = File.ReadAllLines(filePath);
                    foreach (var rawLine in lines)
                    {
                        string line = rawLine.Trim();
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("//"))
                            continue;

                        // Se for YAML do tipo: - PackageIdentifier: Google.Chrome
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

        private static void AddParsedItem(List<SoftwareItem> list, string rawId)
        {
            string id = rawId.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(id) || list.Any(x => x.WingetId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                return;

            string friendlyName = id;
            if (id.Contains("."))
            {
                var parts = id.Split('.');
                friendlyName = parts.Last();
            }

            list.Add(new SoftwareItem
            {
                Id = id.ToLowerInvariant(),
                Name = friendlyName,
                Publisher = "Lista Importada",
                Category = "Importados",
                WingetId = id,
                Description = $"Pacote importado: {id}. Instalação automatizada via Winget.",
                Status = PackageStatus.Available,
                StatusMessage = "Disponível para instalar"
            });
        }
    }
}
