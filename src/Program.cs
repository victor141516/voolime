using System.Threading;
using WpfApplication = System.Windows.Application;
using WpfShutdownMode = System.Windows.ShutdownMode;

namespace Voolime;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\Voolime.FocusVolume", out var created);
        if (!created)
        {
            return;
        }

        var app = new WpfApplication
        {
            ShutdownMode = WpfShutdownMode.OnExplicitShutdown
        };

        using var controller = new AppController(app);
        app.Run();
    }
}
