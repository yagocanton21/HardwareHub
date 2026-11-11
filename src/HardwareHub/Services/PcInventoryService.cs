using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    /// <summary>
    /// Coleta inventário detalhado do computador (Sistema, Ativação, Placa-mãe/BIOS/UEFI,
    /// CPU, Módulos RAM JEDEC/Dual-Channel/XMP, GPUs com VRAM &gt; 4 GB via registro e Unidades).
    /// </summary>
    public sealed class PcInventoryService
    {
        private static readonly Dictionary<string, string> JedecManufacturers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["80CE"] = "Samsung", ["00CE"] = "Samsung", ["CE00"] = "Samsung",
            ["802C"] = "Micron",  ["002C"] = "Micron",  ["2C00"] = "Micron",
            ["80AD"] = "SK Hynix",["00AD"] = "SK Hynix",["AD00"] = "SK Hynix",
            ["0198"] = "Kingston",["9801"] = "Kingston",["8198"] = "Kingston",
            ["029E"] = "Corsair", ["9E02"] = "Corsair", ["829E"] = "Corsair",
            ["04CD"] = "G.Skill", ["CD04"] = "G.Skill", ["84CD"] = "G.Skill",
            ["04CB"] = "ADATA",   ["CB04"] = "ADATA",   ["84CB"] = "ADATA",
            ["059B"] = "Crucial", ["9B05"] = "Crucial", ["859B"] = "Crucial"
        };

        private static readonly Regex SpeedInPartRegex = new(
            @"(?<!\d)(2133|2400|2666|2933|3000|3200|3466|3600|3733|3800|4000|4133|4266|4400|4800|5200|5600|5800|6000|6200|6400|6600|6800|7000|7200|7400|7600|7800|8000|8200|8400|8800)(?!\d)",
            RegexOptions.Compiled);

        private static readonly Regex ChannelInSlotRegex = new(
            @"(?i)(?:channel|chan|ch)[\s_-]*([A-H0-9])|dimm[\s_-]*([A-H])\d|(?:^|[^A-Za-z])([A-H])[12](?:$|[^0-9])",
            RegexOptions.Compiled);

        private readonly Func<SystemInventoryReport>? _collectHook;
        private readonly DiskHealthService _diskHealthService;

        public PcInventoryService(
            Func<SystemInventoryReport>? collectHook = null,
            DiskHealthService? diskHealthService = null)
        {
            _collectHook = collectHook;
            _diskHealthService = diskHealthService ?? new DiskHealthService();
        }

        public Task<SystemInventoryReport> CollectAsync(CancellationToken ct = default)
        {
            return Task.Run(() => Collect(ct), ct);
        }

        public SystemInventoryReport Collect(CancellationToken ct = default)
        {
            if (_collectHook != null)
                return _collectHook();

            var report = new SystemInventoryReport();
            var activationTask = Task.Run(ReadActivationStatus, ct);

            SafeRead(report, "Sistema", () => ReadSystem(report));
            SafeRead(report, "Placa-mãe", () => ReadMotherboard(report));
            SafeRead(report, "Processador", () => ReadProcessor(report));
            SafeRead(report, "Memória RAM", () => ReadMemory(report));
            SafeRead(report, "Placa de vídeo", () => ReadVideo(report));
            SafeRead(report, "Unidades", () => ReadVolumes(report));
            SafeRead(report, "Discos S.M.A.R.T.", () =>
            {
                report.Disks = _diskHealthService.GetDisksHealth();
            });

            try
            {
                string? act = activationTask.Wait(TimeSpan.FromSeconds(15), ct) ? activationTask.Result : null;
                report.SystemLines.Insert(Math.Min(4, report.SystemLines.Count),
                    new PcInfoLine("Ativação", act ?? "Não foi possível verificar"));
            }
            catch { }

            return report;
        }

        public static string ResolveJedecManufacturer(string? raw)
        {
            string s = (raw ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(s)) return "—";

            string cleaned = s.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (cleaned.Length >= 4 && JedecManufacturers.TryGetValue(cleaned[..4], out string? jedec))
                return jedec;

            if (s.Contains("Hynix", StringComparison.OrdinalIgnoreCase)) return "SK Hynix";
            if (s.StartsWith("Samsung", StringComparison.OrdinalIgnoreCase)) return "Samsung";
            if (s.StartsWith("Micron", StringComparison.OrdinalIgnoreCase)) return "Micron";
            return CleanPlaceholder(s);
        }

        public static string? ExtractMemoryChannel(string? slotOrBank)
        {
            if (string.IsNullOrWhiteSpace(slotOrBank))
                return null;

            var match = ChannelInSlotRegex.Match(slotOrBank);
            if (!match.Success) return null;

            for (int i = 1; i <= 3; i++)
            {
                if (match.Groups[i].Success)
                    return match.Groups[i].Value.ToUpperInvariant();
            }
            return null;
        }

        public static string DetectMemoryChannelMode(IReadOnlyList<RamModuleDetailModel> modules)
        {
            if (modules == null || modules.Count == 0)
                return "—";
            if (modules.Count == 1)
                return "Canal único (1 pente). Com 2 pentes iguais a memória roda em dual channel e ganha largura de banda.";

            var channels = modules
                .Select(m => ExtractMemoryChannel(m.Slot) ?? ExtractMemoryChannel(m.BankLabel))
                .ToList();

            if (channels.All(c => c != null))
            {
                int distinct = channels.Distinct().Count();
                return distinct >= 2
                    ? $"Dual channel ({distinct} canais)"
                    : "Canal único: os pentes estão no mesmo canal. Troque para os slots indicados no manual da placa.";
            }

            return "Não deu para identificar pelos nomes dos slots. Confira no manual da placa.";
        }

        public static string EstimateXmpStatus(IReadOnlyList<RamModuleDetailModel> modules)
        {
            if (modules == null || modules.Count == 0)
                return "—";

            int speedInUse = modules.Where(m => m.ConfiguredSpeedMt > 0).Select(m => m.ConfiguredSpeedMt).DefaultIfEmpty(0).Min();
            int nominalSpeed = modules.Select(m => m.NominalSpeedMt).DefaultIfEmpty(0).Max();

            int partSpeed = 0;
            foreach (var m in modules)
            {
                var match = SpeedInPartRegex.Match(m.PartNumber ?? string.Empty);
                if (match.Success && int.TryParse(match.Value, out int parsed))
                    partSpeed = Math.Max(partSpeed, parsed);
            }

            if (speedInUse <= 0)
                return "Não foi possível ler a velocidade em uso.";
            if (partSpeed > 0 && speedInUse < partSpeed * 0.95)
                return $"Provavelmente DESLIGADO: roda a {speedInUse} MT/s, mas o pente suporta {partSpeed} MT/s. Ative o XMP/EXPO na BIOS.";
            if (partSpeed > 0)
                return $"Provavelmente ligado (roda a {speedInUse} MT/s).";
            if (nominalSpeed > 0 && speedInUse < nominalSpeed)
                return $"Roda abaixo do nominal ({speedInUse} de {nominalSpeed} MT/s). Confira o XMP/EXPO na BIOS.";
            return "Sem divergência aparente com a velocidade nominal.";
        }

        public static ulong ParseVramValue(object? raw)
        {
            if (raw is long l && l > 0) return (ulong)l;
            if (raw is ulong ul && ul > 0) return ul;
            if (raw is int i && i > 0) return (uint)i;
            if (raw is uint ui && ui > 0) return ui;
            if (raw is byte[] bytes)
            {
                if (bytes.Length >= 8) return BitConverter.ToUInt64(bytes, 0);
                if (bytes.Length >= 4) return BitConverter.ToUInt32(bytes, 0);
            }
            return 0;
        }

        private static void SafeRead(SystemInventoryReport report, string part, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"{part}: {ex.Message}");
            }
        }

        private static IEnumerable<ManagementObject> QueryWmi(string wql)
        {
            using var searcher = new ManagementObjectSearcher(wql);
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
                yield return obj;
        }

        private static string ReadWmiString(ManagementBaseObject mo, string prop)
        {
            string? val = mo[prop]?.ToString()?.Trim();
            return string.IsNullOrEmpty(val) ? "—" : val;
        }

        private static int ReadWmiInt(object? val)
        {
            try { return val != null ? Convert.ToInt32(val) : 0; }
            catch { return 0; }
        }

        private static ulong ReadWmiUlong(object? val)
        {
            try { return val != null ? Convert.ToUInt64(val) : 0UL; }
            catch { return 0UL; }
        }

        private static string FormatCimDate(object? cim)
        {
            try
            {
                if (cim == null) return "—";
                return ManagementDateTimeConverter.ToDateTime(cim.ToString()!).ToString("dd/MM/yyyy");
            }
            catch { return "—"; }
        }

        private static string CleanPlaceholder(string s)
        {
            if (s.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Default string", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("System Product Name", StringComparison.OrdinalIgnoreCase))
            {
                return "—";
            }
            return s;
        }

        private static string ReadCurrentVersionValue(string name)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                return key?.GetValue(name)?.ToString()?.Trim() ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private void ReadSystem(SystemInventoryReport report)
        {
            string productName = ReadCurrentVersionValue("ProductName");
            if (productName.Length == 0) productName = "Windows";

            int buildNumber = Environment.OSVersion.Version.Build;
            if (buildNumber >= 22000)
                productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);

            string displayVersion = ReadCurrentVersionValue("DisplayVersion");
            if (displayVersion.Length == 0) displayVersion = ReadCurrentVersionValue("ReleaseId");

            string ubr = ReadCurrentVersionValue("UBR");
            string fullBuild = ubr.Length > 0 ? $"{buildNumber}.{ubr}" : buildNumber.ToString();
            string edition = ReadCurrentVersionValue("EditionID");

            report.SystemSummary = $"{productName} {displayVersion}".Trim();
            report.SystemLines.Add(new PcInfoLine("Windows", productName));
            if (edition.Length > 0) report.SystemLines.Add(new PcInfoLine("Edição", edition));
            report.SystemLines.Add(new PcInfoLine("Versão", displayVersion.Length > 0 ? displayVersion : "—"));
            report.SystemLines.Add(new PcInfoLine("Build", fullBuild));

            foreach (var os in QueryWmi("SELECT OSArchitecture, InstallDate FROM Win32_OperatingSystem"))
            {
                report.SystemLines.Add(new PcInfoLine("Arquitetura", ReadWmiString(os, "OSArchitecture")));
                report.SystemLines.Add(new PcInfoLine("Instalado em", FormatCimDate(os["InstallDate"])));
            }

            report.SystemLines.Add(new PcInfoLine("Nome do computador", Environment.MachineName));
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            string uptimeText = uptime.TotalDays >= 1.0
                ? $"{(int)uptime.TotalDays} dia(s) e {uptime.Hours} h"
                : $"{uptime.Hours} h {uptime.Minutes} min";
            report.SystemLines.Add(new PcInfoLine("Ligado há", uptimeText));
        }

        private static string? ReadActivationStatus()
        {
            try
            {
                foreach (var sl in QueryWmi("SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL"))
                {
                    return ReadWmiInt(sl["LicenseStatus"]) switch
                    {
                        1 => "Ativado",
                        0 => "Não ativado",
                        2 or 3 or 4 or 6 => "Período de tolerância (não ativado)",
                        5 => "Não original (notificação)",
                        _ => "Desconhecido"
                    };
                }
                return "Sem chave instalada";
            }
            catch { return null; }
        }

        private void ReadMotherboard(SystemInventoryReport report)
        {
            string manufacturer = "—";
            string model = "—";

            foreach (var bb in QueryWmi("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
            {
                manufacturer = CleanPlaceholder(ReadWmiString(bb, "Manufacturer"));
                model = CleanPlaceholder(ReadWmiString(bb, "Product"));
                report.MotherboardLines.Add(new PcInfoLine("Fabricante", manufacturer));
                report.MotherboardLines.Add(new PcInfoLine("Modelo", model));
                string rev = CleanPlaceholder(ReadWmiString(bb, "Version"));
                if (rev != "—") report.MotherboardLines.Add(new PcInfoLine("Revisão", rev));
            }

            foreach (var cs in QueryWmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            {
                string csMfg = CleanPlaceholder(ReadWmiString(cs, "Manufacturer"));
                string csModel = CleanPlaceholder(ReadWmiString(cs, "Model"));
                if (csModel != "—" && !csModel.Equals(model, StringComparison.OrdinalIgnoreCase))
                    report.MotherboardLines.Add(new PcInfoLine("Computador", csMfg != "—" ? $"{csMfg} {csModel}" : csModel));
            }

            foreach (var bios in QueryWmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            {
                report.MotherboardLines.Add(new PcInfoLine("BIOS", $"{ReadWmiString(bios, "SMBIOSBIOSVersion")} ({ReadWmiString(bios, "Manufacturer")})"));
                report.MotherboardLines.Add(new PcInfoLine("Data da BIOS", FormatCimDate(bios["ReleaseDate"])));
            }

            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var ctrl = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
                object? fw = ctrl?.GetValue("PEFirmwareType");
                if (fw != null)
                {
                    report.MotherboardLines.Add(new PcInfoLine("Modo de inicialização", Convert.ToInt32(fw) == 2 ? "UEFI" : "Legado (BIOS)"));
                }
            }
            catch { }

            report.MotherboardSummary = (manufacturer != "—" || model != "—")
                ? $"{(manufacturer != "—" ? manufacturer + " " : "")}{(model != "—" ? model : "")}".Trim()
                : "—";
        }

        private void ReadProcessor(SystemInventoryReport report)
        {
            var names = new List<string>();
            foreach (var cpu in QueryWmi("SELECT Name, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled FROM Win32_Processor"))
            {
                string name = Regex.Replace(ReadWmiString(cpu, "Name"), @"\s+", " ");
                names.Add(name);
                report.CpuLines.Add(new PcInfoLine("Modelo", name));
                report.CpuLines.Add(new PcInfoLine("Núcleos / threads", $"{ReadWmiString(cpu, "NumberOfCores")} núcleos · {ReadWmiString(cpu, "NumberOfLogicalProcessors")} threads"));

                int mhz = ReadWmiInt(cpu["MaxClockSpeed"]);
                report.CpuLines.Add(new PcInfoLine("Clock base", mhz > 0 ? $"{mhz / 1000.0:0.00} GHz" : "—"));

                int l2 = ReadWmiInt(cpu["L2CacheSize"]);
                int l3 = ReadWmiInt(cpu["L3CacheSize"]);
                if (l2 > 0 || l3 > 0)
                {
                    var cacheParts = new[]
                    {
                        l2 > 0 ? $"L2 {FormatKb(l2)}" : "",
                        l3 > 0 ? $"L3 {FormatKb(l3)}" : ""
                    }.Where(x => x.Length > 0);
                    report.CpuLines.Add(new PcInfoLine("Cache", string.Join(" · ", cacheParts)));
                }

                report.CpuLines.Add(new PcInfoLine("Soquete", ReadWmiString(cpu, "SocketDesignation")));
                if (cpu["VirtualizationFirmwareEnabled"] is bool virt)
                    report.CpuLines.Add(new PcInfoLine("Virtualização", virt ? "Ativada na BIOS" : "Desativada na BIOS"));
            }

            report.CpuSummary = names.Count > 1 ? $"{names.Count}× {names[0]}" : (names.FirstOrDefault() ?? "—");
        }

        private void ReadMemory(SystemInventoryReport report)
        {
            bool hasEcc = false;
            foreach (var mem in QueryWmi("SELECT BankLabel, DeviceLocator, Capacity, SMBIOSMemoryType, MemoryType, FormFactor, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DataWidth, TotalWidth FROM Win32_PhysicalMemory"))
            {
                int dataWidth = ReadWmiInt(mem["DataWidth"]);
                int totalWidth = ReadWmiInt(mem["TotalWidth"]);
                if (dataWidth > 0 && totalWidth > dataWidth) hasEcc = true;

                report.RamModules.Add(new RamModuleDetailModel
                {
                    BankLabel = ReadWmiString(mem, "BankLabel"),
                    Slot = ReadWmiString(mem, "DeviceLocator"),
                    CapacityBytes = ReadWmiUlong(mem["Capacity"]),
                    Type = ResolveRamType(mem["SMBIOSMemoryType"], mem["MemoryType"]),
                    FormFactor = ResolveRamFormFactor(mem["FormFactor"]),
                    NominalSpeedMt = ReadWmiInt(mem["Speed"]),
                    ConfiguredSpeedMt = ReadWmiInt(mem["ConfiguredClockSpeed"]),
                    Manufacturer = ResolveJedecManufacturer(ReadWmiString(mem, "Manufacturer")),
                    PartNumber = ReadWmiString(mem, "PartNumber"),
                    DataWidth = dataWidth,
                    TotalWidth = totalWidth
                });
            }

            int totalSlots = 0;
            ulong maxArrayBytes = 0;
            try
            {
                foreach (var arr in QueryWmi("SELECT MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray"))
                {
                    totalSlots += ReadWmiInt(arr["MemoryDevices"]);
                    ulong maxKb = arr["MaxCapacityEx"] != null ? ReadWmiUlong(arr["MaxCapacityEx"]) : ReadWmiUlong(arr["MaxCapacity"]);
                    maxArrayBytes += maxKb * 1024;
                }
            }
            catch { }

            ulong installedBytes = report.RamModules.Aggregate(0UL, (acc, m) => acc + m.CapacityBytes);
            var (osTotal, osFree) = MemoryRamService.GetMemorySnapshot();
            if (installedBytes == 0) installedBytes = osTotal;

            var distinctTypes = report.RamModules.Select(m => m.Type).Where(t => t != "—").Distinct().ToList();
            int minConfigured = report.RamModules.Where(m => m.ConfiguredSpeedMt > 0).Select(m => m.ConfiguredSpeedMt).DefaultIfEmpty(0).Min();
            int maxNominal = report.RamModules.Select(m => m.NominalSpeedMt).DefaultIfEmpty(0).Max();

            report.MemorySummary = string.Join(" ", new[]
            {
                RamModuleDetailModel.FormatBytes((long)installedBytes),
                string.Join("/", distinctTypes),
                minConfigured > 0 ? $"{minConfigured} MT/s" : ""
            }.Where(x => x.Length > 0));

            report.MemoryLines.Add(new PcInfoLine("Total instalado", RamModuleDetailModel.FormatBytes((long)installedBytes)));
            if (osTotal > 0)
            {
                ulong inUse = osTotal > osFree ? osTotal - osFree : 0;
                double pct = (double)inUse * 100.0 / osTotal;
                report.MemoryLines.Add(new PcInfoLine("Em uso agora",
                    $"{RamModuleDetailModel.FormatBytes((long)inUse)} de {RamModuleDetailModel.FormatBytes((long)osTotal)} ({pct:0}%)"));
            }

            report.MemoryLines.Add(new PcInfoLine("Pentes",
                totalSlots > 0 ? $"{report.RamModules.Count} de {totalSlots} slots ocupados" : report.RamModules.Count.ToString()));
            if (maxArrayBytes > 0)
                report.MemoryLines.Add(new PcInfoLine("Máximo da placa", RamModuleDetailModel.FormatBytes((long)maxArrayBytes)));
            if (distinctTypes.Count > 0)
                report.MemoryLines.Add(new PcInfoLine("Tipo", string.Join(" / ", distinctTypes)));
            if (minConfigured > 0)
                report.MemoryLines.Add(new PcInfoLine("Velocidade em uso", $"{minConfigured} MT/s"));
            if (maxNominal > 0 && maxNominal != minConfigured)
                report.MemoryLines.Add(new PcInfoLine("Velocidade dos pentes", $"{maxNominal} MT/s"));

            if (report.RamModules.Count > 0)
            {
                report.MemoryLines.Add(new PcInfoLine("Canais (estimado)", DetectMemoryChannelMode(report.RamModules)));
                report.MemoryLines.Add(new PcInfoLine("XMP/EXPO (estimado)", EstimateXmpStatus(report.RamModules)));
                report.MemoryLines.Add(new PcInfoLine("ECC", hasEcc ? "Sim (corrige erros)" : "Não"));
            }
        }

        private void ReadVideo(SystemInventoryReport report)
        {
            foreach (var gpu in QueryWmi("SELECT Name, PNPDeviceID, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
            {
                string name = ReadWmiString(gpu, "Name");
                string pnp = gpu["PNPDeviceID"]?.ToString() ?? string.Empty;
                if (pnp.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) && !name.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                    continue;

                ulong vram = ReadVramFromRegistry(pnp);
                if (vram == 0) vram = ReadWmiUlong(gpu["AdapterRAM"]);

                string driverVer = ReadWmiString(gpu, "DriverVersion");
                string driverDate = FormatCimDate(gpu["DriverDate"]);
                int w = ReadWmiInt(gpu["CurrentHorizontalResolution"]);
                int h = ReadWmiInt(gpu["CurrentVerticalResolution"]);
                int hz = ReadWmiInt(gpu["CurrentRefreshRate"]);
                string resolution = (w > 0 && h > 0)
                    ? (hz > 1 ? $"{w} × {h} a {hz} Hz" : $"{w} × {h}")
                    : "—";

                var detail = new GpuInventoryDetailModel
                {
                    Name = name,
                    PnpDeviceId = pnp,
                    VramBytes = vram,
                    DriverVersion = driverVer,
                    DriverDate = driverDate,
                    Resolution = resolution,
                    Lines = new List<PcInfoLine>
                    {
                        new("Memória de vídeo (VRAM)", vram > 0 ? RamModuleDetailModel.FormatBytes((long)vram) : "—"),
                        new("Driver", driverVer),
                        new("Data do driver", driverDate)
                    }
                };
                if (resolution != "—")
                    detail.Lines.Add(new("Resolução", resolution));

                report.VideoAdapters.Add(detail);
            }
        }

        private static ulong ReadVramFromRegistry(string pnp)
        {
            if (string.IsNullOrWhiteSpace(pnp)) return 0;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var enumKey = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnp}");
                if (enumKey?.GetValue("Driver") is not string driverSubKey)
                    return 0;

                using var classKey = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverSubKey}");
                if (classKey == null) return 0;

                foreach (string valueName in new[] { "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" })
                {
                    ulong parsed = ParseVramValue(classKey.GetValue(valueName));
                    if (parsed > 0) return parsed;
                }
            }
            catch { }
            return 0;
        }

        private void ReadVolumes(SystemInventoryReport report)
        {
            var diskMap = BuildDriveLetterDiskMap();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    string letter = drive.Name.TrimEnd('\\');
                    diskMap.TryGetValue(letter, out var info);
                    bool isUsb = string.Equals(info.Bus, "USB", StringComparison.OrdinalIgnoreCase);

                    string kind = drive.DriveType switch
                    {
                        DriveType.Removable => "Removível",
                        DriveType.Fixed => isUsb ? "Externo (USB)" : "Interno",
                        DriveType.Network => "Rede",
                        DriveType.CDRom => "CD/DVD",
                        _ => drive.DriveType.ToString()
                    };

                    report.Volumes.Add(new VolumeInventoryModel
                    {
                        DriveLetter = letter,
                        Label = drive.VolumeLabel,
                        Kind = kind,
                        FileSystem = drive.DriveFormat,
                        DiskModel = info.Model ?? string.Empty,
                        TotalBytes = drive.TotalSize,
                        FreeBytes = drive.TotalFreeSpace,
                        IsRemovable = drive.DriveType == DriveType.Removable || isUsb
                    });
                }
                catch { }
            }

            report.Volumes.Sort((a, b) => string.Compare(a.DriveLetter, b.DriveLetter, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, (string? Model, string? Bus)> BuildDriveLetterDiskMap()
        {
            var result = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var disks = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
                foreach (var disk in QueryWmi("SELECT DeviceID, Model, InterfaceType, PNPDeviceID FROM Win32_DiskDrive"))
                {
                    string? bus = disk["InterfaceType"]?.ToString();
                    if (disk["PNPDeviceID"]?.ToString()?.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase) == true)
                        bus = "USB";

                    string? devId = disk["DeviceID"]?.ToString();
                    if (devId != null)
                        disks[devId] = (disk["Model"]?.ToString()?.Trim(), bus);
                }

                var partToDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var link in QueryWmi("SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
                {
                    string? ant = ExtractDeviceId(link["Antecedent"]);
                    string? dep = ExtractDeviceId(link["Dependent"]);
                    if (ant != null && dep != null) partToDisk[dep] = ant;
                }

                foreach (var link in QueryWmi("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
                {
                    string? part = ExtractDeviceId(link["Antecedent"]);
                    string? letter = ExtractDeviceId(link["Dependent"]);
                    if (part != null && letter != null && partToDisk.TryGetValue(part, out var diskId) && disks.TryGetValue(diskId, out var meta))
                        result[letter] = meta;
                }
            }
            catch { }
            return result;
        }

        private static string? ExtractDeviceId(object? wmiRef)
        {
            var match = Regex.Match(wmiRef?.ToString() ?? "", @"DeviceID=""(.*)""");
            return match.Success ? match.Groups[1].Value.Replace(@"\\", @"\") : null;
        }

        private static string FormatKb(int kb) => kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} KB";

        private static string ResolveRamType(object? smbios, object? legacy)
        {
            string mapped = ReadWmiInt(smbios) switch
            {
                20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4",
                27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4",
                34 => "DDR5", 35 => "LPDDR5", _ => ""
            };
            if (mapped.Length > 0) return mapped;

            return ReadWmiInt(legacy) switch
            {
                20 => "DDR", 21 => "DDR2", 24 => "DDR3", _ => "—"
            };
        }

        private static string ResolveRamFormFactor(object? ff) => ReadWmiInt(ff) switch
        {
            8 => "DIMM (desktop)",
            12 => "SO-DIMM (notebook)",
            13 => "Micro-DIMM",
            0 => "—",
            _ => "Outro"
        };
    }
}

