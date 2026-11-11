using System.Windows;
using System.Windows.Controls;
using HardwareHub.ViewModels;
using Microsoft.Win32;

namespace HardwareHub.Views
{
    public partial class DriversUpdateView : UserControl
    {
        public DriversUpdateView()
        {
            InitializeComponent();
        }

        private void OnBackupDriversClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DriversUpdateViewModel vm)
                return;

            var dlg = new OpenFolderDialog
            {
                Title = "Escolha onde salvar a pasta de backup dos drivers"
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
                _ = vm.BackupDriversAsync(dlg.FolderName);
        }

        private void OnImportDriversClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DriversUpdateViewModel vm)
                return;

            var dlg = new OpenFolderDialog
            {
                Title = "Selecione a pasta contendo os drivers (.inf) para importar e instalar"
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
                _ = vm.ImportDriversAsync(dlg.FolderName);
        }
    }
}

