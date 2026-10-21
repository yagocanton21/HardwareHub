using System.Diagnostics;
using System.IO;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    public class MaintenanceService
    {
        public List<MaintenanceTool> GetTools()
        {
            return new List<MaintenanceTool>
            {
                new()
                {
                    Id = "clean_temp",
                    Title = "Limpeza de Arquivos Temporários",
                    Description = "Varre e limpa com segurança arquivos obsoletos de %TEMP%, Windows Temp e Prefetch para liberar espaço no disco.",
                    Icon = "🧹",
                    ActionButtonText = "Limpar Agora",
                    AccentColor = "#10B981"
                },
                new()
                {
                    Id = "sfc_scannow",
                    Title = "Verificador de Arquivos do Sistema (SFC)",
                    Description = "Examina a integridade de todos os arquivos protegidos do Windows (sfc /scannow) e recupera arquivos corrompidos.",
                    Icon = "🛡️",
                    ActionButtonText = "Executar SFC",
                    AccentColor = "#6366F1"
                },
                new()
                {
                    Id = "dism_restore",
                    Title = "Reparo de Imagem do Windows (DISM)",
                    Description = "Restaura e repara a integridade da imagem de componentes do Windows usando o repositório oficial da Microsoft (DISM RestoreHealth).",
                    Icon = "🩺",
                    ActionButtonText = "Reparar Imagem",
                    AccentColor = "#3B82F6"
                },
                new()
                {
                    Id = "chkdsk_scan",
                    Title = "Checagem Online de Disco (CHKDSK)",
                    Description = "Varre o volume C: em busca de inconsistências de arquivos e setores defeituosos sem travar o computador nem reiniciar (chkdsk C: /scan).",
                    Icon = "🔍",
                    ActionButtonText = "Escanear Disco",
                    AccentColor = "#F59E0B"
                },
                new()
                {
                    Id = "trim_ssd",
                    Title = "Otimização e TRIM de SSDs",
                    Description = "Envia a instrução TRIM aos SSDs e desfragmenta volumes mecânicos para manter o desempenho máximo de escrita (defrag C: /O).",
                    Icon = "⚡",
                    ActionButtonText = "Otimizar SSD",
                    AccentColor = "#8B5CF6"
                },
                new()
                {
                    Id = "flush_dns",
                    Title = "Limpeza do Cache DNS",
                    Description = "Limpa e redefine o cache do resolvedor do cliente DNS (ipconfig /flushdns) para reparar falhas de conexão de rede.",
                    Icon = "🌐",
                    ActionButtonText = "Limpar DNS",
                    AccentColor = "#06B6D4"
                }
            };
        }

        public async Task RunToolAsync(MaintenanceTool tool, Action<string> logCallback)
        {
            tool.IsRunning = true;
            tool.StatusText = "Executando...";
            tool.ProgressPercent = 0;

            try
            {
                if (tool.Id == "clean_temp")
                {
                    await CleanTemporaryFilesAsync(tool, logCallback);
                }
                else
                {
                    await RunCommandLineToolAsync(tool, logCallback);
                }
            }
            catch (Exception ex)
            {
                tool.StatusText = "Falha";
                tool.LastOutput = ex.Message;
                logCallback($"[ERRO] Falha ao executar {tool.Title}: {ex.Message}");
            }
            finally
            {
                tool.IsRunning = false;
            }
        }

        private async Task CleanTemporaryFilesAsync(MaintenanceTool tool, Action<string> logCallback)
        {
            await Task.Run(() =>
            {
                logCallback("=== Iniciando Limpeza Segura de Arquivos Temporários ===");

                var directoriesToClean = new List<string>
                {
                    Path.GetTempPath(),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch")
                };

                long totalBytesFreed = 0;
                int filesDeleted = 0;

                foreach (var dir in directoriesToClean)
                {
                    if (!Directory.Exists(dir)) continue;

                    logCallback($"Examinando: {dir}...");
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        foreach (var file in di.GetFiles())
                        {
                            try
                            {
                                long size = file.Length;
                                file.Delete();
                                totalBytesFreed += size;
                                filesDeleted++;
                            }
                            catch
                            {
                                // Arquivo em uso pelo Windows ou outro processo em execução (seguro ignorar)
                            }
                        }

                        foreach (var subDir in di.GetDirectories())
                        {
                            try
                            {
                                subDir.Delete(true);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        logCallback($"[AVISO] Acesso restrito a {dir}: {ex.Message}");
                    }
                }

                double mbFreed = Math.Round(totalBytesFreed / (1024.0 * 1024.0), 1);
                string summary = $"Concluído! {mbFreed:N1} MB liberados ({filesDeleted:N0} arquivos removidos).";
                tool.StatusText = $"✓ Liberados {mbFreed:N1} MB";
                tool.LastOutput = summary;
                tool.ProgressPercent = 100;
                logCallback($"[SUCESSO] {summary}");
            });
        }

        private (string fileName, string arguments) GetToolCommand(string toolId)
        {
            return toolId switch
            {
                "sfc_scannow" => ("sfc.exe", "/scannow"),
                "dism_restore" => ("dism.exe", "/Online /Cleanup-Image /RestoreHealth"),
                "chkdsk_scan" => ("chkdsk.exe", "C: /scan"),
                "trim_ssd" => ("defrag.exe", "C: /O"),
                "flush_dns" => ("ipconfig.exe", "/flushdns"),
                "clean_temp" => ("cmd.exe", "/c echo Limpando diretorios temporarios... && del /q/f/s \"%TEMP%\\*\" 2>nul && del /q/f/s \"C:\\Windows\\Temp\\*\" 2>nul && echo Concluido!"),
                _ => throw new InvalidOperationException("Ferramenta desconhecida.")
            };
        }

        public void RunToolInCmd(MaintenanceTool tool, Action<string>? logCallback = null)
        {
            if (tool.IsRunning) return;

            var (fileName, arguments) = GetToolCommand(tool.Id);
            string fullCmd = $"{fileName} {arguments}".Trim();

            logCallback?.Invoke($"[CMD] Abrindo janela do Prompt de Comando: {tool.Title} ({fullCmd})");

            tool.IsRunning = true;
            tool.StatusText = "Executando no CMD...";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k title Hardware HUB - {tool.Title} && echo ================================================================= && echo  [Hardware HUB] Executando: {fullCmd} && echo ================================================================= && {fullCmd}",
                UseShellExecute = true,
                Verb = "runas"
            };

            try
            {
                var process = Process.Start(psi);
                if (process != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await process.WaitForExitAsync();
                        }
                        catch { }
                        finally
                        {
                            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                            {
                                tool.IsRunning = false;
                                tool.StatusText = "✓ Finalizado no CMD";
                                logCallback?.Invoke($"[CMD] Janela de {tool.Title} foi fechada.");
                            });
                        }
                    });
                }
                else
                {
                    tool.IsRunning = false;
                    tool.StatusText = "Pronto";
                }
            }
            catch (Exception ex)
            {
                tool.IsRunning = false;
                tool.StatusText = "Falha ao abrir CMD";
                logCallback?.Invoke($"[ERRO] Não foi possível abrir o CMD: {ex.Message}");
            }
        }

        private async Task RunCommandLineToolAsync(MaintenanceTool tool, Action<string> logCallback)
        {
            var (fileName, arguments) = GetToolCommand(tool.Id);

            logCallback($"\n>>> Executando comando: {fileName} {arguments}");

            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.Default,
                    StandardErrorEncoding = System.Text.Encoding.Default
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var outTask = Task.Run(async () =>
                {
                    var sb = new System.Text.StringBuilder();
                    char[] buffer = new char[128];
                    int read;
                    while ((read = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            char c = buffer[i];
                            if (c == '\r' || c == '\n')
                            {
                                string line = sb.ToString().Trim();
                                sb.Clear();
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                                    {
                                        tool.LastOutput = line;
                                        tool.StatusText = line;
                                        var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*%", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                        if (m.Success && int.TryParse(m.Groups[1].Value, out int pct))
                                        {
                                            tool.ProgressPercent = Math.Clamp(pct, 0, 100);
                                        }
                                    });
                                    logCallback(line);
                                }
                            }
                            else
                            {
                                sb.Append(c);
                            }
                        }
                    }
                });

                var errTask = Task.Run(async () =>
                {
                    string? err;
                    while ((err = await process.StandardError.ReadLineAsync()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(err))
                        {
                            logCallback($"[ERRO] {err}");
                        }
                    }
                });

                process.WaitForExit();
                Task.WaitAll(new[] { outTask, errTask }, TimeSpan.FromSeconds(5));

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (process.ExitCode == 0)
                    {
                        tool.StatusText = "✓ Concluído com Sucesso";
                        logCallback($"[SUCESSO] Operação de {tool.Title} finalizada com código 0.");
                    }
                    else
                    {
                        tool.StatusText = $"Finalizado (Código {process.ExitCode})";
                        logCallback($"[INFO] {tool.Title} finalizado com código {process.ExitCode}.");
                    }
                    tool.ProgressPercent = 100;
                });
            });
        }
    }
}
