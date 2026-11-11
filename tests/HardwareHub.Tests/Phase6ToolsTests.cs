using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using HardwareHub.Models;
using HardwareHub.Services;
using HardwareHub.ViewModels;
using HardwareHub.Views;
using Xunit;

namespace HardwareHub.Tests
{
    public sealed class Phase6ToolsTests
    {
        // ===================== 1. PRIVACIDADE & MEMÓRIA RAM =====================

        [Fact]
        public void PrivacySanitizer_MasksUserMachineDomainAndUserFolders()
        {
            string raw = @"Erro para Yago em DESKTOP-9988 usando C:\Users\Yago\Desktop\log.txt (domínio CORPHUB)";
            string clean = PrivacySanitizer.SanitizeWithTokens(raw, "Yago", "DESKTOP-9988", "CORPHUB");

            Assert.DoesNotContain("Yago", clean);
            Assert.DoesNotContain("DESKTOP-9988", clean);
            Assert.DoesNotContain("CORPHUB", clean);
            Assert.Contains("[usuário]", clean);
            Assert.Contains("[nome do PC]", clean);
            Assert.Contains("[domínio]", clean);
            Assert.Contains(@"C:\Users\[usuário]\Desktop\log.txt", clean);
        }

        [Fact]
        public async Task MemoryRamService_ProtectsSystemProcesses_AndTrimsViaHook()
        {
            Assert.True(MemoryRamService.IsProtectedProcess("System"));
            Assert.True(MemoryRamService.IsProtectedProcess("dwm"));
            Assert.True(MemoryRamService.IsProtectedProcess("Memory Compression"));
            Assert.False(MemoryRamService.IsProtectedProcess("chrome"));

            var svc = new MemoryRamService(trimHook: () => new RamCleanupResult(
                Success: true,
                FreedBytes: 512L * 1024 * 1024,
                BeforeFreeBytes: 3_000_000_000L,
                AfterFreeBytes: 3_536_870_912L,
                ProcessesTrimmed: 24,
                Message: "RAM liberada."));

            var res = await svc.TrimIdleMemoryAsync();
            Assert.True(res.Success);
            Assert.Equal(24, res.ProcessesTrimmed);
            Assert.Equal("512 MB", res.FormattedFreed);
        }

        // ===================== 2. INVENTÁRIO HARDWARE (JEDEC / DUAL CHANNEL / VRAM > 4GB) =====================

        [Fact]
        public void PcInventoryService_ResolvesJedecChannelsXmpAndLargeVram()
        {
            Assert.Equal("Samsung", PcInventoryService.ResolveJedecManufacturer("0x80CE"));
            Assert.Equal("G.Skill", PcInventoryService.ResolveJedecManufacturer("04CD"));
            Assert.Equal("Kingston", PcInventoryService.ResolveJedecManufacturer("0198"));

            var singleSlot = new List<RamModuleDetailModel>
            {
                new() { Slot = "DIMM_A1", ConfiguredSpeedMt = 2133, NominalSpeedMt = 3200, PartNumber = "CMK16GX4M1E3200C16" }
            };
            Assert.Contains("Canal único", PcInventoryService.DetectMemoryChannelMode(singleSlot));
            Assert.Contains("DESLIGADO", PcInventoryService.EstimateXmpStatus(singleSlot));

            var dualModules = new List<RamModuleDetailModel>
            {
                new() { Slot = "DIMM_A2", ConfiguredSpeedMt = 3600, NominalSpeedMt = 3600, PartNumber = "F4-3600C16D" },
                new() { Slot = "DIMM_B2", ConfiguredSpeedMt = 3600, NominalSpeedMt = 3600, PartNumber = "F4-3600C16D" }
            };
            Assert.Contains("Dual channel (2 canais)", PcInventoryService.DetectMemoryChannelMode(dualModules));
            Assert.Contains("Provavelmente ligado", PcInventoryService.EstimateXmpStatus(dualModules));

            ulong vram8Gb = 8UL * 1024 * 1024 * 1024;
            Assert.Equal(vram8Gb, PcInventoryService.ParseVramValue((long)vram8Gb));
            Assert.Equal(vram8Gb, PcInventoryService.ParseVramValue(BitConverter.GetBytes(vram8Gb)));
        }

