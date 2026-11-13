using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HardwareHub.Models;
using HardwareHub.Services;
using HardwareHub.ViewModels;
using HardwareHub.Views;
using Xunit;

namespace HardwareHub.Tests;

public class Phase7AppsCatalogTests
{
    [Fact]
    public void CatalogJson_LoadsHubProKitsProgramsAndIcons()
    {
        var doc = AppCatalogService.LoadCatalog();

        Assert.Equal(6, doc.Kits.Count);
        Assert.Contains(doc.Kits, k => k.Name == "Essencial");
        Assert.Contains(doc.Kits, k => k.Name == "Gamer");
        Assert.Contains(doc.Kits, k => k.Name == "Técnico");

        Assert.True(doc.Programs.Count >= 55);
        Assert.DoesNotContain(doc.Programs, p => p.Id == "kaspersky");

        var davinci = Assert.Single(doc.Programs, p => p.Id == "davinci");
        Assert.True(davinci.IsLink);

        var whatsapp = Assert.Single(doc.Programs, p => p.Id == "whatsapp");
        Assert.Equal("msstore", whatsapp.Source);
        Assert.Equal("9NKSQGP7F2NH", whatsapp.WingetId);
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("firefox")]
    [InlineData("7zip")]
    [InlineData("whatsapp")]
    [InlineData("vscode")]
    [InlineData("vlc")]
    [InlineData("chatgpt")]
    public void AppIconService_LoadsEmbeddedOfficialPngIcons(string iconId)
    {
        var img = AppIconService.LoadEmbeddedIcon(iconId);
        Assert.NotNull(img);
        var bmp = Assert.IsAssignableFrom<BitmapSource>(img);
        Assert.True(bmp.PixelWidth > 0 && bmp.PixelHeight > 0);
    }

    [Fact]
    public void AppCatalogService_BuildsOfficialWingetArguments()
    {
        var winrar = new AppPackageDefinition
        {
            Id = "winrar",
            WingetId = "RARLab.WinRAR",
            Source = "winget",
            Locale = "pt-BR"
        };
        string argsWinrar = AppCatalogService.BuildInstallArguments(winrar, winrar.Locale);
        Assert.Contains("--id RARLab.WinRAR -e --source winget --silent", argsWinrar);
        Assert.Contains("--locale pt-BR", argsWinrar);

        var discord = new AppPackageDefinition
        {
            Id = "discord",
            WingetId = "Discord.Discord",
            Source = "winget",
            UserScope = true
        };
        string argsDiscord = AppCatalogService.BuildInstallArguments(discord, null);
        Assert.Equal("install --id Discord.Discord -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", argsDiscord);

        var storeApp = new AppPackageDefinition
        {
            Id = "whatsapp",
            WingetId = "9NKSQGP7F2NH",
            Source = "msstore"
        };
        string argsStore = AppCatalogService.BuildInstallArguments(storeApp, null);
        Assert.Contains("--source msstore", argsStore);
    }

    [Fact]
    public void AppCatalogService_TranslatesWingetExitCodes()
    {
        Assert.True(AppCatalogService.TranslateExitCode(0, "").Success);
        Assert.True(AppCatalogService.TranslateExitCode(unchecked((int)0x8A15002B), "").Success);
        Assert.Contains("Já estava instalado", AppCatalogService.TranslateExitCode(unchecked((int)0x8A150061), "").ShortMessage);
        Assert.Contains("reiniciar", AppCatalogService.TranslateExitCode(unchecked((int)0x8A150079), "").ShortMessage);

        var notFound = AppCatalogService.TranslateExitCode(unchecked((int)0x8A150014), "");
        Assert.False(notFound.Success);
        Assert.Contains("0x8A150014", notFound.ShortMessage);

        var noMatchLocale = AppCatalogService.TranslateExitCode(unchecked((int)0x8A150010), "");
        Assert.False(noMatchLocale.Success);
        Assert.Contains("0x8A150010", noMatchLocale.ShortMessage);
    }

    [Fact]
    public void AppCatalogService_DetectsInstalledAppsAccurately()
    {
        string tempPackages = Path.Combine(Path.GetTempPath(), "hh-appx-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempPackages, "5319275A.WhatsAppDesktop_cv1g1gvanyjgm"));
        try
        {
            var registryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Google Chrome",
                "Git",
                "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33816"
            };

            Assert.True(AppCatalogService.IsProgramInstalled(new AppPackageDefinition { Id = "chrome", InstalledName = "Google Chrome" }, registryNames, tempPackages));
            Assert.True(AppCatalogService.IsProgramInstalled(new AppPackageDefinition { Id = "git", InstalledName = "Git$" }, registryNames, tempPackages));
            Assert.False(AppCatalogService.IsProgramInstalled(new AppPackageDefinition { Id = "git", InstalledName = "GitHub$" }, registryNames, tempPackages));
            Assert.True(AppCatalogService.IsProgramInstalled(new AppPackageDefinition { Id = "vcredist", InstalledName = "Microsoft Visual C++ 2015-" }, registryNames, tempPackages));
            Assert.False(AppCatalogService.IsProgramInstalled(new AppPackageDefinition { Id = "vcredist86", InstalledName = "Microsoft Visual C++ 2015-" }, registryNames, tempPackages));
            Assert.True(AppCatalogService.IsProgramInstalled(new AppPackageDefinition { Id = "whatsapp", AppxPackage = "5319275A.WhatsAppDesktop" }, registryNames, tempPackages));
            Assert.False(AppCatalogService.IsProgramInstalled(new AppPackageDefinition { Id = "baixar-windows", Type = "link" }, registryNames, tempPackages));
        }
        finally
        {
            Directory.Delete(tempPackages, recursive: true);
        }
    }

