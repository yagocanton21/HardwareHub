using System.Diagnostics;
using System.IO;
using System.Management;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    public class DiskHealthService
    {
        private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

        public List<DiskHealthInfo> GetDisksHealth()
        {
            var results = new Dictionary<int, DiskHealthInfo>();
            var pnpIds = new Dictionary<int, string>();

            // 1. Base com Win32_DiskDrive
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Index, Model, SerialNumber, InterfaceType, Size, MediaType, PNPDeviceID FROM Win32_DiskDrive");
                foreach (ManagementObject obj in searcher.Get())
                {
                    int index = Convert.ToInt32(obj["Index"] ?? 0);
                    string pnp = obj["PNPDeviceID"]?.ToString() ?? "";
                    string model = obj["Model"]?.ToString()?.Trim() ?? $"Disco {index}";
                    string serial = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                    long sizeBytes = Convert.ToInt64(obj["Size"] ?? 0L);
                    string iface = pnp.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase)
                        ? "USB"
                        : (obj["InterfaceType"]?.ToString() ?? "SATA");

                    pnpIds[index] = pnp;
                    results[index] = new DiskHealthInfo
                    {
                        Index = index,
                        Model = model,
                        Serial = serial,
                        Interface = iface,
                        MediaType = (model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) ? "SSD" : "HDD",
                        CapacityGb = Math.Round(sizeBytes / (1024.0 * 1024.0 * 1024.0), 1),
                        HealthStatus = "Desconhecido",
                        HealthPercentage = 100
                    };
                }
            }
            catch { }

            // 2. Mapeamento de Letras de Unidades (C:, D:) via MSFT_Partition
            try
            {
                var scope = new ManagementScope(StorageScope);
                scope.Connect();
                using var partSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition WHERE DriveLetter IS NOT NULL"));
                foreach (ManagementObject part in partSearcher.Get())
                {
                    if (int.TryParse(part["DiskNumber"]?.ToString(), out int dNum) && results.TryGetValue(dNum, out var info))
                    {
                        string letter = part["DriveLetter"]?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(letter) && letter != "0" && letter.Length == 1 && char.IsLetter(letter[0]))
                        {
                            string formatted = $"{char.ToUpperInvariant(letter[0])}:";
                            if (string.IsNullOrEmpty(info.DriveLetters))
                                info.DriveLetters = formatted;
                            else if (!info.DriveLetters.Contains(formatted, StringComparison.OrdinalIgnoreCase))
                                info.DriveLetters += $", {formatted}";
                        }
                    }
                }
            }
            catch { }

            // 3. API Avançada de Armazenamento do Windows (MSFT_PhysicalDisk & Confiabilidade)
            try
            {
                var scope = new ManagementScope(StorageScope);
                scope.Connect();

                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, HealthStatus, MediaType, BusType FROM MSFT_PhysicalDisk")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string devId = obj["DeviceId"]?.ToString() ?? "";
                        if (!int.TryParse(devId, out int idx) || !results.TryGetValue(idx, out var info)) continue;

                        int health = Convert.ToInt32(obj["HealthStatus"] ?? 5);
                        info.HealthStatus = health switch
                        {
                            0 => "Saudável",
                            1 => "Atenção",
                            2 => "Crítico",
                            5 => "Desconhecido",
                            _ => "Desconhecido"
                        };

                        int media = Convert.ToInt32(obj["MediaType"] ?? 0);
                        info.MediaType = media switch
                        {
                            3 => "HDD",
                            4 => "SSD",
                            5 => "SCM",
                            _ => info.MediaType
                        };

                        int bus = Convert.ToInt32(obj["BusType"] ?? 0);
                        info.Interface = bus switch
                        {
                            17 => "NVMe",
                            11 => "SATA",
                            10 => "SAS",
                            7 => "USB",
                            8 => "RAID",
                            _ => info.Interface
                        };
                    }
                }

                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                    "SELECT DeviceId, Temperature, TemperatureMax, PowerOnHours, ReadErrorsUncorrected, WriteErrorsUncorrected, StartStopCycleCount, Wear FROM MSFT_StorageReliabilityCounter")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string devId = obj["DeviceId"]?.ToString() ?? "";
                        if (!int.TryParse(devId, out int idx) || !results.TryGetValue(idx, out var info)) continue;

                        if (TryReadDouble(obj["Temperature"], out double temp) && temp > 0 && temp < 150)
                        {
                            info.Temperature = temp;
                        }
                        else if (TryReadDouble(obj["TemperatureMax"], out double tempMax) && tempMax > 0 && tempMax < 150 && !info.Temperature.HasValue)
                        {
                            info.Temperature = tempMax;
                        }

                        if (TryReadLong(obj["PowerOnHours"], out long hours) && hours >= 0 && hours < 1_000_000)
                        {
                            info.PowerOnHours = hours;
                        }

                        if (TryReadLong(obj["StartStopCycleCount"], out long cycles) && cycles > 0 && cycles < 10_000_000)
                        {
                            info.PowerOnCount = cycles;
                        }

                        if (TryReadLong(obj["Wear"], out long wear) && wear >= 0 && wear <= 100)
                        {
                            info.HealthPercentage = Math.Max(0, 100 - (int)wear);
                        }

                        TryReadLong(obj["ReadErrorsUncorrected"], out long readUnc);
                        TryReadLong(obj["WriteErrorsUncorrected"], out long writeUnc);
                        if (readUnc > 0 || writeUnc > 0)
                        {
                            info.HasSmartErrors = true;
                            if (info.HealthStatus is "Saudável" or "Desconhecido")
                                info.HealthStatus = "Atenção";
                        }
                    }
                }
            }
            catch { }

            // 4. S.M.A.R.T. nativo via root\wmi (MSStorageDriver_FailurePredict*)
            ReadNativeWmiSmart(results, pnpIds);

            // 5. Integração complementar com Base S.M.A.R.T. do CrystalDiskInfo (se presente em pastas portáteis ou padrão)
            try
            {
                string[] possibleSmartDirs = {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Smart"),
                    @"C:\Crystal disk info e mark Portable\Smart",
                    @"D:\Crystal disk info e mark Portable\Smart",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CrystalDiskInfo", "Smart"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "CrystalDiskInfo", "Smart")
                };

                string? cdiSmartBase = possibleSmartDirs.FirstOrDefault(Directory.Exists);
                if (cdiSmartBase != null)
                {
                    foreach (var info in results.Values)
                    {
                        var dirs = Directory.GetDirectories(cdiSmartBase);
                        string? matchedDir = dirs.FirstOrDefault(d =>
                        {
                            string folderName = Path.GetFileName(d).Replace(" ", "");
                            return (!string.IsNullOrEmpty(info.Serial) && folderName.Contains(info.Serial, StringComparison.OrdinalIgnoreCase)) ||
                                   folderName.Contains(info.Model.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
                        });

                        if (matchedDir != null)
                        {
                            string smartIni = Path.Combine(matchedDir, "Smart.ini");
                            if (File.Exists(smartIni))
                            {
                                var lines = File.ReadAllLines(smartIni);
                                foreach (var line in lines)
                                {
                                    var parts = line.Split('=');
                                    if (parts.Length != 2) continue;
                                    string prop = parts[0].Trim();
                                    string val = parts[1].Trim();

                                    if (prop.Equals("Temperature", StringComparison.OrdinalIgnoreCase) && double.TryParse(val, out double cdiTemp) && cdiTemp > 0)
                                    {
                                        info.Temperature = cdiTemp;
                                    }
                                    else if (prop.Equals("Life", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int cdiLife) && cdiLife > 0)
                                    {
                                        info.HealthPercentage = cdiLife;
                                    }
                                    else if (prop.Equals("PowerOnHours", StringComparison.OrdinalIgnoreCase) && long.TryParse(val, out long cdiHours) && cdiHours > 0)
                                    {
                                        info.PowerOnHours = cdiHours;
                                    }
                                    else if (prop.Equals("PowerOnCount", StringComparison.OrdinalIgnoreCase) && long.TryParse(val, out long cdiCount) && cdiCount > 0)
                                    {
                                        info.PowerOnCount = cdiCount;
                                    }
                                    else if (prop.Equals("HostWrites", StringComparison.OrdinalIgnoreCase) && double.TryParse(val, out double writesGb))
                                    {
                                        info.TotalWrittenTb = Math.Round(writesGb / 1024.0, 1);
                                    }
                                    else if (prop.Equals("HostReads", StringComparison.OrdinalIgnoreCase) && double.TryParse(val, out double readsGb))
                                    {
                                        info.TotalReadTb = Math.Round(readsGb / 1024.0, 1);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return results.Values.OrderBy(x => x.Index).ToList();
        }

        private static bool TryReadDouble(object? raw, out double value)
        {
            value = 0;
            if (raw == null) return false;
            try
            {
                value = Convert.ToDouble(raw);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadLong(object? raw, out long value)
        {
            value = 0;
            if (raw == null) return false;
            try
            {
                value = Convert.ToInt64(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static DiskHealthInfo? MatchDiskByInstance(
            Dictionary<int, DiskHealthInfo> results,
            Dictionary<int, string> pnpIds,
            string? instanceName)
        {
            if (string.IsNullOrEmpty(instanceName)) return null;
            foreach (var kv in pnpIds)
            {
                if (!string.IsNullOrEmpty(kv.Value) &&
                    instanceName.StartsWith(kv.Value, StringComparison.OrdinalIgnoreCase) &&
                    results.TryGetValue(kv.Key, out var info))
                {
                    return info;
                }
            }
            return null;
        }

        private static void ReadNativeWmiSmart(
            Dictionary<int, DiskHealthInfo> results,
            Dictionary<int, string> pnpIds)
        {
            try
            {
                var wmiScope = new ManagementScope(@"\\.\root\wmi");
                wmiScope.Connect();

                try
                {
                    using var statusSearcher = new ManagementObjectSearcher(
                        wmiScope,
                        new ObjectQuery("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"));
                    foreach (ManagementObject obj in statusSearcher.Get())
                    {
                        var disk = MatchDiskByInstance(results, pnpIds, obj["InstanceName"]?.ToString());
                        if (disk == null) continue;

                        if (obj["PredictFailure"] is bool predict && predict)
                        {
                            disk.HasSmartErrors = true;
                            disk.HealthStatus = "Crítico";
                        }
                    }
                }
                catch { }

                var thresholdsByDisk = new Dictionary<int, byte[]>();
                try
                {
                    using var threshSearcher = new ManagementObjectSearcher(
                        wmiScope,
                        new ObjectQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds"));
                    foreach (ManagementObject obj in threshSearcher.Get())
                    {
                        var disk = MatchDiskByInstance(results, pnpIds, obj["InstanceName"]?.ToString());
                        if (disk != null && obj["VendorSpecific"] is byte[] threshBytes)
                        {
                            thresholdsByDisk[disk.Index] = threshBytes;
                        }
                    }
                }
                catch { }

                using var dataSearcher = new ManagementObjectSearcher(
                    wmiScope,
                    new ObjectQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData"));
                foreach (ManagementObject obj in dataSearcher.Get())
                {
                    var disk = MatchDiskByInstance(results, pnpIds, obj["InstanceName"]?.ToString());
                    if (disk == null || obj["VendorSpecific"] is not byte[] rawSmart) continue;

                    thresholdsByDisk.TryGetValue(disk.Index, out byte[]? threshBytes);
                    DecodeSmartAttributes(disk, rawSmart, threshBytes);
                }
            }
            catch
            {
                // Sem permissão de administrador ou driver sem suporte a root\wmi SMART
            }
        }

        private static void DecodeSmartAttributes(DiskHealthInfo info, byte[] data, byte[]? thresholds)
        {
            bool parsedAny = false;
            long pendingAndUncorrectable = 0;

            for (int i = 2; i + 12 <= data.Length && i < 362; i += 12)
            {
                byte id = data[i];
                if (id == 0) continue;

                parsedAny = true;
                byte currentVal = data[i + 3];
                long raw = 0L;
                for (int j = 0; j < 6; j++)
                {
                    raw |= (long)((ulong)data[i + 5 + j] << (8 * j));
                }

                byte threshold = 0;
                if (thresholds != null)
                {
                    for (int k = 2; k + 12 <= thresholds.Length && k < 362; k += 12)
                    {
                        if (thresholds[k] == id)
                        {
                            threshold = thresholds[k + 1];
                            break;
                        }
                    }
                }

                if (threshold > 0 && currentVal > 0 && currentVal <= threshold)
                {
                    info.HasSmartErrors = true;
                    if (info.HealthStatus is "Saudável" or "Desconhecido")
                        info.HealthStatus = "Atenção";
                }

                switch (id)
                {
                    case 5: // Reallocated Sectors Count
                    {
                        long reallocated = raw & 0xFFFFFFFFL;
                        if (reallocated > 0)
                        {
                            info.HasSmartErrors = true;
                            if (reallocated >= 100)
                                info.HealthStatus = "Crítico";
                            else if (info.HealthStatus is "Saudável" or "Desconhecido")
                                info.HealthStatus = "Atenção";
                        }
                        break;
                    }
                    case 197: // Current Pending Sector Count
                    case 198: // Uncorrectable Sector Count
                    {
                        long sectorErrors = raw & 0xFFFFFFFFL;
                        if (sectorErrors > 0)
                        {
                            pendingAndUncorrectable += sectorErrors;
                            info.HasSmartErrors = true;
                            if (pendingAndUncorrectable >= 10)
                                info.HealthStatus = "Crítico";
                            else if (info.HealthStatus is "Saudável" or "Desconhecido")
                                info.HealthStatus = "Atenção";
                        }
                        break;
                    }
                    case 9: // Power-On Hours
                    {
                        long hours = raw & 0xFFFFFFFFL;
                        if (!info.PowerOnHours.HasValue && hours > 0 && hours < 1_000_000)
                        {
                            info.PowerOnHours = hours;
                        }
                        break;
                    }
                    case 12: // Power Cycle Count
                    {
                        if (!info.PowerOnCount.HasValue && raw > 0 && raw < 10_000_000)
                        {
                            info.PowerOnCount = raw;
                        }
                        break;
                    }
                    case 194: // Temperature
                    case 190: // Airflow Temperature
                    {
                        long temp = raw & 0xFFL;
                        if (!info.Temperature.HasValue && temp > 0 && temp < 120)
                        {
                            info.Temperature = temp;
                        }
                        break;
                    }
                    case 231: // SSD Life Left
                    {
                        if (info.MediaType == "SSD" && info.HealthPercentage == 100 && currentVal <= 100)
                        {
                            info.HealthPercentage = currentVal;
                        }
                        break;
                    }
                }
            }

            if (parsedAny && info.HealthStatus == "Desconhecido")
            {
                info.HealthStatus = info.HasSmartErrors ? "Atenção" : "Saudável";
            }
        }

        public async Task RunSpeedBenchmarkAsync(DiskHealthInfo disk, Action<string> progressCallback)
        {
            if (disk.IsTestingSpeed) return;
            disk.IsTestingSpeed = true;
            disk.SpeedStatusText = "Preparando teste...";

            await Task.Run(() =>
            {
                string targetDir = Path.GetTempPath();
                // Tenta achar a primeira letra montada deste disco com espaço disponível (>= 128 MB)
                if (!string.IsNullOrEmpty(disk.DriveLetters))
                {
                    foreach (string entry in disk.DriveLetters.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string candidate = $"{entry.Trim()}\\";
                        try
                        {
                            if (Directory.Exists(candidate))
                            {
                                var driveInfo = new DriveInfo(candidate);
                                if (driveInfo.IsReady && driveInfo.AvailableFreeSpace >= 128L * 1024 * 1024)
                                {
                                    targetDir = candidate;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }

                string testFile = Path.Combine(targetDir, $"hub_speedtest_{Guid.NewGuid():N}.tmp");
                int bufferSize = 1024 * 1024 * 4; // 4 MB buffer
                int totalBlocks = 16;             // 64 MB no total para ser ágil e não desgastar o SSD
                long totalBytes = (long)bufferSize * totalBlocks;
                byte[] data = new byte[bufferSize];
                new Random().NextBytes(data);

                try
                {
                    progressCallback($"Iniciando teste de gravação sequencial (64 MB) em {targetDir}...");
                    disk.SpeedStatusText = "Gravando...";

                    var sw = Stopwatch.StartNew();
                    using (var fs = new FileStream(testFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.WriteThrough))
                    {
                        for (int i = 0; i < totalBlocks; i++)
                        {
                            fs.Write(data, 0, bufferSize);
                        }
                        fs.Flush(true);
                    }
                    sw.Stop();

                    double writeSeconds = sw.Elapsed.TotalSeconds;
                    double writeMbPerSec = (totalBytes / (1024.0 * 1024.0)) / Math.Max(writeSeconds, 0.001);
                    disk.WriteSpeedMbps = Math.Round(writeMbPerSec, 1);
                    progressCallback($"Gravação concluída: {disk.FormattedWriteSpeed}");

                    progressCallback("Iniciando teste de leitura sequencial...");
                    disk.SpeedStatusText = "Lendo...";

                    sw.Restart();
                    try
                    {
                        // 0x20000000 = FILE_FLAG_NO_BUFFERING (contorna o cache da RAM e lê direto do disco físico)
                        using var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize, (FileOptions)0x20000000 | FileOptions.WriteThrough);
                        byte[] readBuffer = new byte[bufferSize];
                        while (fs.Read(readBuffer, 0, bufferSize) > 0) { }
                    }
                    catch
                    {
                        using var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize, FileOptions.SequentialScan);
                        byte[] readBuffer = new byte[bufferSize];
                        while (fs.Read(readBuffer, 0, bufferSize) > 0) { }
                    }
                    sw.Stop();

                    double readSeconds = sw.Elapsed.TotalSeconds;
                    double readMbPerSec = (totalBytes / (1024.0 * 1024.0)) / Math.Max(readSeconds, 0.001);
                    disk.ReadSpeedMbps = Math.Round(readMbPerSec, 1);
                    progressCallback($"Leitura concluída: {disk.FormattedReadSpeed}");

                    disk.SpeedStatusText = "Concluído";
                }
                catch (Exception ex)
                {
                    progressCallback($"Erro no benchmark: {ex.Message}");
                    disk.SpeedStatusText = "Falha";
                }
                finally
                {
                    try
                    {
                        if (File.Exists(testFile)) File.Delete(testFile);
                    }
                    catch { }

                    disk.IsTestingSpeed = false;
                }
            });
        }
    }
}
