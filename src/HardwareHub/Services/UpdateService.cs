using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HardwareHub.Services
{
    public class UpdateInfo
    {
        public bool IsAvailable { get; set; }
        public string CurrentVersion { get; set; } = "1.0.0";
        public string LatestVersion { get; set; } = "";
        public string Changelog { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public long AssetSizeBytes { get; set; }
        public string FormattedSize => AssetSizeBytes > 0 ? $"{AssetSizeBytes / (1024.0 * 1024.0):F1} MB" : "";
    }

    public class UpdateService
    {
        private const string GitHubRepo = "yagocanton21/HardwareHub";
        private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
        private readonly HttpClient _httpClient;

        public UpdateService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HardwareHub", "1.0"));
            _httpClient.Timeout = TimeSpan.FromSeconds(20);
        }

        public static Version GetCurrentVersion()
        {
            try
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    var vi = FileVersionInfo.GetVersionInfo(exePath);
                    if (vi.FileMajorPart > 0 || vi.FileMinorPart > 0 || vi.FileBuildPart > 0)
                    {
                        return new Version(vi.FileMajorPart, vi.FileMinorPart, Math.Max(vi.FileBuildPart, 0));
                    }
                }
            }
            catch { }

            var ver = Assembly.GetEntryAssembly()?.GetName().Version ?? Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? new Version(ver.Major, ver.Minor, Math.Max(ver.Build, 0)) : new Version(1, 0, 1);
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            var info = new UpdateInfo
            {
                CurrentVersion = GetCurrentVersion().ToString(3)
            };

            try
            {
                var response = await _httpClient.GetAsync(GitHubApiUrl);
                if (!response.IsSuccessStatusCode)
                {
                    return info;
                }

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string rawTag = root.GetProperty("tag_name").GetString() ?? "";
                string cleanTag = Regex.Replace(rawTag, @"^[^\d]*", "").Trim(); // remove 'v' inicial se houver

                if (!Version.TryParse(cleanTag, out Version? latestVer))
                {
                    // Tenta completar ex: "1.0" -> "1.0.0"
                    if (Version.TryParse(cleanTag + ".0", out Version? paddedVer))
                    {
                        latestVer = paddedVer;
                    }
                }

                var currentVer = GetCurrentVersion();

                info.LatestVersion = cleanTag;
                info.Changelog = root.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? "") : "";

                // Procura o executável anexado na release
                if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsEl.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            info.DownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            info.AssetSizeBytes = asset.GetProperty("size").GetInt64();
                            break;
                        }
                    }
                }

                if (latestVer != null && latestVer > currentVer && !string.IsNullOrEmpty(info.DownloadUrl))
                {
                    info.IsAvailable = true;
                }
            }
            catch
            {
                // Falha de rede ou repositório sem releases ainda
                info.IsAvailable = false;
            }

            return info;
        }

        /// <summary>
        /// Caminho do arquivo de log de atualização persistente em %TEMP%.
        /// Pode ser consultado pelo usuário para diagnosticar falhas.
        /// </summary>
        public static readonly string UpdateLogPath = Path.Combine(Path.GetTempPath(), "HardwareHub_Update.log");

        private static void WriteUpdateLog(string msg, Action<string>? uiLog = null)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [C#] {msg}";
                File.AppendAllText(UpdateLogPath, line + Environment.NewLine);
            }
            catch { }
            uiLog?.Invoke(msg);
        }

        public async Task DownloadAndInstallUpdateAsync(string downloadUrl, IProgress<double> progress, Action<string>? log = null)
        {
            WriteUpdateLog("=========================================================", log);
            WriteUpdateLog("INICIO DO PROCESSO DE ATUALIZACAO", log);

            string currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            WriteUpdateLog($"Executavel atual: {currentExePath}", log);
            WriteUpdateLog($"Versao atual: {GetCurrentVersion()}", log);
            WriteUpdateLog($"URL de download: {downloadUrl}", log);

            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(currentExePath))
            {
                WriteUpdateLog("ERRO: Caminho do executavel atual nao identificado!", log);
                throw new InvalidOperationException("Não foi possível identificar o caminho do executável atual para substituição.");
            }

            // Usa nome fixo para facilitar diagnóstico (sem GUID)
            string tempUpdateExe = Path.Combine(Path.GetTempPath(), "HardwareHub_NewVersion.exe");

            // Remove qualquer download anterior incompleto
            try { if (File.Exists(tempUpdateExe)) File.Delete(tempUpdateExe); } catch { }

            WriteUpdateLog($"Arquivo temporario: {tempUpdateExe}", log);
            log?.Invoke("[ATUALIZAÇÃO] Conectando ao servidor para baixar a nova versão...");

            // ═══════════════════ DOWNLOAD ═══════════════════
            using (var dlClient = new HttpClient())
            {
                dlClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HardwareHub", "1.0"));
                dlClient.Timeout = TimeSpan.FromMinutes(5);

                using var response = await dlClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                WriteUpdateLog($"Download iniciado. Tamanho esperado: {totalBytes} bytes", log);

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempUpdateExe, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

                var buffer = new byte[65536];
                long totalRead = 0;
                int read;

                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read);
                    totalRead += read;

                    if (totalBytes > 0)
                    {
                        double percent = Math.Round((double)totalRead / totalBytes * 100.0, 1);
                        progress.Report(percent);
                    }
                }

                WriteUpdateLog($"Download concluido. Bytes recebidos: {totalRead}", log);
            }

            // ═══════════════════ VALIDAÇÃO ═══════════════════
            if (!File.Exists(tempUpdateExe))
            {
                WriteUpdateLog("ERRO: Arquivo baixado nao existe no disco!", log);
                throw new InvalidOperationException("Arquivo baixado não encontrado.");
            }

            var downloadedFileInfo = new FileInfo(tempUpdateExe);
            WriteUpdateLog($"Arquivo baixado verificado: {downloadedFileInfo.Length} bytes", log);

            if (downloadedFileInfo.Length < 1_000_000) // < 1MB é claramente inválido para um app .NET self-contained
            {
                WriteUpdateLog($"ERRO: Arquivo baixado muito pequeno ({downloadedFileInfo.Length} bytes). Provavelmente corrompido.", log);
                throw new InvalidOperationException($"Arquivo baixado é inválido ({downloadedFileInfo.Length} bytes).");
            }

            log?.Invoke("[ATUALIZAÇÃO] Download verificado. Criando script de substituição...");

            // ═══════════════════ SCRIPT DE ATUALIZAÇÃO (BATCH) ═══════════════════
            // Usa .cmd ao invés de PowerShell porque:
            // 1. Batch NUNCA é bloqueado por ExecutionPolicy (comum no Windows 10 corporativo)
            // 2. Usa "ping" para delays ao invés de "timeout" (que falha em janelas ocultas/não-interativas)
            // 3. Funciona em TODAS as versões do Windows sem dependência de runtime

            string updaterCmd = Path.Combine(Path.GetTempPath(), "HardwareHub_Updater.cmd");
            int currentPid = Environment.ProcessId;
            string oldExePath = currentExePath + ".old";

            string batContent = $@"@echo off
