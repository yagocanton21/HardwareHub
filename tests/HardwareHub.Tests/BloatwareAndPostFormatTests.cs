using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HardwareHub.Models;
using HardwareHub.Services;
using HardwareHub.ViewModels;
using Xunit;

namespace HardwareHub.Tests
{
    public sealed class BloatwareAndPostFormatTests : IDisposable
    {
        private readonly string _tempDir;

        public BloatwareAndPostFormatTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"HardwareHub_BloatTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void BloatwareWildcardAndScanParsing_WorksForUserAndProvisionedPackages()
        {
            string historyPath = Path.Combine(_tempDir, "history.json");
            var svc = new BloatwareService(historyPath: historyPath);

            const string fakeJson = """
                {
                  "u": [
                    { "Name": "king.com.CandyCrushSaga", "PackageFullName": "king.com.CandyCrushSaga_1.0_x64__abc", "PackageFamilyName": "king.com.CandyCrushSaga_abc" },
                    { "Name": "Microsoft.WindowsStore", "PackageFullName": "Microsoft.WindowsStore_224.0_x64__8wekyb3d8bbwe", "PackageFamilyName": "Microsoft.WindowsStore_8wekyb3d8bbwe" }
                  ],
                  "p": [
                    { "DisplayName": "Microsoft.BingWeather", "PackageName": "Microsoft.BingWeather_4.53_neutral_~_8wekyb3d8bbwe" }
                  ]
                }
                """;

            var withProtected = svc.ParseScanJson(fakeJson, includeProtected: true);
            Assert.Contains(withProtected, x => x.Id == "king" && x.IsInstalled && x.CanRemove);
            Assert.Contains(withProtected, x => x.Id == "clima" && !x.IsInstalled && x.IsProvisioned);
            Assert.Contains(withProtected, x => x.Id == "store" && x.IsProtected && !x.CanRemove);

            var withoutProtected = svc.ParseScanJson(fakeJson, includeProtected: false);
            Assert.DoesNotContain(withoutProtected, x => x.Id == "store");
        }

