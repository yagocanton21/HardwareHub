using System.Diagnostics;
using System.IO;
using System.Management;
using HardwareHub.Models;
using LibreHardwareMonitor.Hardware;

namespace HardwareHub.Services
{
    public class HardwareService : IDisposable
    {
        private Computer? _computer;
        private readonly PerformanceCounter? _cpuCounter;
        private bool _isComputerOpened = false;

        private string _cachedCpuName = "";
        private int _cachedCpuCores = 0;
        private int _cachedCpuThreads = 0;
        private string _cachedGpuName = "";
        private double _cachedRamTotalGb = 0;
        private double _cachedCpuClockSpeedGhz = 0;
        private string _cachedOsName = "";
        private double _smoothedCpuTemp = 0;
        private static DateTime _systemBootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

        public static bool IsPawnIOInstalled
        {
            get
            {
                try
                {
                    using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                        Microsoft.Win32.RegistryHive.LocalMachine,
                        Microsoft.Win32.RegistryView.Registry64);
                    using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                    return key != null;
                }
                catch { return false; }
            }
        }

        public static void InstallPawnIO()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                proc?.WaitForExit(45000); // aguarda até 45 segundos para concluir a instalação
            }
            catch { }
        }

        private class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer)
            {
                computer.Traverse(this);
            }
            public void VisitHardware(IHardware hardware)
            {
                hardware.Update();
                foreach (IHardware subHardware in hardware.SubHardware)
                {
                    subHardware.Accept(this);
                }
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }

        private readonly UpdateVisitor _updateVisitor = new();
        private Process? _hwInfoProcess;

        public HardwareService()
        {
            try
            {
                // % Processor Utility do "Processor Information" é exatamente a métrica usada pelo Gerenciador de Tarefas do Windows (Task Manager)
                _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                _cpuCounter.NextValue();
            }
            catch
            {
                try
                {
                    _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    _cpuCounter.NextValue();
                }
                catch
                {
                    _cpuCounter = null;
                }
            }
        }

        public async Task InitializeAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    if (!IsPawnIOInstalled)
                    {
                        InstallPawnIO();
                    }
                }
                catch { }

                EnsureHwInfoBackendRunning();

                try
                {
                    var comp = new Computer
                    {
                        IsCpuEnabled = true,
                        IsGpuEnabled = true,
                        IsMemoryEnabled = true,
                        IsMotherboardEnabled = true,
                        IsStorageEnabled = true,
                        IsControllerEnabled = true
                    };
                    comp.Open();
                    _computer = comp;
                    _isComputerOpened = true;
                }
                catch (Exception ex)
                {
                    _isComputerOpened = false;
                    try
                    {
                        File.AppendAllText("sensors.log", $"[LHM Open Error] {ex}\n");
                    }
                    catch { }
                }

                LoadStaticHardwareInfo();
            });
        }

        private void LoadStaticHardwareInfo()
        {
            try
            {
                using var cpuSearcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
                foreach (var obj in cpuSearcher.Get())
                {
                    string rawName = obj["Name"]?.ToString() ?? "";
                    _cachedCpuName = System.Text.RegularExpressions.Regex.Replace(rawName, @"\s+", " ").Trim();
                    if (int.TryParse(obj["NumberOfCores"]?.ToString(), out int cores)) _cachedCpuCores = cores;
                    if (int.TryParse(obj["NumberOfLogicalProcessors"]?.ToString(), out int threads)) _cachedCpuThreads = threads;
                    if (uint.TryParse(obj["MaxClockSpeed"]?.ToString(), out uint mhz) && mhz > 0) _cachedCpuClockSpeedGhz = mhz / 1000.0;
                    break;
                }
            }
            catch { }

            try
            {
                using var gpuSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (var obj in gpuSearcher.Get())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
                        // Prioritize dedicated GPUs (NVIDIA or AMD) over Intel integrated GPU
                        if (string.IsNullOrEmpty(_cachedGpuName) || 
                            name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || 
                            name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || 
                            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                        {
                            _cachedGpuName = name;
                        }
                    }
                }
            }
            catch { }

            try
            {
                using var memSearcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in memSearcher.Get())
                {
                    if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out ulong totalBytes))
                    {
                        _cachedRamTotalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);
                    }
                }
            }
            catch { }

            try
            {
                using var osSearcher = new ManagementObjectSearcher("SELECT Caption, LastBootUpTime FROM Win32_OperatingSystem");
                foreach (var obj in osSearcher.Get())
                {
                    _cachedOsName = obj["Caption"]?.ToString()?.Trim() ?? "";
                    if (obj["LastBootUpTime"] != null)
                    {
                        try
                        {
                            _systemBootTime = ManagementDateTimeConverter.ToDateTime(obj["LastBootUpTime"].ToString());
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public (CpuMetrics Cpu, GpuMetrics Gpu, RamMetrics Ram, List<DiskMetrics> Disks, SystemOverview System) GetCurrentMetrics()
        {
            var cpu = new CpuMetrics
            {
                Name = string.IsNullOrEmpty(_cachedCpuName) ? "Processador" : _cachedCpuName,
                Cores = _cachedCpuCores,
                Threads = _cachedCpuThreads
            };

            var gpu = new GpuMetrics
            {
                Name = string.IsNullOrEmpty(_cachedGpuName) ? "Placa de Vídeo" : _cachedGpuName
            };

            var ram = new RamMetrics
            {
                TotalGb = _cachedRamTotalGb > 0 ? _cachedRamTotalGb : 16.0
            };

            var disks = new List<DiskMetrics>();

            // 1. Prioridade: Se o HWiNFO estiver rodando com Memória Compartilhada, lê direto dele!
            try
            {
                if (HwInfoReader.IsHwInfoAvailable())
                {
                    var hwReadings = HwInfoReader.ReadAllSensors();
                    foreach (var reading in hwReadings)
                    {
                        if (reading.ReadingType == 1 && reading.Value > 0) // Temperatura
                        {
                            if (reading.Label.Contains("Núcleo máximo", StringComparison.OrdinalIgnoreCase) ||
                                reading.Label.Contains("Nucleo maximo", StringComparison.OrdinalIgnoreCase) ||
                                reading.Label.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                            {
                                cpu.Temperature = reading.Value;
                            }
                            else if (cpu.Temperature <= 0 && (
                                     reading.Label.Contains("Temperaturas centrais", StringComparison.OrdinalIgnoreCase) ||
                                     reading.Label.Contains("Core 0", StringComparison.OrdinalIgnoreCase) ||
                                     reading.Label.Contains("CPU Core #1", StringComparison.OrdinalIgnoreCase) ||
                                     reading.Label.Contains("CPU Core", StringComparison.OrdinalIgnoreCase)))
                            {
                                cpu.Temperature = reading.Value;
                            }
                            else if (gpu.Temperature <= 0 &&
                                     (reading.Label.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase) ||
                                      reading.Label.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                                      reading.Label.Contains("GPU Thermal Diode", StringComparison.OrdinalIgnoreCase) ||
                                      (reading.Label.Contains("GPU", StringComparison.OrdinalIgnoreCase) &&
                                       !reading.Label.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) &&
                                       !reading.Label.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) &&
                                       !reading.Label.Contains("Memory", StringComparison.OrdinalIgnoreCase))))
                            {
                                gpu.Temperature = reading.Value;
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Leitura com LibreHardwareMonitor
            if (_isComputerOpened && _computer != null)
            {
                try
                {
                    _computer.Accept(_updateVisitor);

                    void ProcessHardware(IHardware hw)
                    {
                        if (hw.HardwareType == HardwareType.Cpu)
                        {
                            if (string.IsNullOrEmpty(_cachedCpuName))
                                cpu.Name = hw.Name;

                            // Filtra apenas sensores reais de temperatura de núcleos/encapsulamento,
                            // ignorando sensores de "Distance to TjMax" (distância até o limite térmico)
                            var cpuTemps = hw.Sensors
                                .Where(s => s.SensorType == SensorType.Temperature 
                                         && s.Value.HasValue 
                                         && s.Value.Value > 0
                                         && !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
                                         && !s.Name.Contains("TjMax", StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            if (cpuTemps.Count > 0)
                            {
                                // Prioriza "Core Max" / "Núcleo Máximo" (padrão HWiNFO), ou "Package", "Tctl", "Tdie"
                                var main = cpuTemps.FirstOrDefault(s => s.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase) ||
                                                                        s.Name.Equals("Max", StringComparison.OrdinalIgnoreCase))
                                        ?? cpuTemps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                                                        s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                                                        s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                                                                        s.Name.Contains("CCD", StringComparison.OrdinalIgnoreCase))
                                        ?? cpuTemps.FirstOrDefault(s => s.Name.Contains("Core #", StringComparison.OrdinalIgnoreCase) ||
                                                                        s.Name.Contains("CPU Core", StringComparison.OrdinalIgnoreCase));

                                double rawTemp = main?.Value ?? cpuTemps.Max(s => s.Value!.Value);
                                if (rawTemp > 0)
                                {
                                    if (_smoothedCpuTemp <= 0)
                                    {
                                        _smoothedCpuTemp = rawTemp;
                                    }
                                    else
                                    {
                                        // Suavização inteligente (EMA): absorve micro-saltos de 1 segundo sem atrasar cargas contínuas
                                        double alpha = Math.Abs(rawTemp - _smoothedCpuTemp) > 8 ? 0.50 : 0.30;
                                        _smoothedCpuTemp = (_smoothedCpuTemp * (1.0 - alpha)) + (rawTemp * alpha);
                                    }
                                    cpu.Temperature = Math.Round(_smoothedCpuTemp, 1);
                                }
                            }

                            foreach (var sensor in hw.Sensors)
                            {
                                if (sensor.SensorType == SensorType.Load)
                                {
                                    if (sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) || sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (sensor.Value.HasValue) cpu.UsagePercentage = sensor.Value.Value;
                                    }
                                }
                                else if (sensor.SensorType == SensorType.Clock && !sensor.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (sensor.Value.HasValue && sensor.Value.Value > 300)
                                    {
                                        if (cpu.ClockSpeedGhz <= 0 || sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                        {
                                            cpu.ClockSpeedGhz = Math.Max(cpu.ClockSpeedGhz, sensor.Value.Value / 1000.0);
                                        }
                                    }
                                }
                            }
                        }
                        else if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel)
                        {
                            bool isDedicated = hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd;
                            if (string.IsNullOrEmpty(_cachedGpuName) || isDedicated)
                            {
                                gpu.Name = hw.Name;
                            }

                            // 1. Filtragem inteligente de temperatura da GPU: prioriza GPU Core e ignora Hot Spot e Memória na temperatura principal
                            var gpuTemps = hw.Sensors
                                .Where(s => s.SensorType == SensorType.Temperature 
                                         && s.Value.HasValue 
                                         && s.Value.Value > 0)
                                .ToList();

                            if (gpuTemps.Count > 0)
                            {
                                var mainTemp = gpuTemps.FirstOrDefault(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                                                                            s.Name.Equals("Core", StringComparison.OrdinalIgnoreCase) ||
                                                                            s.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase))
                                            ?? gpuTemps.FirstOrDefault(s => !s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) &&
                                                                            !s.Name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) &&
                                                                            !s.Name.Contains("Junction", StringComparison.OrdinalIgnoreCase) &&
                                                                            !s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) &&
                                                                            !s.Name.Contains("VRM", StringComparison.OrdinalIgnoreCase))
                                            ?? gpuTemps.First();

                                if (gpu.Temperature <= 0 || isDedicated)
                                {
                                    gpu.Temperature = mainTemp.Value!.Value;
                                }
                            }

                            // 2. Filtragem inteligente de uso da GPU: prioriza GPU Core / GPU Total em vez de Video Engine ou Memory Controller
                            var gpuLoads = hw.Sensors
                                .Where(s => s.SensorType == SensorType.Load 
                                         && s.Value.HasValue 
                                         && s.Value.Value >= 0)
                                .ToList();

                            if (gpuLoads.Count > 0)
                            {
                                var mainLoad = gpuLoads.FirstOrDefault(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                                                                            s.Name.Equals("Core", StringComparison.OrdinalIgnoreCase) ||
                                                                            s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                                            ?? gpuLoads.FirstOrDefault(s => !s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) &&
                                                                            !s.Name.Contains("Video", StringComparison.OrdinalIgnoreCase) &&
                                                                            !s.Name.Contains("Frame", StringComparison.OrdinalIgnoreCase))
                                            ?? gpuLoads.First();

                                if (gpu.UsagePercentage <= 0 || isDedicated)
                                {
                                    gpu.UsagePercentage = mainLoad.Value!.Value;
                                }
                            }

                            // 3. Memória VRAM utilizada e total
                            foreach (var sensor in hw.Sensors)
                            {
                                if (sensor.SensorType == SensorType.SmallData && sensor.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (sensor.Value.HasValue) gpu.MemoryUsedGb = sensor.Value.Value / 1024.0;
                                }
                                else if (sensor.SensorType == SensorType.SmallData && sensor.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (sensor.Value.HasValue) gpu.MemoryTotalGb = sensor.Value.Value / 1024.0;
                                }
                            }
                        }
                        else if (hw.HardwareType == HardwareType.Memory)
                        {
                            foreach (var sensor in hw.Sensors)
                            {
                                if (sensor.SensorType == SensorType.Data && sensor.Name.Contains("Used", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (sensor.Value.HasValue) ram.UsedGb = sensor.Value.Value;
                                }
                            }
                        }

                        // Processa sub-hardwares recursivamente
                        foreach (var sub in hw.SubHardware)
                        {
                            ProcessHardware(sub);
                        }
                    }

                    foreach (var hw in _computer.Hardware)
                    {
                        ProcessHardware(hw);
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.AppendAllText("sensors.log", $"[Sensor Update Error] {ex}\n");
                    }
                    catch { }
                }
            }

            // Fallback WMI para Temperatura caso o sensor de kernel não esteja liberado
            if (cpu.Temperature <= 0)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                    foreach (var obj in searcher.Get())
                    {
                        if (double.TryParse(obj["CurrentTemperature"]?.ToString(), out double kelvinTenths))
                        {
                            // A temperatura no ACPI vem em décimos de Kelvin: (K/10) - 273.15 = C
                            double celsius = (kelvinTenths / 10.0) - 273.15;
                            if (celsius > 0 && celsius < 125)
                            {
                                if (_smoothedCpuTemp <= 0) _smoothedCpuTemp = celsius;
                                else _smoothedCpuTemp = (_smoothedCpuTemp * 0.7) + (celsius * 0.3);
                                cpu.Temperature = Math.Round(_smoothedCpuTemp, 1);
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            // Uso de CPU alinhado com o Gerenciador de Tarefas do Windows (% Processor Utility)
            if (_cpuCounter != null)
            {
                try
                {
                    float val = _cpuCounter.NextValue();
                    if (val >= 0)
                    {
                        cpu.UsagePercentage = Math.Min(100.0, Math.Max(0.0, (double)val));
                    }
                }
                catch { }
            }

            // Fallback de Memória RAM via WMI
            if (ram.UsedGb <= 0)
            {
                try
                {
                    using var os = new ManagementObjectSearcher("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
                    foreach (var obj in os.Get())
                    {
                        if (ulong.TryParse(obj["TotalVisibleMemorySize"]?.ToString(), out ulong totalKb) &&
                            ulong.TryParse(obj["FreePhysicalMemory"]?.ToString(), out ulong freeKb))
                        {
                            ram.TotalGb = totalKb / (1024.0 * 1024.0);
                            ram.UsedGb = (totalKb - freeKb) / (1024.0 * 1024.0);
                        }
                    }
                }
                catch { }
            }

            // Discos de Armazenamento
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable))
                    {
                        double totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                        double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);

                        disks.Add(new DiskMetrics
                        {
                            DriveLetter = drive.Name.TrimEnd('\\'),
                            VolumeLabel = drive.VolumeLabel,
                            TotalGb = totalGb,
                            FreeGb = freeGb
                        });
                    }
                }
            }
            catch { }

            // Visão Geral do Sistema
            var system = new SystemOverview
            {
                OsName = string.IsNullOrEmpty(_cachedOsName) ? Environment.OSVersion.VersionString : _cachedOsName,
                OsArchitecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                Uptime = DateTime.Now > _systemBootTime ? (DateTime.Now - _systemBootTime) : TimeSpan.FromMilliseconds(Environment.TickCount64)
            };

            // Fallback de Clock da CPU caso o sensor não esteja liberado
            if (cpu.ClockSpeedGhz <= 0 && _cachedCpuClockSpeedGhz > 0)
            {
                cpu.ClockSpeedGhz = _cachedCpuClockSpeedGhz;
            }

            return (cpu, gpu, ram, disks, system);
        }

        public void EnrichDisksWithSensors(IEnumerable<DiskHealthInfo> disks)
        {
            if (_computer == null || !_isComputerOpened) return;
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Storage)
                    {
                        hw.Update();
                        string hwName = hw.Name ?? "";

                        var matched = disks.FirstOrDefault(d =>
                            (!string.IsNullOrEmpty(d.Model) && (hwName.Contains(d.Model, StringComparison.OrdinalIgnoreCase) || d.Model.Contains(hwName, StringComparison.OrdinalIgnoreCase))) ||
                            (!string.IsNullOrEmpty(d.Serial) && hwName.Contains(d.Serial, StringComparison.OrdinalIgnoreCase)));

                        if (matched != null)
                        {
                            foreach (var sensor in hw.Sensors)
                            {
                                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue && sensor.Value.Value > 0)
                                {
                                    if (!matched.Temperature.HasValue || matched.Temperature <= 0 || sensor.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                                    {
                                        matched.Temperature = Math.Round(sensor.Value.Value, 0);
                                    }
                                }
                                else if (sensor.SensorType == SensorType.Level && sensor.Value.HasValue && sensor.Value.Value >= 0)
                                {
                                    if (sensor.Name.Contains("Life", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Health", StringComparison.OrdinalIgnoreCase))
                                    {
                                        matched.HealthPercentage = (int)Math.Round(sensor.Value.Value);
                                    }
                                }
                                else if (sensor.SensorType == SensorType.Data && sensor.Value.HasValue && sensor.Value.Value >= 0)
                                {
                                    if (sensor.Name.Contains("Written", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Write", StringComparison.OrdinalIgnoreCase))
                                    {
                                        double val = sensor.Value.Value;
                                        matched.TotalWrittenTb = val >= 100 ? Math.Round(val / 1024.0, 1) : Math.Round(val, 1);
                                    }
                                    else if (sensor.Name.Contains("Read", StringComparison.OrdinalIgnoreCase))
                                    {
                                        double val = sensor.Value.Value;
                                        matched.TotalReadTb = val >= 100 ? Math.Round(val / 1024.0, 1) : Math.Round(val, 1);
                                    }
                                }
                                else if (sensor.SensorType == SensorType.TimeSpan && sensor.Value.HasValue && sensor.Value.Value > 0)
                                {
                                    matched.PowerOnHours = (long)sensor.Value.Value;
                                }
                                else if (sensor.Name.Contains("Power", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (sensor.Name.Contains("Hour", StringComparison.OrdinalIgnoreCase) && sensor.Value.HasValue && sensor.Value.Value > 0)
                                    {
                                        matched.PowerOnHours = (long)sensor.Value.Value;
                                    }
                                    else if ((sensor.Name.Contains("Count", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Cycle", StringComparison.OrdinalIgnoreCase)) && sensor.Value.HasValue && sensor.Value.Value > 0)
                                    {
                                        matched.PowerOnCount = (long)sensor.Value.Value;
                                    }
                                }
                            }

                            // Extrai atributos S.M.A.R.T. brutos caso a biblioteca exponha (Horas de Uso e Contagem de Inicializações)
                            try
                            {
                                var attrsProp = hw.GetType().GetProperty("Attributes");
                                if (attrsProp != null)
                                {
                                    var attrs = attrsProp.GetValue(hw) as System.Collections.IEnumerable;
                                    if (attrs != null)
                                    {
                                        foreach (var attr in attrs)
                                        {
                                            var nameProp = attr.GetType().GetProperty("Name");
                                            var valProp = attr.GetType().GetProperty("Value");
                                            var idProp = attr.GetType().GetProperty("Id");

                                            string attrName = nameProp?.GetValue(attr)?.ToString() ?? "";
                                            byte attrId = idProp != null ? Convert.ToByte(idProp.GetValue(attr) ?? 0) : (byte)0;
                                            float attrVal = valProp != null ? Convert.ToSingle(valProp.GetValue(attr) ?? 0f) : 0f;

                                            if (attrVal > 0)
                                            {
                                                if (attrId == 9 || attrName.Contains("Power-On Hours", StringComparison.OrdinalIgnoreCase) || attrName.Contains("Power On Hours", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    if (!matched.PowerOnHours.HasValue || matched.PowerOnHours <= 0)
                                                    {
                                                        matched.PowerOnHours = (long)attrVal;
                                                    }
                                                }
                                                else if (attrId == 12 || attrName.Contains("Power Cycle Count", StringComparison.OrdinalIgnoreCase) || attrName.Contains("Power-On Count", StringComparison.OrdinalIgnoreCase) || attrName.Contains("Power Cycles", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    if (!matched.PowerOnCount.HasValue || matched.PowerOnCount <= 0)
                                                    {
                                                        matched.PowerOnCount = (long)attrVal;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private void EnsureHwInfoBackendRunning()
        {
            try
            {
                // Se já estiver disponível, não precisa fazer nada
                if (HwInfoReader.IsHwInfoAvailable()) return;

                // Procura o executável do HWiNFO64 na pasta hwi_854 do projeto
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates = {
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\hwi_854\HWiNFO64.exe")),
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\hwi_854\HWiNFO64.exe")),
                    Path.GetFullPath(Path.Combine(baseDir, @"hwi_854\HWiNFO64.exe")),
                    @"C:\Users\Yago Canton\Desktop\HUB\hwi_854\HWiNFO64.exe"
                };

                foreach (var exePath in candidates)
                {
                    if (File.Exists(exePath))
                    {
                        // Garante que o HWiNFO64.ini ao lado dele tem SharedMemory=1 ativado
                        string iniPath = Path.Combine(Path.GetDirectoryName(exePath)!, "HWiNFO64.ini");
                        if (!File.Exists(iniPath))
                        {
                            File.WriteAllText(iniPath, "[Settings]\nSensorsOnly=1\nShowSensorsOnStartup=1\nMinimizeSensorsOnStartup=1\nMinimizeMainWndOnStartup=1\nEnableSharedMemory=1\nSharedMemory=1\nAutoUpdate=0\n");
                        }

                        if (Process.GetProcessesByName("HWiNFO64").Length == 0)
                        {
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = exePath,
                                Arguments = "-min -sensors",
                                UseShellExecute = true,
                                WindowStyle = ProcessWindowStyle.Minimized
                            };
                            _hwInfoProcess = Process.Start(startInfo);
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                _computer?.Close();
                _cpuCounter?.Dispose();
                if (_hwInfoProcess != null && !_hwInfoProcess.HasExited)
                {
                    _hwInfoProcess.Kill();
                }
            }
            catch { }
        }
    }
}