setlocal EnableDelayedExpansion

set ""LOGFILE={UpdateLogPath}""
set ""SRC={tempUpdateExe}""
set ""DST={currentExePath}""
set ""OLD={oldExePath}""
set ""APPPID={currentPid}""

echo [%date% %time%] [BAT] ================================================= >> ""%LOGFILE%""
echo [%date% %time%] [BAT] SCRIPT DE ATUALIZACAO INICIADO >> ""%LOGFILE%""
echo [%date% %time%] [BAT] PID do app: %APPPID% >> ""%LOGFILE%""
echo [%date% %time%] [BAT] Arquivo novo: %SRC% >> ""%LOGFILE%""
echo [%date% %time%] [BAT] Destino: %DST% >> ""%LOGFILE%""

REM ====== 1. AGUARDA O PROCESSO ENCERRAR (polling com tasklist) ======
echo [%date% %time%] [BAT] Aguardando processo %APPPID% encerrar... >> ""%LOGFILE%""
set /a waitcount=0
:waitloop
tasklist /FI ""PID eq %APPPID%"" 2>nul | find ""%APPPID%"" >nul 2>&1
if not errorlevel 1 (
    set /a waitcount+=1
    echo [%date% %time%] [BAT] Processo %APPPID% ainda ativo ^(tentativa !waitcount!^) >> ""%LOGFILE%""
    if !waitcount! gtr 60 (
        echo [%date% %time%] [BAT] AVISO: Forcando encerramento do processo %APPPID% >> ""%LOGFILE%""
        taskkill /F /PID %APPPID% >nul 2>&1
    )
    ping 127.0.0.1 -n 2 >nul
    goto waitloop
)
echo [%date% %time%] [BAT] Processo %APPPID% encerrado com sucesso >> ""%LOGFILE%""

