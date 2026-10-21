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

                    results[index] = new DiskHealthInfo
                    {
                        Index = index,
                        Model = model,
                        Serial = serial,
                        Interface = iface,
                        MediaType = (model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) ? "SSD" : "HDD",
                        CapacityGb = Math.Round(sizeBytes / (1024.0 * 1024.0 * 1024.0), 1),
                        HealthStatus = "Saudável",
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
                            string formatted = $"{letter.ToUpper()}:";
                            if (string.IsNullOrEmpty(info.DriveLetters))
                                info.DriveLetters = formatted;
                            else if (!info.DriveLetters.Contains(formatted))
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

                        int health = Convert.ToInt32(obj["HealthStatus"] ?? 0);
                        info.HealthStatus = health switch
                        {
                            0 => "Saudável",
                            1 => "Atenção",
                            2 => "Crítico",
                            _ => "Saudável"
                        };

                        int media = Convert.ToInt32(obj["MediaType"] ?? 0);
                        info.MediaType = media switch
                        {
                            3 => "HDD",
                            4 => "SSD",
                            _ => info.MediaType
                        };

                        int bus = Convert.ToInt32(obj["BusType"] ?? 0);
                        info.Interface = bus switch
                        {
                            17 => "NVMe",
                            11 => "SATA",
                            7 => "USB",
                            8 => "RAID",
                            _ => info.Interface
                        };
                    }
                }

                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, Temperature, PowerOnHours, ReadErrorsTotal, WriteErrorsTotal, Wear FROM MSFT_StorageReliabilityCounter")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string devId = obj["DeviceId"]?.ToString() ?? "";
                        if (!int.TryParse(devId, out int idx) || !results.TryGetValue(idx, out var info)) continue;

                        if (obj["Temperature"] != null && double.TryParse(obj["Temperature"]?.ToString(), out double temp) && temp > 0)
                        {
                            info.Temperature = temp;
                        }

                        if (obj["PowerOnHours"] != null && long.TryParse(obj["PowerOnHours"]?.ToString(), out long hours))
                        {
                            info.PowerOnHours = hours;
                        }

                        if (obj["Wear"] != null && int.TryParse(obj["Wear"]?.ToString(), out int wear))
                        {
                            info.HealthPercentage = Math.Max(0, 100 - wear);
                        }

                        long readErr = Convert.ToInt64(obj["ReadErrorsTotal"] ?? 0L);
                        long writeErr = Convert.ToInt64(obj["WriteErrorsTotal"] ?? 0L);
                        if (readErr > 0 || writeErr > 0)
                        {
                            info.HasSmartErrors = true;
                            if (info.HealthStatus == "Saudável") info.HealthStatus = "Atenção";
                        }
                    }
                }
            }
            catch { }

            // 4. Integração nativa com Base S.M.A.R.T. do CrystalDiskInfo (se presente em D:\Crystal disk info e mark Portable)
            try
            {
                string cdiSmartBase = @"D:\Crystal disk info e mark Portable\Smart";
                if (Directory.Exists(cdiSmartBase))
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

        public async Task RunSpeedBenchmarkAsync(DiskHealthInfo disk, Action<string> progressCallback)
        {
            if (disk.IsTestingSpeed) return;
            disk.IsTestingSpeed = true;
            disk.SpeedStatusText = "Preparando teste...";

            await Task.Run(() =>
            {
                string targetDir = Path.GetTempPath();
                // Tenta achar a primeira letra montada deste disco
                if (!string.IsNullOrEmpty(disk.DriveLetters))
                {
                    string firstLetter = disk.DriveLetters.Split(',')[0].Trim();
                    string candidate = $"{firstLetter}\\";
                    if (Directory.Exists(candidate))
                    {
                        targetDir = candidate;
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
