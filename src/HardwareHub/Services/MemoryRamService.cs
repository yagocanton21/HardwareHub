using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HardwareHub.Models;

namespace HardwareHub.Services
{
    /// <summary>
    /// Consulta a memória física real e libera working set ocioso de processos não-críticos
    /// via <c>psapi.dll!EmptyWorkingSet</c>.
    /// </summary>
    public sealed class MemoryRamService
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ProcessSetQuota = 0x0100;

        private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Idle", "System", "Registry", "Memory Compression", "Secure System",
            "audiodg", "dwm", "csrss", "smss", "wininit"
        };

        private readonly Func<RamCleanupResult>? _trimHook;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        public MemoryRamService(Func<RamCleanupResult>? trimHook = null)
        {
            _trimHook = trimHook;
        }

        public static bool IsProtectedProcess(string? processName)
            => !string.IsNullOrWhiteSpace(processName) && ProtectedProcessNames.Contains(processName);

        public static (ulong TotalBytes, ulong AvailableBytes) GetMemorySnapshot()
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return (status.ullTotalPhys, status.ullAvailPhys);

            return (0, 0);
        }

        public Task<RamCleanupResult> TrimIdleMemoryAsync(CancellationToken ct = default)
        {
            return Task.Run(() => TrimIdleMemory(), ct);
        }

        public RamCleanupResult TrimIdleMemory()
        {
            if (_trimHook != null)
                return _trimHook();

            long beforeFree = (long)GetMemorySnapshot().AvailableBytes;
            int trimmed = 0;

            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        if (proc.Id <= 4 || IsProtectedProcess(proc.ProcessName))
                            continue;

                        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetQuota, false, proc.Id);
                        if (handle == IntPtr.Zero)
                            continue;

                        try
                        {
                            if (EmptyWorkingSet(handle))
                                trimmed++;
                        }
                        finally
                        {
                            CloseHandle(handle);
                        }
                    }
                    catch
                    {
                        // Processos protegidos pelo sistema (PPL) recusam abertura — seguro ignorar.
                    }
                }
            }

            Thread.Sleep(450);
            long afterFree = (long)GetMemorySnapshot().AvailableBytes;
            long freed = Math.Max(0L, afterFree - beforeFree);

            var result = new RamCleanupResult(
                Success: trimmed > 0,
                FreedBytes: freed,
                BeforeFreeBytes: beforeFree,
                AfterFreeBytes: afterFree,
                ProcessesTrimmed: trimmed,
                Message: trimmed > 0
                    ? $"RAM ociosa reduzida em {trimmed} processo(s) • liberados {RamModuleDetailModel.FormatBytes(freed)} (livre: {RamModuleDetailModel.FormatBytes(afterFree)})."
                    : "Nenhum processo aceitou redução de working set agora (tente como Administrador).");

            return result;
        }
    }
}

