using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    public sealed class PostFormatService
    {
        public static IReadOnlyList<SoftwareKit> GetSoftwareKits() =>
            AppCatalogService.LoadCatalog().Kits
                .Select(k => new SoftwareKit
                {
                    Id = NormalizeKitId(k.Name),
                    Name = $"Kit {k.Name}",
                    Description = k.Description,
                    SoftwareIds = k.ProgramIds.ToArray()
                })
                .ToList();

        private static string NormalizeKitId(string name) =>
            name.ToLowerInvariant().Replace(' ', '-').Replace("ó", "o").Replace("é", "e");

        public static List<PostFormatStepViewModel> CreateDefaultSteps() => new()
        {
            new()
            {
                Id = "restore_point",
                Order = 1,
                Title = "Ponto de Restauração do Windows",
                Description = "Cria um ponto de restauração de segurança antes de alterar sistema e aplicativos."
            },
            new()
            {
                Id = "activation_check",
                Order = 2,
                Title = "Diagnóstico de Licença e Ativação",
                Description = "Consulta o status de ativação (OEM/Retail/Volume) sem expor sua chave completa."
            },
            new()
            {
                Id = "windows_update",
                Order = 3,
                Title = "Verificar Windows Update",
                Description = "Checa se existem atualizações críticas pendentes ou reinicialização aguardando."
            },
            new()
            {
                Id = "remove_bloat",
                Order = 4,
                Title = "Remover Bloatware Recomendado",
                Description = "Desinstala apps patrocinados/propagandas mantendo loja, segurança e runtime protegidos."
            },
            new()
            {
                Id = "apply_profile",
                Order = 5,
                Title = "Aplicar Perfil de Otimização",
                Description = "Aplica o perfil selecionado gravando backup na Central Desfazer."
            },
            new()
            {
                Id = "install_kit",
                Order = 6,
                Title = "Instalar Kit de Programas",
                Description = "Instala automaticamente os programas do kit escolhido que ainda faltam no PC."
            }
        };

        public static string SanitizeRestorePointName(string? name)
        {
            string clean = new string((name ?? "")
                .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '(' or ')' or ',' or '-' or '.' or '/' or ':')
                .ToArray());
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            if (clean.Length > 60) clean = clean[..60].Trim();
            return clean.Length == 0 ? "Hardware HUB" : clean;
        }

        public static async Task<(bool Ok, string Message)> CreateRestorePointAsync(
            string description = "Hardware HUB Pos-Formatacao",
            CancellationToken ct = default)
        {
            string safeDesc = SanitizeRestorePointName(description);
            string script = $@"
$k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
$antes = (Get-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
try {{
  try {{ Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction Stop }} catch {{ }}
  Checkpoint-Computer -Description '{safeDesc}' -RestorePointType MODIFY_SETTINGS -WarningAction Stop -ErrorAction Stop
}} finally {{
  if ($null -eq $antes) {{ Remove-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }}
  else {{ Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value $antes -Type DWord -ErrorAction SilentlyContinue }}
}}";

            try
            {
                var (exitCode, _, stderr) = await BloatwareService.RunPowerShellAsync(script, ct, TimeSpan.FromMinutes(3));
                bool ok = exitCode == 0 && string.IsNullOrWhiteSpace(stderr);
                return ok
                    ? (true, $"Ponto de restauração '{safeDesc}' criado com sucesso.")
                    : (false, "Não foi possível criar o ponto de restauração (verifique se a Proteção do Sistema está ativada e execute como Administrador).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, $"Falha ao criar ponto de restauração: {ex.Message}");
            }
        }

        public WindowsActivationStatus GetWindowsActivationStatus()
        {
            int status = -1;
            string channel = "";
            string partialKey = "";
            bool hasFirmwareKey = false;
            string errorMsg = "";

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT LicenseStatus, PartialProductKey, ProductKeyChannel FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        int curStatus = Convert.ToInt32(obj["LicenseStatus"] ?? -1);
                        if (status == -1 || curStatus == 1)
                        {
                            status = curStatus;
                            partialKey = obj["PartialProductKey"]?.ToString() ?? "";
                            try { channel = obj["ProductKeyChannel"]?.ToString() ?? ""; } catch { channel = ""; }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }

            try
            {
                using var svcSearcher = new ManagementObjectSearcher("SELECT OA3xOriginalProductKey FROM SoftwareLicensingService");
                foreach (ManagementObject obj in svcSearcher.Get())
                {
                    using (obj)
                    {
                        string oa3 = obj["OA3xOriginalProductKey"]?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(oa3)) hasFirmwareKey = true;
                    }
                }
            }
            catch { }

            string edition = WindowsRegistryBackend.Instance.Read(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName").Data?.ToString() ?? "Windows";
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                edition = edition.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            return new WindowsActivationStatus
            {
                QuerySucceeded = string.IsNullOrEmpty(errorMsg),
                IsActivated = status == 1,
                LicenseStatusCode = status,
                Edition = edition,
                Channel = channel,
                ChannelDescription = DescribeActivationChannel(channel),
                PartialKey = partialKey,
                HasFirmwareKey = hasFirmwareKey,
                ErrorMessage = errorMsg
            };
        }

        public static string DescribeActivationChannel(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return "Desconhecido";
            if (channel.StartsWith("OEM", StringComparison.OrdinalIgnoreCase)) return "OEM (licença de fábrica)";
            if (channel.Equals("Retail", StringComparison.OrdinalIgnoreCase)) return "Varejo / Digital";
            if (channel.Contains("MAK", StringComparison.OrdinalIgnoreCase)) return "Volume (MAK)";
            if (channel.Contains("GVLK", StringComparison.OrdinalIgnoreCase)) return "Volume (KMS)";
            return channel;
        }

        public async Task<(int PendingCount, bool RebootPending, string Summary)> CheckWindowsUpdateAsync(CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                bool rebootPending = false;
                try
                {
                    Type? sysInfoType = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo");
                    if (sysInfoType != null && Activator.CreateInstance(sysInfoType) is { } sysInfoObj)
                    {
                        dynamic sysInfo = sysInfoObj;
                        rebootPending = (bool)sysInfo.RebootRequired;
                    }
                }
                catch { }

                try
                {
                    ct.ThrowIfCancellationRequested();
                    Type? sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                    if (sessionType == null || Activator.CreateInstance(sessionType) is not { } sessionObj)
                    {
                        return (0, rebootPending, "Serviço Windows Update indisponível.");
                    }

                    dynamic session = sessionObj;
                    session.ClientApplicationID = "Hardware HUB";
                    dynamic searcher = session.CreateUpdateSearcher();
                    dynamic result = searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0");
                    int count = (int)result.Updates.Count;

                    string msg = count == 0
                        ? "Nenhuma atualização pendente encontrada."
                        : $"{count} atualização(ões) disponível(eis) no Windows Update.";
                    if (rebootPending) msg += " Reinicialização pendente.";
                    return (count, rebootPending, msg);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return (0, rebootPending, $"Não foi possível consultar o Windows Update ({ex.Message}).");
                }
            }, ct);
        }

        public static void OpenWindowsUpdateSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true });
            }
            catch { }
        }
    }
}

