using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    public class MaintenanceService
    {
        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        static MaintenanceService()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public List<MaintenanceTool> GetTools()
        {
            return new List<MaintenanceTool>
            {
                new()
                {
                    Id = "clean_temp",
                    Title = "Limpeza de Arquivos Temporários",
                    Description = "Varre e limpa com segurança arquivos obsoletos de %TEMP% e Windows Temp para liberar espaço no disco (preserva Prefetch para manter o boot rápido).",
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

        public async Task RunToolAsync(MaintenanceTool tool, Action<string> logCallback, CancellationToken cancellationToken = default)
        {
            tool.IsRunning = true;
            tool.StatusText = "Executando...";
            tool.ProgressPercent = 0;

            try
            {
                if (tool.Id == "clean_temp")
                {
                    await CleanTemporaryFilesAsync(tool, logCallback, cancellationToken);
                }
                else
                {
                    await RunCommandLineToolAsync(tool, logCallback, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                tool.StatusText = "Cancelado";
                logCallback($"[INFO] Execução de {tool.Title} cancelada.");
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

        private async Task CleanTemporaryFilesAsync(MaintenanceTool tool, Action<string> logCallback, CancellationToken ct)
        {
            await Task.Run(() =>
            {
                logCallback("=== Iniciando Limpeza Segura de Arquivos Temporários ===");

                var directoriesToClean = new List<string>
                {
                    Path.GetTempPath(),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
                };

                long totalBytesFreed = 0;
                int filesDeleted = 0;

                foreach (var dir in directoriesToClean)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Directory.Exists(dir)) continue;

                    logCallback($"Examinando: {dir}...");
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        foreach (var file in di.GetFiles())
                        {
                            if (ct.IsCancellationRequested) break;
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
                            if (ct.IsCancellationRequested) break;
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
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    tool.StatusText = $"✓ Liberados {mbFreed:N1} MB";
                    tool.LastOutput = summary;
                    tool.ProgressPercent = 100;
                });
                logCallback($"[SUCESSO] {summary}");
            }, ct);
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
                        using (process)
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

        private static TimeSpan GetToolTimeout(string toolId) => toolId switch
        {
            "dism_restore" => TimeSpan.FromMinutes(60),
            "sfc_scannow" => TimeSpan.FromMinutes(45),
            "chkdsk_scan" or "trim_ssd" => TimeSpan.FromMinutes(20),
            _ => TimeSpan.FromMinutes(5)
        };

        private async Task RunCommandLineToolAsync(MaintenanceTool tool, Action<string> logCallback, CancellationToken cancellationToken = default)
        {
            var (fileName, arguments) = GetToolCommand(tool.Id);
            TimeSpan timeout = GetToolTimeout(tool.Id);

            logCallback($"\n>>> Executando comando: {fileName} {arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {fileName}.");
            try { process.StandardInput.Close(); } catch { }

            var fullOutput = new StringBuilder();
            bool timedOut = false;

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var killRegistration = linkedCts.Token.Register(() =>
            {
                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    timedOut = true;
                }
                try { process.Kill(entireProcessTree: true); } catch { }
            });

            var outTask = Task.Run(() => ReadDecodedStream(process.StandardOutput.BaseStream, line =>
            {
                lock (fullOutput)
                {
                    fullOutput.AppendLine(line);
                }
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    tool.LastOutput = line;
                    tool.StatusText = line;
                    var m = Regex.Match(line, @"(\d+(?:[.,]\d+)?)\s*%");
                    if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double pct))
                    {
                        tool.ProgressPercent = (int)Math.Clamp(pct, 0, 100);
                    }
                });
                logCallback(line);
            }), CancellationToken.None);

            var errTask = Task.Run(() => ReadDecodedStream(process.StandardError.BaseStream, err =>
            {
                lock (fullOutput)
                {
                    fullOutput.AppendLine(err);
                }
                logCallback($"[ERRO] {err}");
            }), CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timedOut)
            {
                await Task.WhenAll(outTask, errTask);
                string hint = tool.Id is "dism_restore" or "sfc_scannow"
                    ? " (Costuma ocorrer se o Windows Update estiver ocupado ou pendente de reinício.)"
                    : "";
                string msg = $"Tempo limite de {(int)timeout.TotalMinutes} min atingido{hint}";
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    tool.StatusText = "Tempo limite excedido";
                    tool.LastOutput = msg;
                });
                logCallback($"[AVISO] {tool.Title}: {msg}");
                return;
            }
            catch (OperationCanceledException)
            {
                await Task.WhenAll(outTask, errTask);
                throw;
            }

            await Task.WhenAll(outTask, errTask);

            string collectedText;
            lock (fullOutput)
            {
                collectedText = fullOutput.ToString();
            }

            int exitCode = process.ExitCode;
            var (statusText, summaryLog) = InterpretToolResult(tool.Id, tool.Title, exitCode, collectedText);

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                tool.StatusText = statusText;
                tool.ProgressPercent = 100;
            });
            logCallback(summaryLog);
        }

        private static void ReadDecodedStream(Stream stream, Action<string> onLine)
        {
            byte[] buffer = new byte[4096];
            char[] charBuffer = new char[8192];
            Decoder? decoder = null;
            var lineBuilder = new StringBuilder();
            bool pendingCr = false;

            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (decoder == null)
                {
                    bool looksUtf16Le = false;
                    if (bytesRead >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
                    {
                        looksUtf16Le = true;
                    }
                    else
                    {
                        int zeroOdd = 0;
                        int pairs = bytesRead / 2;
                        for (int i = 1; i < bytesRead; i += 2)
                        {
                            if (buffer[i] == 0) zeroOdd++;
                        }
                        looksUtf16Le = pairs > 0 && zeroOdd * 10 >= pairs * 6;
                    }

                    Encoding enc;
                    if (looksUtf16Le)
                    {
                        enc = Encoding.Unicode;
                    }
                    else
                    {
                        try
                        {
                            uint oemCp = GetOEMCP();
                            enc = oemCp == 65001
                                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                                : Encoding.GetEncoding((int)oemCp);
                        }
                        catch
                        {
                            enc = Encoding.Latin1;
                        }
                    }
                    decoder = enc.GetDecoder();
                }

                int chars = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0);
                for (int i = 0; i < chars; i++)
                {
                    char c = charBuffer[i];
                    if (c == '\0' || c == '\ufeff') continue;

                    if (pendingCr && c != '\n')
                    {
                        FlushBuilder();
                    }
                    pendingCr = false;

                    if (c == '\r')
                    {
                        pendingCr = true;
                    }
                    else if (c == '\n')
                    {
                        FlushBuilder();
                    }
                    else
                    {
                        lineBuilder.Append(c);
                    }
                }
            }

            FlushBuilder();

            void FlushBuilder()
            {
                string line = lineBuilder.ToString().Trim();
                lineBuilder.Clear();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    onLine(line);
                }
            }
        }

        private static (string statusText, string logMessage) InterpretToolResult(
            string toolId,
            string title,
            int exitCode,
            string output)
        {
            uint unsignedCode = unchecked((uint)exitCode);

            if (exitCode == 3010)
            {
                return ("✓ Concluído (Reinicie o PC)",
                    $"[SUCESSO] {title} concluído; reinicialização do Windows necessária para finalizar alterações (código 3010).");
            }

            if (exitCode is 740 or 5 ||
                unsignedCode == 0x80070005 ||
                output.Contains("must be an administrator", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("privilégios elevados", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("precisa ser administrador", StringComparison.OrdinalIgnoreCase))
            {
                return ("Requer Administrador",
                    $"[ERRO] {title} precisa de privilégios de Administrador (código {exitCode}). Execute o app como Admin ou use 'Abrir no CMD'.");
            }

            if (toolId == "dism_restore")
            {
                if (unsignedCode == 0x800F081F)
                {
                    return ("Falha: origem não encontrada",
                        "[ERRO] DISM não encontrou os arquivos de origem (0x800F081F). Verifique a internet ou forneça uma ISO montada como fonte.");
                }
                if (unsignedCode == 0x800F0906 || unsignedCode == 0x8024002E)
                {
                    return ("Falha ao baixar arquivos",
                        $"[ERRO] DISM não conseguiu baixar os arquivos do Windows Update (0x{unsignedCode:X8}).");
                }
            }

            if (exitCode == 0)
            {
                if (toolId == "sfc_scannow")
                {
                    if (output.Contains("successfully repaired", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("reparou com êxito", StringComparison.OrdinalIgnoreCase))
                    {
                        return ("✓ Arquivos reparados",
                            "[SUCESSO] SFC encontrou arquivos corrompidos e os reparou com êxito. Recomenda-se reiniciar o PC.");
                    }
                    if (output.Contains("unable to fix", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("não pôde corrigir", StringComparison.OrdinalIgnoreCase))
                    {
                        return ("Atenção: não corrigido totalmente",
                            "[AVISO] SFC encontrou arquivos corrompidos que não puderam ser corrigidos. Rode o DISM RestoreHealth primeiro e depois o SFC novamente.");
                    }
                }

                return ("✓ Concluído com Sucesso", $"[SUCESSO] Operação de {title} finalizada com código 0.");
            }

            return ($"Finalizado (Código {exitCode})",
                $"[INFO] {title} finalizado com código {exitCode} (0x{unsignedCode:X8}).");
        }
    }
}
