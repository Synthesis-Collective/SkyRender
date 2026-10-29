using System;
using System.Threading.Tasks;
using System.Windows;

namespace SkyRender.CellDemo;

public partial class App : Application
{
    public App()
    {
        // Anything that escapes is logged (the log file survives a crash) rather than lost.
        DispatcherUnhandledException += (_, e) =>
        {
            CellLog.Instance.LogError("Unhandled exception on the UI thread", e.Exception);
            if (!StartupOptions.Current.Automated)
                MessageBox.Show(e.Exception.Message + "\n\nDetails are in the log.", "SkyRender Cell Demo",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CellLog.Instance.LogError("Unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
            CellLog.Instance.LogError("Unobserved task exception", e.Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupOptions.Current = StartupOptions.Parse(e.Args);
        base.OnStartup(e);
    }
}
