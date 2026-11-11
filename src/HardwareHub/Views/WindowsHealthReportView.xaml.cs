using System.Windows;
using System.Windows.Controls;
using HardwareHub.ViewModels;
using Microsoft.Win32;

namespace HardwareHub.Views
{
    public partial class WindowsHealthReportView : UserControl
    {
        public WindowsHealthReportView()
        {
            InitializeComponent();
        }

        private void OnSaveHtmlReportClick(object sender, RoutedEventArgs e)
            => SaveReportWithFilter("Relatório HTML (*.html)|*.html|Texto Resumido (*.txt)|*.txt", ".html", "HardwareHUB-Relatorio");

        private void OnSaveJsonReportClick(object sender, RoutedEventArgs e)
            => SaveReportWithFilter("JSON Estruturado (*.json)|*.json", ".json", "HardwareHUB-Relatorio");

        private void SaveReportWithFilter(string filter, string defaultExt, string defaultName)
        {
            if (DataContext is not WindowsHealthReportViewModel vm) return;

            var dlg = new SaveFileDialog
            {
                Title = "Salvar Relatório Técnico do PC",
                Filter = filter,
                DefaultExt = defaultExt,
                FileName = $"{defaultName}-{System.DateTime.Now:yyyyMMdd-HHmm}"
            };

            if (dlg.ShowDialog() == true)
                vm.SaveReportFile(dlg.FileName);
        }
    }
}

