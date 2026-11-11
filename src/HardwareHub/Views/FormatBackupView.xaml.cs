using System.Windows;
using System.Windows.Controls;
using HardwareHub.ViewModels;
using Microsoft.Win32;

namespace HardwareHub.Views
{
    public partial class FormatBackupView : UserControl
    {
        public FormatBackupView()
        {
            InitializeComponent();
        }

        private void OnChooseDestinationClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not FormatBackupViewModel vm)
                return;

            var dlg = new OpenFolderDialog
            {
                Title = "Selecione a pasta de destino para salvar o backup"
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
                vm.DestinationFolder = dlg.FolderName;
        }

        private void OnOpenBackupFolderClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not FormatBackupViewModel vm)
                return;

            var dlg = new OpenFolderDialog
            {
                Title = "Selecione uma pasta HUB-Backup-* contendo hub-backup.json"
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                vm.ActiveSubTab = "Restore";
                vm.LoadBackupFromFolder(dlg.FolderName);
            }
        }
    }
}

