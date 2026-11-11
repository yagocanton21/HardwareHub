using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace HardwareHub.Tests
{
    internal static class WpfTestHost
    {
        private static readonly Lazy<Dispatcher> SharedWpfDispatcher = new(() =>
        {
            Dispatcher? dispatcher = null;
            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                if (Application.Current == null)
                {
                    var app = new HardwareHub.App();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.Wait();
            return dispatcher!;
        });

        public static void RunOnWpfThread(Action action) => SharedWpfDispatcher.Value.Invoke(action);

        public static void PumpDispatcher(DispatcherPriority priority = DispatcherPriority.ApplicationIdle)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(priority, new DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }), null);
            Dispatcher.PushFrame(frame);
        }

        public static Window ShowHostedControl(FrameworkElement control)
        {
            bool loaded = false;
            control.Loaded += (_, _) => loaded = true;

            var hostWindow = new Window
            {
                Width = 1000,
                Height = 700,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.ToolWindow,
                Content = control
            };
            hostWindow.Show();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!loaded && DateTime.UtcNow < deadline)
            {
                PumpDispatcher(DispatcherPriority.ApplicationIdle);
                Thread.Sleep(15);
            }

            hostWindow.UpdateLayout();
            PumpDispatcher(DispatcherPriority.ApplicationIdle);
            return hostWindow;
        }

        public static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed) yield return typed;
                foreach (var nested in FindVisualChildren<T>(child))
                {
                    yield return nested;
                }
            }
        }
    }
}

