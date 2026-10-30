using System;
using System.Threading.Tasks;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Builder;

namespace SkyRender.CharacterDemo;

public partial class App : Application
{
    public App()
    {
        // Anything that escapes is logged (the log file survives a crash) rather than lost.
        DispatcherUnhandledException += (_, e) =>
        {
            DemoLog.Instance.LogError("Unhandled exception on the UI thread", e.Exception);
            if (!StartupOptions.Current.Automated)
                MessageBox.Show(e.Exception.Message + "\n\nDetails are in the log.", "SkyRender Character Demo",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DemoLog.Instance.LogError("Unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
            DemoLog.Instance.LogError("Unobserved task exception", e.Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupOptions.Current = StartupOptions.Parse(e.Args);
        // SkyRender.Character's view model is built on ReactiveUI, which (from version 20) must be
        // initialized by the application before the first view model is created; without this the
        // viewer's constructor throws "ReactiveUI has not been initialized" (USAGE.md, section 6).
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithWpf()
            .BuildApp();
        // Property-change notifications are delivered on the UI thread.
        RxSchedulers.MainThreadScheduler = new System.Reactive.Concurrency.DispatcherScheduler(Dispatcher);
        base.OnStartup(e);
    }
}
