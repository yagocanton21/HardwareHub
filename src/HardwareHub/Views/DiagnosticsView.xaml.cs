using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HardwareHub.ViewModels;

namespace HardwareHub.Views
{
    public partial class DiagnosticsView : UserControl
    {
        public DiagnosticsView()
        {
            InitializeComponent();
        }

        private DiagnosticsViewModel? Vm => DataContext as DiagnosticsViewModel;

        private void FocusKeyboardButton_Click(object sender, RoutedEventArgs e)
        {
            KeyboardTesterSurface.Focus();
            Keyboard.Focus(KeyboardTesterSurface);
        }

        private void KeyboardSurface_MouseDown(object sender, MouseButtonEventArgs e)
        {
            KeyboardTesterSurface.Focus();
            Keyboard.Focus(KeyboardTesterSurface);
        }

        private void KeyboardSurface_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Vm == null) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            Vm.HandleKeyEvent(key.ToString(), isDown: true);
            e.Handled = true;
        }

        private void KeyboardSurface_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (Vm == null) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            Vm.HandleKeyEvent(key.ToString(), isDown: false);
            e.Handled = true;
        }

        private void MouseTestPad_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (Vm == null) return;
            var buttonName = e.ChangedButton switch
            {
                MouseButton.Left => "Left",
                MouseButton.Right => "Right",
                MouseButton.Middle => "Middle",
                MouseButton.XButton1 => "XButton1",
                MouseButton.XButton2 => "XButton2",
                _ => ""
            };
            if (!string.IsNullOrEmpty(buttonName))
                Vm.RecordMouseButton(buttonName);
            e.Handled = true;
        }

        private void MouseTestPad_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            Vm?.RecordMouseWheel(e.Delta);
            e.Handled = true;
        }

        private void MouseTestPad_MouseMove(object sender, MouseEventArgs e)
        {
            Vm?.RecordMouseMove(Stopwatch.GetTimestamp());
        }

        private void PreviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Vm?.NextPixelColor();
        }

        private void LaunchFullscreenPixelTest_Click(object sender, RoutedEventArgs e)
        {
            if (Vm == null) return;

            var hintText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Text = "Clique ou use → / Espaço para trocar de cor • ← volta • ESC sai"
            };

            var hintPill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(140, 12, 12, 16)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10, 16, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 32),
                Child = hintText
            };

            var root = new Grid();
            root.Children.Add(hintPill);

            var fullWindow = new Window
            {
                Title = "Teste de Dead Pixel - Hardware HUB",
                WindowStyle = WindowStyle.None,
                WindowState = WindowState.Maximized,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                Cursor = Cursors.Hand,
                Content = root
            };

            void SyncColor()
            {
                var color = (Color)ColorConverter.ConvertFromString(Vm.CurrentPixelColorHex);
                fullWindow.Background = new SolidColorBrush(color);
                hintText.Text = $"{Vm.CurrentPixelColorName} ({Vm.CurrentPixelColorIndex + 1}/{DiagnosticsViewModel.DisplayTestColors.Length}) — Clique ou → avança • ← volta • ESC sai";
            }

            SyncColor();

            fullWindow.MouseLeftButtonDown += (_, _) =>
            {
                Vm.NextPixelColor();
                SyncColor();
            };

            fullWindow.MouseRightButtonDown += (_, _) =>
            {
                Vm.PrevPixelColor();
                SyncColor();
            };

            fullWindow.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    fullWindow.Close();
                }
                else if (args.Key is Key.Right or Key.Space or Key.Enter)
                {
                    Vm.NextPixelColor();
                    SyncColor();
                }
                else if (args.Key == Key.Left)
                {
                    Vm.PrevPixelColor();
                    SyncColor();
                }
            };

            fullWindow.Show();
        }
    }
}

