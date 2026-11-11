using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public sealed class RestorePointsService
    {
        private readonly Func<(List<RestorePointModel> Points, string? Error)> _readPointsHook;
        private readonly Func<SystemProtectionStatus> _readProtectionHook;
        private readonly Func<string, CancellationToken, Task<(bool Success, string Message)>> _createPointHook;
        private readonly Func<CancellationToken, Task<(bool Success, string Message)>> _enableProtectionHook;
        private readonly Func<uint, uint> _removePointHook;

        public RestorePointsService(
            Func<(List<RestorePointModel> Points, string? Error)>? readPointsHook = null,
            Func<SystemProtectionStatus>? readProtectionHook = null,
            Func<string, CancellationToken, Task<(bool Success, string Message)>>? createPointHook = null,
            Func<CancellationToken, Task<(bool Success, string Message)>>? enableProtectionHook = null,
            Func<uint, uint>? removePointHook = null)
        {
            _readPointsHook = readPointsHook ?? DefaultReadPoints;
            _readProtectionHook = readProtectionHook ?? DefaultReadProtection;
            _createPointHook = createPointHook ?? DefaultCreatePointAsync;
            _enableProtectionHook = enableProtectionHook ?? DefaultEnableProtectionAsync;
            _removePointHook = removePointHook ?? DefaultRemoveRestorePoint;
        }

        public static string SanitizeRestoreDescription(string rawName)
        {
            string cleaned = Regex.Replace(rawName ?? "", @"[\r\n\t'\""`$;|&<>]", " ").Trim();
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = "Hardware HUB Ponto Manual";
            return cleaned.Length > 64 ? cleaned[..64].Trim() : cleaned;
        }

        public (List<RestorePointModel> Points, string? Error) ReadPoints()
        {
            var (points, err) = _readPointsHook();
            if (err != null) return (new List<RestorePointModel>(), err);

            var sorted = points
                .OrderByDescending(p => p.CreationTime)
                .ThenByDescending(p => p.SequenceNumber)
                .ToList();

            var tagged = sorted.Select((p, idx) => new RestorePointModel
            {
                SequenceNumber = p.SequenceNumber,
                Description = p.Description,
                RestorePointType = p.RestorePointType,
                CreationTime = p.CreationTime,
                IsLatest = idx == 0
            }).ToList();

            return (tagged, null);
        }

        public SystemProtectionStatus ReadProtectionStatus() => _readProtectionHook();

        public Task<(bool Success, string Message)> CreatePointAsync(string description, CancellationToken ct = default)
            => _createPointHook(SanitizeRestoreDescription(description), ct);

        public Task<(bool Success, string Message)> EnableProtectionAsync(CancellationToken ct = default)
            => _enableProtectionHook(ct);

        public (bool Success, string Message) DeletePoint(RestorePointModel point)
        {
            var (list, err) = ReadPoints();
            if (err != null)
                return (false, err);

            if (list.Count == 0 || !list.Any(p => p.SequenceNumber == point.SequenceNumber))
                return (false, "Este ponto não existe mais na lista atualizada.");

            if (list[0].SequenceNumber == point.SequenceNumber)
                return (false, "O ponto mais recente é protegido e não pode ser apagado.");

            uint code;
            try
            {
                code = _removePointHook(point.SequenceNumber);
            }
            catch (Exception ex)
            {
                return (false, $"Não foi possível remover o ponto: {ex.Message}");
            }

            return code switch
            {
                0 => (true, $"Ponto #{point.SequenceNumber} (\"{point.Description}\") removido."),
                5 => (false, "Execute o Hardware HUB como Administrador para apagar pontos de restauração."),
                _ => (false, $"O Windows recusou a remoção do ponto (código {code}).")
            };
        }

        public static void OpenSystemRestoreWizard() => LaunchSystemExe("rstrui.exe");
        public static void OpenProtectionSettings() => LaunchSystemExe("SystemPropertiesProtection.exe");

        private static void LaunchSystemExe(string exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe))
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private static (List<RestorePointModel> Points, string? Error) DefaultReadPoints()
        {
            var list = new List<RestorePointModel>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\default");
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SequenceNumber, Description, RestorePointType, CreationTime FROM SystemRestore"));
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        DateTime dt = DateTime.MinValue;
                        try
                        {
                            string raw = obj["CreationTime"]?.ToString() ?? "";
                            if (raw.Length > 0)
                                dt = ManagementDateTimeConverter.ToDateTime(raw);
                        }
                        catch
                        {
                        }

                        list.Add(new RestorePointModel
                        {
                            SequenceNumber = Convert.ToUInt32(obj["SequenceNumber"] ?? 0u),
                            Description = obj["Description"]?.ToString()?.Trim() ?? "",
                            RestorePointType = Convert.ToUInt32(obj["RestorePointType"] ?? 99u),
                            CreationTime = dt
                        });
                    }
                }
                return (list, null);
            }
            catch (UnauthorizedAccessException)
            {
                return (new List<RestorePointModel>(), "Execute o HUB como Administrador para consultar os pontos de restauração.");
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
            {
                return (new List<RestorePointModel>(), "Execute o HUB como Administrador para consultar os pontos de restauração.");
            }
            catch (Exception ex)
            {
                return (new List<RestorePointModel>(), $"Não foi possível ler os pontos: {ex.Message}");
            }
        }

        private static SystemProtectionStatus DefaultReadProtection()
        {
            string sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\").TrimEnd('\\').ToUpperInvariant();
            bool? enabled = null;

            try
            {
                using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients");
                if (k == null)
                {
                    enabled = false;
                }
                else
                {
                    object? val = k.GetValue("{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}");
                    string[] items = val as string[] ?? (val is string s ? new[] { s } : Array.Empty<string>());
                    string encodedDrive = $"({sysDrive.Replace(":", "%3A")})";
                    enabled = items.Length > 0 &&
                              (items.Any(x => x.Contains(encodedDrive, StringComparison.OrdinalIgnoreCase)) ||
                               !items.Any(x => x.Contains("%3A", StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch
            {
            }

            long? used = null, alloc = null, max = null;
            try
            {
                string? volId = null;
                using (var s = new ManagementObjectSearcher($"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter='{sysDrive}'"))
                {
                    var first = s.Get().Cast<ManagementObject>().FirstOrDefault();
                    volId = first?["DeviceID"]?.ToString();
                }

                string guid = string.IsNullOrEmpty(volId) ? "" : Regex.Match(volId, @"\{[0-9A-Fa-f-]{36}\}").Value;
                if (guid.Length > 0)
                {
                    using var s2 = new ManagementObjectSearcher("SELECT Volume, UsedSpace, AllocatedSpace, MaxSpace FROM Win32_ShadowStorage");
                    foreach (ManagementObject o in s2.Get())
                    {
                        using (o)
                        {
                            if ((o["Volume"]?.ToString() ?? "").Contains(guid, StringComparison.OrdinalIgnoreCase))
                            {
                                used = Convert.ToInt64(o["UsedSpace"] ?? 0L);
                                alloc = Convert.ToInt64(o["AllocatedSpace"] ?? 0L);
                                max = Convert.ToInt64(o["MaxSpace"] ?? 0L);
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return new SystemProtectionStatus
            {
                IsEnabled = enabled,
                UsedBytes = used,
                AllocatedBytes = alloc,
                MaxBytes = max
            };
        }

        private static Task<(bool Success, string Message)> DefaultCreatePointAsync(string description, CancellationToken ct)
            => RunPowerShellAsync($"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'MODIFY_SETTINGS'", ct,
                "Ponto de restauração criado com sucesso!");

        private static Task<(bool Success, string Message)> DefaultEnableProtectionAsync(CancellationToken ct)
            => RunPowerShellAsync("Enable-ComputerRestore -Drive \"$env:SystemDrive\\\"", ct,
                "Proteção do Sistema ativada na unidade do Windows.");

        private static async Task<(bool Success, string Message)> RunPowerShellAsync(string script, CancellationToken ct, string successMsg)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; {script}");

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
            using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            string err = (await errTask).Trim();
            string outStr = (await outTask).Trim();

            if (p.ExitCode == 0)
                return (true, successMsg);

            string detail = !string.IsNullOrEmpty(err) ? err : outStr;
            if (detail.Contains("frequency", StringComparison.OrdinalIgnoreCase) || detail.Contains("1440", StringComparison.OrdinalIgnoreCase))
                return (false, "O Windows limita a criação de vários pontos em menos de 24h (SystemRestorePointCreationFrequency).");

            return (false, string.IsNullOrEmpty(detail) ? $"Falha (código {p.ExitCode}). Execute como Administrador." : detail);
        }

        [DllImport("srclient.dll")]
        private static extern uint SRRemoveRestorePoint(uint dwRpNum);

        private static uint DefaultRemoveRestorePoint(uint seq) => SRRemoveRestorePoint(seq);
    }
}

