using System.Threading;
using WpfApplication = System.Windows.Application;
using WpfShutdownMode = System.Windows.ShutdownMode;

namespace Voolime;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppLogger.Initialize();
        AppLogger.Info("Voolime starting.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Error("Unhandled exception.", e.ExceptionObject as Exception);

        using var mutex = new Mutex(true, @"Local\Voolime.FocusVolume", out var created);
        if (!created)
        {
            AppLogger.Info("Another Voolime instance is already running.");
            return;
        }

        var app = new WpfApplication
        {
            ShutdownMode = WpfShutdownMode.OnExplicitShutdown
        };

        using var controller = new AppController(app);
        app.DispatcherUnhandledException += (_, e) =>
        {
            AppLogger.Error("Dispatcher exception.", e.Exception);
            e.Handled = true;
        };
        app.Run();
        AppLogger.Info("Voolime stopped.");
    }
}
