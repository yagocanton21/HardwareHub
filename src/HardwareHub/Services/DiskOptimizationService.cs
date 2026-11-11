using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public sealed class DiskOptimizationService
    {
        public const int HddThresholdPercent = 10;
        public const int SsdThresholdPercent = 20;

        private readonly Func<DiskOptimizationConfig> _readConfigHook;
        private readonly Func<List<OptimizableVolumeModel>> _listVolumesHook;
        private readonly Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>> _runProcess;

        public DiskOptimizationService(
            Func<DiskOptimizationConfig>? readConfigHook = null,
            Func<List<OptimizableVolumeModel>>? listVolumesHook = null,
            Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>>? processHook = null)
        {
            _readConfigHook = readConfigHook ?? DefaultReadConfig;
            _listVolumesHook = listVolumesHook ?? DefaultListVolumes;
            _runProcess = processHook ?? DefaultRunProcessAsync;
        }

        public DiskOptimizationConfig ReadConfig() => _readConfigHook();
        public List<OptimizableVolumeModel> ListVolumes() => _listVolumesHook();

        public async Task<int> AnalyzeFragmentationAsync(string driveLetter, CancellationToken ct = default)
        {
            string normalized = (driveLetter ?? "").Trim().TrimEnd('\\').ToUpperInvariant();
            if (!Regex.IsMatch(normalized, "^[A-Z]:$"))
                return -1;

            string script =
                $"$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; " +
                $"$v = Get-CimInstance Win32_Volume -Filter \"DriveLetter='{normalized}'\"; " +
                $"$r = Invoke-CimMethod -InputObject $v -MethodName DefragAnalysis; " +
                $"if ($r.ReturnValue -eq 0) {{ [int]$r.DefragAnalysis.FilePercentFragmentation }} else {{ -1 }}";

            string psExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var (exit, output) = await _runProcess(psExe, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script }, ct);
            if (exit != 0)
                return -1;

            string? lastLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            return int.TryParse(lastLine, out int pct) ? pct : -1;
        }

        public async Task<(bool Success, string Message)> OptimizeVolumeAsync(OptimizableVolumeModel volume, bool forceSsdDefrag = false, CancellationToken ct = default)
        {
            string trimmedLetter = (volume?.DriveLetter ?? "").Trim().TrimEnd('\\');
            if (volume == null || !Regex.IsMatch(trimmedLetter, "^[A-Z]:$", RegexOptions.IgnoreCase))
                return (false, "Letra de unidade inválida.");

            char letter = char.ToUpperInvariant(trimmedLetter[0]);
            string flag = volume.IsSsd ? (forceSsdDefrag ? "-Defrag" : "-ReTrim") : "-Defrag";
            string psExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            string script = $"$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; Optimize-Volume -DriveLetter {letter} {flag}";

            var (exit, output) = await _runProcess(psExe, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script }, ct);
            if (exit == 0)
            {
                string actionDesc = volume.IsSsd ? (forceSsdDefrag ? "Desfragmentação SSD" : "TRIM") : "Desfragmentação";
                return (true, $"{actionDesc} concluído(a) em {letter}:.");
            }

            return (false, string.IsNullOrWhiteSpace(output)
                ? $"Falha ao otimizar {letter}: (código {exit}). Execute como Administrador."
                : output.Trim());
        }

        public async Task<List<string>> EnableMissingDefaultsAsync(DiskOptimizationConfig config, CancellationToken ct = default)
        {
            var enabled = new List<string>();

            if (!config.TrimEnabled)
            {
                string fsutil = Path.Combine(Environment.SystemDirectory, "fsutil.exe");
                var (exit, _) = await _runProcess(fsutil, new[] { "behavior", "set", "DisableDeleteNotify", "0" }, ct);
                if (exit == 0) enabled.Add("TRIM");
            }

            if (config.WeeklyScheduleEnabled == false)
            {
                string schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
                var (exit, _) = await _runProcess(schtasks, new[] { "/Change", "/TN", @"\Microsoft\Windows\Defrag\ScheduledDefrag", "/ENABLE" }, ct);
                if (exit == 0) enabled.Add("Agendamento semanal");
            }

            if (!config.ProtectionEnabled)
            {
                string psExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var (exit, _) = await _runProcess(psExe, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "Enable-ComputerRestore -Drive \"$env:SystemDrive\\\"" }, ct);
                if (exit == 0) enabled.Add("Proteção do Sistema");
            }

            return enabled;
        }

        public static string SummarizeFragmentation(OptimizableVolumeModel volume, int percent, bool trimEnabled)
        {
            if (percent < 0)
                return "Não foi possível medir a fragmentação (execute como Administrador).";

            if (volume.IsSsd)
            {
                return percent >= SsdThresholdPercent
                    ? $"{percent}% fragmentado: acima de {SsdThresholdPercent}% para SSD. Considere executar ReTrim/otimização."
                    : $"{percent}% fragmentado: saudável para SSD." + (trimEnabled ? " TRIM ativo." : " TRIM desativado!");
            }

            return percent >= HddThresholdPercent
                ? $"{percent}% fragmentado: recomendado desfragmentar este HD."
                : $"{percent}% fragmentado: nível normal.";
        }

        private static DiskOptimizationConfig DefaultReadConfig()
        {
            bool trim = true;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
                if (k?.GetValue("DisableDeleteNotification") is int val && val != 0)
                    trim = false;
            }
            catch
            {
            }

            bool? sched = null;
            try
            {
                var t = Type.GetTypeFromProgID("Schedule.Service");
                if (t != null)
                {
                    object? svc = Activator.CreateInstance(t);
                    if (svc != null)
                    {
                        t.InvokeMember("Connect", System.Reflection.BindingFlags.InvokeMethod, null, svc, null);
                        object? folder = t.InvokeMember("GetFolder", System.Reflection.BindingFlags.InvokeMethod, null, svc, new object[] { @"\Microsoft\Windows\Defrag" });
                        if (folder != null)
                        {
                            object? task = folder.GetType().InvokeMember("GetTask", System.Reflection.BindingFlags.InvokeMethod, null, folder, new object[] { "ScheduledDefrag" });
                            if (task != null)
                            {
                                object? enabledVal = task.GetType().InvokeMember("Enabled", System.Reflection.BindingFlags.GetProperty, null, task, null);
                                if (enabledVal is bool b) sched = b;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            bool prot = false;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients");
                prot = k?.GetValue("{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}") != null;
            }
            catch
            {
            }

            return new DiskOptimizationConfig
            {
                TrimEnabled = trim,
                WeeklyScheduleEnabled = sched,
                ProtectionEnabled = prot
            };
        }

        private static List<OptimizableVolumeModel> DefaultListVolumes()
        {
            var ssdLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var letterModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();

                var ssdDiskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var diskModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, MediaType, FriendlyName FROM MSFT_PhysicalDisk")))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        using (o)
                        {
                            string id = o["DeviceId"]?.ToString() ?? "";
                            int media = Convert.ToInt32(o["MediaType"] ?? 0);
                            if (media == 4) ssdDiskIds.Add(id);
                            diskModels[id] = o["FriendlyName"]?.ToString()?.Trim() ?? "";
                        }
                    }
                }

                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        using (o)
                        {
                            string? dl = o["DriveLetter"]?.ToString();
                            if (string.IsNullOrWhiteSpace(dl) || dl == "\0") continue;
                            string letter = dl.Trim()[0] + ":";
                            string diskId = o["DiskNumber"]?.ToString() ?? "";
                            if (ssdDiskIds.Contains(diskId))
                                ssdLetters.Add(letter);
                            if (diskModels.TryGetValue(diskId, out var mod))
                                letterModels[letter] = mod;
                        }
                    }
                }
            }
            catch
            {
            }

            var list = new List<OptimizableVolumeModel>();
            try
            {
                foreach (var di in DriveInfo.GetDrives())
                {
                    if (!di.IsReady || di.DriveType != DriveType.Fixed) continue;
                    if (!di.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) continue;

                    string letter = di.Name.TrimEnd('\\').ToUpperInvariant();
                    list.Add(new OptimizableVolumeModel
                    {
                        DriveLetter = letter,
                        Label = di.VolumeLabel,
                        Model = letterModels.GetValueOrDefault(letter, ""),
                        IsSsd = ssdLetters.Contains(letter),
                        SizeBytes = di.TotalSize
                    });
                }
            }
            catch
            {
            }

            return list.OrderBy(v => v.DriveLetter, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static async Task<(int ExitCode, string Output)> DefaultRunProcessAsync(string exe, string[] args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {exe}.");
            using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            string text = (await outTask) + "\n" + (await errTask);
            return (p.ExitCode, text);
        }
    }
}

