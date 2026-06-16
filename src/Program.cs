using System.Threading;
using WpfApplication = System.Windows.Application;
using WpfShutdownMode = System.Windows.ShutdownMode;

namespace Voolime;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (UpdateService.IsUpdaterCommand(args))
        {
            return UpdateService.RunUpdaterCommand(args);
        }

        AppLogger.Initialize();
        AppLogger.Info("Voolime starting.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Error("Unhandled exception.", e.ExceptionObject as Exception);

        if (IsTestFlyoutCommand(args))
        {
            return RunTestFlyoutCommand(args);
        }

        using var mutex = new Mutex(true, @"Local\Voolime.FocusVolume", out var created);
        if (!created)
        {
            AppLogger.Info("Another Voolime instance is already running.");
            return 0;
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
        return 0;
    }

    private static bool IsTestFlyoutCommand(string[] args) =>
        args.Any(arg => string.Equals(arg, "--test-flyout", StringComparison.OrdinalIgnoreCase));

    private static int RunTestFlyoutCommand(string[] args)
    {
        var monitorDeviceName = GetTestMonitorDeviceName(args) ?? AppSettings.LoadIndicatorMonitorDeviceName();
        AppLogger.Info($"Starting test flyout mode. Monitor selection: {FormatMonitorSelection(monitorDeviceName)}.");

        var app = new WpfApplication
        {
            ShutdownMode = WpfShutdownMode.OnExplicitShutdown
        };

        var flyout = new FlyoutWindow();
        flyout.SetIndicatorMonitorDeviceName(monitorDeviceName);

        app.Startup += (_, _) =>
        {
            flyout.ShowStatus("Voolime", "42%", 0.42, muted: false, null);

            if (args.Any(arg => string.Equals(arg, "--rapid", StringComparison.OrdinalIgnoreCase)))
            {
                var values = new[] { 0.46, 0.51, 0.47, 0.55, 0.59, 0.54, 0.62, 0.66 };
                var index = 0;
                var rapidTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(90)
                };
                rapidTimer.Tick += (_, _) =>
                {
                    if (index >= values.Length)
                    {
                        rapidTimer.Stop();
                        return;
                    }

                    var value = values[index++];
                    flyout.ShowStatus("Voolime", $"{Math.Round(value * 100)}%", value, muted: false, null);
                };
                rapidTimer.Start();
            }

            var secondShowTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1700)
            };
            secondShowTimer.Tick += (_, _) =>
            {
                secondShowTimer.Stop();
                flyout.ShowStatus("Voolime", "73%", 0.73, muted: false, null);
            };
            secondShowTimer.Start();

            var shutdownTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            shutdownTimer.Tick += (_, _) =>
            {
                shutdownTimer.Stop();
                flyout.Close();
                app.Shutdown();
            };
            shutdownTimer.Start();
        };

        app.Run();
        AppLogger.Info("Test flyout mode stopped.");
        return 0;
    }

    private static string? GetTestMonitorDeviceName(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], "--monitor", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = args[index + 1];
            if (string.Equals(value, "primary", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return value.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? value
                : $@"\\.\{value}";
        }

        return null;
    }

    private static string FormatMonitorSelection(string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceName) ? "Primary Monitor" : deviceName;
}
