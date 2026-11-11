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
    public sealed class Phase5ToolsTests
    {
        // ===================== 1. COMPATIBILIDADE WINDOWS 11 =====================

        [Fact]
        public async Task Windows11Service_ClassifiesCpusAndEvaluatesEligibleVsBlockedSystems()
        {
            Assert.Equal(Windows11CheckStatus.Ok, Windows11Service.ClassifyCpu("Intel Core i7-13700K").Status);
            Assert.Equal(Windows11CheckStatus.Fail, Windows11Service.ClassifyCpu("Intel Core i7-7700K").Status);
            Assert.Equal(Windows11CheckStatus.Ok, Windows11Service.ClassifyCpu("AMD Ryzen 5 5600X").Status);
            Assert.Equal(Windows11CheckStatus.Fail, Windows11Service.ClassifyCpu("AMD Ryzen 5 1600").Status);
            Assert.Equal(Windows11CheckStatus.Ok, Windows11Service.ClassifyCpu("AMD Ryzen 7 2700X").Status);
            Assert.Equal(Windows11CheckStatus.Fail, Windows11Service.ClassifyCpu("AMD Ryzen 5 2400G").Status);

            var eligibleProbe = new Windows11Service.HardwareProbeSnapshot(
                WindowsEdition: "Windows 10 Pro 22H2 (build 19045)",
                CurrentBuild: 19045,
                Cpu: new Windows11Service.CpuSnapshot("AMD Ryzen 5 5600X", Cores: 6, MaxClockMhz: 3700, Architecture: 9),
                IsSse42Supported: true,
                IsPopcntSupported: true,
                RamGb: 16.0,
                SystemDriveTotalBytes: 512_000_000_000L,
                SystemDriveFreeBytes: 180_000_000_000L,
                SystemDriveLetter: "C:",
                IsUefi: true,
                SecureBootEnabledReg: 1,
                TpmTbsResult: 0u,
                TpmVersion: 2u,
                TpmWmiDetails: " • AMD • Ativado",
                Gpus: new List<Windows11Service.GpuSnapshot>
                {
                    new("NVIDIA GeForce RTX 4060", "32.0.15.6094", 1920, 1080)
                },
                HasDirectX12: true);

            var vmOk = new Windows11ViewModel(new Windows11Service(() => eligibleProbe));
            await vmOk.VerifyAsync();

            Assert.NotNull(vmOk.Report);
            Assert.True(vmOk.Report!.IsEligible);
            Assert.Equal(10, vmOk.Report.PassedCount);
            Assert.Equal(0, vmOk.Report.FailedCount);
            Assert.Equal(10, vmOk.Requirements.Count);

            var legacyProbe = eligibleProbe with
            {
                Cpu = new Windows11Service.CpuSnapshot("Intel Core i5-6500", Cores: 4, MaxClockMhz: 3200, Architecture: 9),
                IsUefi = false,
                TpmTbsResult = 0x8028400Fu,
                TpmVersion = 0u
            };

            var vmFail = new Windows11ViewModel(new Windows11Service(() => legacyProbe));
            await vmFail.VerifyAsync();

            Assert.NotNull(vmFail.Report);
            Assert.False(vmFail.Report!.IsEligible);
            Assert.True(vmFail.Report.FailedCount >= 4); // cpu, uefi, secureboot, tpm
        }

        // ===================== 2. PONTOS DE RESTAURAÇÃO =====================

        [Fact]
        public async Task RestorePointsService_SanitizesProtectsLatestPointAndDeletesOlderPoints()
        {
            Assert.Equal("Meu Ponto Seguro", RestorePointsService.SanitizeRestoreDescription("Meu 'Ponto'\nSeguro;$|"));
            Assert.Equal("Hardware HUB Ponto Manual", RestorePointsService.SanitizeRestoreDescription("   "));
            Assert.Equal(64, RestorePointsService.SanitizeRestoreDescription(new string('A', 90)).Length);

            var backing = new List<RestorePointModel>
            {
                new() { SequenceNumber = 10, Description = "Mais antigo", RestorePointType = 10, CreationTime = DateTime.Now.AddDays(-10) },
                new() { SequenceNumber = 12, Description = "Mais recente", RestorePointType = 12, CreationTime = DateTime.Now.AddHours(-2) }
            };

            uint deletedSeq = 0;
            string lastCreated = "";

            var svc = new RestorePointsService(
                readPointsHook: () => (backing.ToList(), null),
                readProtectionHook: () => new SystemProtectionStatus { IsEnabled = true, UsedBytes = 2_000_000_000, MaxBytes = 10_000_000_000 },
                createPointHook: (desc, _) =>
                {
                    lastCreated = desc;
                    backing.Add(new RestorePointModel { SequenceNumber = 15, Description = desc, RestorePointType = 12, CreationTime = DateTime.Now });
                    return Task.FromResult((true, "Criado com sucesso"));
                },
                removePointHook: seq =>
                {
                    deletedSeq = seq;
                    backing.RemoveAll(p => p.SequenceNumber == seq);
                    return 0u;
                });

            var vm = new RestorePointsViewModel(svc)
            {
                NewPointDescription = "Antes do Driver 'Novo'"
            };

            await vm.RefreshAsync();
            Assert.Equal(2, vm.Points.Count);
            Assert.True(vm.Points[0].IsLatest);
            Assert.Equal(12u, vm.Points[0].SequenceNumber);

            // Segurança: nunca permitir apagar o ponto mais recente
            Assert.False(vm.DeletePointCommand.CanExecute(vm.Points[0]));
            Assert.True(vm.DeletePointCommand.CanExecute(vm.Points[1]));
            var blockRes = svc.DeletePoint(vm.Points[0]);
            Assert.False(blockRes.Success);

            // Apagar ponto mais antigo funciona
            vm.DeletePointCommand.Execute(vm.Points[1]);
            Assert.Equal(10u, deletedSeq);
            Assert.Single(vm.Points);

            // Criar novo ponto atualiza a lista e marca o novo como mais recente
            await vm.CreatePointAsync();
            Assert.Equal("Antes do Driver Novo", lastCreated);
            Assert.Equal(2, vm.Points.Count);
            Assert.Equal(15u, vm.Points[0].SequenceNumber);
            Assert.True(vm.Points[0].IsLatest);
        }

        // ===================== 3. OTIMIZAÇÃO & TRIM DE DISCOS =====================

        [Fact]
        public async Task DiskOptimizationService_AuditsTrimMeasuresFragmentationAndOptimizesVolumes()
        {
            var commands = new List<(string Exe, string Args)>();

            var cfg = new DiskOptimizationConfig
            {
                TrimEnabled = false,
                WeeklyScheduleEnabled = false,
                ProtectionEnabled = true
            };

            var vols = new List<OptimizableVolumeModel>
            {
                new() { DriveLetter = "C:", Label = "Sistema", Model = "NVMe SSD", IsSsd = true, SizeBytes = 500_000_000_000L },
                new() { DriveLetter = "D:", Label = "Arquivos", Model = "Seagate HDD", IsSsd = false, SizeBytes = 2_000_000_000_000L }
            };

            var svc = new DiskOptimizationService(
                readConfigHook: () => cfg,
                listVolumesHook: () => vols,
                processHook: (exe, args, _) =>
                {
                    string joined = string.Join(" ", args);
                    commands.Add((System.IO.Path.GetFileName(exe), joined));

                    if (joined.Contains("DefragAnalysis", StringComparison.Ordinal))
                    {
                        string outStr = joined.Contains("'C:'", StringComparison.Ordinal) ? "4" : "18";
                        return Task.FromResult((0, outStr));
                    }

                    return Task.FromResult((0, "OK"));
                });

            Assert.Equal(-1, await svc.AnalyzeFragmentationAsync("BAD_PATH"));

            var vm = new DiskOptimizationViewModel(svc);
            await vm.RefreshAsync();

            Assert.Equal(2, vm.Volumes.Count);
            Assert.True(vm.HasMissingDefault);
            Assert.Equal("TRIM: Desativado", vm.TrimStatusLabel);

            await vm.AnalyzeAllAsync();
            Assert.Equal(4, vm.Volumes[0].FragmentationPercent);
            Assert.Equal(18, vm.Volumes[1].FragmentationPercent);
            Assert.Contains("recomendado desfragmentar", vm.Volumes[1].StatusMessage, StringComparison.OrdinalIgnoreCase);

            // SSD usa -ReTrim; HDD usa -Defrag
            await vm.OptimizeVolumeAsync(vm.Volumes[0]);
            Assert.Contains(commands, c => c.Args.Contains("Optimize-Volume -DriveLetter C -ReTrim", StringComparison.Ordinal));

            await vm.OptimizeVolumeAsync(vm.Volumes[1]);
            Assert.Contains(commands, c => c.Args.Contains("Optimize-Volume -DriveLetter D -Defrag", StringComparison.Ordinal));

            // Restaurar padrões habilita somente TRIM e ScheduledDefrag (Proteção já estava true)
            await vm.EnableDefaultsAsync();
            Assert.Contains(commands, c => c.Exe.Equals("fsutil.exe", StringComparison.OrdinalIgnoreCase) && c.Args.Contains("DisableDeleteNotify 0"));
            Assert.Contains(commands, c => c.Exe.Equals("schtasks.exe", StringComparison.OrdinalIgnoreCase) && c.Args.Contains("/ENABLE"));
        }

        // ===================== 4. TESTES STA WPF UI & NAVEGAÇÃO PHASE 5 =====================

        [Fact]
        public void MainWindow_NavigatesToAllPhase5Sections_AndViewsRenderWithoutHeaderOverlap()
        {
            WpfTestHost.RunOnWpfThread(() =>
            {
                using var mainVm = new MainViewModel();

                var window = new MainWindow
                {
                    DataContext = mainVm,
                    Width = 960,
                    Height = 680,
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

                    foreach (var (param, checkActive) in new (string, Func<bool>)[]
                    {
                        ("Windows11", () => mainVm.IsWindows11Active),
                        ("DiskOptimization", () => mainVm.IsDiskOptimizationActive),
                        ("RestorePoints", () => mainVm.IsRestorePointsActive)
                    })
                    {
                        var navBtn = WpfTestHost.FindVisualChildren<Button>(navScroller!)
                            .First(b => Equals(b.CommandParameter, param));
                        navBtn.BringIntoView();
                        WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                        ((IInvokeProvider)new ButtonAutomationPeer(navBtn).GetPattern(PatternInterface.Invoke)).Invoke();
                        WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                        Assert.True(checkActive(), $"A navegação para '{param}' deveria ativar a seção.");
                    }
                }
                finally
                {
                    window.Close();
                }

                // Layout check em cada view da Fase 5 sem sobreposição
                var win11View = new Windows11View { DataContext = mainVm.Windows11 };
                var w1 = WpfTestHost.ShowHostedControl(win11View);
                try
                {
                    AssertHeaderActionDoesNotOverlapTitle(win11View, "Verificar Agora", "Compatibilidade Windows 11");
                }
                finally
                {
                    w1.Close();
                }

                var rpView = new RestorePointsView { DataContext = mainVm.RestorePoints };
                var w2 = WpfTestHost.ShowHostedControl(rpView);
                try
                {
                    AssertHeaderActionDoesNotOverlapTitle(rpView, "Atualizar Lista", "Pontos de Restauração");
                }
                finally
                {
                    w2.Close();
                }

                var dskOptView = new DiskOptimizationView { DataContext = mainVm.DiskOptimization };
                var w3 = WpfTestHost.ShowHostedControl(dskOptView);
                try
                {
                    AssertHeaderActionDoesNotOverlapTitle(dskOptView, "Atualizar Discos", "Otimização & TRIM de Discos");
                }
                finally
                {
                    w3.Close();
                }
            });
        }

        private static void AssertHeaderActionDoesNotOverlapTitle(UserControl view, string buttonTextFragment, string titleFragment)
        {
            var titleBlock = WpfTestHost.FindVisualChildren<TextBlock>(view)
                .First(t => t.Text.Contains(titleFragment, StringComparison.Ordinal));
            var actionButton = WpfTestHost.FindVisualChildren<Button>(view)
                .First(b => b.Content?.ToString()?.Contains(buttonTextFragment, StringComparison.Ordinal) == true);

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