REM Aguarda handles serem liberados pelo SO
ping 127.0.0.1 -n 4 >nul
echo [%date% %time%] [BAT] Aguardou 3 segundos adicionais para liberacao de handles >> ""%LOGFILE%""

REM ====== 2. VERIFICA QUE O ARQUIVO BAIXADO EXISTE ======
if not exist ""%SRC%"" (
    echo [%date% %time%] [BAT] ERRO CRITICO: Arquivo fonte nao encontrado: %SRC% >> ""%LOGFILE%""
    goto endscript
)
echo [%date% %time%] [BAT] Arquivo fonte verificado: %SRC% >> ""%LOGFILE%""

REM ====== 3. RENOMEIA O EXECUTAVEL ANTIGO (seguro em NTFS) ======
if exist ""%OLD%"" (
    del /f /q ""%OLD%"" >nul 2>&1
    echo [%date% %time%] [BAT] Removido .old anterior >> ""%LOGFILE%""
)

if exist ""%DST%"" (
    echo [%date% %time%] [BAT] Tentando renomear executavel antigo para .old... >> ""%LOGFILE%""
    move /y ""%DST%"" ""%OLD%"" >nul 2>&1
    if errorlevel 1 (
        echo [%date% %time%] [BAT] AVISO: Rename falhou, tentando copiar diretamente sobre o arquivo... >> ""%LOGFILE%""
    ) else (
        echo [%date% %time%] [BAT] Executavel antigo renomeado para .old >> ""%LOGFILE%""
    )
)

REM ====== 4. COPIA O NOVO EXECUTAVEL (com retentativas) ======
set /a tentativa=0
:copyloop
set /a tentativa+=1
echo [%date% %time%] [BAT] Tentativa !tentativa! de copiar novo executavel... >> ""%LOGFILE%""

copy /y ""%SRC%"" ""%DST%"" >nul 2>&1
if errorlevel 1 (
    echo [%date% %time%] [BAT] FALHA na tentativa !tentativa! >> ""%LOGFILE%""
    if !tentativa! lss 30 (
        ping 127.0.0.1 -n 3 >nul
        goto copyloop
    )
    echo [%date% %time%] [BAT] ERRO CRITICO: Nao foi possivel copiar apos 30 tentativas >> ""%LOGFILE%""
    REM Restaura o executavel antigo se possivel
    if exist ""%OLD%"" (
        echo [%date% %time%] [BAT] Restaurando executavel antigo... >> ""%LOGFILE%""
        move /y ""%OLD%"" ""%DST%"" >nul 2>&1
    )
    goto endscript
)

echo [%date% %time%] [BAT] SUCESSO: Novo executavel copiado na tentativa !tentativa! >> ""%LOGFILE%""

