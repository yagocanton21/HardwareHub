using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using HardwareHub.Models;
using Microsoft.Win32;

namespace HardwareHub.Services
{
    public interface ICapacityBlockDevice : IDisposable
    {
        long LengthBytes { get; }
        int SectorBytes { get; }
        void Read(long offset, byte[] buffer, int count);
        void Write(long offset, byte[] buffer, int count);
        void Flush();
    }

    public interface ICapacityBlockSource
    {
        ICapacityBlockDevice Open(RemovableDriveCandidate drive);
    }

    public sealed class FakeCapacityService
    {
        private const long MiB = 1048576L;
        private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("HUBCAP01");

        private readonly ICapacityBlockSource? _blockSource;
        private readonly Func<string, bool> _isUnsafeTargetPath;

        public FakeCapacityService(
            ICapacityBlockSource? blockSource = null,
            Func<string, bool>? unsafeDriveGuard = null)
        {
            _blockSource = blockSource;
            _isUnsafeTargetPath = unsafeDriveGuard ?? FormatBackupService.IsOnWindowsDrive;
        }

        public static bool IsRemovableBus(int busType) => busType == 7 || busType is 12 or 13;

        public static string? CheckDriveSafetyBlock(int diskNumber, int busType, IReadOnlySet<int>? protectedSystemDisks = null)
        {
            if (!IsRemovableBus(busType))
                return "Somente pendrives, cartões SD ou HDs/SSDs externos USB podem ser testados.";

            var sysDisks = protectedSystemDisks ?? GetProtectedSystemDisks();
            if (sysDisks.Contains(diskNumber))
                return "Este disco contém o Windows, o aplicativo ou o arquivo de paginação — bloqueado por segurança.";

            return null;
        }

