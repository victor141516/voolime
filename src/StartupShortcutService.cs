using System.IO;
using System.Runtime.InteropServices;

namespace Voolime;

internal sealed class StartupShortcutService
{
    private const string ShortcutFileName = "Voolime.lnk";
    private readonly string _startupShortcutPath;
    private readonly string? _executablePath;

    public StartupShortcutService()
    {
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _startupShortcutPath = Path.Combine(startupFolder, ShortcutFileName);
        _executablePath = Environment.ProcessPath;
        AppLogger.Info($"Startup shortcut path resolved: {_startupShortcutPath}");
    }

    public string ShortcutPath => _startupShortcutPath;

    public bool IsEnabled() =>
        File.Exists(_startupShortcutPath);

    public bool Toggle()
    {
        if (IsEnabled())
        {
            File.Delete(_startupShortcutPath);
            AppLogger.Info("Startup shortcut deleted.");
            return false;
        }

        CreateShortcut();
        AppLogger.Info("Startup shortcut created.");
        return true;
    }

    private void CreateShortcut()
    {
        if (string.IsNullOrWhiteSpace(_executablePath) || !File.Exists(_executablePath))
        {
            throw new InvalidOperationException("The running executable path could not be resolved.");
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("WScript.Shell could not be created.");

        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [_startupShortcutPath]);

            if (shortcut is null)
            {
                throw new InvalidOperationException("The startup shortcut could not be created.");
            }

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [_executablePath]);
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(_executablePath) ?? string.Empty]);
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, [$"{_executablePath},0"]);
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, []);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
