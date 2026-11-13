using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public sealed class DriversUpdateService
    {
        private static readonly Regex OemInfRegex = new(@"oem\d+\.inf", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>> _runProcess;
        private readonly Func<CancellationToken, Task<List<WindowsUpdateItemModel>>>? _searchUpdatesHook;
        private readonly Func<IReadOnlyList<WindowsUpdateItemModel>, Action<string>?, CancellationToken, Task<WindowsUpdateRunResult>>? _installUpdatesHook;

        public DriversUpdateService(
            Func<string, string, CancellationToken, Task<(int ExitCode, string Output)>>? processHook = null,
            Func<CancellationToken, Task<List<WindowsUpdateItemModel>>>? searchUpdatesHook = null,
            Func<IReadOnlyList<WindowsUpdateItemModel>, Action<string>?, CancellationToken, Task<WindowsUpdateRunResult>>? installUpdatesHook = null)
        {
            _runProcess = processHook ?? DefaultRunProcessAsync;
            _searchUpdatesHook = searchUpdatesHook;
            _installUpdatesHook = installUpdatesHook;
        }

        public static bool IsAdministrator()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static string MapDeviceClassName(string code) => (code ?? "").Trim().ToUpperInvariant() switch
        {
            "DISPLAY" => "Vídeo",
            "NET" => "Rede",
            "MEDIA" or "AUDIOENDPOINT" => "Som",
            "BLUETOOTH" => "Bluetooth",
            "USB" => "USB",
            "HIDCLASS" => "Controles (HID)",
            "KEYBOARD" => "Teclado",
            "MOUSE" => "Mouse",
            "SYSTEM" => "Sistema / Chipset",
            "HDC" or "SCSIADAPTER" => "Controlador de Disco",
            "DISKDRIVE" => "Armazenamento",
            "CDROM" => "Unidade Óptica",
            "MONITOR" => "Monitor",
            "PRINTER" or "PRINTQUEUE" => "Impressão",
            "IMAGE" or "CAMERA" => "Câmera / Scanner",
            "BIOMETRIC" => "Biometria",
            "PROCESSOR" => "Processador",
            "FIRMWARE" => "Firmware / UEFI",
            "SECURITYDEVICES" => "Segurança (TPM)",
            "PORTS" => "Portas (COM/LPT)",
            "BATTERY" => "Bateria",
            "SDHOST" => "Leitor SD",
            "" => "Outros",
            var s when s.Length > 1 => char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant(),
            var s => s
        };

        public static string FormatNvidiaFriendlyVersion(string wmiVersion)
        {
            if (string.IsNullOrWhiteSpace(wmiVersion)) return "";
            var parts = wmiVersion.Split('.');
            if (parts.Length != 4) return wmiVersion;
            var merged = parts[2] + parts[3];
            if (merged.Length < 5) return wmiVersion;
            var tail = merged[^5..];
            return tail[..3] + "." + tail[3..];
        }

        public static (string Title, string Tip, bool IsSerious) ExplainDeviceErrorCode(int code) => code switch
        {
            28 => ("Sem driver instalado.", "Verifique atualizações opcionais do Windows Update ou baixe no fabricante da placa-mãe/notebook.", true),
            1 or 18 or 19 or 39 or 40 => ("Driver defeituoso ou incompleto.", "Reinstale o driver usando o instalador oficial do fabricante ou o Windows Update.", true),
            3 => ("Driver corrompido ou falha de memória ao carregar.", "Reinicie o PC; se persistir, reinstale o driver.", true),
            10 => ("O dispositivo falhou ao iniciar.", "Reinicie o sistema e atualize o driver pelo site do fabricante.", true),
            12 or 16 or 33 or 34 or 35 or 36 => ("Conflito de recursos com outro hardware.", "Atualize BIOS e chipset no suporte oficial da placa-mãe.", true),
            14 or 21 or 38 or 46 => ("Reinicialização pendente para concluir instalação.", "Reinicie o computador.", false),
            22 => ("Dispositivo desativado manualmente.", "Se precisar dele, clique com o botão direito no Gerenciador de Dispositivos e escolha Habilitar.", false),
            24 => ("Dispositivo desconectado ou sem resposta.", "Se for porta não usada (ex.: PS/2), ignore; caso contrário verifique cabo/slot.", false),
            29 => ("Desativado na BIOS/UEFI.", "Habilite o recurso na BIOS se precisar dele.", false),
            31 => ("Driver reportando erro operacional.", "Atualize o driver pelo fabricante ou Windows Update.", true),
            32 => ("Serviço do driver desativado.", "Reinstale o pacote de driver para reativar o serviço.", true),
            37 or 41 => ("Falha ao inicializar controlador.", "Desinstale o dispositivo no Gerenciador de Dispositivos e reinicie.", true),
            42 => ("Dispositivo duplicado detectado.", "Reinicie o computador.", false),
            43 => ("O Windows interrompeu o dispositivo por erro de hardware/USB.", "Reconecte em outra porta e atualize o driver; pode indicar falha física.", true),
            44 => ("Desligado por software/serviço externo.", "Reinicie o computador.", false),
            47 => ("Preparado para remoção segura.", "Reconecte o dispositivo se ainda desejar usá-lo.", false),
            48 => ("Driver bloqueado pelo Windows por incompatibilidade conhecida.", "Baixe uma versão mais recente assinada do fabricante.", true),
            52 => ("Assinatura digital do driver não pôde ser verificada.", "Instale o pacote oficial assinado do fabricante.", true),
            _ => ($"Erro Plug & Play (código {code}).", "Reinstale ou atualize o driver correspondente.", true)
        };

        public List<DriverItemModel> ReadInstalledDrivers()
        {
            var list = new List<DriverItemModel>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceName, DeviceClass, Manufacturer, DriverVersion, DriverDate, IsSigned FROM Win32_PnPSignedDriver");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        string device = obj["DeviceName"]?.ToString()?.Trim() ?? "";
                        string version = obj["DriverVersion"]?.ToString()?.Trim() ?? "";
                        if (device.Length == 0 || version.Length == 0)
                            continue;

                        string classCode = obj["DeviceClass"]?.ToString()?.Trim() ?? "";
                        string vendor = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                        bool signed = obj["IsSigned"] is bool b && b;
                        bool generic = vendor.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                                       vendor.StartsWith('(') ||
                                       vendor.StartsWith("Standard ", StringComparison.OrdinalIgnoreCase);

                        string friendly = classCode.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) &&
                                          vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                            ? FormatNvidiaFriendlyVersion(version)
                            : "";

                        list.Add(new DriverItemModel
                        {
                            Device = device,
                            ClassCode = classCode,
                            ClassLabel = MapDeviceClassName(classCode),
                            Vendor = vendor.Length > 0 ? vendor : "—",
                            Version = version,
                            FriendlyVersion = friendly,
                            Date = ParseWmiDate(obj["DriverDate"]?.ToString()),
                            IsSigned = signed,
                            IsGeneric = generic
                        });
                    }
                }
            }
            catch
            {
            }

            return list.OrderBy(r => r.ClassLabel).ThenBy(r => r.Device).ToList();
        }

        public List<DriverProblemItem> ReadDriverProblems()
        {
            var raw = new List<DriverProblemItem>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPClass, ConfigManagerErrorCode, HardwareID, DeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        int code = Convert.ToInt32(obj["ConfigManagerErrorCode"] ?? 0);
                        if (code == 0 || code == 45)
                            continue;

                        var (title, tip, serious) = ExplainDeviceErrorCode(code);
                        string hwId = obj["HardwareID"] is string[] ids && ids.Length > 0
                            ? ids[0]
                            : obj["DeviceID"]?.ToString()?.Trim() ?? "";

                        if (code == 28 && hwId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                        {
                            if (hwId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase))
                                tip = "Instale o Intel Chipset Device Software pelo site da placa-mãe ou suporte Intel.";
                            else if (hwId.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase))
                                tip = "Instale os drivers oficiais AMD Chipset no site da placa-mãe ou da AMD.";
                        }

                        string name = obj["Name"]?.ToString()?.Trim() ?? "Dispositivo desconhecido";
                        string pnpClass = obj["PNPClass"]?.ToString()?.Trim() ?? "";
                        raw.Add(new DriverProblemItem
                        {
                            Device = name.Length > 0 ? name : "Dispositivo desconhecido",
                            ClassLabel = pnpClass.Length > 0 ? MapDeviceClassName(pnpClass) : "Hardware",
                            ErrorCode = code,
                            Title = title,
                            Tip = tip,
                            HardwareId = hwId,
                            IsSerious = serious
                        });
                    }
                }
            }
            catch
            {
            }

            // Detect Microsoft Basic Display Adapter
            try
            {
                using var gpuSearcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
                foreach (ManagementObject obj in gpuSearcher.Get())
                {
                    using (obj)
                    {
                        string name = obj["Name"]?.ToString()?.Trim() ?? "";
                        string pnp = obj["PNPDeviceID"]?.ToString()?.Trim() ?? "";
                        string vendor = DetectGpuVendorFromPnp(pnp);
                        if (vendor.Length > 0 &&
                            (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Básico da Microsoft", StringComparison.OrdinalIgnoreCase)))
                        {
                            raw.Add(new DriverProblemItem
                            {
                                Device = $"Placa de vídeo {vendor}",
                                ClassLabel = "Vídeo",
                                ErrorCode = 0,
                                Title = "Usando driver básico genérico do Windows.",
                                Tip = $"Instale o driver oficial {vendor} pelo botão de suporte ou pelo Windows Update.",
                                HardwareId = pnp,
                                IsSerious = true
                            });
                        }
                    }
                }
            }
            catch
            {
            }

            return raw
                .GroupBy(p => (p.Device, p.ErrorCode))
                .Select(g =>
                {
                    var first = g.First();
                    return g.Count() == 1
                        ? first
                        : new DriverProblemItem
                        {
                            Device = first.Device,
                            ClassLabel = first.ClassLabel,
                            ErrorCode = first.ErrorCode,
                            Title = first.Title,
                            Tip = first.Tip,
                            HardwareId = first.HardwareId,
                            IsSerious = first.IsSerious,
                            Count = g.Count()
                        };
                })
                .OrderByDescending(p => p.IsSerious)
                .ThenByDescending(p => p.Count)
                .ThenBy(p => p.Device)
                .ToList();
        }

        public DriverBoardInfo ReadBoardInfo()
        {
            var info = new DriverBoardInfo();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        using (obj)
                        {
                            info.Maker = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                            info.Model = obj["Product"]?.ToString()?.Trim() ?? "";
                            break;
                        }
                    }
                }

                using (var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        using (obj)
                        {
                            string ver = obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                            string dt = ParseWmiDate(obj["ReleaseDate"]?.ToString());
                            info.Bios = string.IsNullOrWhiteSpace(ver) ? dt : $"{ver} ({dt})";
                            break;
                        }
                    }
                }

                string sysMaker = "", sysModel = "";
                using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        using (obj)
                        {
                            sysMaker = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                            sysModel = obj["Model"]?.ToString()?.Trim() ?? "";
                            break;
                        }
                    }
                }

                if (info.Maker.Length == 0) info.Maker = sysMaker;
                if (info.Model.Length == 0) info.Model = sysModel;

                string combined = $"{info.Maker} {sysMaker}";
                string modelQuery = Uri.EscapeDataString(info.Model);
                if (combined.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
                {
                    info.Brand = "ASUS";
                    info.SupportUrl = "https://www.asus.com/support/download-center/";
                }
                else if (combined.Contains("MSI", StringComparison.OrdinalIgnoreCase) || combined.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase))
                {
                    info.Brand = "MSI";
                    info.SupportUrl = $"https://www.msi.com/search/{modelQuery}";
                }
                else if (combined.Contains("GIGABYTE", StringComparison.OrdinalIgnoreCase))
                {
                    info.Brand = "Gigabyte";
                    info.SupportUrl = "https://www.gigabyte.com/Support";
                }
                else if (combined.Contains("ASRock", StringComparison.OrdinalIgnoreCase))
                {
                    info.Brand = "ASRock";
                    info.SupportUrl = "https://www.asrock.com/support/index.asp";
                }
                else if (combined.Contains("Dell", StringComparison.OrdinalIgnoreCase))
                {
                    info.Brand = "Dell";
                    info.SupportUrl = "https://www.dell.com/support/home/";
                }
                else if (combined.Contains("LENOVO", StringComparison.OrdinalIgnoreCase))
                {
                    info.Brand = "Lenovo";
                    info.SupportUrl = "https://support.lenovo.com/";
                }
                else if (Regex.IsMatch(combined, @"\bHP\b|Hewlett", RegexOptions.IgnoreCase))
                {
                    info.Brand = "HP";
                    info.SupportUrl = "https://support.hp.com/";
                }
                else if (combined.Contains("Acer", StringComparison.OrdinalIgnoreCase))
                {
                    info.Brand = "Acer";
                    info.SupportUrl = "https://www.acer.com/support";
                }
                else if ((info.Maker + info.Model).Trim().Length > 0)
                {
                    info.SupportUrl = "https://www.google.com/search?q=" + Uri.EscapeDataString($"{info.Maker} {info.Model} drivers".Trim());
                }
            }
            catch
            {
            }

            return info;
        }

        public List<DriverGpuInfo> ReadGpus()
        {
            var gpus = new List<DriverGpuInfo>();
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController");
                foreach (ManagementObject obj in s.Get())
                {
                    using (obj)
                    {
                        string name = obj["Name"]?.ToString()?.Trim() ?? "";
                        if (name.Length == 0 || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string compat = $"{obj["AdapterCompatibility"]} {name}";
                        string pnp = obj["PNPDeviceID"]?.ToString()?.Trim() ?? "";
                        string version = obj["DriverVersion"]?.ToString()?.Trim() ?? "";
                        string date = ParseWmiDate(obj["DriverDate"]?.ToString());

                        string vendor = DetectGpuVendorFromPnp(pnp);
                        if (vendor.Length == 0)
                        {
                            if (compat.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) vendor = "NVIDIA";
                            else if (compat.Contains("AMD", StringComparison.OrdinalIgnoreCase) || compat.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) vendor = "AMD";
                            else if (compat.Contains("Intel", StringComparison.OrdinalIgnoreCase)) vendor = "Intel";
                        }

                        string url = vendor switch
                        {
                            "NVIDIA" => "https://www.nvidia.com/pt-br/geforce/drivers/",
                            "AMD" => "https://www.amd.com/en/support/download/drivers.html",
                            "Intel" => "https://www.intel.com/content/www/us/en/support/intel-driver-support-assistant.html",
                            _ => ""
                        };

                        gpus.Add(new DriverGpuInfo
                        {
                            Name = name,
                            Vendor = vendor.Length > 0 ? vendor : "Outro",
                            DriverVersion = version,
                            FriendlyVersion = vendor == "NVIDIA" ? FormatNvidiaFriendlyVersion(version) : "",
                            DriverDate = date,
                            SupportUrl = url
                        });
                    }
                }
            }
            catch
            {
            }

            return gpus;
        }

        public async Task<(bool Success, string Message, string ExportFolder)> BackupDriversAsync(
            string baseFolder,
            Action<string, double?>? progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(baseFolder))
                return (false, "Pasta de destino inválida.", "");

            string dest = Path.Combine(baseFolder, $"Drivers-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}");
            Directory.CreateDirectory(dest);
            progress?.Invoke("Exportando pacotes de drivers de terceiros...", null);

            string pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
            var (exit, output) = await _runProcess(pnputil, $"/export-driver * \"{dest}\"", ct);
            int exportedDirs = 0;
            try
            {
                exportedDirs = Directory.GetDirectories(dest).Length;
            }
            catch
            {
            }

            if (exportedDirs == 0 && !Directory.EnumerateFiles(dest, "*.inf", SearchOption.AllDirectories).Any())
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dest).Any())
                        Directory.Delete(dest);
                }
                catch
                {
                }

                return exit == 0 && IsAdministrator()
                    ? (false, "Nenhum driver de terceiros encontrado para exportar (apenas drivers nativos do Windows).", "")
                    : (false, "Não foi possível exportar os drivers. Execute como Administrador e verifique permissões da pasta.", "");
            }

            return (true, $"{exportedDirs} pacote(s) de driver exportado(s) para {dest}.", dest);
        }

        public Task<RestoreDriversResult> ImportDriversAsync(
            string driversFolder,
            Action<string, double?>? progress = null,
            CancellationToken ct = default,
            Func<bool>? isAdminHook = null)
        {
            var backupSvc = new FormatBackupService(_runProcess, isAdminHook: isAdminHook ?? IsAdministrator);
            return backupSvc.RestoreDriversAsync(driversFolder, progress, ct);
        }

        public bool IsRebootPending()
        {
            try
            {
                var type = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo");
                if (type != null)
                {
                    dynamic sysInfo = Activator.CreateInstance(type)!;
                    if ((bool)sysInfo.RebootRequired) return true;
                }
            }
            catch
            {
            }

            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var cbs = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
                using var wu = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                return cbs != null || wu != null;
            }
            catch
            {
                return false;
            }
        }

        public Task<List<WindowsUpdateItemModel>> SearchWindowsUpdatesAsync(CancellationToken ct = default)
        {
            if (_searchUpdatesHook != null)
                return _searchUpdatesHook(ct);

            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var list = new List<WindowsUpdateItemModel>();
                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
                    ?? throw new InvalidOperationException("O serviço Windows Update (COM) não está disponível.");

                dynamic session = Activator.CreateInstance(sessionType)!;
                try { session.ClientApplicationID = "Hardware HUB"; } catch { }

                dynamic searcher = session.CreateUpdateSearcher();
                dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
                int count = result.Updates.Count;

                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    dynamic upd = result.Updates.Item(i);

                    string kb = "";
                    try { if (upd.KBArticleIDs.Count > 0) kb = "KB" + (string)upd.KBArticleIDs.Item(0); } catch { }

                    long size = 0;
                    try { size = Convert.ToInt64(upd.MaxDownloadSize); } catch { }

                    bool isDriver = false;
                    bool isOpt = false;
                    bool reqInput = false;
                    try { isDriver = (int)upd.Type == 2; } catch { }
                    try { isOpt = (bool)upd.BrowseOnly; } catch { }
                    try { reqInput = (bool)upd.InstallationBehavior.CanRequestUserInput; } catch { }

                    list.Add(new WindowsUpdateItemModel
                    {
                        Title = (string)upd.Title,
                        KbId = kb,
                        SizeBytes = size,
                        IsDriver = isDriver,
                        IsOptional = isOpt,
                        RequiresUserInput = reqInput,
                        IsSelected = !isOpt,
                        NativeComUpdate = (object)upd
                    });
                }

                return list;
            }, ct);
        }

        public Task<WindowsUpdateRunResult> DownloadAndInstallUpdatesAsync(
            IReadOnlyList<WindowsUpdateItemModel> selected,
            Action<string>? onStage = null,
            CancellationToken ct = default)
        {
            if (_installUpdatesHook != null)
                return _installUpdatesHook(selected, onStage, ct);

            if (selected.Count == 0)
                return Task.FromResult(new WindowsUpdateRunResult(false, "Nenhuma atualização selecionada.", false));

            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
                    ?? throw new InvalidOperationException("Windows Update indisponível.");
                var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")
                    ?? throw new InvalidOperationException("UpdateColl indisponível.");

                dynamic coll = Activator.CreateInstance(collType)!;
                bool allDownloaded = true;

                foreach (var item in selected)
                {
                    if (item.NativeComUpdate == null) continue;
                    dynamic com = item.NativeComUpdate;
                    try { if (!(bool)com.EulaAccepted) com.AcceptEula(); } catch { }
                    try { allDownloaded &= (bool)com.IsDownloaded; } catch { allDownloaded = false; }
                    coll.Add(com);
                }

                if (coll.Count == 0)
                    return new WindowsUpdateRunResult(false, "Itens selecionados não possuem referência ativa do Windows Update.", false);

                dynamic session = Activator.CreateInstance(sessionType)!;

                if (!allDownloaded)
                {
                    onStage?.Invoke($"Baixando {coll.Count} atualização(ões)...");
                    dynamic downloader = session.CreateUpdateDownloader();
                    downloader.Updates = coll;
                    dynamic dlRes = downloader.Download();
                    int dlCode = (int)dlRes.ResultCode;
                    if (dlCode != 2 && dlCode != 3)
                        return new WindowsUpdateRunResult(false, $"Falha ao baixar atualizações (código {dlCode}).", false);
                }

                ct.ThrowIfCancellationRequested();
                onStage?.Invoke($"Instalando {coll.Count} atualização(ões)...");
                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = coll;
                dynamic instRes = installer.Install();
                int resCode = (int)instRes.ResultCode;
                bool reboot = (bool)instRes.RebootRequired;

                bool ok = resCode is 2 or 3;
                string msg = ok
                    ? (reboot ? "Instalado com sucesso. Reinicialização pendente." : "Atualizações instaladas com sucesso.")
                    : $"Falha ao instalar atualizações (código {resCode}, verifique se está como Administrador).";
                return new WindowsUpdateRunResult(ok, msg, reboot);
            }, ct);
        }

        public static void OpenWindowsUpdateDrivers() => OpenShellTarget("ms-settings:windowsupdate-optionalupdates");
        public static void OpenDeviceManager() => OpenShellTarget("devmgmt.msc");
        public static void OpenUrl(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                OpenShellTarget(url);
            }
        }

        private static void OpenShellTarget(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        private static string DetectGpuVendorFromPnp(string pnp)
        {
            if (pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
            if (pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return "AMD";
            if (pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return "Intel";
            return "";
        }

        private static string ParseWmiDate(string? wmiDate)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(wmiDate))
                    return ManagementDateTimeConverter.ToDateTime(wmiDate).ToString("dd/MM/yyyy");
            }
            catch
            {
            }
            return "—";
        }

        private static async Task<(int ExitCode, string Output)> DefaultRunProcessAsync(string exe, string args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {exe}.");
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            var stdout = await proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = await proc.StandardError.ReadToEndAsync(CancellationToken.None);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
        }
    }
}

