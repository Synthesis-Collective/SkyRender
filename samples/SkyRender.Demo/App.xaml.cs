using System;
using System.Threading.Tasks;
using System.Windows;

namespace SkyRender.Demo;

public partial class App : Application
{
    public App()
    {
        // Anything that escapes is logged (the log file survives a crash) rather than lost.
        DispatcherUnhandledException += (_, e) =>
        {
            DemoLog.Instance.LogError("Unhandled exception on the UI thread", e.Exception);
            MessageBox.Show(e.Exception.Message + "\n\nDetails are in the log.", "SkyRender Demo",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DemoLog.Instance.LogError("Unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
            DemoLog.Instance.LogError("Unobserved task exception", e.Exception);
    }
}