    [Fact]
    public void AppCatalogService_ParsesWingetSearchTableAndOrganizesLocales()
    {
        string table = string.Join("\n",
            "Nome                             Id                           Versão  Correspondência   Origem",
            "-------------------------------------------------------------------------------------------------",
            "Mozilla Firefox                  Mozilla.Firefox              132.0   Moniker: firefox  winget",
            "Mozilla Firefox (pt-BR)          Mozilla.Firefox.pt-BR        132.0                     winget",
            "Mozilla Firefox (en-US)          Mozilla.Firefox.en-US        132.0                     winget",
            "Mozilla Firefox (es-ES)          Mozilla.Firefox.es-ES        132.0                     winget",
            "GIMP                             GIMP.GIMP                    2.10.38                   winget");

        var parsed = AppCatalogService.ParseWingetSearchOutput(table, "winget");
        Assert.Equal(5, parsed.Count);

        var (organized, hidden) = AppCatalogService.OrganizeWingetResults(parsed, "pt-BR", "firefox");
        Assert.Equal(2, hidden); // esconde en-US e es-ES, mantendo pt-BR e base
        Assert.Equal("Mozilla.Firefox.pt-BR", organized.First().Id);
    }

    [Fact]
    public void AppsCatalogViewModel_TogglesKitsFiltersCategoriesAndFlagsConflicts()
    {
        var vm = new AppsCatalogViewModel();

        Assert.Equal(6, vm.Kits.Count);
        Assert.Contains(vm.CategoryChips, c => c.Label == "Bibliotecas");
        Assert.Contains(vm.CategoryChips, c => c.Label == "IA");
        Assert.Contains(vm.CategoryChips, c => c.Label == "Diagnóstico");

        var essencial = vm.Kits.First(k => k.Name == "Essencial");
        vm.ToggleKit(essencial);
        Assert.True(essencial.IsActive);
        Assert.True(vm.SelectedCount > 0);
        Assert.Contains("marcado", vm.SelectionSummaryText);

        // Marca WinRAR junto com 7-Zip para validar aviso de duplicidade
        var winrar = vm.VisibleApps.First(a => a.Id == "winrar");
        vm.ToggleProgram(winrar);
        Assert.True(vm.HasArchiveConflictWarning);

        vm.ClearSelection();
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(essencial.IsActive);

        var iaChip = vm.CategoryChips.First(c => c.Label == "IA");
        vm.SelectCategory(iaChip);
        Assert.All(vm.VisibleApps, a => Assert.Equal("IA", a.ShortCategory));
        Assert.Contains(vm.VisibleApps, a => a.Id == "chatgpt");
        Assert.Contains(vm.VisibleApps, a => a.Id == "claude");
    }

    [Fact]
    public void AppsCatalogView_RendersCleanlyOnStaDispatcher()
    {
        Exception? renderEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new AppsCatalogViewModel();
                var view = new AppsCatalogView { DataContext = vm, Width = 960, Height = 680 };
                view.Measure(new Size(960, 680));
                view.Arrange(new Rect(0, 0, 960, 680));
                view.UpdateLayout();

                Assert.True(vm.VisibleApps.Count >= 55);
            }
            catch (Exception ex)
            {
                renderEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(renderEx);
    }

    [Fact]
    public void OptimizationViewModel_ShowsProgramNamesInSelectedKit()
    {
        var optVm = new OptimizationViewModel();
        Assert.Contains("Google Chrome", optVm.SelectedKitPrograms);

        optVm.SelectedKitId = "programador";
        Assert.Contains("Visual Studio Code", optVm.SelectedKitPrograms);
        Assert.Contains("Git", optVm.SelectedKitPrograms);
        Assert.False(string.IsNullOrWhiteSpace(optVm.SelectedProfileSummary));
    }

    [Fact]
    public void CatalogPrograms_AuditWingetAndShortcutIntegrity()
    {
        var doc = AppCatalogService.LoadCatalog();
        Assert.NotEmpty(doc.Programs);

        foreach (var p in doc.Programs)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Id), "ID do programa não pode ser vazio.");
            Assert.False(string.IsNullOrWhiteSpace(p.Name), $"Nome do programa '{p.Id}' não pode ser vazio.");

            if (!p.IsLink)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.WingetId), $"Programa '{p.Id}' deve ter WingetId configurado.");
            }
        }

        // Testa que programas portáteis (como Rufus e Ventoy) são detectados e atalhos são garantidos
        var rufusDef = doc.Programs.First(p => p.Id == "rufus");
        bool rufusDetected = AppCatalogService.IsProgramInstalled(rufusDef, new HashSet<string>());
        Assert.True(rufusDetected);

        bool shortcutEnsured = AppCatalogService.EnsureStartMenuShortcutForPackage(rufusDef);
        Assert.True(shortcutEnsured);
    }
}

