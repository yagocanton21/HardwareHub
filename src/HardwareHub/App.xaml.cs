using System.IO;
using System.Windows;

namespace HardwareHub
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += (s, e) =>
            {
                try
                {
                    File.WriteAllText("crash.log", e.Exception.ToString());
                }
                catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    File.WriteAllText("crash_domain.log", e.ExceptionObject?.ToString());
                }
                catch { }
            };
        }
    }
}
