using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeepVault.App.Services;

namespace DeepVault.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => AppState.Current.Log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { AppState.Current.Log.Error("Background task failed", a.Exception); a.SetObserved(); };
        AppState.Current.Log.Info($"DeepVault started (elevated: {AppState.Current.Disks.IsElevated})");

        var window = new MainWindow();
        MainWindow = window;
        int i = Array.IndexOf(e.Args, "--selftest");
        if (i >= 0 && i + 2 < e.Args.Length)
        {
            // UI self-test: drives every page with a disk image and saves screenshots (used for automated launch testing).
            window.Show();
            _ = new UiSelfTest(window, e.Args[i + 1], e.Args[i + 2]).RunAsync();
            return;
        }
        int o = Array.IndexOf(e.Args, "--open");
        window.Show();
        if (o >= 0 && o + 1 < e.Args.Length && File.Exists(e.Args[o + 1])) window.OpenPath(e.Args[o + 1]);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the application alive: a UI fault must never take down a running scan or restore silently.
        AppState.Current.ReportError("Unexpected error", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppState.Current.Log.Info("DeepVault closing");
        AppState.Current.Project?.Dispose();
        AppState.Current.CloseSource();
        base.OnExit(e);
    }
}
