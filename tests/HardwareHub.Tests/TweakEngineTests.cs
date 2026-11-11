using System.IO;
using HardwareHub.Models;
using HardwareHub.Services;
using Microsoft.Win32;
using Xunit;

namespace HardwareHub.Tests
{
    public sealed class TweakEngineTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _backupPath;
        private readonly InMemoryRegistryBackend _registry;
        private readonly ServiceStartupHelper _services;

        public TweakEngineTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"HardwareHub_Tests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _backupPath = Path.Combine(_tempDir, "tweaks-backup.json");
            _registry = new InMemoryRegistryBackend();
            _services = new ServiceStartupHelper(_registry);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void ApplyAndRevert_RestoresExistingRegistryTypesAndDeletesNewlyCreatedHierarchy()
        {
            // Pré-popula apenas parte do caminho e um valor antigo
            _registry.Write(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", "ExistingStr", RegistryValueKind.String, "Original");
            _registry.Write(@"HKLM\SYSTEM\CurrentControlSet\Services\TestSvc", "Start", RegistryValueKind.DWord, 2);
            _registry.Write(@"HKLM\SYSTEM\CurrentControlSet\Services\TestSvc", "DelayedAutostart", RegistryValueKind.DWord, 1);

            var customTweaks = new List<TweakDefinition>
            {
                new()
                {
                    Id = "t1",
                    Categoria = "Teste",
                    Titulo = "Ajuste Completo",
                    Ativar = new()
                    {
                        new() { Tipo = "reg", Chave = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", Valor = "ExistingStr", Kind = "sz", Dados = "Modified" },
                        new() { Tipo = "reg", Chave = @"HKCU\Software\Policies\BrandNewFolder\SubKey", Valor = "NewDword", Kind = "dword", Dados = "1" },
                        new() { Tipo = "reg", Chave = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", Valor = "BinVal", Kind = "binary", Dados = "DE AD BE EF" },
                        new() { Tipo = "servico", Servico = "TestSvc", Inicio = "disabled" }
                    },
                    Desativar = new()
                }
            };

            var engine = new TweakEngine(_registry, _services, _backupPath, currentBuild: 22631, customTweaks: customTweaks);
            var tweak = customTweaks[0];

            Assert.False(engine.IsActive(tweak));
            engine.Apply(tweak);

            Assert.True(engine.IsActive(tweak));
            Assert.True(engine.HasBackup("t1"));
            Assert.True(File.Exists(_backupPath));
            Assert.Equal("Modified", _registry.Read(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", "ExistingStr").Data);
            Assert.True(_registry.KeyExists(@"HKCU\Software\Policies\BrandNewFolder\SubKey"));
            Assert.Equal((4, (int?)null), _services.Get("TestSvc"));

            // Recarrega outra instância a partir do JSON persistido e reverte
            var reloaded = new TweakEngine(_registry, _services, _backupPath, currentBuild: 22631, customTweaks: customTweaks);
            Assert.True(reloaded.HasBackup("t1"));
            Assert.Equal(1, reloaded.RevertAll());

            Assert.False(reloaded.IsActive(tweak));
            Assert.False(reloaded.HasBackup("t1"));
            Assert.Equal("Original", _registry.Read(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", "ExistingStr").Data);
            Assert.False(_registry.Read(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", "BinVal").Exists);
            // A árvore vazia recém-criada deve ter sido removida até a raiz que faltava
            Assert.False(_registry.KeyExists(@"HKCU\Software\Policies"));
            Assert.Equal((2, 1), _services.Get("TestSvc"));
        }

        [Fact]
        public void Apply_RollsBackAndRemovesBackupWhenMiddleActionFails()
        {
            _registry.Write(@"HKCU\Software\TestA", "Flag", RegistryValueKind.DWord, 5);
            _registry.ThrowOnWriteKeys.Add(@"HKLM\SOFTWARE\BlockedKey");

            var failingTweak = new TweakDefinition
            {
                Id = "fail_mid",
                Ativar = new()
                {
                    new() { Tipo = "reg", Chave = @"HKCU\Software\TestA", Valor = "Flag", Kind = "dword", Dados = "99" },
                    new() { Tipo = "reg", Chave = @"HKLM\SOFTWARE\BlockedKey", Valor = "AdminOnly", Kind = "dword", Dados = "1" }
                }
            };

            var engine = new TweakEngine(_registry, _services, _backupPath, customTweaks: new[] { failingTweak });

            Assert.Throws<UnauthorizedAccessException>(() => engine.Apply(failingTweak));
            Assert.Equal(5, _registry.Read(@"HKCU\Software\TestA", "Flag").Data);
            Assert.False(engine.HasBackup("fail_mid"));
        }

        [Fact]
        public void IsApplicable_RespectsMinBuildAndMaxBuild()
        {
            var win11Only = new TweakDefinition { Id = "w11", MinBuild = 22000 };
            var win10Only = new TweakDefinition { Id = "w10", MinBuild = 10240, MaxBuild = 21999 };

            var win10Engine = new TweakEngine(_registry, _services, _backupPath, currentBuild: 19045, customTweaks: Array.Empty<TweakDefinition>());
            var win11Engine = new TweakEngine(_registry, _services, _backupPath, currentBuild: 22631, customTweaks: Array.Empty<TweakDefinition>());

            Assert.False(win10Engine.IsApplicable(win11Only));
            Assert.True(win10Engine.IsApplicable(win10Only));
            Assert.True(win11Engine.IsApplicable(win11Only));
            Assert.False(win11Engine.IsApplicable(win10Only));
        }

        [Fact]
        public void ReadOnlyLiveCheck_CanInspectWindowsRegistryAndRealCatalogWithoutMutating()
        {
            var realEngine = new TweakEngine(WindowsRegistryBackend.Instance, new ServiceStartupHelper(), _backupPath);
            Assert.True(WindowsRegistryBackend.Instance.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion"));
            Assert.True(realEngine.Tweaks.Count >= 60);
            // Apenas consulta IsActive/IsApplicable em modo leitura
            var results = realEngine.Tweaks.Where(realEngine.IsApplicable).Select(t => realEngine.IsActive(t)).ToList();
            Assert.NotEmpty(results);
            Assert.False(File.Exists(_backupPath));
        }
    }
}