        // ===================== 3. SAÚDE NÃO-DESTRUTIVA & REPAROS EXTRAS =====================

        [Fact]
        public async Task WindowsHealthRepairService_ParsesHealthChecksAndRepairsWmi()
        {
            Assert.Equal("Consistente", WindowsHealthRepairService.InterpretWmiVerification(0, "WMI repository is consistent"));
            Assert.Equal("Inconsistente", WindowsHealthRepairService.InterpretWmiVerification(0, "WMI repository is inconsistent"));
            Assert.Equal("SemPermissao", WindowsHealthRepairService.InterpretWmiVerification(5, "Erro 0x80070005 acesso negado"));
            Assert.True(WindowsHealthRepairService.DetectSfcVerifyCorruption("A Proteção de Recursos encontrou violações de integridade."));
            Assert.Equal("5 min adiantado", WindowsHealthRepairService.DescribeClockDelta(TimeSpan.FromMinutes(5)));

            bool wmiSalvaged = false;
            var svc = new WindowsHealthRepairService(
                processRunner: (exe, args, _, onPct, _) =>
                {
                    string joined = string.Join(" ", args);
                    if (exe.EndsWith("dism.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        onPct?.Invoke(100);
                        return Task.FromResult((0, "100.0% Operação concluída", ""));
                    }
                    if (exe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult((0, "Repairable", ""));
                    if (exe.EndsWith("winmgmt.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (joined.Contains("/salvagerepository", StringComparison.OrdinalIgnoreCase))
                        {
                            wmiSalvaged = true;
                            return Task.FromResult((0, "Salvaged", ""));
                        }
                        return Task.FromResult((0, "WMI repository is inconsistent", ""));
                    }
                    if (exe.EndsWith("sfc.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        onPct?.Invoke(100);
                        return Task.FromResult((0, "Windows Resource Protection did not find any integrity violations.", ""));
                    }
                    return Task.FromResult((0, "O Windows verificou o sistema de arquivos e não encontrou problemas.", ""));
                },
                clockDeltaHook: _ => Task.FromResult<TimeSpan?>(TimeSpan.FromMinutes(6)),
                pendingRebootHook: () => true,
                dirtyBitHook: () => false,
                isAdminHook: () => true);

            var report = await svc.RunHealthCheckAsync(fullScan: true);
            Assert.True(report.IsFullScan);
            Assert.Equal("Repairable", report.ImageHealthState);
            Assert.True(report.HasWmiIssue);
            Assert.False(report.SfcFoundCorruption);
            Assert.True(report.PendingReboot);
            Assert.Equal(6, report.Items.Count);

            var wmiStep = WindowsHealthRepairService.CreateAdvancedRepairSteps().First(s => s.Id == "wmi");
            var repairRes = await svc.ExecuteAdvancedStepAsync(wmiStep);
            Assert.True(wmiSalvaged);
            Assert.Equal(WindowsHealthStatus.Healthy, repairRes.Status);
            Assert.True(repairRes.RequiresReboot);
        }

        // ===================== 4. RELATÓRIO TÉCNICO & SUGESTÕES PROATIVAS =====================

        [Fact]
        public void ReportExportService_GeneratesSuggestionsAndSanitizedExports()
        {
            var inv = new SystemInventoryReport
            {
                SystemSummary = "Windows 11 Pro 24H2",
                MotherboardSummary = "ASUS B550M",
                CpuSummary = "AMD Ryzen 7 5700X",
                MemorySummary = "32 GB DDR4 3200 MT/s",
                SystemLines = { new("Build", "26100.1"), new("Ligado há", "2 h 15 min"), new("Nome do computador", Environment.MachineName) },
                MemoryLines = { new("Canais (estimado)", "Dual channel (2 canais)"), new("XMP/EXPO (estimado)", "Provavelmente DESLIGADO: roda a 2133 MT/s") },
                RamModules = { new RamModuleDetailModel { Slot = "DIMM_A2", Manufacturer = "Kingston", CapacityBytes = 16UL * 1024 * 1024 * 1024, Type = "DDR4", ConfiguredSpeedMt = 2133, NominalSpeedMt = 3200, PartNumber = "KF432C16BB/16" } },
                VideoAdapters = { new GpuInventoryDetailModel { Name = "RTX 4070", VramBytes = 12UL * 1024 * 1024 * 1024, DriverVersion = "566.36", Lines = { new("VRAM", "12 GB") } } },
                Volumes = { new VolumeInventoryModel { DriveLetter = "C:", Kind = "Interno", FileSystem = "NTFS", DiskModel = "NVMe SSD", TotalBytes = 500_000_000_000, FreeBytes = 25_000_000_000 } }
            };

            var health = new WindowsHealthReport
            {
                ImageHealthState = "Repairable",
                WmiStatus = "Inconsistente",
                PendingReboot = true,
                ClockDelta = TimeSpan.FromMinutes(10),
                SummaryMessage = "4 pontos requerem atenção.",
                Items = { new WindowsHealthCheckItem { Title = "Banco WMI", Status = WindowsHealthStatus.Critical, StatusText = "Inconsistente", Detail = "WMI precisa reparo" } }
            };

            var svc = new ReportExportService();
            var suggestions = svc.EvaluateSuggestions(inv, health, new DiskOptimizationConfig { TrimEnabled = false });

            Assert.Contains(suggestions, s => s.Id == "low_space_C:");
            Assert.Contains(suggestions, s => s.Id == "ram_xmp_off");
            Assert.Contains(suggestions, s => s.Id == "windows_repair_needed");
            Assert.Contains(suggestions, s => s.Id == "wmi_repair_needed");
            Assert.Contains(suggestions, s => s.Id == "clock_out_of_sync");
            Assert.Contains(suggestions, s => s.Id == "pending_reboot");
            Assert.Contains(suggestions, s => s.Id == "trim_disabled");

            string summary = svc.BuildTextSummary(inv, health);
            Assert.Contains("RTX 4070 (12 GB)", summary);

            string html = svc.BuildHtmlReport(inv, health);
            Assert.Contains("<!doctype html>", html);
            Assert.Contains("ASUS B550M", html);

            string json = svc.BuildJsonReport(inv, health);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Hardware HUB", doc.RootElement.GetProperty("gerado_por").GetString());

            string tempHtml = Path.Combine(Path.GetTempPath(), $"hh_rep_{Guid.NewGuid():N}.html");
            try
            {
                svc.SaveReportToFile(inv, tempHtml, health);
                Assert.True(File.Exists(tempHtml));
                Assert.Contains("Hardware HUB", File.ReadAllText(tempHtml));
            }
            finally
            {
                if (File.Exists(tempHtml)) File.Delete(tempHtml);
            }
        }

        // ===================== 5. UI WPF STA: NAVEGAÇÃO & RENDERIZAÇÃO =====================

        [Fact]
        public void MainWindow_NavigatesToHealthReport_AndSubTabsRenderWithoutHeaderOverlap()
        {
            WpfTestHost.RunOnWpfThread(() =>
            {
                var mockedVm = new WindowsHealthReportViewModel(
                    inventoryService: new PcInventoryService(() => new SystemInventoryReport
                    {
                        SystemSummary = "Windows 11 Pro 24H2",
                        MotherboardSummary = "Gigabyte B650M",
                        CpuSummary = "Ryzen 5 7600",
                        MemorySummary = "32 GB DDR5 6000 MT/s",
                        Volumes = { new VolumeInventoryModel { DriveLetter = "C:", Kind = "Interno", TotalBytes = 1_000_000_000_000, FreeBytes = 600_000_000_000 } }
                    }),
                    healthRepairService: new WindowsHealthRepairService(
                        processRunner: (_, _, _, _, _) => Task.FromResult((0, "Healthy", "")),
                        clockDeltaHook: _ => Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(1)),
                        pendingRebootHook: () => false,
                        dirtyBitHook: () => false,
                        isAdminHook: () => true),
                    memoryService: new MemoryRamService(() => new RamCleanupResult(true, 128 * 1024 * 1024, 4_000_000_000, 4_134_217_728, 12, "128 MB liberados.")));

                var view = new WindowsHealthReportView { DataContext = mockedVm };
                var hostWin = WpfTestHost.ShowHostedControl(view);
                try
                {
                    AssertHeaderActionDoesNotOverlapTitle(view, "Checagem Rápida", "Saúde do Windows, Inventário & Relatório");
                    AssertHeaderActionDoesNotOverlapTitle(view, "Checagem Completa", "Saúde do Windows, Inventário & Relatório");

                    mockedVm.RunQuickHealthCheckCommand.Execute(null);
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while ((mockedVm.IsBusy || mockedVm.HealthItems.Count < 6) && DateTime.UtcNow < deadline)
                    {
                        WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);
                        Thread.Sleep(25);
                    }

                    mockedVm.TrimRamCommand.Execute(null);
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    Assert.Equal(6, mockedVm.HealthItems.Count);
                    Assert.NotEmpty(mockedVm.Suggestions);
                    Assert.Contains("128 MB liberados", mockedVm.RamCleanupBanner);

                    foreach (string tab in new[] { "Repairs", "Inventory", "Report", "Health" })
                    {
                        mockedVm.SelectSubTabCommand.Execute(tab);
                        WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);
                        Assert.Equal(tab, mockedVm.ActiveSubTab);
                    }
                }
                finally
                {
                    hostWin.Close();
                }

                // Testa navegação na MainWindow até HealthReport
                using var mainVm = new MainViewModel();
                var window = new MainWindow { DataContext = mainVm };
                window.Show();
                WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);
                try
                {
                    var navScroller = WpfTestHost.FindVisualChildren<ScrollViewer>(window).FirstOrDefault();
                    Assert.NotNull(navScroller);

                    var navBtn = WpfTestHost.FindVisualChildren<Button>(navScroller!)
                        .First(b => Equals(b.CommandParameter, "HealthReport"));
                    navBtn.BringIntoView();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    ((IInvokeProvider)new ButtonAutomationPeer(navBtn).GetPattern(PatternInterface.Invoke)).Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    Assert.True(mainVm.IsHealthReportActive);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        private static void AssertHeaderActionDoesNotOverlapTitle(UserControl view, string buttonTextFragment, string titleFragment)
        {
            var titleBlock = WpfTestHost.FindVisualChildren<TextBlock>(view)
                .First(t => t.Text.Contains(titleFragment, StringComparison.Ordinal));
            var actionButton = WpfTestHost.FindVisualChildren<Button>(view)
                .First(b => (b.Content?.ToString()?.Contains(buttonTextFragment, StringComparison.Ordinal) == true) ||
                            WpfTestHost.FindVisualChildren<TextBlock>(b).Any(tb => tb.Text.Contains(buttonTextFragment, StringComparison.Ordinal)));

            Assert.True(titleBlock.ActualWidth > 0 && titleBlock.ActualHeight > 0);
            Assert.True(actionButton.ActualWidth > 0 && actionButton.ActualHeight > 0);

            var titleRect = new Rect(
                titleBlock.TranslatePoint(new Point(0, 0), view),
                new Size(titleBlock.ActualWidth, titleBlock.ActualHeight));
            var btnRect = new Rect(
                actionButton.TranslatePoint(new Point(0, 0), view),
                new Size(actionButton.ActualWidth, actionButton.ActualHeight));

            Assert.False(titleRect.IntersectsWith(btnRect),
                $"Header title '{titleRect}' overlapped button '{btnRect}'.");
        }
    }
}
