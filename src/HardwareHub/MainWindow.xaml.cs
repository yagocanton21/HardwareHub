using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HardwareHub
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        public MainWindow()
        {
            InitializeComponent();

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int trueValue = 1;
                // Ativa o Dark Mode imersivo da barra de título nativa no Windows 10 (1809+) e Windows 11
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref trueValue, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref trueValue, sizeof(int));

                // Cor escura sólida (#18181B em formato BGR: 0x001B1818)
                int darkCaptionColor = 0x001B1818;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref darkCaptionColor, sizeof(int));

                // Texto do título em branco puro
                int whiteTextColor = 0x00FFFFFF;
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref whiteTextColor, sizeof(int));
            };

            Closing += (s, e) =>
            {
                (DataContext as IDisposable)?.Dispose();
            };
        }
    }
}