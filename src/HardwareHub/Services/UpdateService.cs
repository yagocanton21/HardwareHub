using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
        public string AssetName { get; set; } = "";
        public string Sha256SumsUrl { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
        public long AssetSizeBytes { get; set; }
        public string FormattedSize => AssetSizeBytes > 0 ? $"{AssetSizeBytes / (1024.0 * 1024.0):F1} MB" : "";
    }

    public class UpdateService
    {
        private const string GitHubRepo = "yagocanton21/HardwareHub";
        private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
        private const string GitHubWebLatestUrl = $"https://github.com/{GitHubRepo}/releases/latest";
        private const string RawChangelogJsonUrl = $"https://raw.githubusercontent.com/{GitHubRepo}/main/src/HardwareHub/Resources/Data/novidades.json";

        private readonly HttpClient _httpClient;
        private readonly HttpClient _noRedirectClient;

        public UpdateService(HttpMessageHandler? apiAndCdnHandler = null, HttpMessageHandler? noRedirectHandler = null)
        {
            _httpClient = apiAndCdnHandler != null
                ? new HttpClient(apiAndCdnHandler, disposeHandler: false)
                : new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HardwareHub", "1.0"));
            _httpClient.Timeout = TimeSpan.FromSeconds(20);

            _noRedirectClient = noRedirectHandler != null
                ? new HttpClient(noRedirectHandler, disposeHandler: false)
                : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            _noRedirectClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HardwareHub", "1.0"));
            _noRedirectClient.Timeout = TimeSpan.FromSeconds(15);
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

        public Task<UpdateInfo> CheckForUpdatesAsync()
        {
            return CheckForUpdatesAsync(GetCurrentVersion());
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync(Version currentVer)
        {
            var info = new UpdateInfo
            {
                CurrentVersion = currentVer.ToString(3)
            };

            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                info.ErrorMessage = "Sem conexão com a internet no momento.";
                return info;
            }

            try
            {
                var response = await _httpClient.GetAsync(GitHubApiUrl);
                if (!response.IsSuccessStatusCode)
                {
                    if (await TryPopulateViaWebFallbackAsync(info, currentVer))
                    {
                        return info;
                    }

                    info.ErrorMessage = response.StatusCode == HttpStatusCode.NotFound
                        ? "Nenhuma release publicada encontrada no GitHub."
                        : $"Servidor de atualizações retornou HTTP {(int)response.StatusCode}.";
                    return info;
                }

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string rawTag = root.GetProperty("tag_name").GetString() ?? "";
                string cleanTag = Regex.Replace(rawTag, @"^[^\d]*", "").Trim();
                Version? latestVer = ParseVersionFlexible(cleanTag);

                info.LatestVersion = cleanTag;
                info.Changelog = root.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? "") : "";

                // Procura o executável e o manifesto SHA256SUMS.txt anexados na release
                if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsEl.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (string.IsNullOrEmpty(info.DownloadUrl) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            info.AssetName = name;
                            info.DownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            info.AssetSizeBytes = asset.GetProperty("size").GetInt64();
                        }
                        else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            info.Sha256SumsUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        }
                    }
                }

                if (latestVer != null && latestVer > currentVer && !string.IsNullOrEmpty(info.DownloadUrl))
                {
                    info.IsAvailable = true;
                }
            }
            catch (TaskCanceledException)
            {
                if (!await TryPopulateViaWebFallbackAsync(info, currentVer))
                {
                    info.IsAvailable = false;
                    info.ErrorMessage = "Tempo limite esgotado ao consultar atualizações.";
                }
            }
            catch (HttpRequestException ex)
            {
                if (!await TryPopulateViaWebFallbackAsync(info, currentVer))
                {
                    info.IsAvailable = false;
                    info.ErrorMessage = $"Falha de rede ao consultar atualizações: {ex.Message}";
                }
            }
            catch (Exception ex)
            {
                if (!await TryPopulateViaWebFallbackAsync(info, currentVer))
                {
                    info.IsAvailable = false;
                    info.ErrorMessage = $"Falha ao verificar atualizações: {ex.Message}";
                }
            }

            return info;
        }

        /// <summary>
        /// Plano B livre do limite de 60 req/h da REST API: lê o redirecionamento HTTP 302 de
        /// github.com/.../releases/latest, obtém tamanho via HEAD CDN e busca notas em raw.githubusercontent.com.
        /// </summary>
        private async Task<bool> TryPopulateViaWebFallbackAsync(UpdateInfo info, Version currentVer)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, GitHubWebLatestUrl);
                using var resp = await _noRedirectClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

                string? targetUrl = resp.Headers.Location?.ToString();
                if (string.IsNullOrWhiteSpace(targetUrl))
                {
                    targetUrl = resp.RequestMessage?.RequestUri?.ToString();
                }

                var (tagName, cleanVersion, latestVer) = ParseReleaseTagFromUrl(targetUrl);
                if (string.IsNullOrWhiteSpace(tagName) || latestVer == null)
                    return false;

                info.LatestVersion = cleanVersion;
                info.AssetName = "HardwareHub.exe";
                info.DownloadUrl = $"https://github.com/{GitHubRepo}/releases/download/{tagName}/HardwareHub.exe";
                info.Sha256SumsUrl = string.Empty;
                info.ErrorMessage = string.Empty;

                if (latestVer > currentVer)
                {
                    info.IsAvailable = true;
                    await TryEnrichFallbackMetadataAsync(info, cleanVersion);
                }
                else
                {
                    info.IsAvailable = false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task TryEnrichFallbackMetadataAsync(UpdateInfo info, string cleanVersion)
        {
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, info.DownloadUrl);
                using var headResp = await _httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead);
                if (headResp.IsSuccessStatusCode && headResp.Content.Headers.ContentLength is long len && len > 0)
                {
                    info.AssetSizeBytes = len;
                }
            }
            catch { }

            try
            {
                string rawJson = await _httpClient.GetStringAsync(RawChangelogJsonUrl);
                string markdown = FormatChangelogMarkdownFromJson(rawJson, cleanVersion);
                if (!string.IsNullOrWhiteSpace(markdown))
                {
                    info.Changelog = markdown;
                }
            }
            catch { }
        }

        internal static (string TagName, string CleanVersion, Version? ParsedVersion) ParseReleaseTagFromUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return (string.Empty, string.Empty, null);

            var match = Regex.Match(url, @"/releases/tag/([^/?#\s]+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                return (string.Empty, string.Empty, null);

            string tag = Uri.UnescapeDataString(match.Groups[1].Value.Trim());
            string clean = Regex.Replace(tag, @"^[^\d]*", "").Trim();
            return (tag, clean, ParseVersionFlexible(clean));
        }

        internal static Version? ParseVersionFlexible(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string trimmed = raw.Trim();
            if (Version.TryParse(trimmed, out var parsed) && parsed != null)
                return parsed;

            if (Version.TryParse(trimmed + ".0", out var padded) && padded != null)
                return padded;

            return null;
        }

        internal static string FormatChangelogMarkdownFromJson(string json, string targetVersion)
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<VersionReleaseNotes>>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (entries == null || entries.Count == 0)
                    return string.Empty;

                var match = entries.FirstOrDefault(e =>
                    string.Equals(e.Version?.Trim().TrimStart('v', 'V'), targetVersion.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase))
                    ?? entries[0];

                var sb = new StringBuilder();
                if (match.Novidades.Count > 0)
                {
                    sb.AppendLine("### Novidades");
                    foreach (var n in match.Novidades) sb.AppendLine($"- {n}");
                    sb.AppendLine();
                }
                if (match.Melhorias.Count > 0)
                {
                    sb.AppendLine("### Melhorias");
                    foreach (var m in match.Melhorias) sb.AppendLine($"- {m}");
                    sb.AppendLine();
                }
                if (match.Correcoes.Count > 0)
                {
                    sb.AppendLine("### Correções");
                    foreach (var c in match.Correcoes) sb.AppendLine($"- {c}");
                }
                return sb.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
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

        public async Task DownloadAndInstallUpdateAsync(
            string downloadUrl,
            IProgress<double> progress,
            Action<string>? log = null,
            string? sha256SumsUrl = null,
            string? expectedAssetName = null)
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

            // ═══════════════════ SCRIPT DE ATUALIZAÇÃO (POWERSHELL) ═══════════════════
            // Os caminhos são codificados em Base64 para preservar acentos, espaços,
            // aspas simples e quaisquer caracteres do perfil do usuário.
            string updaterPs1 = Path.Combine(Path.GetTempPath(), "HardwareHub_Updater.ps1");
            int currentPid = Environment.ProcessId;
            string oldExePath = currentExePath + ".old";

            string ToBase64(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
            string psContent = $@"$ErrorActionPreference = 'Stop'
$log = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{ToBase64(UpdateLogPath)}'))
$source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{ToBase64(tempUpdateExe)}'))
$destination = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{ToBase64(currentExePath)}'))
$backup = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{ToBase64(oldExePath)}'))
$appPid = {currentPid}

function Write-UpdateLog([string]$message) {{
    Add-Content -LiteralPath $log -Value (('[{{0:yyyy-MM-dd HH:mm:ss}}] [PS] {{1}}' -f (Get-Date), $message)) -Encoding UTF8
}}

try {{
    Write-UpdateLog 'SCRIPT DE ATUALIZACAO INICIADO'
    Write-UpdateLog ('Destino: ' + $destination)
    $process = Get-Process -Id $appPid -ErrorAction SilentlyContinue
    if ($null -ne $process) {{
        Write-UpdateLog ('Aguardando processo ' + $appPid + ' encerrar...')
        Wait-Process -Id $appPid -Timeout 120 -ErrorAction SilentlyContinue
    }}
    Start-Sleep -Seconds 2
    if (-not (Test-Path -LiteralPath $source)) {{ throw 'Arquivo baixado não encontrado.' }}

    $updated = $false
    for ($attempt = 1; $attempt -le 30 -and -not $updated; $attempt++) {{
        try {{
            if (Test-Path -LiteralPath $backup) {{ Remove-Item -LiteralPath $backup -Force }}
            if (Test-Path -LiteralPath $destination) {{ Move-Item -LiteralPath $destination -Destination $backup -Force }}
            Copy-Item -LiteralPath $source -Destination $destination -Force
            $updated = Test-Path -LiteralPath $destination
        }} catch {{
            Write-UpdateLog ('Tentativa ' + $attempt + ' falhou: ' + $_.Exception.Message)
            Start-Sleep -Seconds 2
        }}
    }}

    if (-not $updated) {{
        if ((-not (Test-Path -LiteralPath $destination)) -and (Test-Path -LiteralPath $backup)) {{ Move-Item -LiteralPath $backup -Destination $destination -Force }}
        throw 'Não foi possível substituir o executável após 30 tentativas.'
    }}

    Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    Write-UpdateLog 'SUCESSO: executável atualizado. Iniciando nova versão.'
    Start-Process -FilePath $destination -WorkingDirectory (Split-Path -LiteralPath $destination)
}} catch {{
    Write-UpdateLog ('ERRO CRITICO: ' + $_.Exception.Message)
}}
";

            WriteUpdateLog($"Escrevendo script de atualizacao em: {updaterPs1}", log);
            File.WriteAllText(updaterPs1, psContent, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // Verifica que o script foi criado
            if (!File.Exists(updaterPs1))
            {
                WriteUpdateLog("ERRO CRITICO: Script PowerShell de atualização não foi criado no disco!", log);
                throw new InvalidOperationException("Falha ao criar script de atualização.");
            }
            WriteUpdateLog($"Script criado com sucesso ({new FileInfo(updaterPs1).Length} bytes)", log);

            // ═══════════════════ INICIA O SCRIPT COM ELEVAÇÃO TOTAL ═══════════════════
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{updaterPs1}\"",
                UseShellExecute = true,      // Executa de forma desacoplada
                Verb = "runas",              // Garante permissões administrativas
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(updaterPs1) ?? ""
            };

            WriteUpdateLog("Iniciando script de atualizacao com elevacao...", log);

            Process? scriptProcess = null;
            try
            {
                scriptProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                WriteUpdateLog($"AVISO: Falha ao iniciar elevado ({ex.Message}), tentando fallback sem elevação...", log);
                psi.UseShellExecute = false;
                psi.Verb = "";
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

        private async Task<string?> FetchExpectedSha256Async(string sha256SumsUrl, string assetName, Action<string>? log)
        {
            try
            {
                string body = await _httpClient.GetStringAsync(sha256SumsUrl);
                foreach (string rawLine in body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = rawLine.Trim();
                    var parts = Regex.Split(line, @"\s+");
                    if (parts.Length >= 2 && parts[0].Length == 64)
                    {
                        string listedName = parts[^1].TrimStart('*');
                        if (listedName.Equals(assetName, StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(listedName).Equals(assetName, StringComparison.OrdinalIgnoreCase))
                        {
                            return parts[0];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteUpdateLog($"AVISO: Não foi possível obter SHA256SUMS.txt ({ex.Message}).", log);
            }

            return null;
        }
    }
}
