using HardwareHub.Models;
using HardwareHub.Services;
using Xunit;

namespace HardwareHub.Tests
{
    public sealed class CatalogIntegrityTests
    {
        [Fact]
        public void EmbeddedTweaksCatalog_IsValidAndPreservesSecurityFeatures()
        {
            var tweaks = TweakEngine.LoadEmbeddedTweaks();

            Assert.True(tweaks.Count >= 60, $"Esperado >= 60 tweaks, obtido {tweaks.Count}");

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TweakDefinition t in tweaks)
            {
                Assert.False(string.IsNullOrWhiteSpace(t.Id));
                Assert.True(ids.Add(t.Id), $"ID duplicado em tweaks.json: {t.Id}");
                Assert.False(string.IsNullOrWhiteSpace(t.Categoria));
                Assert.False(string.IsNullOrWhiteSpace(t.Titulo));
                Assert.NotEmpty(t.Ativar);

                // Valida todas as ações e conversão de dados sem exceção
                foreach (var a in t.Ativar.Concat(t.Desativar))
                {
                    Assert.Contains(a.Tipo, new[] { "reg", "regDel", "regDelKey", "servico" });
                    if (a.Tipo == "reg")
                    {
                        Assert.False(string.IsNullOrWhiteSpace(a.Chave));
                        var kind = RegistryValueConverter.ParseKind(a.Kind);
                        _ = RegistryValueConverter.ConvertData(kind, a.Dados);
                        WindowsRegistryBackend.ParsePath(a.Chave!);
                    }
                    else if (a.Tipo == "servico")
                    {
                        Assert.False(string.IsNullOrWhiteSpace(a.Servico));
                        _ = ServiceStartupHelper.ParseStartupMode(a.Inicio ?? "");
                    }
                }

                // Garante que nenhum tweak mexe para desligar Defender/Firewall/UAC/SmartScreen
                string joinedPaths = string.Join(" ", t.Ativar.Select(a => $"{a.Chave} {a.Valor} {a.Servico}"));
                Assert.DoesNotContain("EnableLUA", joinedPaths, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DisableAntiSpyware", joinedPaths, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("EnableFirewall", joinedPaths, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void EmbeddedBloatwareCatalog_ProtectsCriticalSystemApps()
        {
            var catalog = BloatwareService.LoadEmbeddedCatalog();

            Assert.True(catalog.Apps.Count >= 30);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in catalog.Apps)
            {
                Assert.True(ids.Add(app.Id), $"ID duplicado em bloatware.json: {app.Id}");
                Assert.NotEmpty(app.Pacotes);
            }

            string[] mustStayProtected = { "store", "appinstaller", "seguranca", "shell", "edge", "bibliotecas" };
            foreach (string id in mustStayProtected)
            {
                var def = catalog.Apps.Single(a => a.Id == id);
                Assert.Equal(BloatwareRecommendation.KeepProtected, def.Level);
                var vm = new BloatwareItemViewModel(def);
                Assert.False(vm.CanRemove);
                Assert.False(vm.IsSelected);
                vm.IsSelected = true;
                Assert.False(vm.IsSelected);
            }
        }

        [Fact]
        public void OptimizationProfiles_AllIdsValidAndNoProtectedAppsIncluded()
        {
            var tweaks = TweakEngine.LoadEmbeddedTweaks();
            var bloat = BloatwareService.LoadEmbeddedCatalog();
            var profiles = OptimizationProfiles.BuildProfiles(tweaks, bloat.Apps);

            Assert.Equal(3, profiles.Count);
            var tweakSet = tweaks.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bloatById = bloat.Apps.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var p in profiles)
            {
                Assert.NotEmpty(p.TweakIds);
                foreach (string tid in p.TweakIds)
                {
                    Assert.Contains(tid, tweakSet);
                }
                foreach (string bid in p.BloatwareIds)
                {
                    Assert.True(bloatById.TryGetValue(bid, out var def), $"Bloatware '{bid}' não encontrado.");
                    Assert.NotEqual(BloatwareRecommendation.KeepProtected, def!.Level);
                }
            }
        }

        [Fact]
        public void PostFormatKits_ReferenceValidSoftwareCatalogIds()
        {
            var softwareIds = new SoftwareService().GetCatalog().Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kits = PostFormatService.GetSoftwareKits();

            Assert.True(kits.Count >= 4);
            foreach (var kit in kits)
            {
                Assert.NotEmpty(kit.SoftwareIds);
                foreach (string id in kit.SoftwareIds)
                {
                    Assert.True(softwareIds.Contains(id), $"Software '{id}' do kit '{kit.Name}' não existe no catálogo.");
                }
            }
        }
    }
}

