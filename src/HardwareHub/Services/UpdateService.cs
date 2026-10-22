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

        public async Task DownloadAndInstallUpdateAsync(string downloadUrl, IProgress<double> progress, Action<string>? log = null)
        {
            string currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(currentExePath))
            {
                throw new InvalidOperationException("Não foi possível identificar o caminho do executável atual para substituição.");
            }

            string tempUpdateExe = Path.Combine(Path.GetTempPath(), $"HardwareHub_Update_{Guid.NewGuid():N}.exe");

            log?.Invoke("[ATUALIZAÇÃO] Conectando ao servidor para baixar a nova versão...");

            using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempUpdateExe, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
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
            }

            log?.Invoke("[ATUALIZAÇÃO] Download finalizado. Preparando substituição do executável...");

            // Gera script auxiliar em PowerShell com fallback e log detalhado para garantir a substituição e reinicialização no Windows 10/11
            string updaterPs1 = Path.Combine(Path.GetTempPath(), $"update_{Guid.NewGuid():N}.ps1");
            int currentPid = Environment.ProcessId;
            string destExe = currentExePath;
            string destFileName = Path.GetFileName(destExe);
            string oldDestExe = currentExePath + ".old";

            string ps1Content = $@"# HardwareHub Auto-Updater
$logFile = ""$env:TEMP\HardwareHub_Update.log""
function Write-Log($m) {{
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -Path $logFile -Value ""[$ts] $m"" -ErrorAction SilentlyContinue
}}

Write-Log ""=======================================================""
Write-Log ""Iniciando atualizacao do HardwareHub...""
Write-Log ""PID anterior: {currentPid}""
Write-Log ""Executavel destino: {destExe}""
Write-Log ""Novo binario baixado: {tempUpdateExe}""

# 1. Aguarda o processo anterior encerrar completamente
try {{
    $proc = Get-Process -Id {currentPid} -ErrorAction SilentlyContinue
    if ($proc) {{
        Write-Log ""Aguardando processo {currentPid} encerrar...""
        $proc.WaitForExit(10000)
    }}
}} catch {{
    Write-Log ""Processo anterior ja finalizado ou sem permissao: $_""
}}

Start-Sleep -Seconds 1

# 2. Substituicao com ate 30 tentativas
$dest = ""{destExe}""
$oldDest = ""{oldDestExe}""
$copied = $false

for ($i = 1; $i -le 30; $i++) {{
    try {{
        if (Test-Path $dest) {{
            Remove-Item -Path $oldDest -Force -ErrorAction SilentlyContinue
            Rename-Item -Path $dest -NewName ([System.IO.Path]::GetFileName($oldDest)) -Force -ErrorAction SilentlyContinue
        }}
        Copy-Item -Path ""{tempUpdateExe}"" -Destination $dest -Force -ErrorAction Stop
        Write-Log ""Executavel substituido com sucesso na tentativa $i.""
        $copied = $true
        break
    }} catch {{
        Write-Log ""Tentativa $i falhou: $_. Aguardando 1s...""
        Start-Sleep -Seconds 1
    }}
}}

# 3. Executa a nova versao atualizada
if ($copied) {{
    Write-Log ""Iniciando versao atualizada: $dest""
    try {{
        Start-Process -FilePath $dest
    }} catch {{
        Write-Log ""Falha com Start-Process ($_); tentando iniciar via cmd.exe...""
        Start-Process ""cmd.exe"" -ArgumentList ""/c start """" """" `""$dest`""""""
    }}
}} else {{
    Write-Log ""ERRO CRITICO: Nao foi possivel copiar a nova versao apos 30 tentativas.""
}}

# 4. Limpeza de arquivos temporarios
Start-Sleep -Seconds 2
Remove-Item -Path ""{tempUpdateExe}"" -Force -ErrorAction SilentlyContinue
Remove-Item -Path $oldDest -Force -ErrorAction SilentlyContinue
Write-Log ""Processo de atualizacao finalizado.""
Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue
";

            File.WriteAllText(updaterPs1, ps1Content, System.Text.Encoding.UTF8);

            string powershellExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershellExe))
            {
                powershellExe = "powershell.exe";
            }

            var psi = new ProcessStartInfo
            {
                FileName = powershellExe,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{updaterPs1}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);

            // Encerra imediatamente o aplicativo atual para liberar o arquivo para substituição
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });

            Environment.Exit(0);
        }
    }
}