        public static HashSet<int> GetProtectedSystemDisks()
        {
            var protectedSet = new HashSet<int>();
            var roots = new List<string>();

            try
            {
                string? sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
                if (!string.IsNullOrEmpty(sysRoot)) roots.Add(sysRoot);

                string? appRoot = Path.GetPathRoot(AppContext.BaseDirectory);
                if (!string.IsNullOrEmpty(appRoot)) roots.Add(appRoot);
            }
            catch
            {
            }

            try
            {
                using var mmKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
                if (mmKey?.GetValue("PagingFiles") is string[] pageFiles)
                {
                    foreach (var entry in pageFiles)
                    {
                        if (entry.Length >= 3 && char.IsLetter(entry[0]) && entry[1] == ':')
                            roots.Add($"{char.ToUpperInvariant(entry[0])}:\\");
                    }
                }
            }
            catch
            {
            }

            try
            {
                var lettersNeeded = roots
                    .Select(r => r.TrimEnd('\\').ToUpperInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                using var diskSearcher = new ManagementObjectSearcher("SELECT Index, DeviceID FROM Win32_DiskDrive");
                foreach (ManagementObject disk in diskSearcher.Get())
                {
                    using (disk)
                    {
                        int diskIndex = Convert.ToInt32(disk["Index"]);
                        string devId = disk["DeviceID"]?.ToString() ?? "";
                        if (devId.Length == 0) continue;

                        using var partSearcher = new ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{devId.Replace("'", "''")}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                        foreach (ManagementObject part in partSearcher.Get())
                        {
                            using (part)
                            {
                                string partId = part["DeviceID"]?.ToString() ?? "";
                                using var logSearcher = new ManagementObjectSearcher(
                                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId.Replace("'", "''")}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                                foreach (ManagementObject ld in logSearcher.Get())
                                {
                                    using (ld)
                                    {
                                        string letter = (ld["DeviceID"]?.ToString() ?? "").ToUpperInvariant();
                                        if (lettersNeeded.Contains(letter))
                                            protectedSet.Add(diskIndex);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return protectedSet;
        }

        public List<RemovableDriveCandidate> ListRemovableDrives()
        {
            var list = new List<RemovableDriveCandidate>();
            var sysDisks = GetProtectedSystemDisks();

            try
            {
                using var diskSearcher = new ManagementObjectSearcher(
                    "SELECT Index, Model, SerialNumber, Size, InterfaceType, MediaType, DeviceID FROM Win32_DiskDrive");
                foreach (ManagementObject disk in diskSearcher.Get())
                {
                    using (disk)
                    {
                        int index = Convert.ToInt32(disk["Index"]);
                        long size = Convert.ToInt64(disk["Size"] ?? 0L);
                        string iface = disk["InterfaceType"]?.ToString()?.Trim() ?? "";
                        string media = disk["MediaType"]?.ToString()?.Trim() ?? "";
                        int busType = iface.Equals("USB", StringComparison.OrdinalIgnoreCase)
                            ? 7
                            : (media.Contains("Removable", StringComparison.OrdinalIgnoreCase) ? 12 : 11);

                        if (!IsRemovableBus(busType) && !media.Contains("External", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var letters = new List<string>();
                        string devId = disk["DeviceID"]?.ToString() ?? "";
                        if (devId.Length > 0)
                        {
                            try
                            {
                                using var partSearcher = new ManagementObjectSearcher(
                                    $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{devId.Replace("'", "''")}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                                foreach (ManagementObject part in partSearcher.Get())
                                {
                                    using (part)
                                    {
                                        string partId = part["DeviceID"]?.ToString() ?? "";
                                        using var logSearcher = new ManagementObjectSearcher(
                                            $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId.Replace("'", "''")}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                                        foreach (ManagementObject ld in logSearcher.Get())
                                        {
                                            using (ld)
                                            {
                                                string letter = ld["DeviceID"]?.ToString() ?? "";
                                                if (letter.Length > 0) letters.Add(letter + "\\");
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }

                        string? block = CheckDriveSafetyBlock(index, busType, sysDisks);
                        list.Add(new RemovableDriveCandidate
                        {
                            DiskNumber = index,
                            Model = disk["Model"]?.ToString()?.Trim() ?? $"Disco {index}",
                            Serial = disk["SerialNumber"]?.ToString()?.Trim() ?? "",
                            BusType = busType,
                            BusLabel = busType == 12 ? "SD / Cartão" : "USB",
                            AnnouncedBytes = size,
                            MountLetters = letters.ToArray(),
                            BlockReason = block
                        });
                    }
                }
            }
            catch
            {
            }

            return list.OrderBy(d => d.DiskNumber).ToList();
        }

        public FakeCapacityReport ExecuteBlockCapacityTest(
            RemovableDriveCandidate drive,
            FakeCapacityOptions? options = null,
            IProgress<FakeCapacityProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (_blockSource == null)
                throw new InvalidOperationException("Nenhuma fonte de blocos conectada para teste em setor bruto.");

            options ??= new FakeCapacityOptions();
            var sw = Stopwatch.StartNew();
            var report = new FakeCapacityReport
            {
                Drive = drive,
                AnnouncedBytes = drive.AnnouncedBytes,
                RealBytes = drive.AnnouncedBytes
            };

            string? safetyBlock = CheckDriveSafetyBlock(drive.DiskNumber, drive.BusType, Array.Empty<int>().ToHashSet());
            if (!drive.CanTest && safetyBlock == null && !string.IsNullOrWhiteSpace(drive.BlockReason))
                safetyBlock = drive.BlockReason;
            if (safetyBlock != null)
            {
                report.ErrorMessage = safetyBlock;
                report.Verdict = FakeCapacityVerdict.Inconclusive;
                return report;
            }

            using var dev = _blockSource.Open(drive);
            long totalSize = dev.LengthBytes;
            int bs = Math.Max(4096, dev.SectorBytes);
            report.AnnouncedBytes = totalSize;

            long[] points = BuildTestOffsets(totalSize, bs, Math.Clamp(options.Points, 8, 256));
            var states = new CapacityPointState[points.Length];
            var collidedFrom = new long[points.Length];
            Array.Fill(collidedFrom, -1L);

            var sessionId = Guid.NewGuid();
            var originals = new byte[points.Length][];
            byte[] buf = new byte[bs];

            try
            {
                // Step 1: Read and preserve original sector contents before touching anything
                progress?.Report(new FakeCapacityProgress("Lendo setores originais para backup...", 0.1, 0, points.Length));
                for (int i = 0; i < points.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        dev.Read(points[i], buf, bs);
                        originals[i] = (byte[])buf.Clone();
                    }
                    catch
                    {
                        states[i] = CapacityPointState.Error;
                    }
                }

                // Step 2: Write signed marker packets sequentially/randomly across capacity map
                var writeSw = Stopwatch.StartNew();
                long bytesWritten = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (states[i] == CapacityPointState.Error) continue;

                    WriteSignaturePacket(buf, sessionId, points[i], i);
                    try
                    {
                        dev.Write(points[i], buf, bs);
                        states[i] = CapacityPointState.Written;
                        bytesWritten += bs;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw;
                    }
                    catch
                    {
                        states[i] = CapacityPointState.Lost;
                    }

                    if (i % 8 == 0 || i == points.Length - 1)
                    {
                        progress?.Report(new FakeCapacityProgress(
                            $"Gravando assinaturas de teste ({i + 1}/{points.Length})...",
                            0.1 + 0.4 * (i + 1) / points.Length,
                            i + 1,
                            points.Length));
                    }
                }
                dev.Flush();
                writeSw.Stop();
                if (writeSw.Elapsed.TotalSeconds > 0.001)
                    report.WriteSpeedMBps = Math.Round((bytesWritten / (1024.0 * 1024.0)) / writeSw.Elapsed.TotalSeconds, 2);

                // Step 3: Read back all points and detect wrap-around collisions or lost blocks
                var readSw = Stopwatch.StartNew();
                long bytesRead = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (states[i] != CapacityPointState.Written) continue;

                    try
                    {
                        dev.Read(points[i], buf, bs);
                        bytesRead += bs;
                        var (valid, decodedOffset) = VerifySignaturePacket(buf, sessionId);
                        if (valid && decodedOffset == points[i])
                        {
                            states[i] = CapacityPointState.Ok;
                        }
                        else if (valid)
                        {
                            states[i] = CapacityPointState.Wrapped;
                            collidedFrom[i] = decodedOffset;
                        }
                        else
                        {
                            states[i] = CapacityPointState.Lost;
                        }
                    }
                    catch
                    {
                        states[i] = CapacityPointState.Error;
                    }
                }
                readSw.Stop();
                if (readSw.Elapsed.TotalSeconds > 0.001)
                    report.ReadSpeedMBps = Math.Round((bytesRead / (1024.0 * 1024.0)) / readSw.Elapsed.TotalSeconds, 2);

                // Compute real capacity & verdict
                long realBytes = totalSize;
                var periods = new List<long>();
                for (int i = 0; i < points.Length; i++)
                {
                    if (states[i] == CapacityPointState.Wrapped && collidedFrom[i] >= 0 && collidedFrom[i] != points[i])
                        periods.Add(Math.Abs(points[i] - collidedFrom[i]));
                }

                if (periods.Count > 0)
                {
                    report.WrapAroundDetected = true;
                    long gcdPeriod = periods.Aggregate(Gcd);
                    realBytes = Math.Min(realBytes, gcdPeriod);
                }

                var badIndices = Enumerable.Range(0, points.Length)
                    .Where(i => states[i] is CapacityPointState.Lost or CapacityPointState.Error)
                    .ToList();

                if (badIndices.Count > 0)
                {
                    long firstBadOffset = points[badIndices.Min()];
                    int totalAfterBad = points.Count(p => p >= firstBadOffset);
                    int badAfterFirst = badIndices.Count(i => points[i] >= firstBadOffset);

                    report.IrregularPoints = badAfterFirst < totalAfterBad * 0.5 ? badIndices.Count : 0;
                    realBytes = Math.Min(realBytes, firstBadOffset);
                }

                report.PointStates = states;
                report.PointsOk = states.Count(s => s == CapacityPointState.Ok);
                report.PointsBad = states.Count(s => s is CapacityPointState.Wrapped or CapacityPointState.Lost or CapacityPointState.Error);
                report.RealBytes = Math.Max(0, realBytes);

                if (report.IrregularPoints > 0 && !report.WrapAroundDetected)
                    report.Verdict = FakeCapacityVerdict.Defective;
                else if (report.RealBytes < totalSize - 4 * MiB)
                    report.Verdict = FakeCapacityVerdict.Counterfeit;
                else
                {
                    report.Verdict = FakeCapacityVerdict.Original;
                    report.RealBytes = totalSize;
                }
            }
            catch (OperationCanceledException)
            {
                report.Verdict = FakeCapacityVerdict.Inconclusive;
                report.ErrorMessage = "Teste cancelado pelo usuário.";
            }
            catch (Exception ex)
            {
                report.Verdict = FakeCapacityVerdict.Inconclusive;
                report.ErrorMessage = ex.Message;
            }
            finally
            {
                // Always restore original sectors before releasing handle
                bool allRestored = true;
                for (int i = points.Length - 1; i >= 0; i--)
                {
                    if (originals[i] == null) continue;
                    try { dev.Write(points[i], originals[i], bs); } catch { allRestored = false; }
                }
                try { dev.Flush(); } catch { }
                report.OriginalDataRestored = allRestored;
            }

            sw.Stop();
            report.Duration = sw.Elapsed;
            progress?.Report(new FakeCapacityProgress("Concluído e dados originais preservados.", 1.0, points.Length, points.Length));
            return report;
        }

        /// <summary>
        /// Non-destructive file-level probe for mounted removable drives (requires zero raw sector locking/admin).
        /// Writes uniquely signed files up to free-space sample limit, reads them back to verify integrity and speed, then deletes all temp files.
        /// </summary>
        public async Task<FakeCapacityReport> ExecuteMountedVolumeProbeAsync(
            RemovableDriveCandidate drive,
            string mountedRootPath,
            int sampleFilesMb = 128,
            IProgress<FakeCapacityProgress>? progress = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var report = new FakeCapacityReport
            {
                Drive = drive,
                AnnouncedBytes = drive.AnnouncedBytes,
                RealBytes = drive.AnnouncedBytes
            };

            if (!Directory.Exists(mountedRootPath))
            {
                report.ErrorMessage = $"Unidade montada não acessível: {mountedRootPath}";
                return report;
            }

            if (_isUnsafeTargetPath(mountedRootPath))
            {
                report.ErrorMessage = "Bloqueado por segurança: nunca executa escrita na unidade do Windows.";
                return report;
            }

            var di = new DriveInfo(Path.GetPathRoot(mountedRootPath)!);
            long usableFree = di.AvailableFreeSpace;
            if (usableFree < 2 * MiB)
            {
                report.ErrorMessage = "Espaço livre insuficiente na unidade para testar arquivos temporários.";
                return report;
            }

            long targetSampleBytes = Math.Min(usableFree - MiB, Math.Clamp(sampleFilesMb, 8, 2048) * MiB);
            const int chunkSize = (int)MiB;
            int chunks = Math.Max(4, (int)(targetSampleBytes / chunkSize));

            string probeDir = Path.Combine(mountedRootPath, $".hub_capacity_probe_{Guid.NewGuid():N}");
            Directory.CreateDirectory(probeDir);
            var files = new List<(string Path, byte[] Digest)>();
            var states = new CapacityPointState[chunks];

            try
            {
                var writeSw = Stopwatch.StartNew();
                long totalWritten = 0;

                for (int i = 0; i < chunks; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] payload = new byte[chunkSize];
                    RandomNumberGenerator.Fill(payload);
                    byte[] digest = SHA256.HashData(payload);

                    string filePath = Path.Combine(probeDir, $"probe_{i:D4}.bin");
                    await File.WriteAllBytesAsync(filePath, payload, ct);
                    files.Add((filePath, digest));
                    states[i] = CapacityPointState.Written;
                    totalWritten += chunkSize;

                    if (i % 4 == 0 || i == chunks - 1)
                    {
                        progress?.Report(new FakeCapacityProgress(
                            $"Gravando arquivo de verificação {i + 1}/{chunks} ({InstalledProgramItem.FormatBytes(totalWritten)})...",
                            0.45 * (i + 1) / chunks,
                            i + 1,
                            chunks));
                    }
                }
                writeSw.Stop();
                report.WriteSpeedMBps = Math.Round((totalWritten / (double)MiB) / Math.Max(0.01, writeSw.Elapsed.TotalSeconds), 2);

                var readSw = Stopwatch.StartNew();
                long totalRead = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        byte[] actual = await File.ReadAllBytesAsync(files[i].Path, ct);
                        totalRead += actual.Length;
                        byte[] actualDigest = SHA256.HashData(actual);
                        states[i] = actualDigest.SequenceEqual(files[i].Digest)
                            ? CapacityPointState.Ok
                            : CapacityPointState.Wrapped;
                    }
                    catch
                    {
                        states[i] = CapacityPointState.Error;
                    }

                    if (i % 4 == 0 || i == files.Count - 1)
                    {
                        progress?.Report(new FakeCapacityProgress(
                            $"Validando hashes SHA-256 ({i + 1}/{files.Count})...",
                            0.45 + 0.5 * (i + 1) / files.Count,
                            i + 1,
                            files.Count));
                    }
                }
                readSw.Stop();
                report.ReadSpeedMBps = Math.Round((totalRead / (double)MiB) / Math.Max(0.01, readSw.Elapsed.TotalSeconds), 2);

                report.PointStates = states;
                report.PointsOk = states.Count(s => s == CapacityPointState.Ok);
                report.PointsBad = states.Count(s => s != CapacityPointState.Ok);
                report.WrapAroundDetected = states.Any(s => s == CapacityPointState.Wrapped);

                if (report.PointsBad == 0)
                {
                    report.Verdict = FakeCapacityVerdict.Original;
                    report.RealBytes = drive.AnnouncedBytes;
                }
                else if (report.WrapAroundDetected)
                {
                    report.Verdict = FakeCapacityVerdict.Counterfeit;
                    int firstBad = Array.FindIndex(states, s => s != CapacityPointState.Ok);
                    report.RealBytes = (long)Math.Max(0, firstBad) * chunkSize;
                }
                else
                {
                    report.Verdict = FakeCapacityVerdict.Defective;
                }
            }
            catch (OperationCanceledException)
            {
                report.Verdict = FakeCapacityVerdict.Inconclusive;
                report.ErrorMessage = "Teste cancelado.";
            }
            catch (Exception ex)
            {
                report.Verdict = FakeCapacityVerdict.Inconclusive;
                report.ErrorMessage = ex.Message;
            }
            finally
            {
                try { if (Directory.Exists(probeDir)) Directory.Delete(probeDir, recursive: true); } catch { }
                report.OriginalDataRestored = true;
            }

            sw.Stop();
            report.Duration = sw.Elapsed;
            progress?.Report(new FakeCapacityProgress("Teste concluído e arquivos temporários limpos.", 1.0, chunks, chunks));
            return report;
        }

        private static long[] BuildTestOffsets(long totalBytes, int blockSize, int desiredPoints)
        {
            var set = new SortedSet<long>();
            long step = Math.Max(blockSize, (totalBytes / desiredPoints) / blockSize * blockSize);
            for (long off = 0; off + blockSize <= totalBytes && set.Count < desiredPoints; off += step)
                set.Add(off);

            long lastAligned = ((totalBytes - blockSize) / blockSize) * blockSize;
            if (lastAligned >= 0) set.Add(lastAligned);
            return set.ToArray();
        }

        private static void WriteSignaturePacket(byte[] buffer, Guid sessionId, long offset, int index)
        {
            Array.Clear(buffer);
            MagicHeader.CopyTo(buffer, 0);
            sessionId.TryWriteBytes(buffer.AsSpan(8, 16));
            BitConverter.TryWriteBytes(buffer.AsSpan(24, 8), offset);
            BitConverter.TryWriteBytes(buffer.AsSpan(32, 4), index);
        }

        private static (bool Valid, long Offset) VerifySignaturePacket(byte[] buffer, Guid expectedSession)
        {
            if (buffer.Length < 36) return (false, -1);
            if (!buffer.AsSpan(0, MagicHeader.Length).SequenceEqual(MagicHeader))
                return (false, -1);
            var guid = new Guid(buffer.AsSpan(8, 16));
            if (guid != expectedSession)
                return (false, -1);
            long off = BitConverter.ToInt64(buffer.AsSpan(24, 8));
            return (true, off);
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0)
            {
                long t = b;
                b = a % b;
                a = t;
            }
            return Math.Abs(a);
        }
    }
}

