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

            // Gera script auxiliar em batch para substituir o executável bloqueado e reabrir o app
            string updaterBat = Path.Combine(Path.GetTempPath(), $"update_{Guid.NewGuid():N}.bat");
            int currentPid = Environment.ProcessId;

            string batContent = $@"@echo off
setlocal
timeout /t 2 /nobreak > nul
taskkill /F /PID {currentPid} > nul 2>&1
timeout /t 1 /nobreak > nul

set /a retries=0

:copy_loop
copy /y ""{tempUpdateExe}"" ""{currentExePath}"" > nul 2>&1
if errorlevel 1 (
    set /a retries+=1
    if %retries% lss 30 (
        timeout /t 1 /nobreak > nul
        goto copy_loop
    )
    exit /b 1
)

timeout /t 1 /nobreak > nul
start """" ""{currentExePath}""
del ""{tempUpdateExe}"" > nul 2>&1
del ""%~f0"" > nul 2>&1
";

            File.WriteAllText(updaterBat, batContent);

            var psi = new ProcessStartInfo
            {
                FileName = updaterBat,
                UseShellExecute = true,
                Verb = "runas",
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
