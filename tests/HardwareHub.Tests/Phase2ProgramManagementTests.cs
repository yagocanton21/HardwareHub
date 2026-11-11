using System.IO;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using HardwareHub.Models;
using HardwareHub.Services;
using HardwareHub.ViewModels;
using HardwareHub.Views;
using Microsoft.Win32;
using Xunit;

namespace HardwareHub.Tests
{
    public sealed class Phase2ProgramManagementTests : IDisposable
    {
        private readonly string _tempDir;

        public Phase2ProgramManagementTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"HardwareHub_Phase2_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }

        [Fact]
        public void StartupCatalog_LoadsEmbeddedRulesAndCompilesValidRegexes()
        {
            var rules = StartupManagerService.LoadEmbeddedRules();
            Assert.NotEmpty(rules);
            Assert.All(rules, rule =>
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.Padrao));
                Assert.NotNull(rule.CompiledRegex);
            });
            Assert.Contains(rules, r => (r.CompiledRegex?.IsMatch("Discord Update.exe") ?? false) && r.Desligar == "pode");
            Assert.Contains(rules, r => (r.CompiledRegex?.IsMatch("SecurityHealthSystray.exe") ?? false) && r.Desligar == "deixe");
        }

        [Fact]
        public void StartupApprovedBinary_EncodesAndDecodes12ByteState()
        {
            var when = new DateTime(2026, 6, 15, 14, 30, 0, DateTimeKind.Local);
            byte[] disabledBytes = StartupManagerService.BuildApprovedBytes(enabled: false, when);

            Assert.Equal(12, disabledBytes.Length);
            Assert.Equal((byte)3, disabledBytes[0]);

            var decodedDisabled = StartupManagerService.DecodeApprovedBytes(disabledBytes);
            Assert.False(decodedDisabled.IsEnabled);
            Assert.NotNull(decodedDisabled.DisabledAt);
            Assert.True(Math.Abs((decodedDisabled.DisabledAt!.Value - when).TotalSeconds) < 1);

            byte[] enabledBytes = StartupManagerService.BuildApprovedBytes(enabled: true, when);
            Assert.Equal((byte)2, enabledBytes[0]);

            var decodedEnabled = StartupManagerService.DecodeApprovedBytes(enabledBytes);
            Assert.True(decodedEnabled.IsEnabled);
            Assert.Null(decodedEnabled.DisabledAt);
        }

        [Fact]
        public void ResolveCommandFile_HandlesQuotesEnvVarsRundllAndSquirrelUpdateExe()
        {
            string appDir = Path.Combine(_tempDir, "AppRoot");
            string verDir = Path.Combine(appDir, "app-1.0.9");
            Directory.CreateDirectory(verDir);

            string myExe = Path.Combine(verDir, "MyChat.exe");
            string updateExe = Path.Combine(appDir, "Update.exe");
            string helperDll = Path.Combine(appDir, "helper.dll");
            File.WriteAllText(myExe, "dummy");
            File.WriteAllText(updateExe, "dummy");
            File.WriteAllText(helperDll, "dummy");

            Assert.Equal(myExe, StartupManagerService.ResolveCommandFile($"\"{myExe}\" --minimized"));
            Assert.Equal(myExe, StartupManagerService.ResolveCommandFile($"\"{updateExe}\" --processStart \"MyChat.exe\""));
            Assert.Equal(helperDll, StartupManagerService.ResolveCommandFile($"rundll32.exe \"{helperDll}\",EntryPoint"));

            string windirExe = StartupManagerService.ResolveCommandFile(@"%SystemRoot%\notepad.exe") ?? "";
            Assert.EndsWith("notepad.exe", windirExe, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%", windirExe);
        }

        [Fact]
        public void StartupManagerService_ListsRegistryAndFolderEntries_AndTogglesApprovedState()
        {
            var reg = new InMemoryRegistryBackend();
            reg.Write(
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
                "Discord",
                RegistryValueKind.String,
                @"C:\Users\Test\AppData\Local\Discord\Update.exe --processStart Discord.exe");
            reg.Write(
                @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run",
                "SecurityHealth",
                RegistryValueKind.String,
                @"%windir%\system32\SecurityHealthSystray.exe");

            string userStartup = Path.Combine(_tempDir, "UserStartup");
            string commonStartup = Path.Combine(_tempDir, "CommonStartup");
            Directory.CreateDirectory(userStartup);
            Directory.CreateDirectory(commonStartup);
            File.WriteAllText(Path.Combine(userStartup, "desktop.ini"), "[.ShellClassInfo]");
            File.WriteAllText(Path.Combine(userStartup, "QuickNote.bat"), "@echo off");

            var svc = new StartupManagerService(reg, userStartup, commonStartup);
            var entries = svc.ListEntries();

            Assert.Equal(3, entries.Count);
            Assert.DoesNotContain(entries, e => e.Name.Equals("desktop", StringComparison.OrdinalIgnoreCase));

            var discord = entries.Single(e => e.Name == "Discord");
            Assert.True(discord.IsEnabled);
            Assert.Equal(StartupAdvice.CanDisable, discord.Advice);

            svc.SetEnabled(discord, enabled: false);
            Assert.False(discord.IsEnabled);

            var approved = reg.Read(discord.ApprovedKey, discord.ApprovedName);
            Assert.True(approved.Exists);
            Assert.False(StartupManagerService.DecodeApprovedBytes(approved.Data).IsEnabled);

            // Re-listar deve refletir estado salvo no StartupApproved
            var reloaded = svc.ListEntries().Single(e => e.Name == "Discord");
            Assert.False(reloaded.IsEnabled);

            svc.SetEnabled(reloaded, enabled: true);
            Assert.True(svc.ListEntries().Single(e => e.Name == "Discord").IsEnabled);
        }

        [Fact]
        public void WingetUpdaterService_ParsesLocalizedOutputAndFiltersEdgeCases()
        {
            string sampleOutput = string.Join("\n", new[]
            {
                "\r   \\ ",
                "Nome                           Id                            Versão          Disponível      Origem",
                "--------------------------------------------------------------------------------------------------",
                "Discord                        Discord.Discord               1.0.9030        1.0.9055        winget",
                "Git                            Git.Git                       < 2.44.0        2.47.1          winget",
                "Nome Muito Longo Cortado…      Empresa.Pacote.Cortado…       1.0             2.0             winget",
                "Programa Sem Versao            Contoso.UnknownApp            Desconhecido    3.0.1           winget",
                "VLC media player…              VideoLAN.VLC                  3.0.20          3.0.21          winget",
                "2 atualizações disponíveis."
            });

            var parsed = WingetUpdaterService.ParseUpgradeOutput(sampleOutput);

            Assert.Equal(3, parsed.Count);
            Assert.Contains(parsed, p => p.Id == "Discord.Discord" && p.InstalledVersion == "1.0.9030" && p.AvailableVersion == "1.0.9055");
            Assert.Contains(parsed, p => p.Id == "Git.Git" && p.InstalledVersion == "< 2.44.0" && p.AvailableVersion == "2.47.1");
            Assert.Contains(parsed, p => p.Id == "VideoLAN.VLC" && p.Name == "VLC media player");
            Assert.DoesNotContain(parsed, p => p.Id.Contains('…'));
            Assert.DoesNotContain(parsed, p => p.Id == "Contoso.UnknownApp");
        }

        [Fact]
        public void WingetUpdaterService_ValidatesIdsAndPersistsIgnoredList()
        {
            Assert.True(WingetUpdaterService.IsValidWingetId("Microsoft.PowerToys"));
            Assert.True(WingetUpdaterService.IsValidWingetId("9NBLGGH4NNS1"));
            Assert.False(WingetUpdaterService.IsValidWingetId("id_invalido…"));
            Assert.False(WingetUpdaterService.IsValidWingetId("semponto"));

            string ignoredPath = Path.Combine(_tempDir, "ignored.json");
            var svc1 = new WingetUpdaterService(ignoredPath);
            svc1.IgnoreUpdate("Discord.Discord");
            svc1.IgnoreUpdate("Git.Git");

            var svc2 = new WingetUpdaterService(ignoredPath);
            Assert.Contains("discord.discord", svc2.GetIgnoredIds());
            Assert.Contains("Git.Git", svc2.GetIgnoredIds());

            svc2.UnignoreAll();
            Assert.Empty(new WingetUpdaterService(ignoredPath).GetIgnoredIds());
        }

        [Fact]
        public void ProgramUninstaller_SplitCommandAndFolderSafetyGuards_ProtectSystemAndPersonalDirs()
        {
            var (exe1, args1) = ProgramUninstallerService.SplitCommand("\"C:\\Program Files\\DemoApp\\unins000.exe\" /SILENT");
            Assert.Equal(@"C:\Program Files\DemoApp\unins000.exe", exe1);
            Assert.Equal("/SILENT", args1);

            var (exe2, args2) = ProgramUninstallerService.SplitCommand(@"MsiExec.exe /X{11111111-2222-3333-4444-555555555555}");
            Assert.Equal("MsiExec.exe", exe2);
            Assert.StartsWith("/X{", args2);

            string scanRoot = Path.Combine(_tempDir, "ProgramFilesMock");
            string safeAppDir = Path.Combine(scanRoot, "DemoWidget");
            string otherAppDir = Path.Combine(scanRoot, "KeepOtherApp");
            Directory.CreateDirectory(safeAppDir);
            Directory.CreateDirectory(otherAppDir);

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { scanRoot };
            var otherFolders = new List<string> { otherAppDir };

            Assert.True(ProgramUninstallerService.IsSafeFolderPath(safeAppDir, otherFolders, roots));
            Assert.False(ProgramUninstallerService.IsSafeFolderPath(scanRoot, otherFolders, roots));
            Assert.False(ProgramUninstallerService.IsSafeFolderPath(otherAppDir, otherFolders, roots));
            Assert.False(ProgramUninstallerService.IsSafeFolderPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows), otherFolders, roots));
            Assert.False(ProgramUninstallerService.IsSafeFolderPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), otherFolders, roots));
            Assert.False(ProgramUninstallerService.IsSafeFolderPath(@"C:\Program Files\WindowsApps\Foo", otherFolders, roots));
        }

        [Fact]
        public async Task ProgramUninstaller_FindsLeftoversAndBacksUpBeforeCleaning()
        {
            string scanRoot = Path.Combine(_tempDir, "ScanPrograms");
            string leftoverDir = Path.Combine(scanRoot, "AcmeWidget");
            string sharedOtherDir = Path.Combine(scanRoot, "SharedNeighbor");
            Directory.CreateDirectory(leftoverDir);
            Directory.CreateDirectory(sharedOtherDir);
            File.WriteAllText(Path.Combine(leftoverDir, "config.dat"), "abc123");

            var reg = new InMemoryRegistryBackend();
            string orphanUninstallKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AcmeWidget";
            reg.Write(orphanUninstallKey, "DisplayName", RegistryValueKind.String, "AcmeWidget");

            string backupsDir = Path.Combine(_tempDir, "Backups");
            bool exportCalled = false;
            var svc = new ProgramUninstallerService(
                backupsDir: backupsDir,
                registry: reg,
                exportRegistryHook: (regPath, outFile) =>
                {
                    exportCalled = true;
                    File.WriteAllText(outFile, $"Windows Registry Editor Version 5.00\r\n; {regPath}");
                    return (0, "");
                });

            var target = new InstalledProgramItem
            {
                KeyId = "AcmeWidget",
                RegistryPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\AcmeWidget",
                Name = "AcmeWidget",
                Publisher = "Acme Labs",
                InstallLocation = leftoverDir
            };
            var other = new InstalledProgramItem
            {
                KeyId = "SharedNeighbor",
                Name = "SharedNeighbor",
                InstallLocation = sharedOtherDir
            };

            var found = svc.FindLeftovers(
                target,
                LeftoverLevel.Moderate,
                new[] { target, other },
                customScanRoots: new[] { (scanRoot, "MockPF") });

            Assert.Contains(found, l => l.Kind == LeftoverKind.Folder && l.Path.Equals(leftoverDir, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(found, l => l.Kind == LeftoverKind.RegistryKey && l.Path.Contains("AcmeWidget", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, l => l.Path.Equals(sharedOtherDir, StringComparison.OrdinalIgnoreCase));

            var cleanResult = await svc.CleanLeftoversAsync("AcmeWidget", found, sendToRecycleBin: false);

            Assert.Empty(cleanResult.Errors);
            Assert.True(cleanResult.RemovedCount >= 2);
            Assert.True(exportCalled);
            Assert.False(Directory.Exists(leftoverDir));
            Assert.False(reg.KeyExists(orphanUninstallKey));

            var backups = svc.LoadBackups();
            var backup = Assert.Single(backups);
            Assert.Equal("AcmeWidget", backup.Title);
            Assert.NotEmpty(backup.Items);
            Assert.Contains(leftoverDir, backup.RemovedPaths);
        }

        [Fact]
        public async Task ProgramUninstaller_AbortsRegistryDeleteWhenBackupFails_AndBlocksPathTraversal()
        {
            var reg = new InMemoryRegistryBackend();
            string key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\KeepMeSafe";
            reg.Write(key, "DisplayName", RegistryValueKind.String, "KeepMeSafe");

            string backupsDir = Path.Combine(_tempDir, "BackupsFail");
            var svc = new ProgramUninstallerService(
                backupsDir: backupsDir,
                registry: reg,
                exportRegistryHook: (_, _) => (1, "simulated export failure"));

            var item = new ProgramLeftoverItem
            {
                Kind = LeftoverKind.RegistryKey,
                Level = LeftoverLevel.Safe,
                Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\KeepMeSafe",
                Reason = "Test"
            };

            var res = await svc.CleanLeftoversAsync("KeepMeSafe", new[] { item }, sendToRecycleBin: false);
            Assert.Single(res.Errors);
            Assert.True(reg.KeyExists(key));

            // Testa proteção contra path traversal na restauração
            string bDir = Path.Combine(backupsDir, "malicious");
            Directory.CreateDirectory(bDir);
            string outsideReg = Path.Combine(_tempDir, "outside.reg");
            File.WriteAllText(outsideReg, "Windows Registry Editor Version 5.00");

            var maliciousBackup = new UninstallBackupInfo
            {
                Title = "TraversalTest",
                FolderPath = bDir,
                Items = { new UninstallBackupItem { Description = "Evil", FileName = @"..\outside.reg" } }
            };
            var restoreRes = await svc.RestoreBackupAsync(maliciousBackup);
            Assert.False(restoreRes.Ok);
            Assert.Contains("inválido", restoreRes.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ProgramsStartupViewModel_FiltersStartupAndSafelyDisablesRecommended()
        {
            var reg = new InMemoryRegistryBackend();
            reg.Write(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Discord", RegistryValueKind.String, @"C:\Mock\Discord.exe");
            reg.Write(@"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", "SecurityHealth", RegistryValueKind.String, @"%windir%\system32\SecurityHealthSystray.exe");

            string emptyStartup = Path.Combine(_tempDir, "EmptyStartup");
            Directory.CreateDirectory(emptyStartup);

            var startupSvc = new StartupManagerService(reg, emptyStartup, emptyStartup);
            var updaterSvc = new WingetUpdaterService(Path.Combine(_tempDir, "vm-ignored.json"));
            var uninstallerSvc = new ProgramUninstallerService(Path.Combine(_tempDir, "vm-backups"), reg);

            var vm = new ProgramsStartupViewModel(startupSvc, updaterSvc, uninstallerSvc);
            await vm.LoadStartupAsync();

            Assert.Equal(2, vm.AllStartupEntries.Count);
            Assert.Equal(2, vm.ActiveStartupCount);
            Assert.Equal(1, vm.SafeDisableStartupCount);

            vm.SetStartupFilterCommand.Execute("Pode");
            var safeItem = Assert.Single(vm.FilteredStartupEntries);
            Assert.Equal("Discord", safeItem.Name);

            vm.DisableRecommendedStartupCommand.Execute(null);
            Assert.Equal(1, vm.ActiveStartupCount);
            Assert.Equal(0, vm.SafeDisableStartupCount);

            vm.SetStartupFilterCommand.Execute("Ativos");
            Assert.Equal("SecurityHealth", Assert.Single(vm.FilteredStartupEntries).Name);

            // Testa filtro/ignorar e alternância de selecionar/desmarcar todos em Updates no ViewModel
            var u1 = new ProgramUpdateItemViewModel { Name = "Discord", Id = "Discord.Discord", InstalledVersion = "1.0", AvailableVersion = "2.0" };
            var u2 = new ProgramUpdateItemViewModel { Name = "Git", Id = "Git.Git", InstalledVersion = "2.40", AvailableVersion = "2.47" };
            vm.AllUpdates.Add(u1);
            vm.AllUpdates.Add(u2);
            vm.ApplyUpdateFilter();

            Assert.True(vm.AreAllFilteredUpdatesSelected);
            Assert.Equal("☐ Desmarcar Todos", vm.SelectAllUpdatesLabel);

            vm.SelectAllUpdatesCommand.Execute(null);
            Assert.False(u1.IsSelected);
            Assert.False(u2.IsSelected);
            Assert.Equal("☑️ Selecionar Todos", vm.SelectAllUpdatesLabel);
            Assert.False(vm.UpgradeSelectedCommand.CanExecute(null));

            vm.SelectAllUpdatesCommand.Execute(null);
            Assert.True(u1.IsSelected);
            Assert.True(u2.IsSelected);
            Assert.Equal("☐ Desmarcar Todos", vm.SelectAllUpdatesLabel);
            Assert.True(vm.UpgradeSelectedCommand.CanExecute(null));

            vm.UpdateSearch = "git";
            Assert.Equal("Git.Git", Assert.Single(vm.FilteredUpdates).Id);

            vm.UpdateSearch = "";
            vm.IgnoreUpdateCommand.Execute(u1);
            Assert.DoesNotContain(u1, vm.AllUpdates);
            Assert.Contains("discord.discord", updaterSvc.GetIgnoredIds());
        }

        [Fact]
        public void Phase2WpfViews_NavigateSubTabsAndClickInteractiveButtonsOnStaThread()
        {
            WpfTestHost.RunOnWpfThread(() =>
            {
                var main = new MainWindow();
                var mainVm = Assert.IsType<MainViewModel>(main.DataContext);

                mainVm.NavigateCommand.Execute("ProgramsManager");
                Assert.True(mainVm.IsProgramsManagerActive);
                Assert.False(mainVm.IsStartupManagerActive);

                mainVm.NavigateCommand.Execute("StartupManager");
                Assert.True(mainVm.IsStartupManagerActive);
                Assert.False(mainVm.IsProgramsManagerActive);
                mainVm.Dispose();
                main.Close();

                // Testa StartupManagerView + ProgramsManagerView isolados com backend em memória
                var reg = new InMemoryRegistryBackend();
                reg.Write(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Discord", RegistryValueKind.String, @"C:\Mock\Discord.exe");
                string emptyDir = Path.Combine(_tempDir, "UiEmptyStartup");
                Directory.CreateDirectory(emptyDir);

                var vm = new ProgramsStartupViewModel(
                    new StartupManagerService(reg, emptyDir, emptyDir),
                    new WingetUpdaterService(Path.Combine(_tempDir, "ui-ignored.json")),
                    new ProgramUninstallerService(Path.Combine(_tempDir, "ui-backups"), reg));

                var startupView = new StartupManagerView { DataContext = vm };
                var startupHost = WpfTestHost.ShowHostedControl(startupView);
                try
                {
                    var loadTask = vm.LoadStartupAsync();
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!loadTask.IsCompleted && DateTime.UtcNow < deadline)
                    {
                        WpfTestHost.PumpDispatcher(DispatcherPriority.Background);
                        Thread.Sleep(15);
                    }
                    Assert.True(loadTask.IsCompletedSuccessfully);
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    Assert.Equal(1, vm.ActiveStartupCount);

                    var toggleBtn = WpfTestHost.FindVisualChildren<Button>(startupView)
                        .FirstOrDefault(b => ReferenceEquals(b.Command, vm.ToggleStartupCommand));
                    Assert.NotNull(toggleBtn);
                    Assert.True(toggleBtn!.IsEnabled);

                    var peer = new ButtonAutomationPeer(toggleBtn);
                    var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
                    invoke.Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.Input);
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    Assert.Equal(0, vm.ActiveStartupCount);
                    Assert.False(vm.AllStartupEntries[0].IsEnabled);
                }
                finally
                {
                    startupHost.Close();
                }

                var programsView = new ProgramsManagerView { DataContext = vm };
                var programsHost = WpfTestHost.ShowHostedControl(programsView);
                try
                {
                    var subtitleBlock = WpfTestHost.FindVisualChildren<TextBlock>(programsView)
                        .First(t => t.Text.StartsWith("Atualize seus programas via Winget", StringComparison.Ordinal));
                    var refreshProgramsBtn = WpfTestHost.FindVisualChildren<Button>(programsView)
                        .Single(b => ReferenceEquals(b.Command, vm.RefreshProgramsCommand));
                    var subPos = subtitleBlock.TransformToAncestor(programsView).Transform(new System.Windows.Point(subtitleBlock.ActualWidth, 0));
                    var btnPos = refreshProgramsBtn.TransformToAncestor(programsView).Transform(new System.Windows.Point(0, 0));
                    Assert.True(subPos.X <= btnPos.X, $"Subtítulo ({subPos.X:0.##}) sobrepôs o botão Atualizar Lista ({btnPos.X:0.##}).");

                    vm.AllUpdates.Add(new ProgramUpdateItemViewModel { Name = "App1", Id = "Vendor.App1", InstalledVersion = "1.0", AvailableVersion = "2.0" });
                    vm.AllUpdates.Add(new ProgramUpdateItemViewModel { Name = "App2", Id = "Vendor.App2", InstalledVersion = "1.0", AvailableVersion = "2.0" });
                    vm.ApplyUpdateFilter();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    var selectAllBtn = WpfTestHost.FindVisualChildren<Button>(programsView)
                        .Single(b => ReferenceEquals(b.Command, vm.SelectAllUpdatesCommand));
                    var selectAllText = Assert.IsType<TextBlock>(selectAllBtn.Content);
                    Assert.True(selectAllBtn.IsEnabled);
                    Assert.Equal("☐ Desmarcar Todos", selectAllText.Text);

                    var selectPeer = new ButtonAutomationPeer(selectAllBtn);
                    var selectInvoke = Assert.IsAssignableFrom<IInvokeProvider>(selectPeer.GetPattern(PatternInterface.Invoke));
                    selectInvoke.Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.Input);
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    Assert.All(vm.AllUpdates, u => Assert.False(u.IsSelected));
                    Assert.Equal("☑️ Selecionar Todos", selectAllText.Text);

                    selectInvoke.Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.Input);
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    Assert.All(vm.AllUpdates, u => Assert.True(u.IsSelected));
                    Assert.Equal("☐ Desmarcar Todos", selectAllText.Text);

                    var tabButtons = WpfTestHost.FindVisualChildren<Button>(programsView)
                        .Where(b => ReferenceEquals(b.Command, vm.SwitchProgramsTabCommand))
                        .ToList();
                    Assert.Equal(3, tabButtons.Count);

                    var backupsTabBtn = tabButtons.Single(b => Equals(b.CommandParameter, "Backups"));
                    var tabPeer = new ButtonAutomationPeer(backupsTabBtn);
                    var tabInvoke = Assert.IsAssignableFrom<IInvokeProvider>(tabPeer.GetPattern(PatternInterface.Invoke));
                    tabInvoke.Invoke();
                    WpfTestHost.PumpDispatcher(DispatcherPriority.ApplicationIdle);

                    Assert.True(vm.IsBackupsTab);
                    Assert.False(vm.IsUpdatesTab);
                }
                finally
                {
                    programsHost.Close();
                }
            });
        }
    }
}
