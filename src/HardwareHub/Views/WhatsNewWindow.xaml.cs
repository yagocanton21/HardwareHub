using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HardwareHub.Services;

namespace HardwareHub.Views
{
    public partial class WhatsNewWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public WhatsNewWindow(PostUpdateCheckResult result)
        {
            InitializeComponent();

            Title = $"Novidades • Hardware Hub v{result.CurrentVersion.ToString(3)}";
            TitleHeaderText.Text = $"🚀 Hardware Hub atualizado para v{result.CurrentVersion.ToString(3)}!";
            SubtitleHeaderText.Text = result.PreviousVersion != null
                ? $"Atualizado de v{result.PreviousVersion.ToString(3)} para v{result.CurrentVersion.ToString(3)}. Veja o que mudou:"
                : "Confira os novos recursos e correções disponíveis nesta versão:";

            VersionsItemsControl.ItemsSource = result.VersionsToShow;
            SourceInitialized += OnWindowSourceInitialized;
        }

        private void OnWindowSourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int useDarkMode = 1;
                    DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int));
                }
            }
            catch
            {
                // Ignora caso o SO não suporte atributo DWM
            }
        }

        private void OnOpenGitHubClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/yagocanton21/HardwareHub/releases",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignora erro ao abrir URL externa
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

