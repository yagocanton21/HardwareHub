using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    /// <summary>
    /// Diagnóstico não-destrutivo de saúde do Windows (DISM CheckHealth/ScanHealth, SFC verifyonly,
    /// repositório WMI, dirty bit de disco, relógio NTP e reinício pendente) + passos cirúrgicos extras
    /// de reparo (Relógio, WMI, Cache de Ícones, Reset de Rede e Agendamento de CHKDSK no boot).
    /// </summary>
    public sealed class WindowsHealthRepairService
    {
        public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(2);
        private static readonly string[] NtpServers = { "time.windows.com", "a.st1.ntp.br", "pool.ntp.org" };

        private static readonly Regex PercentageRegex = new(@"(\d{1,3})(?:[.,]\d+)?\s*%", RegexOptions.Compiled);

        private readonly Func<string, string[], bool, Action<int>?, CancellationToken, Task<(int ExitCode, string Output, string Error)>>? _processRunner;
        private readonly Func<CancellationToken, Task<TimeSpan?>>? _clockDeltaHook;
        private readonly Func<bool>? _pendingRebootHook;
        private readonly Func<bool>? _dirtyBitHook;
        private readonly Func<bool>? _isAdminHook;

        static WindowsHealthRepairService()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public WindowsHealthRepairService(
            Func<string, string[], bool, Action<int>?, CancellationToken, Task<(int ExitCode, string Output, string Error)>>? processRunner = null,
            Func<CancellationToken, Task<TimeSpan?>>? clockDeltaHook = null,
            Func<bool>? pendingRebootHook = null,
            Func<bool>? dirtyBitHook = null,
            Func<bool>? isAdminHook = null)
        {
            _processRunner = processRunner;
            _clockDeltaHook = clockDeltaHook;
            _pendingRebootHook = pendingRebootHook;
            _dirtyBitHook = dirtyBitHook;
            _isAdminHook = isAdminHook;
        }

        public bool IsRunningAsAdministrator()
        {
            if (_isAdminHook != null) return _isAdminHook();
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static IReadOnlyList<RepairStepDefinition> CreateAdvancedRepairSteps() => new List<RepairStepDefinition>
        {
            new()
            {
                Id = "clock",
                Icon = "🕒",
                IsRecommended = true,
                EstimatedTime = "< 1 min",
                Title = "Sincronizar Data, Hora & Serviço W32Time",
                Description = "Ativa o ajuste automático de horário e sincroniza via NTP (evita falhas de certificado HTTPS, Windows Update e ativação). Não altera o fuso.",
                CommandDisplay = "sc config w32time / w32tm /resync /force"
            },
            new()
            {
                Id = "wmi",
                Icon = "🗄️",
                IsRecommended = true,
                EstimatedTime = "< 1 min",
                Title = "Verificar & Recuperar Banco WMI",
                Description = "Verifica a integridade do repositório WMI. Só executa /salvagerepository se inconsistência real for detectada.",
                CommandDisplay = "winmgmt /verifyrepository (e /salvagerepository se necessário)"
            },
            new()
            {
                Id = "icon_cache",
                Icon = "🖼️",
                IsRecommended = false,
                EstimatedTime = "< 1 min",
                Title = "Recriar Cache de Ícones & Miniaturas",
                Description = "Resolve ícones em branco ou trocados removendo iconcache_*.db e thumbcache_*.db e reiniciando o Explorer rapidamente.",
                CommandDisplay = "Restart explorer + limpar iconcache_*.db / thumbcache_*.db"
            },
            new()
            {
                Id = "network_reset",
                Icon = "🌐",
                IsRecommended = false,
                EstimatedTime = "< 1 min",
                RequiresReboot = true,
                Title = "Resetar Pilha Winsock / TCP-IP",
                Description = "Restaura catálogo Winsock, TCP/IP e limpa DNS para corrigir quedas ou erro de protocolo sem mexer no Firewall.",
                CommandDisplay = "netsh winsock reset + netsh int ip reset + ipconfig /flushdns",
                CautionNote = "Remove IP fixo configurado manualmente e pede reinício do PC."
            },
            new()
            {
                Id = "chkdsk_boot",
                Icon = "💿",
                IsRecommended = false,
                EstimatedTime = "No boot",
                RequiresReboot = true,
                Title = "Agendar Correção de Disco C: no Próximo Boot",
                Description = "Agenda chkdsk C: /f para corrigir setores e índices do sistema de arquivos antes do Windows carregar.",
                CommandDisplay = "chkdsk C: /f",
                CautionNote = "Pode demorar bastante em HDs grandes. Use 'Reiniciar' (não 'Desligar')."
            }
        };

        public async Task<WindowsHealthReport> RunHealthCheckAsync(
            bool fullScan,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var report = new WindowsHealthReport
            {
                CheckedAt = DateTime.Now,
                IsFullScan = fullScan,
                PendingReboot = CheckPendingReboot()
            };

            if (fullScan)
            {
                progress?.Report("Conferindo imagem do Windows por completo (DISM /ScanHealth)...");
                await RunProcessAsync(
                    GetSystemToolPath("dism.exe"),
                    new[] { "/Online", "/Cleanup-Image", "/ScanHealth" },
                    unicode: false,
                    pct => progress?.Report($"DISM /ScanHealth... {pct}%"),
                    ct);
            }
            else
            {
                progress?.Report("Checando estado rápido da imagem do Windows...");
            }

            var dismRes = await RunProcessAsync(
                "powershell.exe",
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "$ProgressPreference='SilentlyContinue'; (Repair-WindowsImage -Online -CheckHealth).ImageHealthState" },
                unicode: false,
                null,
                ct);

            string? stateToken = dismRes.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();

            report.ImageHealthState = dismRes.ExitCode == 0 &&
                (stateToken is "Healthy" or "Repairable" or "NonRepairable")
                ? stateToken
                : null;

            progress?.Report("Conferindo sincronia do relógio (NTP)...");
            try
            {
                report.ClockDelta = await GetClockDeltaAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            progress?.Report("Verificando banco de dados WMI...");
            var wmiRes = await RunProcessAsync(
                GetSystemToolPath(@"wbem\winmgmt.exe"),
                new[] { "/verifyrepository" },
                unicode: false,
                null,
                ct);
            report.WmiStatus = InterpretWmiVerification(wmiRes.ExitCode, wmiRes.Output + " " + wmiRes.Error);
            report.HasSystemDriveDirtyBit = CheckSystemDriveDirtyBit();

            if (fullScan)
            {
                progress?.Report("Verificando integridade dos arquivos protegidos (sfc /verifyonly)...");
                var sfcRes = await RunProcessAsync(
                    GetSystemToolPath("sfc.exe"),
                    new[] { "/verifyonly" },
                    unicode: true,
                    pct => progress?.Report($"SFC /verifyonly... {pct}%"),
                    ct);
                report.SfcFoundCorruption = DetectSfcVerifyCorruption(sfcRes.Output + " " + sfcRes.Error);

                string sysRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\').ToUpperInvariant();
                progress?.Report($"Conferindo integridade online do volume {sysRoot} (chkdsk /scan)...");
                var chkRes = await RunProcessAsync(
                    GetSystemToolPath("chkdsk.exe"),
                    new[] { sysRoot, "/scan" },
                    unicode: false,
                    null,
                    ct);
                if (chkRes.Output.Contains("found problems", StringComparison.OrdinalIgnoreCase) ||
                    chkRes.Output.Contains("encontrou problemas", StringComparison.OrdinalIgnoreCase) ||
                    chkRes.ExitCode > 1)
                {
                    report.HasSystemDriveDirtyBit = true;
                }
            }

            PopulateReportItems(report);
            return report;
        }

        public async Task<RepairStepResult> ExecuteAdvancedStepAsync(
            RepairStepDefinition step,
            Action<string>? log = null,
            CancellationToken ct = default)
        {
            log?.Invoke($"===== {step.Title} =====");
            log?.Invoke($"> {step.CommandDisplay}");

            return step.Id switch
            {
                "clock" => await RepairClockStepAsync(step, log, ct),
                "wmi" => await RepairWmiStepAsync(step, log, ct),
                "icon_cache" => await RebuildIconCacheStepAsync(step, log, ct),
                "network_reset" => await ResetNetworkStackStepAsync(step, log, ct),
                "chkdsk_boot" => await ScheduleChkdskBootStepAsync(step, log, ct),
                _ => new RepairStepResult(step.Id, step.Title, WindowsHealthStatus.Unknown, "Passo desconhecido.")
            };
        }

        private async Task<RepairStepResult> RepairClockStepAsync(RepairStepDefinition step, Action<string>? log, CancellationToken ct)
        {
            if (!IsRunningAsAdministrator())
                return new(step.Id, step.Title, WindowsHealthStatus.Attention, "Requer executar o HUB como Administrador.");

            var delta = await GetClockDeltaAsync(ct);
            if (delta.HasValue && delta.Value.Duration() < ClockTolerance)
            {
                log?.Invoke($"Relógio já está sincronizado (diferença: {delta.Value.TotalSeconds:0.0}s).");
                return new(step.Id, step.Title, WindowsHealthStatus.Healthy, $"Relógio já estava certo ({TimeZoneInfo.Local.DisplayName}).");
            }

            await RunProcessAsync(GetSystemToolPath("sc.exe"), new[] { "config", "w32time", "start=", "demand" }, false, null, ct);
            await RunProcessAsync(GetSystemToolPath("sc.exe"), new[] { "start", "w32time" }, false, null, ct);
            await RunProcessAsync(GetSystemToolPath("w32tm.exe"), new[] { "/config", "/update" }, false, null, ct);
            var resync = await RunProcessAsync(GetSystemToolPath("w32tm.exe"), new[] { "/resync", "/force" }, false, null, ct);
            log?.Invoke(resync.Output.Trim());

            var after = await GetClockDeltaAsync(ct);
            if (after.HasValue && after.Value.Duration() < ClockTolerance)
                return new(step.Id, step.Title, WindowsHealthStatus.Healthy, "Data e hora sincronizadas via NTP.");

            return resync.ExitCode == 0
                ? new(step.Id, step.Title, WindowsHealthStatus.Healthy, "Comando /resync concluído (confira internet caso NTP esteja bloqueado).")
                : new(step.Id, step.Title, WindowsHealthStatus.Attention, "O Windows não conseguiu sincronizar automaticamente.");
        }

        private async Task<RepairStepResult> RepairWmiStepAsync(RepairStepDefinition step, Action<string>? log, CancellationToken ct)
        {
            string exe = GetSystemToolPath(@"wbem\winmgmt.exe");
            var check = await RunProcessAsync(exe, new[] { "/verifyrepository" }, false, null, ct);
            string state = InterpretWmiVerification(check.ExitCode, check.Output + " " + check.Error);
            log?.Invoke($"winmgmt /verifyrepository => {state}");

            if (state == "Consistente")
                return new(step.Id, step.Title, WindowsHealthStatus.Healthy, "Repositório WMI consistente.");
            if (state == "SemPermissao")
                return new(step.Id, step.Title, WindowsHealthStatus.Attention, "Abra o HUB como Administrador para verificar/reparar o WMI.");
            if (state != "Inconsistente")
                return new(step.Id, step.Title, WindowsHealthStatus.Unknown, $"Não foi possível verificar o WMI (código {check.ExitCode}).");

            log?.Invoke("> winmgmt /salvagerepository");
            var salvage = await RunProcessAsync(exe, new[] { "/salvagerepository" }, false, null, ct);
            return salvage.ExitCode == 0
                ? new(step.Id, step.Title, WindowsHealthStatus.Healthy, "WMI inconsistente foi reconstruído com sucesso.", "Reinicie o PC para concluir.", RequiresReboot: true)
                : new(step.Id, step.Title, WindowsHealthStatus.Critical, $"Falha ao reconstruir WMI (código {salvage.ExitCode}).");
        }

        private async Task<RepairStepResult> RebuildIconCacheStepAsync(RepairStepDefinition step, Action<string>? log, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string explorerCacheDir = Path.Combine(localAppData, @"Microsoft\Windows\Explorer");
                var files = new List<string>();

                if (Directory.Exists(explorerCacheDir) && !IsReparsePoint(explorerCacheDir))
                {
                    foreach (string pattern in new[] { "iconcache_*.db", "thumbcache_*.db" })
                    {
                        files.AddRange(Directory.EnumerateFiles(explorerCacheDir, pattern, new System.IO.EnumerationOptions
                        {
                            RecurseSubdirectories = false,
                            AttributesToSkip = FileAttributes.ReparsePoint
                        }));
                    }
                }

                string legacyCache = Path.Combine(localAppData, "IconCache.db");
                if (File.Exists(legacyCache) && !IsReparsePoint(legacyCache))
                    files.Add(legacyCache);

                int sessionId = Process.GetCurrentProcess().SessionId;
                foreach (var proc in Process.GetProcessesByName("explorer").Where(p => p.SessionId == sessionId))
                {
                    try { proc.Kill(); proc.WaitForExit(4000); } catch { }
                    finally { proc.Dispose(); }
                }

                int deleted = 0, locked = 0;
                foreach (string file in files)
                {
                    try { File.Delete(file); deleted++; }
                    catch { locked++; }
                }

                if (!Process.GetProcessesByName("explorer").Any(p => p.SessionId == sessionId))
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
                }

                log?.Invoke($"Cache de ícones: {deleted} arquivo(s) removido(s), {locked} em uso.");
                return new RepairStepResult(step.Id, step.Title, WindowsHealthStatus.Healthy,
                    deleted > 0 ? $"{deleted} arquivo(s) de cache removidos; Explorer reiniciado." : "Nenhum cache removível encontrado agora.");
            }, ct);
        }

        private async Task<RepairStepResult> ResetNetworkStackStepAsync(RepairStepDefinition step, Action<string>? log, CancellationToken ct)
        {
            if (!IsRunningAsAdministrator())
                return new(step.Id, step.Title, WindowsHealthStatus.Attention, "Requer executar o HUB como Administrador.");

            var netsh = GetSystemToolPath("netsh.exe");
            var ipconfig = GetSystemToolPath("ipconfig.exe");

            var r1 = await RunProcessAsync(netsh, new[] { "winsock", "reset" }, false, null, ct);
            var r2 = await RunProcessAsync(netsh, new[] { "int", "ip", "reset" }, false, null, ct);
            var r3 = await RunProcessAsync(ipconfig, new[] { "/flushdns" }, false, null, ct);

            log?.Invoke($"winsock={r1.ExitCode}, int ip={r2.ExitCode}, flushdns={r3.ExitCode}");
            bool allOk = r1.ExitCode == 0 && r3.ExitCode == 0;
            return allOk
                ? new(step.Id, step.Title, WindowsHealthStatus.Healthy, "Winsock, TCP/IP e cache DNS restaurados.", "Reinicie o PC para aplicar.", RequiresReboot: true)
                : new(step.Id, step.Title, WindowsHealthStatus.Attention, "Algum comando de rede retornou aviso.", "Reinicie o PC e teste a conexão.", RequiresReboot: true);
        }

        private async Task<RepairStepResult> ScheduleChkdskBootStepAsync(RepairStepDefinition step, Action<string>? log, CancellationToken ct)
        {
            if (!IsRunningAsAdministrator())
                return new(step.Id, step.Title, WindowsHealthStatus.Attention, "Requer executar o HUB como Administrador.");

            string sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\').ToUpperInvariant();
            if (!Regex.IsMatch(sysDrive, "^[A-Z]:$"))
                return new(step.Id, step.Title, WindowsHealthStatus.Unknown, "Não foi possível identificar a letra da unidade do sistema.");

            var psi = new ProcessStartInfo(GetSystemToolPath("chkdsk.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(sysDrive);
            psi.ArgumentList.Add("/f");

            using var proc = Process.Start(psi);
            if (proc == null)
                return new(step.Id, step.Title, WindowsHealthStatus.Attention, "Não foi possível iniciar chkdsk.exe.");

            // Confirma 'S' / 'Y' para agendar na próxima reinicialização caso o volume esteja em uso.
            try
            {
                await proc.StandardInput.WriteLineAsync("S");
                await proc.StandardInput.WriteLineAsync("Y");
                proc.StandardInput.Close();
            }
            catch { }

            await proc.WaitForExitAsync(ct);
            string output = await proc.StandardOutput.ReadToEndAsync(ct);
            log?.Invoke(output.Trim());

            bool scheduled = IsBootChkdskScheduled(sysDrive);
            return scheduled
                ? new(step.Id, step.Title, WindowsHealthStatus.Healthy, $"CHKDSK {sysDrive} /f agendado para o próximo reinício.", "Use 'Reiniciar' no Windows.", RequiresReboot: true)
                : new(step.Id, step.Title, WindowsHealthStatus.Attention, $"Não confirmou agendamento de {sysDrive} /f (código {proc.ExitCode}).");
        }

        public static string InterpretWmiVerification(int exitCode, string? output)
        {
            string clean = RemoveDiacritics(output ?? string.Empty).ToLowerInvariant();
            if (clean.Contains("0x80041003") || clean.Contains("0x80070005") || clean.Contains("acesso negado") || clean.Contains("access denied"))
                return "SemPermissao";
            if (clean.Contains("inconsist") || clean.Contains("incoheren") || clean.Contains("0x8007054e"))
                return "Inconsistente";
            return exitCode == 0 ? "Consistente" : "Erro";
        }

        public static bool DetectSfcVerifyCorruption(string? text)
        {
            string clean = RemoveDiacritics(text ?? string.Empty).ToLowerInvariant();
            return clean.Contains("encontrou violacoes") ||
                   clean.Contains("found integrity violations") ||
                   clean.Contains("encontro infracciones de integridad");
        }

        public static string DescribeClockDelta(TimeSpan delta)
        {
            TimeSpan abs = delta.Duration();
            string amount = abs.TotalDays >= 1.0
                ? $"{(int)abs.TotalDays} dia(s)"
                : abs.TotalHours >= 1.0
                    ? $"{(int)abs.TotalHours} h {abs.Minutes} min"
                    : abs.TotalMinutes >= 1.0
                        ? $"{(int)abs.TotalMinutes} min"
                        : $"{(int)abs.TotalSeconds} s";

            return delta >= TimeSpan.Zero ? $"{amount} adiantado" : $"{amount} atrasado";
        }

        public async Task<TimeSpan?> GetClockDeltaAsync(CancellationToken ct = default)
        {
            if (_clockDeltaHook != null)
                return await _clockDeltaHook(ct);

            foreach (string server in NtpServers)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using var udp = new UdpClient();
                    udp.Connect(server, 123);

                    byte[] packet = new byte[48];
                    packet[0] = 0x1B;
                    DateTime t1 = DateTime.UtcNow;
                    await udp.SendAsync(packet, timeout.Token);
                    var recv = await udp.ReceiveAsync(timeout.Token);
                    DateTime t4 = DateTime.UtcNow;

                    if (recv.Buffer.Length >= 48)
                    {
                        ulong intPart = ((ulong)recv.Buffer[40] << 24) | ((ulong)recv.Buffer[41] << 16) | ((ulong)recv.Buffer[42] << 8) | recv.Buffer[43];
                        ulong fracPart = ((ulong)recv.Buffer[44] << 24) | ((ulong)recv.Buffer[45] << 16) | ((ulong)recv.Buffer[46] << 8) | recv.Buffer[47];
                        if (intPart != 0)
                        {
                            DateTime ntpUtc = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                .AddSeconds(intPart + fracPart / 4294967296.0);
                            return (t1 + (t4 - t1) / 2.0) - ntpUtc;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }

            return null;
        }

        private bool CheckPendingReboot()
        {
            if (_pendingRebootHook != null) return _pendingRebootHook();
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var cbs = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
                using var wu = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                return cbs != null || wu != null;
            }
            catch { return false; }
        }

        private bool CheckSystemDriveDirtyBit()
        {
            if (_dirtyBitHook != null) return _dirtyBitHook();
            try
            {
                string letter = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\').ToUpperInvariant();
                using var searcher = new ManagementObjectSearcher($"SELECT DirtyBitSet FROM Win32_Volume WHERE DriveLetter='{letter}'");
                using var col = searcher.Get();
                foreach (ManagementObject obj in col)
                {
                    if (obj["DirtyBitSet"] is bool dirty && dirty)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsBootChkdskScheduled(string letter)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var sm = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
                if (sm?.GetValue("BootExecute") is string[] entries)
                    return entries.Any(e => e.Contains("autocheck", StringComparison.OrdinalIgnoreCase) && e.Contains(letter, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            return false;
        }

        private static void PopulateReportItems(WindowsHealthReport report)
        {
            var items = new List<WindowsHealthCheckItem>();

            // 1. Imagem do Windows (DISM)
            var (imgStatus, imgText, imgDetail) = report.ImageHealthState switch
            {
                "Healthy" => (WindowsHealthStatus.Healthy, "Saudável", "Imagem de componentes íntegra (DISM CheckHealth/ScanHealth)."),
                "Repairable" => (WindowsHealthStatus.Attention, "Reparável", "Corrupção detectada na imagem. Execute 'Reparar Imagem (DISM)' em Manutenção."),
                "NonRepairable" => (WindowsHealthStatus.Critical, "Crítico", "Imagem não reparável automaticamente; considere restauração in-place do Windows."),
                _ => (WindowsHealthStatus.Unknown, "Indisponível", "Não foi possível consultar o estado da imagem agora.")
            };
            items.Add(new WindowsHealthCheckItem { Id = "image", Title = "Imagem do Windows (DISM)", Status = imgStatus, StatusText = imgText, Detail = imgDetail });

            // 2. Arquivos Protegidos (SFC)
            if (report.SfcFoundCorruption.HasValue)
            {
                items.Add(report.SfcFoundCorruption.Value
                    ? new WindowsHealthCheckItem { Id = "sfc", Title = "Arquivos do Sistema (SFC)", Status = WindowsHealthStatus.Attention, StatusText = "Violação encontrada", Detail = "sfc /verifyonly detectou arquivos alterados ou corrompidos." }
                    : new WindowsHealthCheckItem { Id = "sfc", Title = "Arquivos do Sistema (SFC)", Status = WindowsHealthStatus.Healthy, StatusText = "Íntegros", Detail = "Nenhuma violação encontrada em sfc /verifyonly." });
            }
            else
            {
                items.Add(new WindowsHealthCheckItem { Id = "sfc", Title = "Arquivos do Sistema (SFC)", Status = WindowsHealthStatus.Unknown, StatusText = "Não executado", Detail = "Disponível na Checagem Completa." });
            }

            // 3. Repositório WMI
            var wmiStatus = report.WmiStatus switch
            {
                "Consistente" => WindowsHealthStatus.Healthy,
                "Inconsistente" => WindowsHealthStatus.Critical,
                "SemPermissao" => WindowsHealthStatus.Attention,
                _ => WindowsHealthStatus.Unknown
            };
            items.Add(new WindowsHealthCheckItem
            {
                Id = "wmi",
                Title = "Banco de Dados WMI",
                Status = wmiStatus,
                StatusText = report.WmiStatus,
                Detail = report.HasWmiIssue ? "Repositório WMI inconsistente. Use o reparo 'Verificar & Recuperar Banco WMI'." : "Consulta WMI respondeu normalmente."
            });

            // 4. Relógio do Sistema
            if (report.ClockDelta.HasValue)
            {
                bool ok = report.ClockDelta.Value.Duration() < ClockTolerance;
                items.Add(new WindowsHealthCheckItem
                {
                    Id = "clock",
                    Title = "Relógio & Sincronia NTP",
                    Status = ok ? WindowsHealthStatus.Healthy : WindowsHealthStatus.Attention,
                    StatusText = ok ? "Sincronizado" : "Dessincronizado",
                    Detail = ok ? $"Dentro da tolerância ({DescribeClockDelta(report.ClockDelta.Value)})." : $"Diferença detectada: {DescribeClockDelta(report.ClockDelta.Value)}."
                });
            }
            else
            {
                items.Add(new WindowsHealthCheckItem { Id = "clock", Title = "Relógio & Sincronia NTP", Status = WindowsHealthStatus.Unknown, StatusText = "Sem rede NTP", Detail = "Não foi possível consultar servidores NTP na porta UDP 123." });
            }

            // 5. Disco C: DirtyBit + Reinício Pendente
            items.Add(report.HasSystemDriveDirtyBit
                ? new WindowsHealthCheckItem { Id = "dirty", Title = "Sistema de Arquivos (C:)", Status = WindowsHealthStatus.Critical, StatusText = "Erro detectado", Detail = "Dirty bit ou inconsistência reportada no volume do sistema." }
                : new WindowsHealthCheckItem { Id = "dirty", Title = "Sistema de Arquivos (C:)", Status = WindowsHealthStatus.Healthy, StatusText = "Limpo", Detail = "Volume do sistema sem dirty bit ativo." });

            items.Add(report.PendingReboot
                ? new WindowsHealthCheckItem { Id = "reboot", Title = "Reinicialização Pendente", Status = WindowsHealthStatus.Attention, StatusText = "Pendente", Detail = "Há atualizações aguardando reinício do computador." }
                : new WindowsHealthCheckItem { Id = "reboot", Title = "Reinicialização Pendente", Status = WindowsHealthStatus.Healthy, StatusText = "Nenhuma", Detail = "Nenhuma fila de reinício pendente." });

            report.Items = items;
            int alerts = items.Count(i => i.Status is WindowsHealthStatus.Attention or WindowsHealthStatus.Critical);
            report.SummaryMessage = alerts == 0
                ? $"Checagem {(report.IsFullScan ? "completa" : "rápida")} concluída às {report.CheckedAt:HH:mm}: tudo saudável."
                : $"Checagem {(report.IsFullScan ? "completa" : "rápida")} concluída às {report.CheckedAt:HH:mm}: {alerts} ponto(s) requerem atenção.";
        }

        private async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
            string exe,
            string[] args,
            bool unicode,
            Action<int>? onPercent,
            CancellationToken ct)
        {
            if (_processRunner != null)
                return await _processRunner(exe, args, unicode, onPercent, ct);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (unicode)
            {
                psi.StandardOutputEncoding = Encoding.Unicode;
                psi.StandardErrorEncoding = Encoding.Unicode;
            }
            foreach (string a in args) psi.ArgumentList.Add(a);

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return (-1, string.Empty, string.Empty);

                using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
                Task<string> outTask = onPercent == null
                    ? proc.StandardOutput.ReadToEndAsync(ct)
                    : ReadStreamWithPercentageAsync(proc.StandardOutput, onPercent, ct);
                Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);

                await proc.WaitForExitAsync(ct);
                return (proc.ExitCode, await outTask, await errTask);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (-1, string.Empty, ex.Message); }
        }

        private static async Task<string> ReadStreamWithPercentageAsync(StreamReader reader, Action<int> onPercent, CancellationToken ct)
        {
            var sb = new StringBuilder();
            char[] buffer = new char[512];
            int lastPct = -1;
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sb.Append(buffer, 0, read);
                string tail = sb.Length > 400 ? sb.ToString(sb.Length - 400, 400) : sb.ToString();
                var matches = PercentageRegex.Matches(tail);
                if (matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, out int pct) && pct is >= 0 and <= 100 && pct != lastPct)
                {
                    lastPct = pct;
                    onPercent(pct);
                }
            }
            return sb.ToString();
        }

        private static bool IsReparsePoint(string path)
        {
            try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
            catch { return true; }
        }

        private static string GetSystemToolPath(string relativePath)
            => Path.Combine(Environment.SystemDirectory, relativePath);

        private static string RemoveDiacritics(string text)
        {
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}

