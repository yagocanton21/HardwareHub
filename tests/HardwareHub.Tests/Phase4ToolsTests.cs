using System.IO;
using System.Text.Json;
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
    public sealed class Phase4ToolsTests
    {
        // ===================== HELPER: IN-MEMORY BLOCK DEVICE =====================

        private sealed class FakeCapacityBlockDevice : ICapacityBlockDevice
        {
            private readonly byte[] _backing;
            private readonly int _sectorBytes;
            private readonly long _realBytes;
            private readonly bool _simulateWrapAround;
            private readonly int? _corruptPointByteOffset;

            public FakeCapacityBlockDevice(
                long announcedBytes,
                long realBytes,
                int sectorBytes = 512,
                bool simulateWrapAround = false,
                int? corruptPointByteOffset = null)
            {
                LengthBytes = announcedBytes;
                _realBytes = realBytes;
                _sectorBytes = sectorBytes;
                _simulateWrapAround = simulateWrapAround;
                _corruptPointByteOffset = corruptPointByteOffset;

                _backing = new byte[realBytes];
                for (int i = 0; i < _backing.Length; i++)
                    _backing[i] = (byte)((i * 31 + 17) & 0xFF);
            }

            public long LengthBytes { get; }
            public int SectorBytes => _sectorBytes;

            public byte[] SnapshotBackingBytes() => (byte[])_backing.Clone();

            public void Read(long offset, byte[] buffer, int count)
            {
                long physOffset = TranslateOffset(offset);
                for (int i = 0; i < count; i++)
                {
                    long p = physOffset + i;
                    buffer[i] = (p >= 0 && p < _backing.Length) ? _backing[p] : (byte)0xFF;
                }

                if (_corruptPointByteOffset.HasValue &&
                    offset <= _corruptPointByteOffset.Value &&
                    offset + count > _corruptPointByteOffset.Value &&
                    count > 0)
                {
                    buffer[0] ^= 0xAA;
                }
            }

            public void Write(long offset, byte[] buffer, int count)
            {
                long physOffset = TranslateOffset(offset);
                for (int i = 0; i < count; i++)
                {
                    long p = physOffset + i;
                    if (p >= 0 && p < _backing.Length)
                        _backing[p] = buffer[i];
                }
            }

            public void Flush() { }
            public void Dispose() { }

            private long TranslateOffset(long offset)
            {
                if (_simulateWrapAround && _realBytes > 0)
                    return offset % _realBytes;
                return offset;
            }
        }

        private sealed class FakeBlockSource : ICapacityBlockSource
        {
            private readonly ICapacityBlockDevice _device;
            public FakeBlockSource(ICapacityBlockDevice device) => _device = device;
            public ICapacityBlockDevice Open(RemovableDriveCandidate drive) => _device;
        }

        // ===================== 1. DRIVERS & WINDOWS UPDATE =====================

        [Fact]
        public async Task DriversUpdateService_HelpersAndMockedBackupUpdatesWork()
        {
            Assert.Equal("Vídeo", DriversUpdateService.MapDeviceClassName("Display"));
            Assert.Equal("USB", DriversUpdateService.MapDeviceClassName("USB"));
            Assert.Equal("560.94", DriversUpdateService.FormatNvidiaFriendlyVersion("32.0.15.6094"));

            var err43 = DriversUpdateService.ExplainDeviceErrorCode(43);
            Assert.True(err43.IsSerious);
            Assert.Contains("interrompeu", err43.Title, StringComparison.OrdinalIgnoreCase);

            var tempDir = Path.Combine(Path.GetTempPath(), $"HubDrvTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                string capturedExe = "";
                string capturedArgs = "";

                var svc = new DriversUpdateService(
                    processHook: (exe, args, _) =>
                    {
                        capturedExe = exe;
                        capturedArgs = args;
                        if (args.Contains("/export-driver *", StringComparison.OrdinalIgnoreCase))
                        {
                            // extrai pasta destino e cria um arquivo .inf simulado
                            var marker = "/export-driver * ";
                            var idx = args.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                            var folder = args[(idx + marker.Length)..].Trim().Trim('"');
                            Directory.CreateDirectory(folder);
                            File.WriteAllText(Path.Combine(folder, "oem1.inf"), "; dummy inf");
                            return Task.FromResult((0, "Exportado com sucesso"));
                        }

                        if (args.Contains("/add-driver", StringComparison.OrdinalIgnoreCase))
                        {
                            return Task.FromResult((0, "Total de pacotes de driver: 1\nPacotes de driver adicionados: 1"));
                        }

                        return Task.FromResult((1, "Comando inesperado"));
                    },
                    searchUpdatesHook: _ => Task.FromResult(new List<WindowsUpdateItemModel>
                    {
                        new() { Title = "Atualização Cumulativa KB5000001", KbId = "KB5000001", SizeBytes = 120 * 1024 * 1024 },
                        new() { Title = "Intel - Net - 12.19", KbId = "KB5000002", IsDriver = true, IsOptional = true, SizeBytes = 8 * 1024 * 1024 }
                    }),
                    installUpdatesHook: (selected, onStage, _) =>
                    {
                        onStage?.Invoke("Instalando pacote...");
                        return Task.FromResult(new WindowsUpdateRunResult(true, $"{selected.Count} instalada(s).", RebootRequired: true));
                    });

                var backupRes = await svc.BackupDriversAsync(tempDir);
                Assert.True(backupRes.Success);
                Assert.Equal("pnputil.exe", Path.GetFileName(capturedExe));
                Assert.Contains("/export-driver *", capturedArgs);
                Assert.True(File.Exists(Path.Combine(backupRes.ExportFolder, "oem1.inf")));

                var importRes = await svc.ImportDriversAsync(backupRes.ExportFolder, isAdminHook: () => true);
                Assert.True(importRes.Success);
                Assert.Equal("pnputil.exe", Path.GetFileName(capturedExe));
                Assert.Contains("/add-driver", capturedArgs);
                Assert.Equal(1, importRes.NewlyInstalled);

                var gpuWithoutFriendly = new DriverGpuInfo { Name = "Intel(R) HD Graphics", DriverVersion = "8.15.10.2900" };
                Assert.Equal("8.15.10.2900", gpuWithoutFriendly.DisplayDriverVersion);
                var gpuWithFriendly = new DriverGpuInfo { Name = "NVIDIA RTX", FriendlyVersion = "566.36", DriverVersion = "32.0.15.6636" };
                Assert.Equal("566.36 (32.0.15.6636)", gpuWithFriendly.DisplayDriverVersion);

                var vm = new DriversUpdateViewModel(svc);
                await vm.SearchUpdatesAsync();

                Assert.Equal(2, vm.WindowsUpdates.Count);
                Assert.True(vm.AreAllUpdatesSelected);
                Assert.Equal("Desmarcar Todos", vm.SelectAllUpdatesLabel);

                vm.ToggleSelectAllUpdatesCommand.Execute(null);
                Assert.Equal(0, vm.SelectedUpdatesCount);
                Assert.Equal("Selecionar Todos", vm.SelectAllUpdatesLabel);
                Assert.False(vm.InstallSelectedUpdatesCommand.CanExecute(null));

                vm.ToggleSelectAllUpdatesCommand.Execute(null);
                Assert.Equal(2, vm.SelectedUpdatesCount);
                Assert.True(vm.InstallSelectedUpdatesCommand.CanExecute(null));

                vm.WindowsUpdates[1].IsSelected = false;
                Assert.Equal(1, vm.SelectedUpdatesCount);

                await vm.InstallSelectedUpdatesAsync();
                Assert.True(vm.IsRebootPending);
                Assert.Single(vm.WindowsUpdates);
                Assert.Equal("KB5000002", vm.WindowsUpdates[0].KbId);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        // ===================== 2. BACKUP ANTES DE FORMATAR =====================

        [Fact]
        public async Task FormatBackupService_CreateLoadSecurityGuardsAndRestoreWork()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"HubFormatBackup_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);

            try
            {
                var svc = new FormatBackupService(
                    processHook: (exe, args, _) =>
                    {
                        var exeName = Path.GetFileName(exe);
                        if (exeName.Equals("pnputil.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("/export-driver", StringComparison.OrdinalIgnoreCase))
                        {
                            var marker = "/export-driver * ";
                            var dir = args[(args.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length)..].Trim().Trim('"');
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(Path.Combine(dir, "gpu.inf"), "; inf");
                            return Task.FromResult((0, "OK"));
                        }

                        if (exeName.Equals("netsh.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("export profile", StringComparison.OrdinalIgnoreCase))
                        {
                            var marker = "folder=";
                            var start = args.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length;
                            var dir = args[start..].Trim().Trim('"');
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(Path.Combine(dir, "Wi-Fi-Casa.xml"),
                                "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>MinhaRede5G</name></WLANProfile>");
                            // XML malicioso com DOCTYPE deve ser bloqueado com segurança pelo leitor XXE-safe
                            File.WriteAllText(Path.Combine(dir, "Evil.xml"),
                                "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]><WLANProfile><name>&xxe;</name></WLANProfile>");
                            return Task.FromResult((0, "OK"));
                        }

                        if (exeName.Equals("pnputil.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("/add-driver", StringComparison.OrdinalIgnoreCase))
                        {
                            return Task.FromResult((0,
                                "Total de pacotes de driver: 2\n" +
                                "Pacotes de driver adicionados: 1\n" +
                                "Já existe no sistema.\n" +
                                "É necessário reiniciar"));
                        }

                        if (exeName.Equals("netsh.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("add profile", StringComparison.OrdinalIgnoreCase))
                        {
                            return Task.FromResult((0, "O perfil MinhaRede5G foi adicionado na interface Wi-Fi."));
                        }

                        return Task.FromResult((1, "Comando inesperado"));
                    },
                    isAdminHook: () => true,
                    hasWifiHook: () => true);

                var created = await svc.CreateBackupAsync(tempRoot, includeDrivers: true, includeWifi: true);
                Assert.True(created.Success);
                Assert.True(Directory.Exists(created.BackupFolder));
                Assert.True(File.Exists(Path.Combine(created.BackupFolder, "hub-backup.json")));

                // Somente o XML seguro deve ser carregado; Evil.xml (com DTD) é ignorado sem lançar exceção
                var loaded = svc.TryLoadBackup(created.BackupFolder, out var loadErr);
                Assert.NotNull(loaded);
                Assert.Equal("", loadErr);
                Assert.Equal(1, loaded!.InfCount);
                Assert.Single(loaded.WifiProfiles);
                Assert.Equal("MinhaRede5G", loaded.WifiProfiles[0].Ssid);

                // Diferente placa-mãe no manifest -> IsDifferentHardware == true
                var manifestPath = Path.Combine(created.BackupFolder, "hub-backup.json");
                var diffManifest = JsonSerializer.Deserialize<FormatBackupManifest>(File.ReadAllText(manifestPath))!;
                diffManifest.Motherboard = "FAKE-OTHER-BOARD-Z999";
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(diffManifest));

                var loadedDiff = svc.TryLoadBackup(created.BackupFolder, out _);
                Assert.NotNull(loadedDiff);
                Assert.True(loadedDiff!.IsDifferentHardware);

                // Path traversal no campo DriversFolder deve ser rejeitado (DriversPath vazio)
                diffManifest.DriversFolder = "..\\..\\Windows\\System32";
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(diffManifest));
                var loadedTraversal = svc.TryLoadBackup(created.BackupFolder, out _);
                Assert.NotNull(loadedTraversal);
                Assert.Equal("", loadedTraversal!.DriversPath);

                // Restore drivers + wifi via ViewModel
                var vm = new FormatBackupViewModel(svc);
                Assert.True(vm.LoadBackupFromFolder(created.BackupFolder));
                // Restaura drivers diretamente apontando para a pasta Drivers real do pacote
                var drvRes = await svc.RestoreDriversAsync(Path.Combine(created.BackupFolder, "Drivers"));
                Assert.True(drvRes.Success);
                Assert.True(drvRes.RebootRequired);
                Assert.Equal(1, drvRes.NewlyInstalled);

                await vm.RestoreWifiAsync();
                Assert.Contains("restaurada", vm.StatusBanner, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
        }

        // ===================== 3. DETECTOR DE CAPACIDADE FALSA =====================

        [Fact]
        public async Task FakeCapacityService_DetectsAuthenticWrapAroundCounterfeitAndSafelyRestoresData()
        {
            // Segurança: nunca permitir discos internos ou disco de sistema
            var protectedSet = new HashSet<int> { 0 };
            Assert.NotNull(FakeCapacityService.CheckDriveSafetyBlock(diskNumber: 0, busType: 7, protectedSet));
            Assert.NotNull(FakeCapacityService.CheckDriveSafetyBlock(diskNumber: 2, busType: 17, protectedSet)); // NVMe
            Assert.Null(FakeCapacityService.CheckDriveSafetyBlock(diskNumber: 2, busType: 7, protectedSet));     // USB

            const long announcedBytes = 8L * 1024 * 1024;
            const long realCounterfeitBytes = 2L * 1024 * 1024;

            var candidate = new RemovableDriveCandidate
            {
                DiskNumber = 2,
                Model = "Generic USB Flash",
                BusType = 7,
                BusLabel = "USB",
                AnnouncedBytes = announcedBytes,
                MountLetters = Array.Empty<string>()
            };

            // (a) Autêntico 8 MiB
            var authenticDevice = new FakeCapacityBlockDevice(announcedBytes, announcedBytes);
            var originalAuthBytes = authenticDevice.SnapshotBackingBytes();
            var authSvc = new FakeCapacityService(new FakeBlockSource(authenticDevice));
            var authReport = authSvc.ExecuteBlockCapacityTest(candidate, new FakeCapacityOptions { Points = 32, BlockSizeBytes = 4096 });

            Assert.Equal(FakeCapacityVerdict.Original, authReport.Verdict);
            Assert.False(authReport.WrapAroundDetected);
            Assert.True(authReport.OriginalDataRestored);
            Assert.Equal(originalAuthBytes, authenticDevice.SnapshotBackingBytes());

            // (b) Falsificado com wrap-around físico em 2 MiB (anuncia 8 MiB)
            var fakeDevice = new FakeCapacityBlockDevice(announcedBytes, realCounterfeitBytes, simulateWrapAround: true);
            var originalFakeBytes = fakeDevice.SnapshotBackingBytes();
            var fakeSvc = new FakeCapacityService(new FakeBlockSource(fakeDevice));
            var fakeReport = fakeSvc.ExecuteBlockCapacityTest(candidate, new FakeCapacityOptions { Points = 32, BlockSizeBytes = 4096 });

            Assert.Equal(FakeCapacityVerdict.Counterfeit, fakeReport.Verdict);
            Assert.True(fakeReport.WrapAroundDetected);
            Assert.True(fakeReport.OriginalDataRestored);
            Assert.Equal(originalFakeBytes, fakeDevice.SnapshotBackingBytes());

            // (c) Defeituoso / corrompido em leitura
            var defectDevice = new FakeCapacityBlockDevice(announcedBytes, announcedBytes, corruptPointByteOffset: 1024 * 1024);
            var defectSvc = new FakeCapacityService(new FakeBlockSource(defectDevice));
            var defectReport = defectSvc.ExecuteBlockCapacityTest(candidate, new FakeCapacityOptions { Points = 16, BlockSizeBytes = 4096 });
            Assert.NotEqual(FakeCapacityVerdict.Original, defectReport.Verdict);

            // (d) Teste em volume montado (arquivos temporários SHA-256) e limpeza automática garantida
            var probeDir = Path.Combine(Path.GetTempPath(), $"HubFakeCapProbe_{Guid.NewGuid():N}");
            Directory.CreateDirectory(probeDir);
            try
            {
                var mountedCandidate = new RemovableDriveCandidate
                {
                    DiskNumber = 2,
                    Model = "Kingston DataTraveler",
                    BusType = 7,
                    BusLabel = "USB",
                    AnnouncedBytes = 1024L * 1024 * 1024,
                    MountLetters = new[] { probeDir }
                };

                // Guarda padrão deve bloquear unidade do Windows
                var blockedReport = await authSvc.ExecuteMountedVolumeProbeAsync(mountedCandidate, probeDir, sampleFilesMb: 4);
                Assert.Equal(FakeCapacityVerdict.Inconclusive, blockedReport.Verdict);
                Assert.Contains("Bloqueado", blockedReport.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);

                // Com guarda liberado para pasta temporária isolada do teste, valida SHA-256 + limpeza completa
                var probeSvc = new FakeCapacityService(new FakeBlockSource(authenticDevice), unsafeDriveGuard: _ => false);
                var mountedReport = await probeSvc.ExecuteMountedVolumeProbeAsync(mountedCandidate, probeDir, sampleFilesMb: 4);
                Assert.Equal(FakeCapacityVerdict.Original, mountedReport.Verdict);
                Assert.True(mountedReport.RealBytes >= 4L * 1024 * 1024);
                Assert.True(mountedReport.OriginalDataRestored);
                Assert.Empty(Directory.GetFileSystemEntries(probeDir));
            }
            finally
            {
                if (Directory.Exists(probeDir))
                    Directory.Delete(probeDir, recursive: true);
            }
        }

        [Fact]
        public void FakeCapacityService_TranslatesPagingAndIoErrors_ToFriendlyDefectiveVerdict()
        {
            var pagingEx = new IOException("Erro na operação de paginação. : 'F:\\probe.bin'");
            var (verdictPaging, msgPaging) = FakeCapacityService.TranslateException(pagingEx);
            Assert.Equal(FakeCapacityVerdict.Defective, verdictPaging);
            Assert.Contains("Falha de hardware no pendrive", msgPaging, StringComparison.OrdinalIgnoreCase);

            var crcEx = new IOException("Erro de verificação CRC nos dados");
            var (verdictCrc, msgCrc) = FakeCapacityService.TranslateException(crcEx);
            Assert.Equal(FakeCapacityVerdict.Defective, verdictCrc);
            Assert.Contains("CRC", msgCrc, StringComparison.OrdinalIgnoreCase);

            var genericEx = new InvalidOperationException("Algum erro lógico qualquer");
            var (verdictGen, msgGen) = FakeCapacityService.TranslateException(genericEx);
            Assert.Equal(FakeCapacityVerdict.Inconclusive, verdictGen);
            Assert.Equal("Algum erro lógico qualquer", msgGen);
        }

        // ===================== 4. TESTES STA WPF UI & NAVEGAÇÃO PHASE 4 =====================

        [Fact]
        public void MainWindow_NavigatesToAllPhase4Sections_AndViewsRenderWithoutHeaderOverlap()
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
                        ("DriversUpdate", () => mainVm.IsDriversUpdateActive),
                        ("FormatBackup", () => mainVm.IsFormatBackupActive),
                        ("FakeCapacity", () => mainVm.IsFakeCapacityActive)
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

                // Renderização isolada de cada UserControl sem sobreposição de header
                var drvView = new DriversUpdateView { DataContext = mainVm.DriversUpdate };
                var wDrv = WpfTestHost.ShowHostedControl(drvView);
                try
                {
                    AssertHeaderActionDoesNotOverlapTitle(drvView, "Importar Drivers", "Drivers & Windows Update");
                    AssertHeaderActionDoesNotOverlapTitle(drvView, "Reanalisar Drivers", "Drivers & Windows Update");
                    var updBtn = WpfTestHost.FindVisualChildren<Button>(drvView)
                        .First(b => b.Content?.ToString()?.Contains("Windows Update", StringComparison.Ordinal) == true);
                    ((IInvokeProvider)new ButtonAutomationPeer(updBtn).GetPattern(PatternInterface.Invoke)).Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);
                    Assert.Equal("Updates", mainVm.DriversUpdate.ActiveSubTab);
                }
                finally
                {
                    wDrv.Close();
                }

                var fmtView = new FormatBackupView { DataContext = mainVm.FormatBackup };
                var wFmt = WpfTestHost.ShowHostedControl(fmtView);
                try
                {
                    AssertHeaderActionDoesNotOverlapTitle(fmtView, "Varrer Discos", "Backup Antes de Formatar");
                }
                finally
                {
                    wFmt.Close();
                }

                var fakeVm = new FakeCapacityViewModel(
                    listDrivesHook: () => new List<RemovableDriveCandidate>
                    {
                        new() { DiskNumber = 3, Model = "SanDisk Cruzer", BusType = 7, AnnouncedBytes = 32L * 1024 * 1024 * 1024, MountLetters = new[] { "E:\\" } }
                    });
                var capView = new FakeCapacityView { DataContext = fakeVm };
                var wCap = WpfTestHost.ShowHostedControl(capView);
                try
                {
                    AssertHeaderActionDoesNotOverlapTitle(capView, "Iniciar Verificação", "Detector de Capacidade Falsa");
                }
                finally
                {
                    wCap.Close();
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

