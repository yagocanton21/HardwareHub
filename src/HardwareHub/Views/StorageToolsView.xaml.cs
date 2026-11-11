using System.Windows;
using System.Windows.Controls;
using HardwareHub.ViewModels;
using Microsoft.Win32;

namespace HardwareHub.Views
{
    public partial class StorageToolsView : UserControl
    {
        public StorageToolsView()
        {
            InitializeComponent();
        }

        private void BrowseDuplicateFolder_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not StorageToolsViewModel vm)
                return;

            var dlg = new OpenFolderDialog
            {
                Title = "Adicionar pasta para busca de arquivos duplicados"
            };

            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                vm.AddCustomDuplicateFolder(dlg.FolderName);
            }
        }

        private void BrowseDiskMapFolder_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not StorageToolsViewModel vm)
                return;

            var dlg = new OpenFolderDialog
            {
                Title = "Selecionar pasta ou unidade para mapear espaço em disco"
            };

            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                _ = vm.ScanDiskMapAsync(dlg.FolderName);
            }
        }
    }
}