REM ====== 5. VERIFICA QUE O ARQUIVO FOI COPIADO ======
if not exist ""%DST%"" (
    echo [%date% %time%] [BAT] ERRO: Arquivo destino nao existe apos a copia! >> ""%LOGFILE%""
    if exist ""%OLD%"" move /y ""%OLD%"" ""%DST%"" >nul 2>&1
    goto endscript
)
echo [%date% %time%] [BAT] Arquivo destino confirmado no disco >> ""%LOGFILE%""

REM ====== 6. INICIA A NOVA VERSAO ======
ping 127.0.0.1 -n 2 >nul
echo [%date% %time%] [BAT] Iniciando nova versao: %DST% >> ""%LOGFILE%""
for %%I in (""%DST%"") do set ""APPDIR=%%~dpI""
cd /d ""!APPDIR!""
start """" /d ""!APPDIR!"" ""%DST%""
if errorlevel 1 (
    echo [%date% %time%] [BAT] AVISO: start direto falhou, tentando fallback com explorer... >> ""%LOGFILE%""
    explorer.exe ""%DST%""
)
echo [%date% %time%] [BAT] Comando de inicio executado >> ""%LOGFILE%""

REM ====== 7. LIMPEZA ======
:endscript
ping 127.0.0.1 -n 4 >nul
echo [%date% %time%] [BAT] Limpando arquivos temporarios... >> ""%LOGFILE%""
del /f /q ""%SRC%"" >nul 2>&1
if exist ""%OLD%"" del /f /q ""%OLD%"" >nul 2>&1
echo [%date% %time%] [BAT] === SCRIPT DE ATUALIZACAO FINALIZADO === >> ""%LOGFILE%""
REM Auto-deleta o script
(goto) 2>nul & del /f /q ""%~f0""
";

            WriteUpdateLog($"Escrevendo script de atualizacao em: {updaterCmd}", log);
            File.WriteAllText(updaterCmd, batContent, System.Text.Encoding.Default);

            // Verifica que o script foi criado
            if (!File.Exists(updaterCmd))
            {
                WriteUpdateLog("ERRO CRITICO: Script .cmd nao foi criado no disco!", log);
                throw new InvalidOperationException("Falha ao criar script de atualização.");
            }
            WriteUpdateLog($"Script criado com sucesso ({new FileInfo(updaterCmd).Length} bytes)", log);

            // ═══════════════════ INICIA O SCRIPT COM ELEVAÇÃO TOTAL ═══════════════════
            var psi = new ProcessStartInfo
            {
                FileName = updaterCmd,
                UseShellExecute = true,      // Executa de forma desacoplada
                Verb = "runas",              // Garante permissões administrativas
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(updaterCmd) ?? ""
            };

            WriteUpdateLog("Iniciando script de atualizacao com elevacao...", log);

            Process? scriptProcess = null;
            try
            {
                scriptProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                WriteUpdateLog($"AVISO: Falha ao iniciar diretamente ({ex.Message}), tentando fallback com cmd.exe...", log);
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c call \"{updaterCmd}\"";
                scriptProcess = Process.Start(psi);
            }

            if (scriptProcess == null)
            {
                WriteUpdateLog("ERRO CRITICO: Process.Start retornou null! Script nao foi iniciado.", log);
                throw new InvalidOperationException("Não foi possível iniciar o script de atualização.");
            }

            WriteUpdateLog($"Script iniciado com sucesso (PID: {scriptProcess.Id})", log);

            // Dá um momento para o script iniciar antes de encerrar o app
            await Task.Delay(1000);

            WriteUpdateLog("Encerrando aplicativo para permitir substituicao do executavel...", log);

            // Encerra o aplicativo para liberar o arquivo imediatamente
            try
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    System.Windows.Application.Current.Shutdown();
                });
            }
            catch { }

            Environment.Exit(0);
        }
    }
}
