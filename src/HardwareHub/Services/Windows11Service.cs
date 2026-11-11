using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public sealed class Windows11Service
    {
        private const uint TbsTpmNotFound = 0x8028400F;
        private const uint TbsServiceNotRunning = 0x80284008;

        public sealed record CpuSnapshot(string Name, int Cores, int MaxClockMhz, int Architecture);
        public sealed record GpuSnapshot(string Name, string DriverVersion, int Width, int Height);
        public sealed record HardwareProbeSnapshot(
            string WindowsEdition,
            int CurrentBuild,
            CpuSnapshot Cpu,
            bool IsSse42Supported,
            bool IsPopcntSupported,
            double RamGb,
            long SystemDriveTotalBytes,
            long SystemDriveFreeBytes,
            string SystemDriveLetter,
            bool? IsUefi,
            int? SecureBootEnabledReg,
            uint TpmTbsResult,
            uint TpmVersion,
            string TpmWmiDetails,
            List<GpuSnapshot> Gpus,
            bool? HasDirectX12);

        private readonly Func<HardwareProbeSnapshot> _probeSnapshot;

        public Windows11Service(Func<HardwareProbeSnapshot>? probeHook = null)
        {
            _probeSnapshot = probeHook ?? ReadLiveHardwareSnapshot;
        }

        public Task<Windows11Report> VerifyAsync(CancellationToken ct = default)
        {
            return Task.Run(() => Evaluate(_probeSnapshot(), ct), ct);
        }

        public static Windows11Report Evaluate(HardwareProbeSnapshot s, CancellationToken ct = default)
        {
            var items = new List<Windows11RequirementItem>();

            ct.ThrowIfCancellationRequested();
            items.Add(CheckCpuList(s.Cpu));

            ct.ThrowIfCancellationRequested();
            items.Add(CheckCpuBasic(s.Cpu));

            ct.ThrowIfCancellationRequested();
            items.Add(CheckSsePopcnt(s.Cpu.Architecture, s.IsSse42Supported, s.IsPopcntSupported));

            ct.ThrowIfCancellationRequested();
            items.Add(CheckRam(s.RamGb));

            ct.ThrowIfCancellationRequested();
            items.Add(CheckStorage(s.SystemDriveLetter, s.SystemDriveTotalBytes, s.SystemDriveFreeBytes));

            ct.ThrowIfCancellationRequested();
            items.Add(CheckUefi(s.IsUefi));

            ct.ThrowIfCancellationRequested();
            items.Add(CheckSecureBoot(s.IsUefi, s.SecureBootEnabledReg));

            ct.ThrowIfCancellationRequested();
            items.Add(CheckTpm(s.TpmTbsResult, s.TpmVersion, s.TpmWmiDetails));

            ct.ThrowIfCancellationRequested();
            items.Add(CheckDirectX(s.Gpus, s.HasDirectX12));

            ct.ThrowIfCancellationRequested();
            items.Add(CheckDisplayResolution(s.Gpus));

            return new Windows11Report
            {
                WindowsEdition = s.WindowsEdition,
                CurrentBuild = s.CurrentBuild,
                Items = items
            };
        }

        public static (Windows11CheckStatus Status, string Reason) ClassifyCpu(string name)
        {
            string n = Regex.Replace(name ?? "", "\\((R|TM|C)\\)|®|™", " ", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, "\\s+", " ").Trim();

            bool Has(string pattern) => Regex.IsMatch(n, pattern, RegexOptions.IgnoreCase);
            Match MatchOf(string pattern) => Regex.Match(n, pattern, RegexOptions.IgnoreCase);

            if (Has("Snapdragon|Qualcomm|Microsoft SQ\\d"))
                return (Windows11CheckStatus.Ok, "Qualcomm Snapdragon homologado");

            if (Has("\\bIntel\\b|\\bCore\\b|Pentium|Celeron|Xeon|Atom"))
            {
                if (Has("Core Ultra"))
                    return (Windows11CheckStatus.Ok, "Intel Core Ultra, homologado");

                if (Has("\\bCore [3579] \\d{3}[A-Z]*\\b"))
                    return (Windows11CheckStatus.Ok, "Intel Core Série 1+, homologado");

                var coreMatch = MatchOf("\\bi[3579]-(\\d{3,5})([A-Z]*\\d?)");
                var yMatch = MatchOf("\\bi[3579]-(\\d)Y\\d{2}");
                if (coreMatch.Success || yMatch.Success)
                {
                    int gen;
                    if (yMatch.Success)
                    {
                        gen = yMatch.Groups[1].Value[0] - '0';
                    }
                    else
                    {
                        string digits = coreMatch.Groups[1].Value;
                        gen = digits.Length == 3
                            ? 1
                            : (digits.Length == 5 || digits[0] == '1' ? int.Parse(digits[..2]) : (digits[0] - '0'));
                    }

                    if (gen >= 8)
                        return (Windows11CheckStatus.Ok, $"Intel Core de {gen}ª geração, homologado");

                    if (coreMatch.Success && Has("\\bi[79]-(7800X|7820X|7900X|7920X|7940X|7960X|7980XE)\\b"))
                        return (Windows11CheckStatus.Ok, "Intel Core X série 7000X, homologado");

                    return (Windows11CheckStatus.Fail, $"Intel Core de {gen}ª geração: suporte oficial começa na 8ª geração");
                }

                if (Has("\\bi[3579] CPU"))
                    return (Windows11CheckStatus.Fail, "Intel Core 1ª geração: fora da lista oficial");

                if (Has("Core ?2|Core Duo|Core Solo|Pentium ?(4|D|M|Dual|III|II)\\b"))
                    return (Windows11CheckStatus.Fail, "Processador antigo, fora da lista da Microsoft");

                if (Has("Xeon"))
                {
                    return Has("\\bE-2\\d{3}|\\bW-[123]\\d{3}|\\bW-1[01]\\d{3}M|\\b(Bronze|Silver|Gold|Platinum) \\d{4}|\\bD-(17|18|27|28)\\d{2}|\\bw[3579]-\\d{4}|Xeon 6\\d{3}|Xeon Max")
                        ? (Windows11CheckStatus.Ok, "Intel Xeon homologado")
                        : (Windows11CheckStatus.Fail, "Intel Xeon de geração antiga, fora da lista oficial");
                }

                if (Has("\\b(N50|N97|N100|N150|N200|N250|N300|N305|N350|N355|U300E?|Processor 300T?)\\b"))
                    return (Windows11CheckStatus.Ok, "Intel Processor série N/U homologado");
            }

            if (Has("\\bAMD\\b|Ryzen|Athlon|EPYC|Opteron|Phenom|Sempron|\\bFX-"))
            {
                if (Has("Ryzen AI|Ryzen Z\\d"))
                    return (Windows11CheckStatus.Ok, "AMD Ryzen homologado");

                var tr = MatchOf("Threadripper (PRO )?(\\d)\\d{3}");
                if (tr.Success)
                {
                    return tr.Groups[2].Value[0] < '3'
                        ? (Windows11CheckStatus.Fail, "AMD Threadripper 1ª/2ª geração: fora da lista oficial")
                        : (Windows11CheckStatus.Ok, "AMD Threadripper homologado");
                }

                var ryzen = MatchOf("Ryzen (?:[3579] )?(?:PRO )?(\\d)(\\d{3})([A-Z]*)");
                if (ryzen.Success)
                {
                    int series = ryzen.Groups[1].Value[0] - '0';
                    string suffix = ryzen.Groups[3].Value.ToUpperInvariant();
                    if (series >= 3)
                        return (Windows11CheckStatus.Ok, $"AMD Ryzen série {series}000, homologado");
                    if (series == 2 && !suffix.StartsWith('U') && !suffix.StartsWith('H') && !suffix.StartsWith('G'))
                        return (Windows11CheckStatus.Ok, "AMD Ryzen 2000 desktop (Zen+), homologado");
                    return (Windows11CheckStatus.Fail, series == 2
                        ? "AMD Ryzen 2000 com gráficos integrados/notebook (Zen 1): fora da lista oficial"
                        : "AMD Ryzen 1000 (1ª geração): fora da lista oficial");
                }

                if (Has("Athlon|\\b30(15|20)e\\b"))
                {
                    return Has("\\b(3000G|300GE|300U|320GE|3045B|3050(C|E|GE|U)|3125GE|3150(C|G|GE|U)|4150GE|7120(E|U)|7220(E|U)|30(15|20)E)\\b")
                        ? (Windows11CheckStatus.Ok, "AMD Athlon homologado")
                        : (Windows11CheckStatus.Fail, "AMD Athlon antigo, fora da lista oficial");
                }

                var epyc = MatchOf("EPYC (\\w{4})");
                if (epyc.Success)
                {
                    return epyc.Groups[1].Value.EndsWith('1')
                        ? (Windows11CheckStatus.Fail, "AMD EPYC 1ª geração, fora da lista")
                        : (Windows11CheckStatus.Ok, "AMD EPYC homologado");
                }

                return (Windows11CheckStatus.Fail, "Processador AMD legado (FX/A-Series/Phenom), fora da lista oficial");
            }

            return (Windows11CheckStatus.Unknown, "Modelo não catalogado automaticamente");
        }

        private static Windows11RequirementItem CheckCpuList(CpuSnapshot cpu)
        {
            var (st, reason) = ClassifyCpu(cpu.Name);
            return new Windows11RequirementItem
            {
                Key = "cpu",
                Title = "Processador na lista oficial",
                Status = st,
                Found = $"{cpu.Name} • {reason}",
                Required = "CPU suportado pela Microsoft",
                Recommendation = st switch
                {
                    Windows11CheckStatus.Ok => "Atende ao requisito.",
                    Windows11CheckStatus.Fail => "Requer processador compatível para atualização oficial via Windows Update.",
                    _ => "Confira no app oficial 'Verificação de Integridade do PC'."
                }
            };
        }

        private static Windows11RequirementItem CheckCpuBasic(CpuSnapshot cpu)
        {
            bool is64 = cpu.Architecture is 9 or 12 || (cpu.Architecture == -1 && Environment.Is64BitOperatingSystem);
            bool ok = is64 && cpu.Cores >= 2 && (cpu.MaxClockMhz == 0 || cpu.MaxClockMhz >= 1000);
            string found = $"{(is64 ? "64 bits" : "32 bits")} • {cpu.Cores} núcleo(s)" +
                           (cpu.MaxClockMhz > 0 ? $" • {cpu.MaxClockMhz / 1000.0:0.0#} GHz" : "");

            return new Windows11RequirementItem
            {
                Key = "cpu-basic",
                Title = "Arquitetura 64 bits, 2+ núcleos, 1+ GHz",
                Status = ok ? Windows11CheckStatus.Ok : Windows11CheckStatus.Fail,
                Found = found,
                Required = "64 bits, >= 2 núcleos, >= 1 GHz",
                Recommendation = ok ? "Atende ao requisito." : "Hardware abaixo do mínimo básico exigido."
            };
        }

        private static Windows11RequirementItem CheckSsePopcnt(int arch, bool sse42, bool popcnt)
        {
            if (arch == 12) // ARM64
            {
                return new Windows11RequirementItem
                {
                    Key = "sse",
                    Title = "Instruções SSE4.2 e POPCNT (24H2+)",
                    Status = Windows11CheckStatus.Ok,
                    Found = "Não aplicável (ARM64)",
                    Required = "SSE4.2 e POPCNT",
                    Recommendation = "Atende."
                };
            }

            bool ok = sse42 && popcnt;
            return new Windows11RequirementItem
            {
                Key = "sse",
                Title = "Instruções SSE4.2 e POPCNT (24H2+)",
                Status = ok ? Windows11CheckStatus.Ok : Windows11CheckStatus.Fail,
                Found = $"SSE4.2: {(sse42 ? "Sim" : "Não")} • POPCNT: {(popcnt ? "Sim" : "Não")}",
                Required = "SSE4.2 + POPCNT obrigatórias no 24H2+",
                Recommendation = ok ? "Atende." : "Sem SSE4.2/POPCNT o Windows 11 24H2+ não inicializa."
            };
        }

        private static Windows11RequirementItem CheckRam(double ramGb)
        {
            bool ok = ramGb >= 3.9;
            return new Windows11RequirementItem
            {
                Key = "ram",
                Title = "Memória RAM",
                Status = ok ? Windows11CheckStatus.Ok : Windows11CheckStatus.Fail,
                Found = $"{ramGb:0.#} GB",
                Required = "4 GB ou mais",
                Recommendation = !ok
                    ? "Expanda para pelo menos 4 GB (recomendado 8 GB+)."
                    : (ramGb < 8.0 ? "Atende ao mínimo, mas 8 GB+ garante melhor fluidez." : "Atende.")
            };
        }

        private static Windows11RequirementItem CheckStorage(string letter, long totalBytes, long freeBytes)
        {
            double totalGb = totalBytes / 1_000_000_000.0;
            bool ok = totalGb >= 64.0;
            return new Windows11RequirementItem
            {
                Key = "storage",
                Title = "Armazenamento do Sistema",
                Status = ok ? Windows11CheckStatus.Ok : Windows11CheckStatus.Fail,
                Found = $"{letter} {totalGb:0} GB ({InstalledProgramItem.FormatBytes(freeBytes)} livres)",
                Required = "64 GB ou mais no disco do Windows",
                Recommendation = !ok
                    ? "Instale um SSD com 240 GB ou mais para melhor desempenho."
                    : (freeBytes < 20L * 1024 * 1024 * 1024 ? "Libere pelo menos 20 GB na Limpeza Segura antes de atualizar." : "Atende.")
            };
        }

        private static Windows11RequirementItem CheckUefi(bool? isUefi)
        {
            return isUefi switch
            {
                true => new Windows11RequirementItem
                {
                    Key = "uefi",
                    Title = "Firmware UEFI",
                    Status = Windows11CheckStatus.Ok,
                    Found = "UEFI",
                    Required = "UEFI",
                    Recommendation = "Atende."
                },
                false => new Windows11RequirementItem
                {
                    Key = "uefi",
                    Title = "Firmware UEFI",
                    Status = Windows11CheckStatus.Fail,
                    Found = "Legacy / CSM (BIOS antiga)",
                    Required = "UEFI",
                    Recommendation = "Requer conversão do disco MBR -> GPT (mbr2gpt) e ativação de UEFI na BIOS."
                },
                _ => new Windows11RequirementItem
                {
                    Key = "uefi",
                    Title = "Firmware UEFI",
                    Status = Windows11CheckStatus.Unknown,
                    Found = "Não detectado",
                    Required = "UEFI",
                    Recommendation = "Abra msinfo32 e verifique 'Modo da BIOS'."
                }
            };
        }

        private static Windows11RequirementItem CheckSecureBoot(bool? isUefi, int? regValue)
        {
            if (isUefi == false)
            {
                return new Windows11RequirementItem
                {
                    Key = "secureboot",
                    Title = "Inicialização Segura (Secure Boot)",
                    Status = Windows11CheckStatus.Fail,
                    Found = "Indisponível (modo Legacy)",
                    Required = "Compatível com Secure Boot",
                    Recommendation = "Resolva o modo UEFI primeiro para liberar o Secure Boot."
                };
            }

            if (regValue.HasValue)
            {
                bool active = regValue.Value == 1;
                return new Windows11RequirementItem
                {
                    Key = "secureboot",
                    Title = "Inicialização Segura (Secure Boot)",
                    Status = Windows11CheckStatus.Ok,
                    Found = active ? "Ativada" : "Compatível (desligada na BIOS)",
                    Required = "Compatível com Secure Boot",
                    Recommendation = active ? "Atende." : "Já é compatível; opcionalmente ative na aba Security/Boot da BIOS."
                };
            }

            return new Windows11RequirementItem
            {
                Key = "secureboot",
                Title = "Inicialização Segura (Secure Boot)",
                Status = isUefi == true ? Windows11CheckStatus.Ok : Windows11CheckStatus.Unknown,
                Found = isUefi == true ? "Capaz (UEFI)" : "Indeterminado",
                Required = "Compatível com Secure Boot",
                Recommendation = isUefi == true ? "Atende." : "Consulte msinfo32."
            };
        }

        private static Windows11RequirementItem CheckTpm(uint tbsResult, uint tpmVer, string wmiDetails)
        {
            if (tbsResult == 0)
            {
                return tpmVer switch
                {
                    2 => new Windows11RequirementItem
                    {
                        Key = "tpm",
                        Title = "TPM 2.0",
                        Status = Windows11CheckStatus.Ok,
                        Found = "TPM 2.0" + wmiDetails,
                        Required = "TPM versão 2.0",
                        Recommendation = "Atende."
                    },
                    1 => new Windows11RequirementItem
                    {
                        Key = "tpm",
                        Title = "TPM 2.0",
                        Status = Windows11CheckStatus.Fail,
                        Found = "TPM 1.2" + wmiDetails,
                        Required = "TPM versão 2.0",
                        Recommendation = "Verifique no site do fabricante se há atualização de firmware para TPM 2.0."
                    },
                    _ => new Windows11RequirementItem
                    {
                        Key = "tpm",
                        Title = "TPM 2.0",
                        Status = Windows11CheckStatus.Unknown,
                        Found = "Versão não informada" + wmiDetails,
                        Required = "TPM versão 2.0",
                        Recommendation = "Abra tpm.msc para conferir."
                    }
                };
            }

            if (tbsResult == TbsTpmNotFound)
            {
                return new Windows11RequirementItem
                {
                    Key = "tpm",
                    Title = "TPM 2.0",
                    Status = Windows11CheckStatus.Fail,
                    Found = "Não encontrado ou desligado na BIOS",
                    Required = "TPM versão 2.0",
                    Recommendation = "Ative AMD fTPM (PSP fTPM) ou Intel PTT nas configurações de Segurança da BIOS/UEFI."
                };
            }

            return new Windows11RequirementItem
            {
                Key = "tpm",
                Title = "TPM 2.0",
                Status = Windows11CheckStatus.Unknown,
                Found = tbsResult == TbsServiceNotRunning ? "Serviço TPM parado" : $"Código 0x{tbsResult:X8}",
                Required = "TPM versão 2.0",
                Recommendation = "Reinicie o PC e confira em tpm.msc."
            };
        }

        private static Windows11RequirementItem CheckDirectX(List<GpuSnapshot> gpus, bool? hasDx12)
        {
            var mainGpu = gpus.FirstOrDefault(g => g.Width > 0) ?? gpus.FirstOrDefault();
            string name = !string.IsNullOrWhiteSpace(mainGpu?.Name) ? mainGpu.Name : "GPU";
            string wddm = mainGpu != null ? ParseWddm(mainGpu.DriverVersion) ?? "" : "";
            string found = $"{name} • DirectX 12: {(hasDx12 == true ? "Sim" : (hasDx12 == false ? "Não" : "?"))}" +
                           (string.IsNullOrEmpty(wddm) ? "" : $" • {wddm}");

            if (hasDx12 == true)
            {
                return new Windows11RequirementItem
                {
                    Key = "directx",
                    Title = "Placa de vídeo DirectX 12 / WDDM 2.0",
                    Status = Windows11CheckStatus.Ok,
                    Found = found,
                    Required = "DirectX 12 + WDDM 2.0+",
                    Recommendation = "Atende."
                };
            }

            return new Windows11RequirementItem
            {
                Key = "directx",
                Title = "Placa de vídeo DirectX 12 / WDDM 2.0",
                Status = hasDx12 == false ? Windows11CheckStatus.Fail : Windows11CheckStatus.Unknown,
                Found = found,
                Required = "DirectX 12 + WDDM 2.0+",
                Recommendation = hasDx12 == false
                    ? "Instale/atualize o driver de vídeo ou utilize GPU com suporte a DirectX 12."
                    : "Confira o modelo de driver em dxdiag."
            };
        }

        private static Windows11RequirementItem CheckDisplayResolution(List<GpuSnapshot> gpus)
        {
            var best = gpus.Where(g => g.Width > 0).OrderByDescending(g => g.Width * g.Height).FirstOrDefault();
            if (best == null)
            {
                return new Windows11RequirementItem
                {
                    Key = "display",
                    Title = "Resolução de Tela (mínimo 720p)",
                    Status = Windows11CheckStatus.Unknown,
                    Found = "Não detectada",
                    Required = "1280 × 720 ou superior",
                    Recommendation = "Confira em Configurações > Sistema > Tela."
                };
            }

            bool ok = (best.Width >= 1280 && best.Height >= 720) || (best.Width >= 720 && best.Height >= 1280);
            return new Windows11RequirementItem
            {
                Key = "display",
                Title = "Resolução de Tela (mínimo 720p)",
                Status = ok ? Windows11CheckStatus.Ok : Windows11CheckStatus.Fail,
                Found = $"{best.Width} × {best.Height}",
                Required = "1280 × 720 ou superior",
                Recommendation = ok ? "Atende." : "Aumente a resolução do monitor ou atualize o driver de vídeo."
            };
        }

        private static string? ParseWddm(string driverVersion)
        {
            var parts = (driverVersion ?? "").Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || major < 20)
                return null;
            return $"WDDM {major / 10}.{major % 10}";
        }

        private static HardwareProbeSnapshot ReadLiveHardwareSnapshot()
        {
            var cpu = ReadCpu();
            var gpus = ReadGpus();
            string winEd = ReadWindowsEdition(out int build);

            double ramGb = 0;
            if (GetPhysicallyInstalledSystemMemory(out ulong kb) && kb > 0)
                ramGb = kb / 1024.0 / 1024.0;
            else
                ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;

            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            long totalDisk = 0, freeDisk = 0;
            try
            {
                var di = new DriveInfo(root);
                totalDisk = di.TotalSize;
                freeDisk = di.AvailableFreeSpace;
            }
            catch
            {
            }

            bool? uefi = GetFirmwareType(out int fwType) ? fwType switch { 2 => true, 1 => false, _ => null } : null;

            int? sb = null;
            try
            {
                using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
                if (k?.GetValue("UEFISecureBootEnabled") is int v)
                    sb = v;
            }
            catch
            {
            }

            uint tbsRes = TbsTpmNotFound;
            uint tpmVer = 0;
            try
            {
                tbsRes = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info);
                tpmVer = info.TpmVersion;
            }
            catch
            {
            }

            return new HardwareProbeSnapshot(
                winEd,
                build,
                cpu,
                Sse42.IsSupported,
                Popcnt.IsSupported,
                ramGb,
                totalDisk,
                freeDisk,
                root.TrimEnd('\\'),
                uefi,
                sb,
                tbsRes,
                tpmVer,
                ReadTpmWmiDetails(),
                gpus,
                CheckDx12Support());
        }

        private static string ReadWindowsEdition(out int build)
        {
            build = Environment.OSVersion.Version.Build;
            try
            {
                using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string product = k?.GetValue("ProductName")?.ToString() ?? "Windows";
                string disp = k?.GetValue("DisplayVersion")?.ToString() ?? "";
                if (int.TryParse(k?.GetValue("CurrentBuildNumber")?.ToString(), out int b))
                    build = b;
                if (build >= 22000 && product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                return $"{product} {disp} (build {build})".Trim();
            }
            catch
            {
                return Environment.OSVersion.VersionString;
            }
        }

        private static CpuSnapshot ReadCpu()
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, MaxClockSpeed, Architecture FROM Win32_Processor");
                var list = s.Get().Cast<ManagementObject>().ToList();
                if (list.Count > 0)
                {
                    var first = list[0];
                    string name = Regex.Replace(first["Name"]?.ToString() ?? "", "\\s+", " ").Trim();
                    int cores = list.Sum(o => Convert.ToInt32(o["NumberOfCores"] ?? 0));
                    int mhz = Convert.ToInt32(first["MaxClockSpeed"] ?? 0);
                    int arch = Convert.ToInt32(first["Architecture"] ?? -1);
                    return new CpuSnapshot(name, cores, mhz, arch);
                }
            }
            catch
            {
            }
            return new CpuSnapshot("Processador", Environment.ProcessorCount, 0, -1);
        }

        private static List<GpuSnapshot> ReadGpus()
        {
            var gpus = new List<GpuSnapshot>();
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController");
                foreach (ManagementObject o in s.Get())
                {
                    using (o)
                    {
                        gpus.Add(new GpuSnapshot(
                            o["Name"]?.ToString()?.Trim() ?? "",
                            o["DriverVersion"]?.ToString()?.Trim() ?? "",
                            Convert.ToInt32(o["CurrentHorizontalResolution"] ?? 0),
                            Convert.ToInt32(o["CurrentVerticalResolution"] ?? 0)));
                    }
                }
            }
            catch
            {
            }
            return gpus;
        }

        private static string ReadTpmWmiDetails()
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm");
                using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT IsEnabled_InitialValue, IsActivated_InitialValue, ManufacturerIdTxt FROM Win32_Tpm"));
                var first = s.Get().Cast<ManagementObject>().FirstOrDefault();
                if (first == null) return "";
                string mfg = first["ManufacturerIdTxt"]?.ToString()?.Trim() ?? "";
                bool enabled = first["IsEnabled_InitialValue"] is true && first["IsActivated_InitialValue"] is true;
                return (string.IsNullOrEmpty(mfg) ? "" : $" • {mfg}") + (enabled ? " • Ativado" : " • Desativado");
            }
            catch
            {
                return "";
            }
        }

        private static bool? CheckDx12Support()
        {
            try
            {
                Guid iid = new("189819f1-1db6-4b57-be54-1821339b85f7");
                int hr = D3D12CreateDevice(IntPtr.Zero, 45056, ref iid, IntPtr.Zero);
                return (uint)hr <= 1u;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch
            {
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TpmDeviceInfo
        {
            public uint StructVersion;
            public uint TpmVersion;
            public uint TpmInterfaceType;
            public uint TpmImpRevision;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kb);

        [DllImport("kernel32.dll")]
        private static extern bool GetFirmwareType(out int type);

        [DllImport("tbs.dll")]
        private static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

        [DllImport("d3d12.dll")]
        private static extern int D3D12CreateDevice(IntPtr adapter, int minFeatureLevel, ref Guid riid, IntPtr device);
    }
}