        [Fact]
        public async Task BloatwareService_BlocksRemovingProtectedPackages()
        {
            var svc = new BloatwareService(historyPath: Path.Combine(_tempDir, "history.json"));
            var storeDef = svc.Catalog.Single(c => c.Id == "store");
            var vm = new BloatwareItemViewModel(storeDef) { IsInstalled = true };

            var (ok, message) = await svc.RemoveAsync(vm, alsoNewUsers: true);
            Assert.False(ok);
            Assert.Contains("Protegido", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BloatwareHistoryAndRemovalScript_PersistAndEscapeCleanly()
        {
            string historyPath = Path.Combine(_tempDir, "history.json");
            var svc = new BloatwareService(historyPath: historyPath);

            string scriptUserOnly = BloatwareService.BuildRemovalScript(new[] { "king.com.*", "O'Reilly.App" }, alsoNewUsers: false);
            Assert.Contains("'O''Reilly.App'", scriptUserOnly);
            Assert.DoesNotContain("Remove-AppxProvisionedPackage", scriptUserOnly);

            string scriptAllUsers = BloatwareService.BuildRemovalScript(new[] { "king.com.*" }, alsoNewUsers: true);
            Assert.Contains("Remove-AppxProvisionedPackage", scriptAllUsers);
            Assert.Contains("Deprovisioned", scriptAllUsers);

            var rec = new RemovedBloatwareRecord
            {
                Data = DateTime.Now,
                Id = "king",
                Nome = "Jogos da King",
                Pacote = "king.com.CandyCrushSaga"
            };
            svc.RecordRemoval(rec);
            Assert.Single(svc.LoadHistory());
            Assert.Null(svc.LoadHistory()[0].Reinstalado);

            svc.MarkReinstalled(rec);
            Assert.NotNull(svc.LoadHistory()[0].Reinstalado);
        }

        [Fact]
        public void PostFormatService_SanitizesNamesAndReadsLiveActivationWithoutThrowing()
        {
            Assert.Equal("Hardware HUB", PostFormatService.SanitizeRestorePointName("!!!***"));
            Assert.Equal("Ponto Teste (v1)", PostFormatService.SanitizeRestorePointName("  Ponto; 'Teste'* (v1)!  "));
            Assert.Equal(60, PostFormatService.SanitizeRestorePointName(new string('A', 120)).Length);

            Assert.Contains("OEM", PostFormatService.DescribeActivationChannel("OEM:DM"));
            Assert.Contains("Varejo", PostFormatService.DescribeActivationChannel("Retail"));

            var postSvc = new PostFormatService();
            var activation = postSvc.GetWindowsActivationStatus();
            Assert.False(string.IsNullOrWhiteSpace(activation.Edition));
        }

        [Fact]
        public async Task OptimizationViewModel_FilterToggleProfileAndUndoAll_WorkEndToEnd()
        {
            var registry = new InMemoryRegistryBackend();
            var engine = new TweakEngine(
                registry,
                new ServiceStartupHelper(registry),
                Path.Combine(_tempDir, "vm-backup.json"),
                currentBuild: 22631);
            var logs = new List<string>();

            var vm = new OptimizationViewModel(
                tweakEngine: engine,
                bloatwareService: new BloatwareService(historyPath: Path.Combine(_tempDir, "vm-bloat.json")),
                logCallback: msg => logs.Add(msg));

            Assert.True(vm.AllTweaks.Count >= 55);
            Assert.Equal(vm.AllTweaks.Count, vm.FilteredTweaks.Count);

            vm.TweakSearch = "Visão de Tarefas";
            Assert.Single(vm.FilteredTweaks);
            var target = vm.FilteredTweaks[0];
            Assert.False(target.IsActive);

            await vm.ToggleTweakAsync(target);
            Assert.True(target.IsActive);
            Assert.Equal(1, vm.UndoableCount);

            vm.ShowOnlyActiveTweaks = true;
            Assert.Single(vm.FilteredTweaks);

            await vm.RevertAllTweaksAsync();
            Assert.False(target.IsActive);
            Assert.Equal(0, vm.UndoableCount);
            Assert.Empty(vm.FilteredTweaks);
        }

        [Fact]
        public void WpfViewsAndMainWindow_InstantiateAndNavigateOnStaThread()
        {
            WpfTestHost.RunOnWpfThread(() =>
            {
                var main = new HardwareHub.MainWindow();
                var mainVm = Assert.IsType<MainViewModel>(main.DataContext);

                mainVm.NavigateCommand.Execute("Optimization");
                Assert.True(mainVm.IsOptimizationActive);
                Assert.False(mainVm.IsPostFormatActive);

                mainVm.Optimization.SwitchSubTabCommand.Execute("Tweaks");
                Assert.True(mainVm.Optimization.IsTweaksTab);

                mainVm.Optimization.SwitchSubTabCommand.Execute("Desfazer");
                Assert.True(mainVm.Optimization.IsUndoTab);

                mainVm.NavigateCommand.Execute("PostFormat");
                Assert.True(mainVm.IsPostFormatActive);
                Assert.False(mainVm.IsOptimizationActive);

                mainVm.Dispose();
                main.Close();
            });
        }

        [Fact]
        public void BuildBatchRemovalScript_SkipsProtectedPackagesAndEmitsPerAppMarkers()
        {
            var svc = new BloatwareService(historyPath: Path.Combine(_tempDir, "batch-history.json"));
            var protectedStore = svc.Catalog.First(b => b.Id == "store");
            var candy = svc.Catalog.First(b => b.Id == "king");

            var list = new List<BloatwareItemViewModel>
            {
                new(protectedStore) { IsInstalled = true, IsProvisioned = true },
                new(candy) { IsInstalled = true, IsProvisioned = false }
            };

            string script = BloatwareService.BuildBatchRemovalScript(list, alsoNewUsers: false);
            Assert.Contains("APP=king|", script);
            Assert.DoesNotContain("APP=store|", script);
            Assert.DoesNotContain("Microsoft.WindowsStore", script);
        }

        [Fact]
        public void OptimizationView_ApplyButtonsAreEnabledBindCorrectlyAndExecuteOnUiClick()
        {
            WpfTestHost.RunOnWpfThread(() =>
            {
                var registry = new InMemoryRegistryBackend();
                var customTweaks = new List<TweakDefinition>
                {
                    new()
                    {
                        Id = "ui_test_tweak",
                        Titulo = "Tweak UI Teste",
                        Categoria = "Privacidade",
                        Recomendado = true,
                        Ativar = new() { new() { Tipo = "reg", Chave = @"HKCU\Software\UiTest", Valor = "Val", Kind = "dword", Dados = "1" } },
                        Desativar = new()
                    }
                };
                var engine = new TweakEngine(registry, new ServiceStartupHelper(registry), Path.Combine(_tempDir, "ui-backup.json"), currentBuild: 22631, customTweaks: customTweaks);
                var vm = new OptimizationViewModel(tweakEngine: engine, bloatwareService: new BloatwareService(historyPath: Path.Combine(_tempDir, "ui-bloat.json")));

                var view = new HardwareHub.Views.OptimizationView { DataContext = vm };
                var hostWindow = WpfTestHost.ShowHostedControl(view);

                var buttons = WpfTestHost.FindVisualChildren<Button>(view).ToList();
                var profileButtons = buttons.Where(b => ReferenceEquals(b.Command, vm.ApplyProfileCommand)).ToList();
                var recommendedBtn = buttons.FirstOrDefault(b => ReferenceEquals(b.Command, vm.ApplyRecommendedTweaksCommand));

                Assert.True(profileButtons.Count == 3,
                    $"Total buttons={buttons.Count}, ProfileCmdMatches={profileButtons.Count}, NonNullCmds={buttons.Count(b => b.Command != null)}");
                Assert.NotNull(recommendedBtn);
                Assert.All(profileButtons, b =>
                {
                    Assert.True(b.IsEnabled);
                    Assert.IsType<OptimizationProfile>(b.CommandParameter);
                    Assert.True(b.Command.CanExecute(b.CommandParameter));
                });
                Assert.True(recommendedBtn!.IsEnabled);

                // Dispara clique real no botão "Aplicar Recomendados" via UI Automation
                var peer = new ButtonAutomationPeer(recommendedBtn);
                var invokeProvider = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
                invokeProvider.Invoke();

                // Aguarda conclusão assíncrona no Dispatcher
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while ((vm.IsApplyingProfile || vm.UndoableCount == 0) && DateTime.UtcNow < deadline)
                {
                    WpfTestHost.PumpDispatcher(DispatcherPriority.Background);
                    Thread.Sleep(20);
                }

                WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                Assert.False(vm.IsApplyingProfile);
                Assert.Equal(1, vm.UndoableCount);
                Assert.True(recommendedBtn.IsEnabled);
                Assert.All(profileButtons, b => Assert.True(b.IsEnabled));

                // Preenche ScannedBloatware sem itens presentes para validar o clique real em "Aplicar Perfil" sem acionar PowerShell
                var bloatSvc = new BloatwareService(historyPath: Path.Combine(_tempDir, "ui-bloat.json"));
                vm.ScannedBloatware.Add(new BloatwareItemViewModel(bloatSvc.Catalog.First())
                {
                    IsInstalled = false,
                    IsProvisioned = false
                });
                var profilePeer = new ButtonAutomationPeer(profileButtons[0]);
                var profileInvoke = Assert.IsAssignableFrom<IInvokeProvider>(profilePeer.GetPattern(PatternInterface.Invoke));
                profileInvoke.Invoke();

                WpfTestHost.PumpDispatcher(DispatcherPriority.Input);
                deadline = DateTime.UtcNow.AddSeconds(5);
                while ((vm.IsApplyingProfile || !vm.OptimizationStatusBanner.Contains("concluído")) && DateTime.UtcNow < deadline)
                {
                    WpfTestHost.PumpDispatcher(DispatcherPriority.Background);
                    Thread.Sleep(20);
                }
                WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                Assert.False(vm.IsApplyingProfile);
                Assert.Contains("concluído", vm.OptimizationStatusBanner);
                Assert.All(profileButtons, b => Assert.True(b.IsEnabled));

                hostWindow.Close();
            });
        }
    }
}

