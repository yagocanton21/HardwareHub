using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using HardwareHub.Services;
using HardwareHub.ViewModels;
using HardwareHub.Views;
using Xunit;

namespace HardwareHub.Tests
{
    public class Phase8WhatsNewTests
    {
        [Fact]
        public void LoadEmbeddedNotes_ContainsCurrentVersionAndCategorizedEntries()
        {
            var service = new WhatsNewService();
            var notes = service.LoadEmbeddedNotes();

            Assert.NotEmpty(notes);
            Assert.Equal("1.0.26", notes[0].Version);
            Assert.True(notes[0].IsCurrentVersion);
            Assert.NotEmpty(notes[0].Novidades);
            Assert.NotEmpty(notes[0].Melhorias);
            Assert.NotEmpty(notes[0].Correcoes);
        }

        [Fact]
        public void ParseGitHubReleaseBody_SplitsMarkdownSectionsCorrectly()
        {
            var service = new WhatsNewService();
            string markdown = """
                ### Novidades
                - Nova tela de diagnóstico de memória
                - Suporte a tema escuro automático

                ### Melhorias
                - Inicialização 30% mais rápida

                ### Correções
                - Corrigido travamento ao abrir backup de drivers
                """;

            var parsed = service.ParseGitHubReleaseBody("v1.0.26", "12/11/2026", markdown);

            Assert.Equal("1.0.26", parsed.Version);
            Assert.True(parsed.IsPendingUpdate);
            Assert.Equal(2, parsed.Novidades.Count);
            Assert.Single(parsed.Melhorias);
            Assert.Single(parsed.Correcoes);
            Assert.Contains("Nova tela de diagnóstico de memória", parsed.Novidades);
            Assert.Contains("Corrigido travamento ao abrir backup de drivers", parsed.Correcoes);
        }

        [Fact]
        public void CheckAndMarkPostUpdate_DetectsUpgradeAndUpdatesMarkerFile()
        {
            var service = new WhatsNewService();
            string tempDir = Path.Combine(Path.GetTempPath(), $"hw_whatsnew_{Guid.NewGuid():N}");
            string markerPath = Path.Combine(tempDir, "ultima-versao.txt");

            try
            {
                // 1. Primeira execução (sem arquivo): grava 1.0.26 e não dispara popup
                var firstRun = service.CheckAndMarkPostUpdate(markerPath, new Version(1, 0, 26));
                Assert.False(firstRun.WasUpdated);
                Assert.True(File.Exists(markerPath));
                Assert.Equal("1.0.26", File.ReadAllText(markerPath).Trim());

                // 2. Simula upgrade vindo da 1.0.24 para 1.0.26
                File.WriteAllText(markerPath, "1.0.24");
                var upgradedRun = service.CheckAndMarkPostUpdate(markerPath, new Version(1, 0, 26));

                Assert.True(upgradedRun.WasUpdated);
                Assert.Equal(new Version(1, 0, 24), upgradedRun.PreviousVersion);
                Assert.Equal(new Version(1, 0, 26), upgradedRun.CurrentVersion);
                Assert.Equal("1.0.26", File.ReadAllText(markerPath).Trim());
                Assert.Equal(new[] { "1.0.26", "1.0.25" }, upgradedRun.VersionsToShow.Select(v => v.Version).ToArray());

                // 3. Reabertura na mesma versão: não repete popup
                var repeatedRun = service.CheckAndMarkPostUpdate(markerPath, new Version(1, 0, 26));
                Assert.False(repeatedRun.WasUpdated);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void WhatsNewViewModel_SyncPendingUpdate_PopulatesPreUpdatePreview()
        {
            var vm = new WhatsNewViewModel();
            Assert.False(vm.HasPendingUpdate);

            vm.SyncPendingUpdate(new UpdateInfo
            {
                IsAvailable = true,
                CurrentVersion = "1.0.26",
                LatestVersion = "1.0.27",
                AssetSizeBytes = 15_728_640,
                Changelog = "## Correções\n- Corrigida detecção de NVMe USB"
            });

            Assert.True(vm.HasPendingUpdate);
            Assert.Equal("Nova versão v1.0.27 disponível!", vm.PendingVersionTitle);
            Assert.NotNull(vm.PendingUpdateNotes);
            Assert.Contains("Corrigida detecção de NVMe USB", vm.PendingUpdateNotes!.Correcoes);

            vm.SyncPendingUpdate(new UpdateInfo
            {
                IsAvailable = false,
                CurrentVersion = "1.0.26",
                LatestVersion = "1.0.26"
            });

            Assert.False(vm.HasPendingUpdate);
            Assert.Contains("v1.0.26", vm.UpdateStatusMessage);
        }

        [Fact]
        public void WhatsNewViewAndWindow_RenderCleanlyOnStaThread()
        {
            Exception? threadException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var vm = new WhatsNewViewModel();
                    vm.SyncPendingUpdate(new UpdateInfo
                    {
                        IsAvailable = true,
                        CurrentVersion = "1.0.26",
                        LatestVersion = "1.0.27",
                        Changelog = "### Novidades\n- Item teste"
                    });

                    var view = new WhatsNewView { DataContext = vm, Width = 900, Height = 650 };
                    view.Measure(new Size(900, 650));
                    view.Arrange(new Rect(0, 0, 900, 650));
                    view.UpdateLayout();

                    var result = new PostUpdateCheckResult
                    {
                        WasUpdated = true,
                        PreviousVersion = new Version(1, 0, 25),
                        CurrentVersion = new Version(1, 0, 26),
                        VersionsToShow = new WhatsNewService().LoadEmbeddedNotes().Take(1).ToList()
                    };
                    var popup = new WhatsNewWindow(result);
                    popup.Measure(new Size(640, 600));
                    popup.Arrange(new Rect(0, 0, 640, 600));
                    popup.Close();
                }
                catch (Exception ex)
                {
                    threadException = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            Assert.Null(threadException);
        }
    }
}

