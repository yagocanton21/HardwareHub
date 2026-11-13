using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using HardwareHub.Models;
using HardwareHub.Services;
using HardwareHub.ViewModels;
using HardwareHub.Views;
using Microsoft.Win32;
using Xunit;

namespace HardwareHub.Tests
{
    public sealed class Phase3ToolsTests
    {
        [Fact]
        public void StorageCleanerService_LoadsEmbeddedCleanupCatalog_AndEnforcesSafetyGuards()
        {
            var rules = StorageCleanerService.LoadEmbeddedCleanupRules();

            Assert.NotEmpty(rules);
            Assert.Contains(rules, r => r.Id == "temp-usuario");
            Assert.Contains(rules, r => r.Id == "lixeira");
            Assert.True(StorageCleanerService.IsForbiddenDuplicateFolder(Path.GetTempPath()));

            Assert.True(StorageCleanerService.IsDangerousCleanRoot(@"C:\"));
            Assert.True(StorageCleanerService.IsDangerousCleanRoot(@"C:\Windows"));
            Assert.True(StorageCleanerService.IsDangerousCleanRoot(@"C:\Program Files"));
            Assert.False(StorageCleanerService.IsDangerousCleanRoot(Path.Combine(Path.GetTempPath(), "safe_sub")));

            Assert.True(StorageCleanerService.IsForbiddenDuplicateFolder(@"C:\Windows\System32"));
            Assert.True(StorageCleanerService.IsForbiddenDuplicateFolder(@"C:\Users\demo\repo\node_modules"));
            Assert.False(StorageCleanerService.IsForbiddenDuplicateFolder(@"C:\Users\demo\Documents"));
        }

        [Fact]
        public async Task StorageCleanerService_ScansAndCleansTempRule_RespectingAgeAndBlocker()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"hh_clean_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                string oldFile = Path.Combine(tempDir, "old.log");
                string freshFile = Path.Combine(tempDir, "fresh.log");
                await File.WriteAllBytesAsync(oldFile, new byte[2048]);
                await File.WriteAllBytesAsync(freshFile, new byte[1024]);
                File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddHours(-48));
                File.SetLastWriteTimeUtc(freshFile, DateTime.UtcNow);

                var rule = new CleanupRuleDef
                {
                    Id = "test-logs",
                    Categoria = "Sistema",
                    Titulo = "Logs de Teste",
                    Nivel = "seguro",
                    Recomendado = true,
                    Alvos = new List<CleanupTargetDef>
                    {
                        new() { Caminho = tempDir, Padrao = "*.log", IdadeMinimaHoras = 24, Recursivo = false }
                    }
                };

                var svc = new StorageCleanerService(customRules: new[] { rule });
                var scan = svc.ScanRule(rule);

                Assert.Equal(2048, scan.Bytes);
                Assert.Equal(1, scan.FileCount);

                var itemVm = new CleanupItemViewModel(rule) { IsSelected = true };
                var exec = await svc.CleanRulesAsync(new[] { itemVm });

                Assert.Equal(1, exec.RulesExecuted);
                Assert.Equal(2048, exec.BytesFreed);
                Assert.Equal(1, exec.FilesDeleted);
                Assert.False(File.Exists(oldFile));
                Assert.True(File.Exists(freshFile));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task StorageCleanerService_FindsDuplicatesIn3Stages_AndNeverDeletesAllCopies()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, $"_hh_dup_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);

            try
            {
                // Identical payload (2.5 KB) across 2 copies, plus same-size different-byte decoy
                byte[] sameBytes = new byte[2500];
                Random.Shared.NextBytes(sameBytes);
                byte[] diffMiddleBytes = (byte[])sameBytes.Clone();
                diffMiddleBytes[1200] ^= 0xFF;

                string copyA = Path.Combine(dir, "foto_original.jpg");
                string copyB = Path.Combine(dir, "foto_copia.jpg");
                string decoy = Path.Combine(dir, "foto_diferente.jpg");

                await File.WriteAllBytesAsync(copyA, sameBytes);
                await File.WriteAllBytesAsync(copyB, sameBytes);
                await File.WriteAllBytesAsync(decoy, diffMiddleBytes);

                var svc = new StorageCleanerService(customRules: Array.Empty<CleanupRuleDef>());
                var result = await svc.FindDuplicatesAsync(new[] { dir }, minSizeBytes: 512);

                Assert.Single(result.Groups);
                var group = result.Groups[0];
                Assert.Equal(2, group.Files.Count);
                Assert.Equal(2500, group.WasteBytes);

                // Even if caller marks ALL copies in the group, guard MUST leave 1 copy intact
                foreach (var f in group.Files)
                    f.IsSelected = true;

                var (deletedCount, bytesFreed, skipped) = StorageCleanerService.DeleteDuplicatesSafely(
                    result.Groups,
                    sendToRecycleBin: false);

                Assert.Equal(1, deletedCount);
                Assert.Equal(2500, bytesFreed);
                Assert.Single(skipped);
                Assert.Equal(1, new[] { copyA, copyB }.Count(File.Exists));
                Assert.True(File.Exists(decoy));
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task StorageCleanerService_ScansDiskMap_AggregatesFoldersCategoriesAndTopFiles()
        {
            string root = Path.Combine(Path.GetTempPath(), $"hh_map_{Guid.NewGuid():N}");
            string mediaDir = Path.Combine(root, "Videos");
            string docsDir = Path.Combine(root, "Docs");
            Directory.CreateDirectory(mediaDir);
            Directory.CreateDirectory(docsDir);

            try
            {
                await File.WriteAllBytesAsync(Path.Combine(mediaDir, "clip.mp4"), new byte[4096]);
                await File.WriteAllBytesAsync(Path.Combine(docsDir, "manual.pdf"), new byte[1024]);

                var svc = new StorageCleanerService(customRules: Array.Empty<CleanupRuleDef>());
                var map = await svc.ScanDiskMapAsync(root);

                Assert.Equal(5120, map.Root.TotalBytes);
                Assert.Equal(2, map.FileCount);
                Assert.Equal(2, map.Root.Children.Count);
                Assert.Equal(4096, map.Root.BytesByCategory[(int)DiskFileCategory.Videos]);
                Assert.Equal(1024, map.Root.BytesByCategory[(int)DiskFileCategory.Documents]);
                Assert.Equal("clip.mp4", map.TopFiles[0].FileName);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task NetworkOptimizerService_BenchmarksDns_AppliesPlan_AndRevertsSavedState()
        {
            string tempState = Path.Combine(Path.GetTempPath(), $"hh_net_{Guid.NewGuid():N}.json");
            var registry = new InMemoryRegistryBackend();
            const string DeliveryKey = @"HKU\S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings";
            registry.Write(DeliveryKey, "DownloadMode", RegistryValueKind.DWord, 3);

            var executedScripts = new List<string>();
            var svc = new NetworkOptimizerService(
                statePath: tempState,
                registry: registry,
                dnsProbeHook: (ip, domain, _) =>
                {
                    double? ms = ip.ToString() switch
                    {
                        "1.1.1.1" => 8.5,
                        "8.8.8.8" => 24.0,
                        _ => null
                    };
                    return Task.FromResult(ms);
                },
                powerShellHook: (script, _) =>
                {
                    executedScripts.Add(script);
                    if (script.Contains("Get-NetTCPSetting", StringComparison.Ordinal))
                        return Task.FromResult((0, "AutoTuning=disabled"));
                    if (script.Contains("Get-NetAdapterAdvancedProperty", StringComparison.Ordinal))
                        return Task.FromResult((0, "NicPower=Enabled"));
                    return Task.FromResult((0, "OK"));
                });

            try
            {
                var packet = NetworkOptimizerService.BuildDnsQueryPacket(0x1234, "example.com");
                Assert.True(packet.Length > 18);
                Assert.Equal(0x12, packet[0]);
                Assert.Equal(0x34, packet[1]);

                var cfResult = await svc.MeasureDnsAsync(NetworkOptimizerService.PublicDnsPresets[0]);
                Assert.True(cfResult.IsReachable);
                Assert.Equal(8.5, cfResult.MedianMs);

                var unreachable = await svc.MeasureDnsAsync(NetworkOptimizerService.PublicDnsPresets[2]);
                Assert.False(unreachable.IsReachable);

                var realNic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n =>
                        n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                        n.GetIPProperties().GetIPv4Properties() != null);
                string adapterId = realNic?.Id ?? "{TEST-NIC}";

                var plan = new NetworkAnalysisPlan
                {
                    AdapterId = adapterId,
                    AdapterName = "Ethernet Teste",
                    AdapterDescription = "Virtual NIC",
                    InterfaceIndex = realNic?.GetIPProperties().GetIPv4Properties()?.Index ?? 12,
                    IsWiFi = false,
                    HasIPv6 = false,
                    RecommendedDns = NetworkOptimizerService.PublicDnsPresets[0],
                    Adjustments = new List<NetworkTweakItem>
                    {
                        new() { Id = "dns", Title = "Aplicar DNS rápido", IsNeeded = true, IsSelected = true },
                        new() { Id = "autotuning", Title = "TCP Auto-Tuning", IsNeeded = true, IsSelected = true },
                        new() { Id = "entrega", Title = "Desativar P2P do Windows Update", IsNeeded = true, IsSelected = true }
                    },
                    Snapshot = new NetworkSavedState
                    {
                        AdapterId = adapterId,
                        AdapterName = "Ethernet Teste",
                        PreviousDnsV4 = null,
                        PreviousAutoTuning = "disabled",
                        PreviousDeliveryRead = true,
                        PreviousDeliveryMode = 3
                    }
                };

                var (saved, applyErrors) = await svc.ApplyPlanAsync(plan);
                Assert.Empty(applyErrors);
                Assert.Equal(3, saved.AppliedIds.Count);
                Assert.Equal(1, Convert.ToInt32(registry.Read(DeliveryKey, "DownloadMode").Data));
                Assert.NotNull(svc.LoadSavedState());

                var revertErrors = await svc.RevertSavedOptimizationAsync();
                Assert.Empty(revertErrors);
                Assert.Null(svc.LoadSavedState());
                Assert.Equal(3, Convert.ToInt32(registry.Read(DeliveryKey, "DownloadMode").Data));
                Assert.Contains(executedScripts, s => s.Contains("ResetServerAddresses", StringComparison.Ordinal));
                Assert.Contains(executedScripts, s => s.Contains("autotuninglevel=disabled", StringComparison.Ordinal));
            }
            finally
            {
                if (File.Exists(tempState))
                    File.Delete(tempState);
            }
        }

        [Fact]
        public void BatteryDiagnosticsService_ParsesMultiBatteryXmlAndEmptyDesktopXml()
        {
            var emptyDoc = XDocument.Parse("<BatteryReport><Batteries /></BatteryReport>");
            var emptyReport = BatteryDiagnosticsService.ParseBatteryReportXml(emptyDoc);
            Assert.False(emptyReport.HasBattery);

            var laptopDoc = XDocument.Parse("""
                <BatteryReport>
                  <ReportInformation>
                    <LocalScanTime>2026-11-11T10:30:00</LocalScanTime>
                  </ReportInformation>
                  <Batteries>
                    <Battery>
                      <Id>BAT0</Id>
                      <Manufacturer>ASUS</Manufacturer>
                      <Chemistry>LIon</Chemistry>
                      <DesignCapacity>50000</DesignCapacity>
                      <FullChargeCapacity>41000</FullChargeCapacity>
                      <CycleCount>142</CycleCount>
                    </Battery>
                  </Batteries>
                  <RuntimeEstimates>
                    <FullChargeCapacity>
                      <ActiveRuntime>PT4H30M0S</ActiveRuntime>
                    </FullChargeCapacity>
                    <DesignCapacity>
                      <ActiveRuntime>PT5H30M0S</ActiveRuntime>
                    </DesignCapacity>
                  </RuntimeEstimates>
                  <History>
                    <HistoryEntry LocalStartDate="2026-10-01" DesignCapacity="50000" FullChargeCapacity="43000" />
                    <HistoryEntry LocalStartDate="2026-11-01" DesignCapacity="50000" FullChargeCapacity="41000" />
                  </History>
                </BatteryReport>
                """);

            var report = BatteryDiagnosticsService.ParseBatteryReportXml(laptopDoc);
            Assert.True(report.HasBattery);
            Assert.Equal(1, report.BatteryCount);
            Assert.Equal(50000, report.DesignCapacityMwh);
            Assert.Equal(41000, report.FullChargeCapacityMwh);
            Assert.Equal(82.0, report.HealthPercent);
            Assert.Equal(18.0, report.WearPercent);
            Assert.Equal(142, report.CycleCount);
            Assert.Equal(TimeSpan.FromMinutes(270), report.EstimatedRuntimeCurrent);
            Assert.Equal(2, report.History.Count);
        }

        [Fact]
        public void DiagnosticsViewModel_TracksKeyboardMouseAndDisplayColorCycle()
        {
            var vm = new DiagnosticsViewModel();

            Assert.Equal(0, vm.TestedKeysCount);
            Assert.True(vm.TotalKeysCount > 50);

            bool matched = vm.HandleKeyEvent("Escape", isDown: true);
            Assert.True(matched);
            Assert.Equal(1, vm.TestedKeysCount);
            vm.HandleKeyEvent("Escape", isDown: false);

            vm.RecordMouseButton("Left");
            vm.RecordMouseButton("XButton1");
            vm.RecordMouseWheel(120);
            vm.RecordMouseWheel(-120);
            Assert.Equal(1, vm.LeftClicks);
            Assert.Equal(1, vm.X1Clicks);
            Assert.Equal(1, vm.WheelUpTicks);
            Assert.Equal(1, vm.WheelDownTicks);

            Assert.Equal("Preto", vm.CurrentPixelColorName);
            vm.NextPixelColor();
            Assert.Equal("Branco", vm.CurrentPixelColorName);
            vm.PrevPixelColor();
            Assert.Equal("Preto", vm.CurrentPixelColorName);
        }

        [Fact]
        public void MainViewModel_NavigatesToAllPhase3Sections_AndStorageNetworkViewModelsWork()
        {
            var cleanupSvc = new StorageCleanerService(customRules: new[]
            {
                new CleanupRuleDef { Id = "r1", Categoria = "Sistema", Titulo = "Regra 1", Nivel = "seguro", Recomendado = true },
                new CleanupRuleDef { Id = "r2", Categoria = "Avançado", Titulo = "Regra 2", Nivel = "avancado", Recomendado = false }
            });
            var storageVm = new StorageToolsViewModel(cleanupSvc);
            Assert.Equal(2, storageVm.FilteredCleanupItems.Count);
            storageVm.ToggleSelectAllCleanup();
            Assert.All(storageVm.FilteredCleanupItems, i => Assert.True(i.IsSelected));
            storageVm.SelectRecommendedCleanup();
            Assert.True(storageVm.AllCleanupItems[0].IsSelected);
            Assert.False(storageVm.AllCleanupItems[1].IsSelected);

            // Duplicates keep oldest / keep newest behavior
            var oldEntry = new DuplicateFileEntry(@"C:\temp\a_old.txt", 2048, DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(-10));
            var newEntry = new DuplicateFileEntry(@"C:\temp\b_new.txt", 2048, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-1));
            var group = new DuplicateGroupViewModel { FileSizeBytes = 2048, HashHex = "ABCD" };
            group.Files.Add(new DuplicateFileItemViewModel(oldEntry));
            group.Files.Add(new DuplicateFileItemViewModel(newEntry));
            storageVm.DuplicateGroups.Add(group);

            storageVm.MarkDuplicatesExcept(keepNewest: false);
            Assert.False(group.Files[0].IsSelected); // old kept unchecked
            Assert.True(group.Files[1].IsSelected);  // new marked for deletion

            storageVm.MarkDuplicatesExcept(keepNewest: true);
            Assert.True(group.Files[0].IsSelected);  // old marked for deletion
            Assert.False(group.Files[1].IsSelected); // newest kept unchecked

            WpfTestHost.RunOnWpfThread(() =>
            {
                using var main = new MainViewModel();
                main.CurrentSection = "StorageTools";
                Assert.True(main.IsStorageToolsActive);
                Assert.False(main.IsNetworkOptimizerActive);

                main.CurrentSection = "NetworkOptimizer";
                Assert.True(main.IsNetworkOptimizerActive);
                Assert.False(main.IsDiagnosticsActive);

                main.CurrentSection = "Diagnostics";
                Assert.True(main.IsDiagnosticsActive);
            });

            WpfTestHost.RunOnWpfThread(() =>
            {
                var window = new MainWindow
                {
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                window.Show();
                WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);
                try
                {
                    var navScroller = window.FindName("SidebarNavScrollViewer") as ScrollViewer;
                    Assert.NotNull(navScroller);
                    Assert.Equal(ScrollBarVisibility.Auto, navScroller!.VerticalScrollBarVisibility);

                    var diagNavBtn = WpfTestHost.FindVisualChildren<Button>(navScroller)
                        .First(b => Equals(b.CommandParameter, "Diagnostics"));
                    diagNavBtn.BringIntoView();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    ((IInvokeProvider)new ButtonAutomationPeer(diagNavBtn).GetPattern(PatternInterface.Invoke)).Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    var vm = (MainViewModel)window.DataContext;
                    Assert.True(vm.IsDiagnosticsActive);
                    Assert.True(vm.IsBatteryNavActive);
                    Assert.False(vm.IsPeripheralsNavActive);

                    var periphNavBtn = WpfTestHost.FindVisualChildren<Button>(navScroller)
                        .First(b => Equals(b.CommandParameter, "Diagnostics:Peripherals"));
                    periphNavBtn.BringIntoView();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    ((IInvokeProvider)new ButtonAutomationPeer(periphNavBtn).GetPattern(PatternInterface.Invoke)).Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    Assert.True(vm.IsDiagnosticsActive);
                    Assert.False(vm.IsBatteryNavActive);
                    Assert.True(vm.IsPeripheralsNavActive);
                    Assert.True(vm.Diagnostics.IsPeripheralsTabActive);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void Phase3Views_RenderCleanlyInMainWindow_SubTabsAndButtonsWorkWithoutOverlap()
        {
            WpfTestHost.RunOnWpfThread(() =>
            {
                using var mainVm = new MainViewModel();
                var storageView = new StorageToolsView { DataContext = mainVm.StorageTools };
                var networkView = new NetworkOptimizerView { DataContext = mainVm.NetworkOptimizer };
                var diagView = new DiagnosticsView { DataContext = mainVm.Diagnostics };

                // 1. StorageToolsView sub-tabs + non-overlapping header check
                var w1 = WpfTestHost.ShowHostedControl(storageView);
                try
                {
                    Assert.Equal("Cleaner", mainVm.StorageTools.ActiveSubTab);
                    var dupBtn = WpfTestHost.FindVisualChildren<Button>(storageView)
                        .First(b => b.Content?.ToString()?.Contains("Duplicados", StringComparison.Ordinal) == true);
                    ((IInvokeProvider)new ButtonAutomationPeer(dupBtn).GetPattern(PatternInterface.Invoke)).Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);
                    Assert.Equal("Duplicates", mainVm.StorageTools.ActiveSubTab);

                    var mapBtn = WpfTestHost.FindVisualChildren<Button>(storageView)
                        .First(b => b.Content?.ToString()?.Contains("Mapa de Disco", StringComparison.Ordinal) == true);
                    ((IInvokeProvider)new ButtonAutomationPeer(mapBtn).GetPattern(PatternInterface.Invoke)).Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);
                    Assert.Equal("DiskMap", mainVm.StorageTools.ActiveSubTab);

                    AssertHeaderButtonsDoNotOverlapSubtitle(storageView, "Analisar Limpeza");
                }
                finally
                {
                    w1.Close();
                }

                // 2. NetworkOptimizerView
                var w2 = WpfTestHost.ShowHostedControl(networkView);
                try
                {
                    AssertHeaderButtonsDoNotOverlapSubtitle(networkView, "Analisar Rede");
                }
                finally
                {
                    w2.Close();
                }

                // 3. DiagnosticsView sub-tabs + Display color cycler button click
                var w3 = WpfTestHost.ShowHostedControl(diagView);
                try
                {
                    AssertHeaderButtonsDoNotOverlapSubtitle(diagView, "Atualizar Bateria");

                    var dispTabBtn = WpfTestHost.FindVisualChildren<Button>(diagView)
                        .First(b => b.Content?.ToString()?.Contains("Dead Pixel", StringComparison.Ordinal) == true);
                    ((IInvokeProvider)new ButtonAutomationPeer(dispTabBtn).GetPattern(PatternInterface.Invoke)).Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);
                    Assert.Equal("Display", mainVm.Diagnostics.ActiveSubTab);

                    var nextBtn = WpfTestHost.FindVisualChildren<Button>(diagView)
                        .First(b => b.Content?.ToString()?.Contains("Próxima", StringComparison.Ordinal) == true);
                    ((IInvokeProvider)new ButtonAutomationPeer(nextBtn).GetPattern(PatternInterface.Invoke)).Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);
                    Assert.Equal("Branco", mainVm.Diagnostics.CurrentPixelColorName);
                }
                finally
                {
                    w3.Close();
                }
            });
        }

        private static void AssertHeaderButtonsDoNotOverlapSubtitle(FrameworkElement root, string buttonTextContains)
        {
            var subtitle = WpfTestHost.FindVisualChildren<TextBlock>(root)
                .First(tb => tb.FontSize < 14 && tb.Text.Length > 20);
            var btn = WpfTestHost.FindVisualChildren<Button>(root)
                .First(b => b.Content?.ToString()?.Contains(buttonTextContains, StringComparison.Ordinal) == true && b.IsVisible);

            var subRight = subtitle.TranslatePoint(new Point(subtitle.ActualWidth, 0), root).X;
            var btnLeft = btn.TranslatePoint(new Point(0, 0), root).X;
            Assert.True(subRight <= btnLeft + 0.5, $"Subtítulo encostando/sobrepondo botão '{btn.Content}': subRight={subRight}, btnLeft={btnLeft}");
        }

        [Fact]
        public void CpuMetrics_ClockAndTooltip_CalculatesAverageAndPeakProperly()
        {
            var cpu = new CpuMetrics
            {
                ClockSpeedGhz = 2.54,
                PeakClockSpeedGhz = 4.32
            };

            Assert.Equal("2,54 GHz", cpu.FormattedClock);
            Assert.Contains("2,54 GHz", cpu.ClockTooltip);
            Assert.Contains("4,32 GHz", cpu.ClockTooltip);
            Assert.Contains("Pico no núcleo mais rápido", cpu.ClockTooltip);

            // Quando pico e média forem iguais ou muito próximos (ex: carga 100% all-core)
            var cpuUniform = new CpuMetrics
            {
                ClockSpeedGhz = 3.40,
                PeakClockSpeedGhz = 3.40
            };
            Assert.Equal("Frequência atual: 3,40 GHz", cpuUniform.ClockTooltip);
        }
    }
}

